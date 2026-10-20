/* =====================================================================
   Migración 009 · Bitácora de recordatorios por correo (recertificación)
   Un renglón por correo intentado: a quién (certificador, usuario de red, correo), de qué macro áreas,
   cuántos pendientes tenía, quién lo envió y si el relay lo aceptó. Evidencia para auditoría.
   Requiere 001. Idempotente. Ejecutar con QUOTED_IDENTIFIER ON (sqlcmd -I).
   ===================================================================== */
IF OBJECT_ID('gd.recert_recordatorio') IS NULL
CREATE TABLE gd.recert_recordatorio (
    recordatorio_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT pk_recert_recordatorio PRIMARY KEY,
    evento_id       INT NOT NULL CONSTRAINT fk_rr_evento REFERENCES gd.recert_evento (evento_id),
    certificador    NVARCHAR(150) NOT NULL,
    usuario_red     NVARCHAR(100) NULL,
    correo          NVARCHAR(254) NULL,
    macro_areas     NVARCHAR(400) NOT NULL,
    pendientes      INT NOT NULL CONSTRAINT ck_rr_pendientes CHECK (pendientes >= 0),
    enviado         BIT NOT NULL,
    error           NVARCHAR(400) NULL,
    enviado_por     NVARCHAR(100) NOT NULL,
    fecha           DATETIME2(0) NOT NULL CONSTRAINT df_rr_fecha DEFAULT (SYSDATETIME())
);
GO
IF INDEXPROPERTY(OBJECT_ID('gd.recert_recordatorio'), 'ix_rr_evento', 'IndexID') IS NULL
    CREATE INDEX ix_rr_evento ON gd.recert_recordatorio (evento_id, fecha DESC);
GO

IF NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '009_recert_recordatorio')
    INSERT INTO gd.migracion (migracion_id, descripcion)
    VALUES ('009_recert_recordatorio', N'Bitácora de recordatorios por correo de la recertificación');
GO
