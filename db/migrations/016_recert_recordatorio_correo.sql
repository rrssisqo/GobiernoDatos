/* =====================================================================
   Migración 016 · Evidencia del correo de notificación a los responsables de área
   Cada recordatorio enviado guarda el asunto y el correo completo en formato .eml (MIME, se abre en Outlook),
   para descargarlo desde Auditoría → Notificaciones a responsables.
   Los recordatorios enviados antes de esta migración quedan sin correo guardado.
   Requiere 009. Idempotente. Ejecutar con QUOTED_IDENTIFIER ON (sqlcmd -I).
   ===================================================================== */
IF COL_LENGTH('gd.recert_recordatorio', 'asunto') IS NULL
    ALTER TABLE gd.recert_recordatorio ADD asunto NVARCHAR(300) NULL, correo_eml VARBINARY(MAX) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '016_recert_recordatorio_correo')
    INSERT INTO gd.migracion (migracion_id, descripcion)
    VALUES ('016_recert_recordatorio_correo', N'Correo .eml de cada recordatorio como evidencia para Auditoría');
GO
