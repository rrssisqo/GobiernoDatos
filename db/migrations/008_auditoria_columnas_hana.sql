/* =====================================================================
   Migración 008 · Auditoría con las columnas de la consulta de HANA
   Cada acceso guarda AREA, CONTENT, GOBIERNO y RESPONSABLE tal como llegaron en el extracto
   (CONT_TI_RECERTIFICACION), para que Auditoría muestre exactamente esas columnas por evento.
     - gd.stg_recertificacion: nueva columna gobierno.
     - gd.recert_acceso: area, content, gobierno, responsable (nulos en eventos cargados antes).
     - gd.sp_cargar_extracto_recertificacion: las guarda al cargar.
     - gd.vw_recert_evidencia: las expone.
   Requiere 001. Idempotente. Ejecutar con QUOTED_IDENTIFIER ON (sqlcmd -I).
   ===================================================================== */
IF COL_LENGTH('gd.stg_recertificacion', 'gobierno') IS NULL
    ALTER TABLE gd.stg_recertificacion ADD gobierno NVARCHAR(100) NULL;
GO
IF COL_LENGTH('gd.recert_acceso', 'area') IS NULL
    ALTER TABLE gd.recert_acceso ADD area        NVARCHAR(100) NULL,
                                     content     NVARCHAR(100) NULL,
                                     gobierno    NVARCHAR(100) NULL,
                                     responsable NVARCHAR(150) NULL;
GO

CREATE OR ALTER PROCEDURE gd.sp_cargar_extracto_recertificacion
    @nombre         NVARCHAR(60),
    @fecha_extracto DATE = NULL,
    @fecha_limite   DATE = NULL,
    @usuario        NVARCHAR(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @usuario = ISNULL(@usuario, SUSER_SNAME());
    SET @nombre = LTRIM(RTRIM(@nombre));
    DECLARE @fecha DATE = ISNULL(@fecha_extracto, CAST(GETDATE() AS DATE));

    IF NULLIF(@nombre, N'') IS NULL
        THROW 50101, N'Indica el nombre del evento.', 1;
    IF EXISTS (SELECT 1 FROM gd.recert_evento WHERE nombre = @nombre)
        THROW 50102, N'Ya existe un evento con ese nombre.', 1;
    IF EXISTS (SELECT 1 FROM gd.recert_evento WHERE estatus <> 'CERRADO')
        THROW 50103, N'Hay un evento en borrador o abierto: ciérralo o descártalo antes de cargar otro extracto.', 1;
    IF NOT EXISTS (SELECT 1 FROM gd.stg_recertificacion)
        THROW 50104, N'El staging del extracto está vacío.', 1;

    BEGIN TRANSACTION;

    INSERT INTO gd.recert_evento (nombre, fecha_extracto, fecha_limite, estatus, usuario)
    VALUES (@nombre, @fecha_extracto, @fecha_limite, 'BORRADOR', @usuario);
    DECLARE @evento_id INT = SCOPE_IDENTITY();

    -- 1. Limpieza y validación por fila
    SELECT  s.fila,
            UPPER(LTRIM(RTRIM(s.usuario)))               AS usuario,
            NULLIF(LTRIM(RTRIM(s.nombre)), N'')          AS nombre,
            UPPER(LTRIM(RTRIM(s.rol)))                   AS rol,
            gd.fn_normalizar_ruta(ISNULL(s.vista, N''))  AS vista,
            TRY_CAST(REPLACE(ISNULL(s.accesos_3m, N'0'), N',', N'') AS INT) AS accesos_3m,
            NULLIF(LTRIM(RTRIM(s.area_responsable)), N'') AS macro_area,
            NULLIF(LTRIM(RTRIM(s.responsable)), N'')      AS responsable,
            NULLIF(LTRIM(RTRIM(s.area)), N'')             AS area,
            NULLIF(LTRIM(RTRIM(s.content)), N'')          AS content,
            NULLIF(LTRIM(RTRIM(s.gobierno)), N'')         AS gobierno,
            CAST(CASE
                WHEN NULLIF(LTRIM(RTRIM(s.usuario)), N'') IS NULL OR NULLIF(LTRIM(RTRIM(s.rol)), N'') IS NULL
                  OR NULLIF(LTRIM(RTRIM(s.vista)), N'') IS NULL                       THEN N'Usuario, rol o vista vacíos'
                WHEN NULLIF(LTRIM(RTRIM(s.area_responsable)), N'') IS NULL             THEN N'Sin Área Responsable (macro área)'
                WHEN TRY_CAST(REPLACE(ISNULL(s.accesos_3m, N'0'), N',', N'') AS INT) IS NULL
                  OR TRY_CAST(REPLACE(ISNULL(s.accesos_3m, N'0'), N',', N'') AS INT) < 0 THEN N'Accesos de 3 meses no numérico'
            END AS NVARCHAR(200)) AS motivo
    INTO #f
    FROM gd.stg_recertificacion AS s;

    -- 2. 1 rol = 1 vista, dentro del extracto y contra lo registrado
    SELECT rol   INTO #roles_multiples  FROM #f WHERE motivo IS NULL GROUP BY rol   HAVING COUNT(DISTINCT vista) > 1;
    SELECT vista INTO #vistas_multiples FROM #f WHERE motivo IS NULL GROUP BY vista HAVING COUNT(DISTINCT rol)   > 1;

    UPDATE #f SET motivo = N'Rol con más de una vista'
    WHERE motivo IS NULL AND rol IN (SELECT rol FROM #roles_multiples);
    UPDATE #f SET motivo = N'Vista con más de un rol'
    WHERE motivo IS NULL AND vista IN (SELECT vista FROM #vistas_multiples);

    UPDATE f SET motivo = N'El rol ya está asignado a otra vista'
    FROM #f AS f
    JOIN gd.rol_hana AS r ON r.nombre = f.rol
    JOIN gd.vista_gobernada AS v ON v.vista_id = r.vista_id
    WHERE f.motivo IS NULL AND v.ruta_tecnica <> f.vista;

    UPDATE f SET motivo = N'La vista ya tiene otro rol dedicado'
    FROM #f AS f
    JOIN gd.vista_gobernada AS v ON v.ruta_tecnica = f.vista
    JOIN gd.rol_hana AS r ON r.vista_id = v.vista_id
    WHERE f.motivo IS NULL AND r.nombre <> f.rol;

    WITH d AS (SELECT motivo, ROW_NUMBER() OVER (PARTITION BY usuario, rol ORDER BY fila) AS rn FROM #f WHERE motivo IS NULL)
    UPDATE d SET motivo = N'Usuario y rol duplicados en el extracto' WHERE rn > 1;

    INSERT INTO gd.recert_carga_rechazo (evento_id, fila, usuario, rol, vista, motivo)
    SELECT @evento_id, fila, usuario, rol, vista, motivo FROM #f WHERE motivo IS NOT NULL;
    DELETE FROM #f WHERE motivo IS NOT NULL;

    -- 3. Usuarios HANA
    MERGE gd.usuario_hana AS t
    USING (SELECT usuario, MAX(nombre) AS nombre FROM #f GROUP BY usuario) AS s ON t.codigo = s.usuario
    WHEN MATCHED AND s.nombre IS NOT NULL AND t.nombre <> s.nombre THEN UPDATE SET nombre = s.nombre
    WHEN NOT MATCHED THEN INSERT (codigo, nombre) VALUES (s.usuario, ISNULL(s.nombre, s.usuario));

    -- 4. Vistas nuevas: entran al inventario en "Por Iniciar", sin gobierno ni DP hasta clasificarlas
    DECLARE @nuevas TABLE (vista_id INT NOT NULL);
    INSERT INTO gd.vista_gobernada (ruta_tecnica, nombre_negocio, gobierno_id, fecha_alta, fecha_primera_aparicion)
    OUTPUT inserted.vista_id INTO @nuevas
    SELECT x.vista, RIGHT(x.vista, CHARINDEX(N'/', REVERSE(x.vista) + N'/') - 1), NULL, @fecha, @fecha
    FROM (SELECT DISTINCT vista FROM #f) AS x
    WHERE NOT EXISTS (SELECT 1 FROM gd.vista_gobernada AS v WHERE v.ruta_tecnica = x.vista);
    DECLARE @vistas_nuevas INT = @@ROWCOUNT;

    INSERT INTO gd.mov_etapa_vista (vista_id, etapa_id, fecha, motivo_id, es_regreso, comentario, usuario)
    SELECT n.vista_id, e.etapa_id, @fecha, 1, 0, CONCAT(N'Alta por carga del extracto · ', @nombre), @usuario
    FROM @nuevas AS n
    CROSS JOIN (SELECT etapa_id FROM gd.etapa_vista WHERE orden = 0) AS e;

    UPDATE v SET fecha_primera_aparicion = @fecha
    FROM gd.vista_gobernada AS v
    WHERE v.fecha_primera_aparicion IS NULL AND v.ruta_tecnica IN (SELECT vista FROM #f);

    -- 5. Roles (1 rol = 1 vista)
    INSERT INTO gd.rol_hana (nombre, vista_id, area, content)
    SELECT f.rol, v.vista_id, MAX(f.area), MAX(f.content)
    FROM #f AS f
    JOIN gd.vista_gobernada AS v ON v.ruta_tecnica = f.vista
    WHERE NOT EXISTS (SELECT 1 FROM gd.rol_hana AS r WHERE r.nombre = f.rol)
    GROUP BY f.rol, v.vista_id;
    DECLARE @roles_nuevos INT = @@ROWCOUNT;

    -- 6. Macro áreas y certificadores ("Responsable" del extracto entra como titular si la macro área no tiene uno vigente)
    INSERT INTO gd.macro_area (nombre)
    SELECT DISTINCT f.macro_area FROM #f AS f
    WHERE NOT EXISTS (SELECT 1 FROM gd.macro_area AS m WHERE m.nombre = f.macro_area);

    INSERT INTO gd.responsable (nombre)
    SELECT DISTINCT f.responsable FROM #f AS f
    WHERE f.responsable IS NOT NULL AND NOT EXISTS (SELECT 1 FROM gd.responsable AS r WHERE r.nombre = f.responsable);

    INSERT INTO gd.macro_area_certificador (macro_area_id, responsable_id, tipo, fecha_desde)
    SELECT DISTINCT m.macro_area_id, r.responsable_id, 'TITULAR', @fecha
    FROM #f AS f
    JOIN gd.macro_area AS m ON m.nombre = f.macro_area
    CROSS APPLY (SELECT TOP (1) responsable_id FROM gd.responsable WHERE nombre = f.responsable ORDER BY responsable_id) AS r
    WHERE f.responsable IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM gd.macro_area_certificador AS c
                      WHERE c.macro_area_id = m.macro_area_id AND (c.fecha_hasta IS NULL OR c.fecha_hasta >= @fecha));

    -- 7. Accesos del evento
    INSERT INTO gd.recert_acceso (evento_id, usuario_id, rol_id, macro_area_id, accesos_3m, area, content, gobierno, responsable)
    SELECT @evento_id, u.usuario_id, r.rol_id, m.macro_area_id, f.accesos_3m, f.area, f.content, f.gobierno, f.responsable
    FROM #f AS f
    JOIN gd.usuario_hana AS u ON u.codigo = f.usuario
    JOIN gd.rol_hana     AS r ON r.nombre = f.rol
    JOIN gd.macro_area   AS m ON m.nombre = f.macro_area;

    -- 8. Sincronización del inventario: presentes se marcan; ausentes se marcan, nunca se borran
    UPDATE v
    SET en_ultima_recertificacion = CASE WHEN p.vista IS NULL THEN 0 ELSE 1 END,
        fecha_ultima_aparicion    = CASE WHEN p.vista IS NULL THEN v.fecha_ultima_aparicion ELSE @fecha END
    FROM gd.vista_gobernada AS v
    LEFT JOIN (SELECT DISTINCT vista FROM #f) AS p ON p.vista = v.ruta_tecnica;

    -- 9. Uso por vista
    INSERT INTO gd.vista_uso (evento_id, vista_id, accesos_3m, usuarios, roles)
    SELECT @evento_id, r.vista_id, SUM(a.accesos_3m), COUNT(DISTINCT a.usuario_id), COUNT(DISTINCT a.rol_id)
    FROM gd.recert_acceso AS a
    JOIN gd.rol_hana AS r ON r.rol_id = a.rol_id
    WHERE a.evento_id = @evento_id
    GROUP BY r.vista_id;

    UPDATE gd.recert_evento
    SET val_roles_varias_vistas = (SELECT COUNT(*) FROM #roles_multiples),
        val_vistas_varios_roles = (SELECT COUNT(*) FROM #vistas_multiples),
        val_roles_nuevos        = @roles_nuevos,
        val_vistas_nuevas       = @vistas_nuevas
    WHERE evento_id = @evento_id;

    TRUNCATE TABLE gd.stg_recertificacion;

    COMMIT TRANSACTION;

    SELECT e.evento_id, e.val_roles_varias_vistas, e.val_vistas_varios_roles, e.val_roles_nuevos, e.val_vistas_nuevas,
           (SELECT COUNT(*) FROM gd.recert_acceso WHERE evento_id = e.evento_id)        AS accesos,
           (SELECT COUNT(*) FROM gd.recert_carga_rechazo WHERE evento_id = e.evento_id) AS filas_rechazadas
    FROM gd.recert_evento AS e WHERE e.evento_id = @evento_id;
END;
GO

CREATE OR ALTER VIEW gd.vw_recert_evidencia
AS
SELECT  e.evento_id, e.nombre AS evento, e.estatus AS estatus_evento,
        a.acceso_id, m.macro_area_id, m.nombre AS macro_area,
        u.codigo AS usuario, u.nombre AS nombre_usuario,
        r.nombre AS rol, v.vista_id, v.ruta_tecnica AS vista, a.accesos_3m,
        CAST(COALESCE(d.decision, 'PENDIENTE') AS VARCHAR(9)) AS decision, d.comentario,   -- ISNULL truncaría a VARCHAR(8)
        rs.nombre AS decidido_por, d.fecha_registro AS fecha_decision,
        b.ticket, b.fecha_ejecucion, b.ejecutado_por,
        -- Columnas tal como llegaron en el extracto (migración 008); AREA y CONTENT caen al rol en eventos anteriores.
        COALESCE(a.area, r.area) AS area, COALESCE(a.content, r.content) AS content, a.gobierno, a.responsable
FROM gd.recert_acceso AS a
JOIN gd.recert_evento   AS e ON e.evento_id = a.evento_id
JOIN gd.macro_area      AS m ON m.macro_area_id = a.macro_area_id
JOIN gd.usuario_hana    AS u ON u.usuario_id = a.usuario_id
JOIN gd.rol_hana        AS r ON r.rol_id = a.rol_id
JOIN gd.vista_gobernada AS v ON v.vista_id = r.vista_id
LEFT JOIN gd.vw_recert_decision_vigente AS d ON d.acceso_id = a.acceso_id
LEFT JOIN gd.responsable AS rs ON rs.responsable_id = d.decidido_por
LEFT JOIN gd.recert_baja_ejecucion AS b ON b.acceso_id = a.acceso_id;
GO

IF NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '008_auditoria_columnas_hana')
    INSERT INTO gd.migracion (migracion_id, descripcion)
    VALUES ('008_auditoria_columnas_hana', N'AREA, CONTENT, GOBIERNO y RESPONSABLE del extracto por acceso (Auditoría)');
GO
