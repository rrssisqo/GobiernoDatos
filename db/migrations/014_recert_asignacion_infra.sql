/* =====================================================================
   Migración 014 · Asignación de las bajas a infraestructura
   Con el VoBo autorizado, el administrador asigna las bajas pendientes de una macro área a un responsable de
   infraestructura con el número de cambio; el portal le envía un correo con las bajas en Excel.
     - gd.recert_asignacion_infra: historial de asignaciones (la vigente es la más reciente por evento y macro área),
       con el resultado del correo.
   Requiere 011. Idempotente. Ejecutar con QUOTED_IDENTIFIER ON (sqlcmd -I).
   ===================================================================== */
IF OBJECT_ID('gd.recert_asignacion_infra') IS NULL
CREATE TABLE gd.recert_asignacion_infra (
    asignacion_id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT pk_recert_asignacion_infra PRIMARY KEY,
    evento_id               INT NOT NULL CONSTRAINT fk_rai_evento     REFERENCES gd.recert_evento (evento_id),
    macro_area_id           INT NOT NULL CONSTRAINT fk_rai_macro_area REFERENCES gd.macro_area (macro_area_id),
    responsable_usuario_red NVARCHAR(100) NOT NULL,
    responsable_nombre      NVARCHAR(150) NOT NULL,
    responsable_correo      NVARCHAR(254) NOT NULL,
    cambio                  NVARCHAR(60)  NOT NULL CONSTRAINT ck_rai_cambio CHECK (LEN(LTRIM(RTRIM(cambio))) > 0),
    bajas                   INT NOT NULL CONSTRAINT ck_rai_bajas CHECK (bajas > 0),
    asignado_por            NVARCHAR(100) NOT NULL,
    fecha_asignacion        DATETIME2(0) NOT NULL CONSTRAINT df_rai_fecha DEFAULT (SYSDATETIME()),
    notificado              BIT NOT NULL CONSTRAINT df_rai_notificado DEFAULT (0),
    error_notificacion      NVARCHAR(400) NULL
);
GO
IF INDEXPROPERTY(OBJECT_ID('gd.recert_asignacion_infra'), 'ix_rai_evento_macro', 'IndexID') IS NULL
    CREATE INDEX ix_rai_evento_macro ON gd.recert_asignacion_infra (evento_id, macro_area_id, asignacion_id DESC);
GO

IF NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '014_recert_asignacion_infra')
    INSERT INTO gd.migracion (migracion_id, descripcion)
    VALUES ('014_recert_asignacion_infra', N'Asignación de bajas a un responsable de infraestructura con número de cambio y aviso por correo');
GO
