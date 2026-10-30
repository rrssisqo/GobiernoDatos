/* =====================================================================
   Migración 020 · Accesos de Informatica
   Solicitudes de rol en Informatica (Data Owner, Data Administrator, Data Steward) por persona, con la fecha de
   solicitud y el número de change. Se administran en Informatica → Accesos Informatica y se exportan a Excel para
   pedir el cambio a infraestructura.
     - gd.acceso_informatica: una fila por persona y rol (no se repite el rol para la misma persona).
   Idempotente. Ejecutar con: sqlcmd -I -b -f 65001.
   ===================================================================== */
IF OBJECT_ID('gd.acceso_informatica') IS NULL
CREATE TABLE gd.acceso_informatica (
    acceso_id       BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT pk_acceso_informatica PRIMARY KEY,
    usuario_red     NVARCHAR(100) NOT NULL,
    nombre          NVARCHAR(150) NOT NULL,
    correo          NVARCHAR(254) NULL,
    rol             NVARCHAR(30)  NOT NULL CONSTRAINT ck_ai_rol CHECK (rol IN (N'Data Owner', N'Data Administrator', N'Data Steward')),
    fecha_solicitud DATE          NOT NULL,
    numero_change   NVARCHAR(60)  NULL CONSTRAINT ck_ai_change CHECK (numero_change IS NULL OR LEN(LTRIM(RTRIM(numero_change))) > 0),
    comentario      NVARCHAR(500) NULL,
    registrado_por  NVARCHAR(100) NOT NULL,
    fecha_registro  DATETIME2(0)  NOT NULL CONSTRAINT df_ai_fecha DEFAULT (SYSDATETIME()),
    CONSTRAINT uq_acceso_informatica UNIQUE (usuario_red, rol)
);
GO

IF NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '020_acceso_informatica')
    INSERT INTO gd.migracion (migracion_id, descripcion)
    VALUES ('020_acceso_informatica', N'Solicitudes de rol en Informatica (Data Owner, Data Administrator, Data Steward) con fecha y change');
GO
