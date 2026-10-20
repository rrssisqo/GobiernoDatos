/* =====================================================================
   Migración 004 · Tareas por etapa de vista ("Seguimiento tarea")
   La tarea pendiente de la etapa actual de una vista se asigna a un usuario de red (DOMINIO\usuario).
     ABIERTA → TERMINADA (check en el portal: guarda fecha y quién la terminó)
     ABIERTA → CANCELADA (la vista cambió de etapa o la tarea se reasignó)
   Días transcurridos = desde la asignación hasta hoy; al cerrarse se congelan en la fecha de cierre.
   Requiere 001. Idempotente. Ejecutar con QUOTED_IDENTIFIER ON (sqlcmd -I).
   ===================================================================== */
IF OBJECT_ID('gd.migracion') IS NULL OR NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '001_v3_recertificacion')
    THROW 50000, N'Aplica primero db/migrations/001_v3_recertificacion.sql.', 1;
GO

IF OBJECT_ID('gd.tarea_vista') IS NULL
CREATE TABLE gd.tarea_vista (
    tarea_id         INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_tarea_vista PRIMARY KEY,
    vista_id         INT NOT NULL CONSTRAINT fk_tarea_vista_vista REFERENCES gd.vista_gobernada (vista_id),
    etapa_id         INT NOT NULL CONSTRAINT fk_tarea_vista_etapa REFERENCES gd.etapa_vista (etapa_id),
    asignado_a       NVARCHAR(100) NOT NULL,          -- usuario de red: MABENET\usuario
    asignado_nombre  NVARCHAR(150) NOT NULL,          -- nombre en AD al asignar
    asignado_por     NVARCHAR(100) NOT NULL,
    fecha_asignacion DATETIME2(0)  NOT NULL CONSTRAINT df_tarea_vista_fecha_asig DEFAULT (SYSDATETIME()),
    estatus          VARCHAR(10)   NOT NULL CONSTRAINT df_tarea_vista_estatus DEFAULT ('ABIERTA')
                     CONSTRAINT ck_tarea_vista_estatus CHECK (estatus IN ('ABIERTA','TERMINADA','CANCELADA')),
    fecha_cierre     DATETIME2(0)  NULL,
    cerrado_por      NVARCHAR(100) NULL,
    motivo_cierre    NVARCHAR(200) NULL,
    CONSTRAINT ck_tarea_vista_cierre CHECK (
           (estatus = 'ABIERTA' AND fecha_cierre IS NULL AND cerrado_por IS NULL)
        OR (estatus <> 'ABIERTA' AND fecha_cierre IS NOT NULL AND cerrado_por IS NOT NULL AND fecha_cierre >= fecha_asignacion))
);
GO

-- Una sola tarea abierta por vista.
IF INDEXPROPERTY(OBJECT_ID('gd.tarea_vista'), 'ux_tarea_vista_abierta', 'IndexID') IS NULL
    CREATE UNIQUE INDEX ux_tarea_vista_abierta ON gd.tarea_vista (vista_id) WHERE estatus = 'ABIERTA';
IF INDEXPROPERTY(OBJECT_ID('gd.tarea_vista'), 'ix_tarea_vista_asignado', 'IndexID') IS NULL
    CREATE INDEX ix_tarea_vista_asignado ON gd.tarea_vista (asignado_a, estatus) INCLUDE (vista_id, fecha_asignacion);
GO

-- Seguimiento de tareas (también para Power BI). dias_transcurridos se congela al cerrar la tarea.
CREATE OR ALTER VIEW gd.vw_seguimiento_tarea
AS
SELECT  t.tarea_id, t.vista_id, v.nombre_negocio AS vista, v.ruta_tecnica,
        d.dp_id, d.nombre AS data_product,
        e.etapa_id, e.orden AS etapa_orden, e.nombre AS etapa, e.tarea_pendiente,
        t.asignado_a, t.asignado_nombre, t.asignado_por, t.fecha_asignacion,
        t.estatus, t.fecha_cierre, t.cerrado_por, t.motivo_cierre,
        DATEDIFF(DAY, CAST(t.fecha_asignacion AS DATE), CAST(ISNULL(t.fecha_cierre, SYSDATETIME()) AS DATE)) AS dias_transcurridos
FROM gd.tarea_vista AS t
JOIN gd.vista_gobernada AS v ON v.vista_id = t.vista_id
JOIN gd.etapa_vista AS e ON e.etapa_id = t.etapa_id
LEFT JOIN gd.dp_vista AS dv ON dv.vista_id = t.vista_id AND dv.fecha_hasta IS NULL
LEFT JOIN gd.data_product AS d ON d.dp_id = dv.dp_id;
GO

IF NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '004_tarea_vista')
    INSERT INTO gd.migracion (migracion_id, descripcion)
    VALUES ('004_tarea_vista', N'Tareas por etapa de vista asignadas a usuario de red (Seguimiento tarea)');
GO
