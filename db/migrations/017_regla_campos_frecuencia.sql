/* =====================================================================
   Migración 017 · Reglas: catálogo de frecuencias, hora, fecha de producción y comentarios
     - gd.frecuencia_regla: catálogo (Diaria, Semanal, Mensual, Trimestral + las que se agreguen en el portal).
       gd.regla.frecuencia deja el CHECK fijo y referencia el catálogo por nombre (ON UPDATE CASCADE).
     - gd.regla: hora_ejecucion, fecha_produccion y comentarios. La fecha de producción de las reglas existentes
       se toma de su movimiento a "Operación PRD" (o "Pase a PRD" si no lo tiene).
     - El contacto de la regla son los correos de gd.regla_contacto con tipo CONTACTO (ya existía); el portal
       deja de capturar responsable_id (la columna se conserva).
     - task_flow ya existía en gd.regla; ahora se captura en el portal.
   Requiere el DDL v2. Idempotente. Ejecutar con QUOTED_IDENTIFIER ON (sqlcmd -I).
   ===================================================================== */
IF OBJECT_ID('gd.frecuencia_regla') IS NULL
CREATE TABLE gd.frecuencia_regla (
    frecuencia_id INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_frecuencia_regla PRIMARY KEY,
    nombre        NVARCHAR(40) NOT NULL CONSTRAINT uq_frecuencia_regla_nombre UNIQUE,
    orden         INT NOT NULL CONSTRAINT df_frecuencia_regla_orden DEFAULT (100)
);
GO
INSERT INTO gd.frecuencia_regla (nombre, orden)
SELECT f.nombre, f.orden FROM (VALUES (N'Diaria', 1), (N'Semanal', 2), (N'Mensual', 3), (N'Trimestral', 4)) AS f(nombre, orden)
WHERE NOT EXISTS (SELECT 1 FROM gd.frecuencia_regla AS x WHERE x.nombre = f.nombre);
GO

IF OBJECT_ID('gd.ck_regla_frecuencia', 'C') IS NOT NULL
    ALTER TABLE gd.regla DROP CONSTRAINT ck_regla_frecuencia;
GO
UPDATE r SET frecuencia = f.nombre
FROM gd.regla AS r JOIN gd.frecuencia_regla AS f ON f.nombre = r.frecuencia   -- 'SEMANAL' → 'Semanal' (intercalación sin mayúsculas)
WHERE r.frecuencia COLLATE Latin1_General_BIN <> f.nombre COLLATE Latin1_General_BIN;
GO
IF COLUMNPROPERTY(OBJECT_ID('gd.regla'), 'frecuencia', 'Precision') <> 40
    ALTER TABLE gd.regla ALTER COLUMN frecuencia NVARCHAR(40) NULL;
GO
IF OBJECT_ID('gd.fk_regla_frecuencia', 'F') IS NULL
    ALTER TABLE gd.regla ADD CONSTRAINT fk_regla_frecuencia FOREIGN KEY (frecuencia)
        REFERENCES gd.frecuencia_regla (nombre) ON UPDATE CASCADE;
GO

IF COL_LENGTH('gd.regla', 'hora_ejecucion') IS NULL
    ALTER TABLE gd.regla ADD hora_ejecucion   TIME(0) NULL,
                             fecha_produccion DATE NULL,
                             comentarios      NVARCHAR(2000) NULL;
GO

UPDATE r SET fecha_produccion = p.fecha
FROM gd.regla AS r
CROSS APPLY (SELECT TOP (1) m.fecha FROM gd.mov_etapa_regla AS m JOIN gd.etapa_regla AS e ON e.etapa_regla_id = m.etapa_regla_id
             WHERE m.regla_id = r.regla_id AND e.nombre IN (N'Operación PRD', N'Pase a PRD')
             ORDER BY CASE e.nombre WHEN N'Operación PRD' THEN 0 ELSE 1 END, m.fecha) AS p
WHERE r.fecha_produccion IS NULL;
GO

IF NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '017_regla_campos_frecuencia')
    INSERT INTO gd.migracion (migracion_id, descripcion)
    VALUES ('017_regla_campos_frecuencia', N'Reglas: catálogo de frecuencias, hora, fecha de producción, comentarios; contacto en lugar de responsable');
GO
