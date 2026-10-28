/* =====================================================================
   Migración 019 · Correo de cierre del responsable de infraestructura
   Por evento y macro área, el administrador sube el correo (.eml o .msg de Outlook) con el que infraestructura avisa que
   terminó de retirar los roles. Se descarga en Auditoría → Notificaciones y va en el .zip de evidencia.
     - gd.recert_cierre_infra: todos los correos subidos (no se borran; el vigente es el más reciente).
   Requiere 014. Idempotente. Ejecutar con: sqlcmd -I -b -f 65001.
   ===================================================================== */
IF OBJECT_ID('gd.recert_cierre_infra') IS NULL
CREATE TABLE gd.recert_cierre_infra (
    cierre_id      BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT pk_recert_cierre_infra PRIMARY KEY,
    evento_id      INT NOT NULL CONSTRAINT fk_rci_evento     REFERENCES gd.recert_evento (evento_id),
    macro_area_id  INT NOT NULL CONSTRAINT fk_rci_macro_area REFERENCES gd.macro_area (macro_area_id),
    nombre_archivo NVARCHAR(200)  NOT NULL CONSTRAINT ck_rci_archivo CHECK (RIGHT(nombre_archivo, 4) IN ('.eml', '.msg')),
    contenido      VARBINARY(MAX) NOT NULL,
    tamano         INT NOT NULL CONSTRAINT ck_rci_tamano CHECK (tamano > 0 AND tamano <= 10485760),
    comentario     NVARCHAR(500)  NULL,
    subido_por     NVARCHAR(100)  NOT NULL,
    fecha_subida   DATETIME2(0)   NOT NULL CONSTRAINT df_rci_fecha DEFAULT (SYSDATETIME())
);
GO
IF INDEXPROPERTY(OBJECT_ID('gd.recert_cierre_infra'), 'ix_rci_evento_macro', 'IndexID') IS NULL
    CREATE INDEX ix_rci_evento_macro ON gd.recert_cierre_infra (evento_id, macro_area_id, cierre_id DESC);
GO

IF NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '019_recert_cierre_infra')
    INSERT INTO gd.migracion (migracion_id, descripcion)
    VALUES ('019_recert_cierre_infra', N'Correo de cierre del responsable de infraestructura por macro área como evidencia para Auditoría');
GO
