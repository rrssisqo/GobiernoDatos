/* =====================================================================
   Migración 006 · Una sola marca de tiempo por carga de calidad
   Corrige gd.sp_cargar_resumen_calidad (v2): los rechazos y los resúmenes tomaban SYSDATETIME() por separado
   (precisión de segundos). Si la carga cruzaba el cambio de segundo, "Rechazos de la última carga" quedaba vacío
   porque los rechazos tenían una marca anterior a la de los resúmenes. Ahora toda la carga usa @ahora.
   Lógica de validación sin cambios. Idempotente. Ejecutar con QUOTED_IDENTIFIER ON (sqlcmd -I).
   ===================================================================== */
CREATE OR ALTER PROCEDURE gd.sp_cargar_resumen_calidad
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @cargados INT, @rechazados INT;
    DECLARE @ahora DATETIME2(0) = SYSDATETIME();   -- marca única de la carga

    BEGIN TRANSACTION;

    SELECT s.regla_codigo, s.nombre_csv, s.periodo, s.reg_evaluados, s.reg_errores,
           COALESCE(rc.regla_id, rn.regla_id) AS regla_id,
           v.motivo
    INTO #validado
    FROM gd.stg_resumen_calidad AS s
    LEFT JOIN gd.regla AS rc ON rc.codigo     = s.regla_codigo
    LEFT JOIN gd.regla AS rn ON rn.nombre_csv = s.nombre_csv
    CROSS APPLY (SELECT motivo = CASE
        WHEN s.regla_codigo IS NULL AND s.nombre_csv IS NULL               THEN N'Sin identificador de regla'
        WHEN s.periodo IS NULL OR s.reg_evaluados IS NULL
          OR s.reg_errores IS NULL                                         THEN N'Campos obligatorios vacíos'
        WHEN COALESCE(rc.regla_id, rn.regla_id) IS NULL                    THEN N'Regla no registrada'
        WHEN rc.regla_id IS NOT NULL AND rn.regla_id IS NOT NULL
          AND rc.regla_id <> rn.regla_id                                   THEN N'Código y CSV apuntan a reglas distintas'
        WHEN COALESCE(rc.activa, rn.activa) = 0                            THEN N'Regla inactiva'
        WHEN s.periodo NOT BETWEEN 200001 AND 209912
          OR s.periodo % 100 NOT BETWEEN 1 AND 12                          THEN N'Periodo inválido (AAAAMM)'
        WHEN s.reg_evaluados < 0 OR s.reg_errores < 0
          OR s.reg_errores > s.reg_evaluados                               THEN N'Conteos inconsistentes'
    END) AS v;

    -- Duplicados de regla y periodo dentro de la misma carga
    UPDATE x SET motivo = N'Regla y periodo duplicados en la carga'
    FROM #validado AS x
    WHERE x.motivo IS NULL
      AND EXISTS (SELECT 1 FROM #validado AS y
                  WHERE y.motivo IS NULL AND y.regla_id = x.regla_id AND y.periodo = x.periodo
                  GROUP BY y.regla_id, y.periodo HAVING COUNT(*) > 1);

    INSERT INTO gd.carga_rechazo (regla_codigo, nombre_csv, periodo, reg_evaluados, reg_errores, motivo, fecha_carga)
    SELECT regla_codigo, nombre_csv, periodo, reg_evaluados, reg_errores, motivo, @ahora
    FROM #validado WHERE motivo IS NOT NULL;
    SET @rechazados = @@ROWCOUNT;

    MERGE gd.resumen_calidad AS t
    USING (SELECT regla_id, periodo, reg_evaluados, reg_errores FROM #validado WHERE motivo IS NULL) AS s
       ON t.regla_id = s.regla_id AND t.periodo = s.periodo
    WHEN MATCHED THEN
        UPDATE SET reg_evaluados = s.reg_evaluados, reg_errores = s.reg_errores, fecha_carga = @ahora
    WHEN NOT MATCHED THEN
        INSERT (regla_id, periodo, reg_evaluados, reg_errores, fecha_carga)
        VALUES (s.regla_id, s.periodo, s.reg_evaluados, s.reg_errores, @ahora);
    SET @cargados = @@ROWCOUNT;

    TRUNCATE TABLE gd.stg_resumen_calidad;

    COMMIT TRANSACTION;

    SELECT @cargados AS registros_cargados, @rechazados AS registros_rechazados;
END;
GO

IF OBJECT_ID('gd.migracion') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '006_calidad_marca_de_carga')
    INSERT INTO gd.migracion (migracion_id, descripcion)
    VALUES ('006_calidad_marca_de_carga', N'Carga de calidad con una sola marca de tiempo (corrige rechazos de la última carga)');
GO
