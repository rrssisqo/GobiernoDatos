/* =====================================================================
   Migración 013 · Quitar (anular) el ticket de una baja mal capturado
     - gd.recert_baja_ejecucion_anulada: el ticket anulado se conserva con su motivo, quién y cuándo (evidencia).
     - gd.sp_anular_baja_ejecucion: mueve el registro de gd.recert_baja_ejecucion a la tabla de anulados.
       La baja vuelve a "pendiente de ejecución"; otro ticket exige el VoBo autorizado (migración 011).
   Requiere 011. Idempotente. Ejecutar con QUOTED_IDENTIFIER ON (sqlcmd -I).
   ===================================================================== */
IF OBJECT_ID('gd.recert_baja_ejecucion_anulada') IS NULL
CREATE TABLE gd.recert_baja_ejecucion_anulada (
    anulacion_id    BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT pk_recert_baja_ejecucion_anulada PRIMARY KEY,
    acceso_id       BIGINT NOT NULL CONSTRAINT fk_rbea_acceso REFERENCES gd.recert_acceso (acceso_id),
    fecha_ejecucion DATE NOT NULL,
    ticket          NVARCHAR(60) NOT NULL,
    ejecutado_por   NVARCHAR(150) NOT NULL,
    fecha_registro  DATETIME2(0) NOT NULL,
    motivo          NVARCHAR(500) NOT NULL CONSTRAINT ck_rbea_motivo CHECK (LEN(LTRIM(RTRIM(motivo))) > 0),
    anulado_por     NVARCHAR(150) NOT NULL,
    fecha_anulacion DATETIME2(0) NOT NULL CONSTRAINT df_rbea_fecha DEFAULT (SYSDATETIME())
);
GO
IF INDEXPROPERTY(OBJECT_ID('gd.recert_baja_ejecucion_anulada'), 'ix_rbea_acceso', 'IndexID') IS NULL
    CREATE INDEX ix_rbea_acceso ON gd.recert_baja_ejecucion_anulada (acceso_id);
GO

CREATE OR ALTER PROCEDURE gd.sp_anular_baja_ejecucion
    @acceso_id   BIGINT,
    @motivo      NVARCHAR(500),
    @anulado_por NVARCHAR(150)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @motivo = NULLIF(LTRIM(RTRIM(@motivo)), N'');
    IF @motivo IS NULL THROW 50166, N'Indica el motivo para quitar el ticket.', 1;

    BEGIN TRANSACTION;
    IF NOT EXISTS (SELECT 1 FROM gd.recert_baja_ejecucion WITH (UPDLOCK, HOLDLOCK) WHERE acceso_id = @acceso_id)
        THROW 50167, N'La baja no tiene ticket registrado.', 1;

    INSERT INTO gd.recert_baja_ejecucion_anulada (acceso_id, fecha_ejecucion, ticket, ejecutado_por, fecha_registro, motivo, anulado_por)
    SELECT acceso_id, fecha_ejecucion, ticket, ejecutado_por, fecha_registro, @motivo, @anulado_por
    FROM gd.recert_baja_ejecucion WHERE acceso_id = @acceso_id;
    DELETE FROM gd.recert_baja_ejecucion WHERE acceso_id = @acceso_id;
    COMMIT TRANSACTION;
END;
GO

IF NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '013_recert_anular_ticket')
    INSERT INTO gd.migracion (migracion_id, descripcion)
    VALUES ('013_recert_anular_ticket', N'Anular el ticket de una baja conservando el registro anulado');
GO
