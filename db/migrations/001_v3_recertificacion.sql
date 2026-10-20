/* =====================================================================
   Migración 001 · v2 → v3: recertificación de accesos
   Base: db/gobierno_datos_ddl_v2.sql      Modelo: docs/modelo_er_v3.mmd
   Deltas: docs/04_pendientes_y_backlog.md ("Deltas v2 → v3")
   Idempotente: cada objeto se crea o modifica solo si hace falta.
   Ejecutar con QUOTED_IDENTIFIER ON (sqlcmd -I).
   ---------------------------------------------------------------------
     1. Control de migraciones
     2. Función de normalización de ruta técnica
     3. vista_gobernada: apariciones, gobierno opcional
     4. Tablas de recertificación
     5. Triggers: foto inmutable y bloqueo de decisiones
     6. Procedimientos
     7. Vistas de recertificación
     8. Vistas v2 ajustadas (gobierno opcional) y validaciones v3
   ===================================================================== */

/* =====================================================================
   1. CONTROL DE MIGRACIONES
   ===================================================================== */
IF OBJECT_ID('gd.migracion') IS NULL
CREATE TABLE gd.migracion (
    migracion_id     VARCHAR(60)    NOT NULL CONSTRAINT pk_migracion PRIMARY KEY,
    descripcion      NVARCHAR(300)  NOT NULL,
    fecha_aplicacion DATETIME2(0)   NOT NULL CONSTRAINT df_migracion_fecha DEFAULT (SYSDATETIME()),
    usuario          NVARCHAR(100)  NOT NULL CONSTRAINT df_migracion_usuario DEFAULT (SUSER_SNAME())
);
GO

/* =====================================================================
   2. NORMALIZACIÓN DE RUTA TÉCNICA
      PAQUETE/VISTA en mayúsculas, sin "_SYS_BIC". ni comillas.
   ===================================================================== */
CREATE OR ALTER FUNCTION gd.fn_normalizar_ruta (@ruta NVARCHAR(400))
RETURNS NVARCHAR(400)
WITH SCHEMABINDING
AS
BEGIN
    DECLARE @r NVARCHAR(400) = LTRIM(RTRIM(REPLACE(@ruta, N'"', N'')));
    IF @r LIKE N'[_]SYS[_]BIC.%' SET @r = SUBSTRING(@r, 10, 400);
    RETURN UPPER(LTRIM(RTRIM(@r)));
END;
GO

/* =====================================================================
   3. VISTA_GOBERNADA
      + fecha_primera_aparicion, fecha_ultima_aparicion, en_ultima_recertificacion
      gobierno_id NULL hasta clasificar; fecha_recertificacion se elimina (se deriva de vista_uso).
   ===================================================================== */
IF COL_LENGTH('gd.vista_gobernada', 'fecha_primera_aparicion') IS NULL
    ALTER TABLE gd.vista_gobernada ADD fecha_primera_aparicion DATE NULL;
IF COL_LENGTH('gd.vista_gobernada', 'fecha_ultima_aparicion') IS NULL
    ALTER TABLE gd.vista_gobernada ADD fecha_ultima_aparicion DATE NULL;
IF COL_LENGTH('gd.vista_gobernada', 'en_ultima_recertificacion') IS NULL
    ALTER TABLE gd.vista_gobernada ADD en_ultima_recertificacion BIT NOT NULL
        CONSTRAINT df_vista_en_ultima_recert DEFAULT (0);
IF COL_LENGTH('gd.vista_gobernada', 'fecha_recertificacion') IS NOT NULL
    ALTER TABLE gd.vista_gobernada DROP COLUMN fecha_recertificacion;
GO

IF COLUMNPROPERTY(OBJECT_ID('gd.vista_gobernada'), 'gobierno_id', 'AllowsNull') = 0
BEGIN
    ALTER TABLE gd.vista_gobernada DROP CONSTRAINT fk_vista_gobierno;
    ALTER TABLE gd.vista_gobernada ALTER COLUMN gobierno_id INT NULL;
    ALTER TABLE gd.vista_gobernada ADD CONSTRAINT fk_vista_gobierno
        FOREIGN KEY (gobierno_id) REFERENCES gd.gobierno (gobierno_id);
END;
GO

/* =====================================================================
   4. TABLAS DE RECERTIFICACIÓN
   ===================================================================== */
-- "Área Responsable" del extracto.
IF OBJECT_ID('gd.macro_area') IS NULL
CREATE TABLE gd.macro_area (
    macro_area_id INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_macro_area PRIMARY KEY,
    nombre        NVARCHAR(100) NOT NULL CONSTRAINT uq_macro_area_nombre UNIQUE,
    activo        BIT NOT NULL CONSTRAINT df_macro_area_activo DEFAULT (1)
);
GO

-- Quién certifica cada macro área: titular y delegados, con vigencia.
IF OBJECT_ID('gd.macro_area_certificador') IS NULL
CREATE TABLE gd.macro_area_certificador (
    macro_area_id  INT NOT NULL CONSTRAINT fk_mac_macro_area REFERENCES gd.macro_area (macro_area_id),
    responsable_id INT NOT NULL CONSTRAINT fk_mac_responsable REFERENCES gd.responsable (responsable_id),
    tipo           VARCHAR(10) NOT NULL CONSTRAINT ck_mac_tipo CHECK (tipo IN ('TITULAR','DELEGADO')),
    fecha_desde    DATE NOT NULL CONSTRAINT df_mac_desde DEFAULT (CAST(GETDATE() AS DATE)),
    fecha_hasta    DATE NULL,
    CONSTRAINT pk_macro_area_certificador PRIMARY KEY (macro_area_id, responsable_id),
    CONSTRAINT ck_mac_fechas CHECK (fecha_hasta IS NULL OR fecha_hasta >= fecha_desde)
);
GO

IF OBJECT_ID('gd.usuario_hana') IS NULL
CREATE TABLE gd.usuario_hana (
    usuario_id INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_usuario_hana PRIMARY KEY,
    codigo     VARCHAR(30)   NOT NULL CONSTRAINT uq_usuario_hana_codigo UNIQUE,   -- BFRMEXFN
    nombre     NVARCHAR(150) NOT NULL
);
GO

-- Cada vista tiene un rol dedicado: 1 rol = 1 vista.
IF OBJECT_ID('gd.rol_hana') IS NULL
CREATE TABLE gd.rol_hana (
    rol_id   INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_rol_hana PRIMARY KEY,
    nombre   NVARCHAR(128) NOT NULL CONSTRAINT uq_rol_hana_nombre UNIQUE,
    vista_id INT NOT NULL CONSTRAINT fk_rol_vista REFERENCES gd.vista_gobernada (vista_id)
                          CONSTRAINT uq_rol_hana_vista UNIQUE,
    area     NVARCHAR(60)  NULL,
    content  NVARCHAR(100) NULL
);
GO

-- Evento = carga del extracto (foto inmutable). BORRADOR → ABIERTO → CERRADO.
IF OBJECT_ID('gd.recert_evento') IS NULL
CREATE TABLE gd.recert_evento (
    evento_id      INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_recert_evento PRIMARY KEY,
    nombre         NVARCHAR(60) NOT NULL CONSTRAINT uq_recert_evento_nombre UNIQUE,   -- 2026-E2
    fecha_extracto DATE NULL,
    fecha_inicio   DATE NULL,
    fecha_limite   DATE NULL,
    fecha_cierre   DATE NULL,
    estatus        VARCHAR(10) NOT NULL CONSTRAINT df_recert_evento_estatus DEFAULT ('BORRADOR')
                   CONSTRAINT ck_recert_evento_estatus CHECK (estatus IN ('BORRADOR','ABIERTO','CERRADO')),
    -- Resultado de la validación de la carga (pantalla Eventos).
    val_roles_varias_vistas INT NOT NULL CONSTRAINT df_recert_val_rv DEFAULT (0),
    val_vistas_varios_roles INT NOT NULL CONSTRAINT df_recert_val_vr DEFAULT (0),
    val_roles_nuevos        INT NOT NULL CONSTRAINT df_recert_val_rn DEFAULT (0),
    val_vistas_nuevas       INT NOT NULL CONSTRAINT df_recert_val_vn DEFAULT (0),
    usuario        NVARCHAR(100) NOT NULL CONSTRAINT df_recert_evento_usuario DEFAULT (SUSER_SNAME()),
    fecha_registro DATETIME2(0)  NOT NULL CONSTRAINT df_recert_evento_fecha_reg DEFAULT (SYSDATETIME()),
    CONSTRAINT ck_recert_evento_cierre CHECK (estatus <> 'CERRADO' OR fecha_cierre IS NOT NULL)
);
GO

-- Grano: usuario × rol (× vista vía rol_hana) por evento.
IF OBJECT_ID('gd.recert_acceso') IS NULL
CREATE TABLE gd.recert_acceso (
    acceso_id     BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT pk_recert_acceso PRIMARY KEY,
    evento_id     INT NOT NULL CONSTRAINT fk_ra_evento     REFERENCES gd.recert_evento (evento_id),
    usuario_id    INT NOT NULL CONSTRAINT fk_ra_usuario    REFERENCES gd.usuario_hana (usuario_id),
    rol_id        INT NOT NULL CONSTRAINT fk_ra_rol        REFERENCES gd.rol_hana (rol_id),
    macro_area_id INT NOT NULL CONSTRAINT fk_ra_macro_area REFERENCES gd.macro_area (macro_area_id),
    accesos_3m    INT NOT NULL CONSTRAINT ck_ra_accesos CHECK (accesos_3m >= 0),
    CONSTRAINT uq_recert_acceso UNIQUE (evento_id, usuario_id, rol_id)
);
GO
IF INDEXPROPERTY(OBJECT_ID('gd.recert_acceso'), 'ix_ra_evento_macro', 'IndexID') IS NULL
    CREATE INDEX ix_ra_evento_macro ON gd.recert_acceso (evento_id, macro_area_id) INCLUDE (usuario_id, rol_id, accesos_3m);
GO

-- Insert-only: la decisión vigente es la última por acceso. Comentario obligatorio en BAJA.
IF OBJECT_ID('gd.recert_decision') IS NULL
CREATE TABLE gd.recert_decision (
    decision_id    BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT pk_recert_decision PRIMARY KEY,
    acceso_id      BIGINT NOT NULL CONSTRAINT fk_rd_acceso REFERENCES gd.recert_acceso (acceso_id),
    decision       VARCHAR(8) NOT NULL CONSTRAINT ck_rd_decision CHECK (decision IN ('CONTINUA','BAJA')),
    comentario     NVARCHAR(1000) NULL,
    decidido_por   INT NOT NULL CONSTRAINT fk_rd_responsable REFERENCES gd.responsable (responsable_id),
    usuario        NVARCHAR(100) NOT NULL CONSTRAINT df_rd_usuario DEFAULT (SUSER_SNAME()),
    fecha_registro DATETIME2(0)  NOT NULL CONSTRAINT df_rd_fecha_reg DEFAULT (SYSDATETIME()),
    CONSTRAINT ck_rd_comentario_baja CHECK (decision = 'CONTINUA' OR LEN(LTRIM(RTRIM(ISNULL(comentario, N'')))) > 0)
);
GO
IF INDEXPROPERTY(OBJECT_ID('gd.recert_decision'), 'ix_rd_acceso', 'IndexID') IS NULL
    CREATE INDEX ix_rd_acceso ON gd.recert_decision (acceso_id, decision_id DESC) INCLUDE (decision);
GO

-- Evidencia de la ejecución de la baja por TI.
IF OBJECT_ID('gd.recert_baja_ejecucion') IS NULL
CREATE TABLE gd.recert_baja_ejecucion (
    acceso_id       BIGINT NOT NULL CONSTRAINT pk_recert_baja_ejecucion PRIMARY KEY
                    CONSTRAINT fk_rbe_acceso REFERENCES gd.recert_acceso (acceso_id),
    fecha_ejecucion DATE NOT NULL,
    ticket          NVARCHAR(60)  NOT NULL CONSTRAINT ck_rbe_ticket CHECK (LEN(LTRIM(RTRIM(ticket))) > 0),
    ejecutado_por   NVARCHAR(150) NOT NULL,
    fecha_registro  DATETIME2(0)  NOT NULL CONSTRAINT df_rbe_fecha_reg DEFAULT (SYSDATETIME())
);
GO

IF OBJECT_ID('gd.recert_carga_rechazo') IS NULL
CREATE TABLE gd.recert_carga_rechazo (
    rechazo_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT pk_recert_carga_rechazo PRIMARY KEY,
    evento_id  INT NOT NULL CONSTRAINT fk_rcr_evento REFERENCES gd.recert_evento (evento_id),
    fila       INT NULL,
    usuario    NVARCHAR(100) NULL,
    rol        NVARCHAR(128) NULL,
    vista      NVARCHAR(400) NULL,
    motivo     NVARCHAR(200) NOT NULL
);
GO

-- Uso de cada vista según el extracto del evento.
IF OBJECT_ID('gd.vista_uso') IS NULL
CREATE TABLE gd.vista_uso (
    evento_id  INT NOT NULL CONSTRAINT fk_vu_evento REFERENCES gd.recert_evento (evento_id),
    vista_id   INT NOT NULL CONSTRAINT fk_vu_vista  REFERENCES gd.vista_gobernada (vista_id),
    accesos_3m INT NOT NULL,
    usuarios   INT NOT NULL,
    roles      INT NOT NULL,
    CONSTRAINT pk_vista_uso PRIMARY KEY (evento_id, vista_id)
);
GO

-- Staging: columnas del extracto tal como vienen (texto). GOBIERNO se omite; OK-BAJA y Comentarios se capturan en la herramienta.
IF OBJECT_ID('gd.stg_recertificacion') IS NULL
CREATE TABLE gd.stg_recertificacion (
    fila             INT IDENTITY(2,1) NOT NULL,     -- 2 = primera fila de datos del archivo
    area             NVARCHAR(100) NULL,
    content          NVARCHAR(100) NULL,
    usuario          NVARCHAR(100) NULL,
    nombre           NVARCHAR(200) NULL,
    rol              NVARCHAR(200) NULL,
    vista            NVARCHAR(400) NULL,
    accesos_3m       NVARCHAR(30)  NULL,
    area_responsable NVARCHAR(100) NULL,
    responsable      NVARCHAR(150) NULL
);
GO

/* =====================================================================
   5. TRIGGERS
   ===================================================================== */
-- La foto del evento solo se modifica mientras está en BORRADOR (carga y descarte).
CREATE OR ALTER TRIGGER gd.tr_recert_acceso_inmutable ON gd.recert_acceso
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1
               FROM (SELECT evento_id FROM inserted UNION SELECT evento_id FROM deleted) AS x
               JOIN gd.recert_evento AS e ON e.evento_id = x.evento_id
               WHERE e.estatus <> 'BORRADOR')
        THROW 50120, N'Los accesos de un evento abierto o cerrado no se pueden modificar (foto inmutable).', 1;
END;
GO

-- Decisiones: solo con el evento ABIERTO y solo inserción (histórico de auditoría).
CREATE OR ALTER TRIGGER gd.tr_recert_decision_evento ON gd.recert_decision
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 50121, N'Las decisiones son de solo inserción: registra una nueva decisión en lugar de modificarla.', 1;
    IF EXISTS (SELECT 1
               FROM inserted AS i
               JOIN gd.recert_acceso AS a ON a.acceso_id = i.acceso_id
               JOIN gd.recert_evento AS e ON e.evento_id = a.evento_id
               WHERE e.estatus <> 'ABIERTO')
        THROW 50122, N'El evento no está abierto: no se pueden registrar decisiones.', 1;
END;
GO

/* =====================================================================
   6. PROCEDIMIENTOS
   ===================================================================== */
-- Carga del extracto a un evento nuevo en BORRADOR:
--   staging → validación → usuarios/roles/vistas → recert_acceso + vista_uso, y sincroniza el inventario.
--   INSERT INTO gd.stg_recertificacion (area, content, usuario, nombre, rol, vista, accesos_3m, area_responsable, responsable) ...;
--   EXEC gd.sp_cargar_extracto_recertificacion @nombre = N'2026-E2', @fecha_extracto = '2026-10-01', @fecha_limite = '2026-10-31';
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
    INSERT INTO gd.recert_acceso (evento_id, usuario_id, rol_id, macro_area_id, accesos_3m)
    SELECT @evento_id, u.usuario_id, r.rol_id, m.macro_area_id, f.accesos_3m
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

-- El evento no abre con rechazos sin resolver.
CREATE OR ALTER PROCEDURE gd.sp_abrir_evento
    @evento_id INT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @estatus VARCHAR(10) = (SELECT estatus FROM gd.recert_evento WHERE evento_id = @evento_id);
    IF @estatus IS NULL THROW 50130, N'El evento no existe.', 1;
    IF @estatus <> 'BORRADOR' THROW 50131, N'Solo se puede abrir un evento en borrador.', 1;
    IF EXISTS (SELECT 1 FROM gd.recert_carga_rechazo WHERE evento_id = @evento_id)
        THROW 50132, N'El evento no puede abrirse: hay filas rechazadas sin resolver.', 1;
    IF NOT EXISTS (SELECT 1 FROM gd.recert_acceso WHERE evento_id = @evento_id)
        THROW 50133, N'El evento no tiene accesos cargados.', 1;

    UPDATE gd.recert_evento SET estatus = 'ABIERTO', fecha_inicio = CAST(GETDATE() AS DATE) WHERE evento_id = @evento_id;
END;
GO

-- Bloquea las decisiones. Los pendientes se reportan; nunca se asume una decisión.
CREATE OR ALTER PROCEDURE gd.sp_cerrar_evento
    @evento_id INT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @estatus VARCHAR(10) = (SELECT estatus FROM gd.recert_evento WHERE evento_id = @evento_id);
    IF @estatus IS NULL THROW 50140, N'El evento no existe.', 1;
    IF @estatus <> 'ABIERTO' THROW 50141, N'Solo se puede cerrar un evento abierto.', 1;

    UPDATE gd.recert_evento SET estatus = 'CERRADO', fecha_cierre = CAST(GETDATE() AS DATE) WHERE evento_id = @evento_id;

    SELECT COUNT(*) AS pendientes
    FROM gd.recert_acceso AS a
    WHERE a.evento_id = @evento_id
      AND NOT EXISTS (SELECT 1 FROM gd.recert_decision AS d WHERE d.acceso_id = a.acceso_id);
END;
GO

-- Registra la decisión de un certificador vigente de la macro área del acceso.
CREATE OR ALTER PROCEDURE gd.sp_registrar_decision
    @acceso_id      BIGINT,
    @decision       VARCHAR(8),
    @comentario     NVARCHAR(1000) = NULL,
    @responsable_id INT,
    @usuario        NVARCHAR(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @comentario = NULLIF(LTRIM(RTRIM(@comentario)), N'');
    DECLARE @hoy DATE = CAST(GETDATE() AS DATE);
    DECLARE @macro_area_id INT, @estatus VARCHAR(10);

    SELECT @macro_area_id = a.macro_area_id, @estatus = e.estatus
    FROM gd.recert_acceso AS a
    JOIN gd.recert_evento AS e ON e.evento_id = a.evento_id
    WHERE a.acceso_id = @acceso_id;

    IF @macro_area_id IS NULL THROW 50150, N'El acceso no existe.', 1;
    IF @estatus <> 'ABIERTO' THROW 50151, N'El evento no está abierto: no se pueden registrar decisiones.', 1;
    IF @decision NOT IN ('CONTINUA','BAJA') THROW 50152, N'La decisión debe ser CONTINUA o BAJA.', 1;
    IF @decision = 'BAJA' AND @comentario IS NULL THROW 50153, N'La baja requiere un comentario para la auditoría.', 1;
    IF NOT EXISTS (SELECT 1 FROM gd.macro_area_certificador
                   WHERE macro_area_id = @macro_area_id AND responsable_id = @responsable_id
                     AND fecha_desde <= @hoy AND (fecha_hasta IS NULL OR fecha_hasta >= @hoy))
        THROW 50154, N'No eres certificador vigente de la macro área de este acceso.', 1;
    IF EXISTS (SELECT 1 FROM gd.recert_baja_ejecucion WHERE acceso_id = @acceso_id)
        THROW 50155, N'La baja de este acceso ya se ejecutó; la decisión no puede cambiar.', 1;

    INSERT INTO gd.recert_decision (acceso_id, decision, comentario, decidido_por, usuario)
    VALUES (@acceso_id, @decision, @comentario, @responsable_id, ISNULL(@usuario, SUSER_SNAME()));
END;
GO

-- Evidencia de la baja ejecutada por TI.
CREATE OR ALTER PROCEDURE gd.sp_registrar_baja_ejecucion
    @acceso_id       BIGINT,
    @ticket          NVARCHAR(60),
    @ejecutado_por   NVARCHAR(150),
    @fecha_ejecucion DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @fecha_ejecucion = ISNULL(@fecha_ejecucion, CAST(GETDATE() AS DATE));
    SET @ticket = NULLIF(LTRIM(RTRIM(@ticket)), N'');

    IF NOT EXISTS (SELECT 1 FROM gd.recert_acceso WHERE acceso_id = @acceso_id) THROW 50160, N'El acceso no existe.', 1;
    IF ISNULL((SELECT TOP (1) decision FROM gd.recert_decision WHERE acceso_id = @acceso_id ORDER BY decision_id DESC), '') <> 'BAJA'
        THROW 50161, N'El acceso no tiene una decisión vigente de Baja.', 1;
    IF EXISTS (SELECT 1 FROM gd.recert_baja_ejecucion WHERE acceso_id = @acceso_id) THROW 50162, N'La baja ya se registró como ejecutada.', 1;
    IF @ticket IS NULL THROW 50163, N'Indica el ticket de la ejecución.', 1;
    IF @fecha_ejecucion > CAST(GETDATE() AS DATE) THROW 50164, N'La fecha de ejecución no puede ser futura.', 1;

    INSERT INTO gd.recert_baja_ejecucion (acceso_id, fecha_ejecucion, ticket, ejecutado_por)
    VALUES (@acceso_id, @fecha_ejecucion, @ticket, @ejecutado_por);
END;
GO

/* =====================================================================
   7. VISTAS DE RECERTIFICACIÓN
   ===================================================================== */
-- Macro áreas que certifica un responsable (titular o delegado vigente). Base de la seguridad por fila en la aplicación.
CREATE OR ALTER FUNCTION gd.fn_macro_areas_certificador (@responsable_id INT, @fecha DATE)
RETURNS TABLE
AS
RETURN
    SELECT c.macro_area_id, c.tipo
    FROM gd.macro_area_certificador AS c
    WHERE c.responsable_id = @responsable_id
      AND c.fecha_desde <= @fecha AND (c.fecha_hasta IS NULL OR c.fecha_hasta >= @fecha);
GO

CREATE OR ALTER VIEW gd.vw_recert_decision_vigente
AS
SELECT decision_id, acceso_id, decision, comentario, decidido_por, usuario, fecha_registro
FROM (SELECT d.*, ROW_NUMBER() OVER (PARTITION BY d.acceso_id ORDER BY d.decision_id DESC) AS rn
      FROM gd.recert_decision AS d) AS x
WHERE rn = 1;
GO

-- Evidencia de auditoría: un renglón por acceso con su decisión vigente y la ejecución de la baja.
CREATE OR ALTER VIEW gd.vw_recert_evidencia
AS
SELECT  e.evento_id, e.nombre AS evento, e.estatus AS estatus_evento,
        a.acceso_id, m.macro_area_id, m.nombre AS macro_area,
        u.codigo AS usuario, u.nombre AS nombre_usuario,
        r.nombre AS rol, v.vista_id, v.ruta_tecnica AS vista, a.accesos_3m,
        CAST(COALESCE(d.decision, 'PENDIENTE') AS VARCHAR(9)) AS decision, d.comentario,   -- ISNULL truncaría a VARCHAR(8)
        rs.nombre AS decidido_por, d.fecha_registro AS fecha_decision,
        b.ticket, b.fecha_ejecucion, b.ejecutado_por
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

CREATE OR ALTER VIEW gd.vw_recert_avance_macro_area
AS
SELECT  evento_id, evento, macro_area_id, macro_area,
        COUNT(*)                                            AS accesos,
        SUM(CASE WHEN decision = 'CONTINUA'  THEN 1 ELSE 0 END) AS continua,
        SUM(CASE WHEN decision = 'BAJA'      THEN 1 ELSE 0 END) AS baja,
        SUM(CASE WHEN decision = 'PENDIENTE' THEN 1 ELSE 0 END) AS pendientes,
        CAST(SUM(CASE WHEN decision <> 'PENDIENTE' THEN 1.0 ELSE 0 END) / COUNT(*) AS DECIMAL(5,4)) AS avance
FROM gd.vw_recert_evidencia
GROUP BY evento_id, evento, macro_area_id, macro_area;
GO

-- A quién recordar: certificadores vigentes con accesos pendientes en eventos abiertos.
CREATE OR ALTER VIEW gd.vw_recert_pendientes_certificador
AS
SELECT  am.evento_id, am.evento, am.macro_area_id, am.macro_area, c.tipo,
        r.responsable_id, r.nombre AS certificador, r.correo, am.pendientes
FROM gd.vw_recert_avance_macro_area AS am
JOIN gd.recert_evento AS e ON e.evento_id = am.evento_id AND e.estatus = 'ABIERTO'
JOIN gd.macro_area_certificador AS c ON c.macro_area_id = am.macro_area_id
     AND c.fecha_desde <= CAST(GETDATE() AS DATE) AND (c.fecha_hasta IS NULL OR c.fecha_hasta >= CAST(GETDATE() AS DATE))
JOIN gd.responsable AS r ON r.responsable_id = c.responsable_id
WHERE am.pendientes > 0;
GO

CREATE OR ALTER VIEW gd.vw_recert_bajas_por_ejecutar
AS
SELECT  evento_id, evento, acceso_id, macro_area, usuario, nombre_usuario, rol, vista,
        comentario, decidido_por, fecha_decision,
        DATEDIFF(DAY, fecha_decision, SYSDATETIME()) AS dias_sin_ejecutar
FROM gd.vw_recert_evidencia
WHERE decision = 'BAJA' AND fecha_ejecucion IS NULL;
GO

/* =====================================================================
   8. VISTAS v2 AJUSTADAS Y VALIDACIONES v3
      gobierno_id de la vista ahora es opcional: los JOIN a gobierno pasan a LEFT JOIN.
   ===================================================================== */
CREATE OR ALTER VIEW gd.vw_pendientes
AS
SELECT  ea.vista_id,
        ea.nombre_negocio            AS vista,
        ea.ruta_tecnica,
        g.nombre                     AS gobierno,
        d.nombre                     AS data_product,
        ea.etapa,
        ea.tarea_pendiente,
        ea.rol_ejecutor,
        r.nombre                     AS responsable,
        ea.fecha_entrada,
        ea.dias_en_etapa,
        ISNULL(mf.ciclos, 1)         AS ciclo,
        mo.nombre                    AS motivo_ultimo_mov
FROM gd.vw_vista_etapa_actual AS ea
LEFT JOIN gd.gobierno AS g ON g.gobierno_id = ea.gobierno_id
LEFT JOIN gd.responsable AS r ON r.responsable_id = ea.responsable_id
LEFT JOIN gd.dp_vista AS dv ON dv.vista_id = ea.vista_id AND dv.fecha_hasta IS NULL
LEFT JOIN gd.data_product AS d ON d.dp_id = dv.dp_id
LEFT JOIN gd.vw_metricas_flujo_vista AS mf ON mf.vista_id = ea.vista_id
LEFT JOIN gd.motivo AS mo ON mo.motivo_id = ea.motivo_id
WHERE ea.es_final = 0;
GO

CREATE OR ALTER VIEW gd.vw_calidad_vista
AS
WITH res AS (
    SELECT regla_id, periodo, DATEFROMPARTS(periodo / 100, periodo % 100, 1) AS ini_mes
    FROM gd.resumen_calidad
),
vr AS (
    SELECT DISTINCT res.periodo, vo.vista_id, res.regla_id
    FROM res
    JOIN gd.regla_objeto AS ro ON ro.regla_id = res.regla_id
    JOIN gd.vista_objeto AS vo ON vo.objeto_id = ro.objeto_id
     AND vo.fecha_desde <= EOMONTH(res.ini_mes)
     AND (vo.fecha_hasta IS NULL OR vo.fecha_hasta >= res.ini_mes)
)
SELECT  vr.periodo, v.vista_id, v.nombre_negocio AS vista, v.ruta_tecnica, g.nombre AS gobierno,
        r.codigo AS regla, r.descripcion, r.dimension_dq,
        rc.reg_evaluados, rc.reg_errores, rc.reg_validos, rc.pct_calidad
FROM vr
JOIN gd.resumen_calidad  AS rc ON rc.regla_id = vr.regla_id AND rc.periodo = vr.periodo
JOIN gd.vista_gobernada  AS v  ON v.vista_id  = vr.vista_id
LEFT JOIN gd.gobierno    AS g  ON g.gobierno_id = v.gobierno_id
JOIN gd.regla            AS r  ON r.regla_id  = vr.regla_id;
GO

-- Validaciones v2 + v3. "Rol con más de una vista" y "vista con más de un rol" se impiden por diseño
-- (uq_rol_hana_nombre, uq_rol_hana_vista) y se reportan en la carga (recert_carga_rechazo).
CREATE OR ALTER VIEW gd.vw_validaciones
AS
SELECT N'Vista con gobierno distinto al de su DP' AS validacion, v.nombre_negocio AS objeto,
       CONCAT(N'Vista: ', gv.nombre, N' / DP: ', gdp.nombre) AS detalle
FROM gd.vista_gobernada AS v
JOIN gd.dp_vista     AS x   ON x.vista_id = v.vista_id AND x.fecha_hasta IS NULL
JOIN gd.data_product AS d   ON d.dp_id = x.dp_id
JOIN gd.gobierno     AS gv  ON gv.gobierno_id  = v.gobierno_id
JOIN gd.gobierno     AS gdp ON gdp.gobierno_id = d.gobierno_id
WHERE v.gobierno_id <> d.gobierno_id

UNION ALL
SELECT N'Data product activo sin vistas vigentes', d.nombre, d.estatus
FROM gd.data_product AS d
WHERE d.estatus IN ('PLANEADO','EN_CURSO')
  AND NOT EXISTS (SELECT 1 FROM gd.dp_vista AS x WHERE x.dp_id = d.dp_id AND x.fecha_hasta IS NULL)

UNION ALL
SELECT N'Data product cerrado con vista reabierta', d.nombre, CONCAT(ea.nombre_negocio, N' en ', ea.etapa)
FROM gd.data_product AS d
JOIN gd.dp_vista AS x ON x.dp_id = d.dp_id AND x.fecha_hasta IS NULL
JOIN gd.vw_vista_etapa_actual AS ea ON ea.vista_id = x.vista_id
WHERE d.estatus = 'CERRADO' AND ea.es_final = 0

UNION ALL
SELECT N'Regla con STAGE = SI sin resumen en el último periodo', r.codigo, CAST(p.periodo AS NVARCHAR(10))
FROM gd.regla AS r
CROSS JOIN (SELECT MAX(periodo) AS periodo FROM gd.resumen_calidad) AS p
WHERE r.activa = 1 AND r.escribe_log = 1 AND p.periodo IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM gd.resumen_calidad AS rc WHERE rc.regla_id = r.regla_id AND rc.periodo = p.periodo)

UNION ALL
SELECT N'Regla activa sin objetos BW', r.codigo, r.descripcion
FROM gd.regla AS r
WHERE r.activa = 1 AND NOT EXISTS (SELECT 1 FROM gd.regla_objeto AS ro WHERE ro.regla_id = r.regla_id)

UNION ALL
SELECT N'Objeto con reglas sin vista vigente (su calidad no llega a ninguna vista)', o.nombre, NULL
FROM gd.objeto_fuente AS o
WHERE EXISTS (SELECT 1 FROM gd.regla_objeto AS ro WHERE ro.objeto_id = o.objeto_id)
  AND NOT EXISTS (SELECT 1 FROM gd.vista_objeto AS vo WHERE vo.objeto_id = o.objeto_id AND vo.fecha_hasta IS NULL)

UNION ALL
SELECT N'Regla activa sin responsable', r.codigo, r.descripcion
FROM gd.regla AS r
WHERE r.activa = 1 AND r.responsable_id IS NULL

UNION ALL
SELECT N'Vista sin responsable', v.nombre_negocio, v.ruta_tecnica
FROM gd.vista_gobernada AS v
WHERE v.activa = 1 AND v.responsable_id IS NULL

-- ---- v3 ----
UNION ALL
SELECT N'Vista sin gobierno (por clasificar)', v.nombre_negocio, v.ruta_tecnica
FROM gd.vista_gobernada AS v
WHERE v.activa = 1 AND v.gobierno_id IS NULL

UNION ALL
SELECT N'Vista de data product fuera de la última recertificación', v.nombre_negocio, d.nombre
FROM gd.vista_gobernada AS v
JOIN gd.dp_vista AS x ON x.vista_id = v.vista_id AND x.fecha_hasta IS NULL
JOIN gd.data_product AS d ON d.dp_id = x.dp_id
WHERE v.activa = 1 AND v.en_ultima_recertificacion = 0
  AND EXISTS (SELECT 1 FROM gd.recert_evento)

UNION ALL
SELECT N'Vista de data product con 0 accesos en la última recertificación', v.nombre_negocio, CONCAT(d.nombre, N' · ', ue.nombre)
FROM gd.vista_gobernada AS v
JOIN gd.dp_vista AS x ON x.vista_id = v.vista_id AND x.fecha_hasta IS NULL
JOIN gd.data_product AS d ON d.dp_id = x.dp_id
CROSS JOIN (SELECT TOP (1) evento_id, nombre FROM gd.recert_evento ORDER BY evento_id DESC) AS ue
LEFT JOIN gd.vista_uso AS u ON u.evento_id = ue.evento_id AND u.vista_id = v.vista_id
WHERE v.activa = 1 AND v.en_ultima_recertificacion = 1 AND ISNULL(u.accesos_3m, 0) = 0;
GO

IF NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '001_v3_recertificacion')
    INSERT INTO gd.migracion (migracion_id, descripcion)
    VALUES ('001_v3_recertificacion', N'Modelo v3: recertificación de accesos, apariciones de vistas, gobierno opcional, validaciones v3');
GO
