/* =====================================================================
   Migración 012 · Un solo gerente autoriza las bajas de todas las macro áreas
   Sustituye gd.macro_area_gerente (un gerente por macro área, migración 011) por gd.recert_gerente (un solo renglón).
   El VoBo se sigue pidiendo por macro área (cuando su certificador termina), siempre a ese gerente.
   Si había gerentes por macro área, se conserva el asignado más recientemente. Requiere 011. Idempotente.
   Ejecutar con QUOTED_IDENTIFIER ON (sqlcmd -I).
   ===================================================================== */
IF OBJECT_ID('gd.recert_gerente') IS NULL
CREATE TABLE gd.recert_gerente (
    gerente_id     TINYINT NOT NULL CONSTRAINT pk_recert_gerente PRIMARY KEY CONSTRAINT ck_rg_unico CHECK (gerente_id = 1),
    usuario_red    NVARCHAR(100) NOT NULL,
    nombre         NVARCHAR(150) NOT NULL,
    correo         NVARCHAR(254) NOT NULL,
    usuario        NVARCHAR(100) NOT NULL CONSTRAINT df_rg_usuario DEFAULT (SUSER_SNAME()),
    fecha_registro DATETIME2(0) NOT NULL CONSTRAINT df_rg_fecha DEFAULT (SYSDATETIME())
);
GO

IF OBJECT_ID('gd.macro_area_gerente') IS NOT NULL
BEGIN
    INSERT INTO gd.recert_gerente (gerente_id, usuario_red, nombre, correo, usuario, fecha_registro)
    SELECT TOP (1) 1, usuario_red, nombre, correo, usuario, fecha_registro FROM gd.macro_area_gerente
    WHERE NOT EXISTS (SELECT 1 FROM gd.recert_gerente)
    ORDER BY fecha_registro DESC;
    DROP TABLE gd.macro_area_gerente;
END;
GO

CREATE OR ALTER PROCEDURE gd.sp_vobo_solicitar
    @evento_id      INT,
    @macro_area_id  INT,
    @solicitado_por NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    DECLARE @estatus VARCHAR(10) = (SELECT estatus FROM gd.recert_evento WHERE evento_id = @evento_id);
    IF @estatus IS NULL THROW 50170, N'El evento no existe.', 1;
    IF @estatus = 'BORRADOR' THROW 50171, N'El evento todavía no está abierto.', 1;

    DECLARE @pendientes INT, @bajas INT;
    SELECT @pendientes = SUM(CASE WHEN decision = 'PENDIENTE' THEN 1 ELSE 0 END), @bajas = SUM(CASE WHEN decision = 'BAJA' THEN 1 ELSE 0 END)
    FROM gd.vw_recert_evidencia WHERE evento_id = @evento_id AND macro_area_id = @macro_area_id;
    IF @estatus = 'ABIERTO' AND ISNULL(@pendientes, 0) > 0
        THROW 50172, N'La macro área todavía tiene accesos por decidir: el VoBo se pide cuando el certificador termina.', 1;
    IF ISNULL(@bajas, 0) = 0 THROW 50173, N'La macro área no tiene bajas que autorizar.', 1;

    DECLARE @usuario_red NVARCHAR(100), @nombre NVARCHAR(150), @correo NVARCHAR(254);
    SELECT @usuario_red = usuario_red, @nombre = nombre, @correo = correo FROM gd.recert_gerente WITH (UPDLOCK) WHERE gerente_id = 1;
    IF @usuario_red IS NULL THROW 50174, N'No hay gerente asignado para el VoBo de las bajas (Recertificación → Macro áreas).', 1;

    IF EXISTS (SELECT 1 FROM gd.recert_vobo WITH (UPDLOCK, HOLDLOCK)
               WHERE evento_id = @evento_id AND macro_area_id = @macro_area_id AND estatus = 'AUTORIZADA')
        THROW 50175, N'El gerente ya autorizó las bajas de esta macro área.', 1;

    UPDATE gd.recert_vobo SET estatus = 'CANCELADA', motivo_cancelacion = N'Reemplazada por una nueva solicitud'
    WHERE evento_id = @evento_id AND macro_area_id = @macro_area_id AND estatus IN ('PENDIENTE', 'ENVIADA');

    INSERT INTO gd.recert_vobo (evento_id, macro_area_id, gerente_usuario_red, gerente_nombre, gerente_correo, bajas, solicitado_por)
    VALUES (@evento_id, @macro_area_id, @usuario_red, @nombre, @correo, @bajas, @solicitado_por);
    DECLARE @vobo_id BIGINT = SCOPE_IDENTITY();

    COMMIT TRANSACTION;
    SELECT @vobo_id AS vobo_id;
END;
GO

IF NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '012_recert_gerente_unico')
    INSERT INTO gd.migracion (migracion_id, descripcion)
    VALUES ('012_recert_gerente_unico', N'Un solo gerente da el VoBo de las bajas de todas las macro áreas');
GO
