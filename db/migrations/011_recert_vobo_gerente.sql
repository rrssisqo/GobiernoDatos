/* =====================================================================
   Migración 011 · VoBo del gerente de la macro área antes de ejecutar las bajas (Power Automate)
     - gd.macro_area_gerente: gerente de cada macro área (usuario de red, nombre y correo; se elige en el portal).
     - gd.recert_vobo: una solicitud de VoBo por evento y macro área. Power Automate la lee, pide la aprobación al gerente
       y registra la respuesta. Estatus: PENDIENTE (por enviar) → ENVIADA → AUTORIZADA | RECHAZADA; CANCELADA si cambia una
       decisión de la macro área o se reemplaza por otra solicitud. Solo una activa (PENDIENTE, ENVIADA o AUTORIZADA).
     - Para Power Automate: gd.vw_recert_vobo_por_enviar, gd.vw_recert_vobo_detalle, gd.sp_vobo_marcar_enviada y
       gd.sp_vobo_responder (ver docs/06_power_automate_vobo.md).
     - gd.sp_registrar_baja_ejecucion: el ticket exige el VoBo AUTORIZADO de la macro área.
   Requiere 010. Idempotente. Ejecutar con QUOTED_IDENTIFIER ON (sqlcmd -I).
   ===================================================================== */
IF OBJECT_ID('gd.macro_area_gerente') IS NULL
CREATE TABLE gd.macro_area_gerente (
    macro_area_id  INT NOT NULL CONSTRAINT pk_macro_area_gerente PRIMARY KEY
                   CONSTRAINT fk_mag_macro_area REFERENCES gd.macro_area (macro_area_id),
    usuario_red    NVARCHAR(100) NOT NULL,
    nombre         NVARCHAR(150) NOT NULL,
    correo         NVARCHAR(254) NOT NULL,
    usuario        NVARCHAR(100) NOT NULL CONSTRAINT df_mag_usuario DEFAULT (SUSER_SNAME()),
    fecha_registro DATETIME2(0) NOT NULL CONSTRAINT df_mag_fecha DEFAULT (SYSDATETIME())
);
GO

IF OBJECT_ID('gd.recert_vobo') IS NULL
CREATE TABLE gd.recert_vobo (
    vobo_id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT pk_recert_vobo PRIMARY KEY,
    evento_id            INT NOT NULL CONSTRAINT fk_rv_evento     REFERENCES gd.recert_evento (evento_id),
    macro_area_id        INT NOT NULL CONSTRAINT fk_rv_macro_area REFERENCES gd.macro_area (macro_area_id),
    gerente_usuario_red  NVARCHAR(100) NOT NULL,
    gerente_nombre       NVARCHAR(150) NOT NULL,
    gerente_correo       NVARCHAR(254) NOT NULL,
    bajas                INT NOT NULL CONSTRAINT ck_rv_bajas CHECK (bajas > 0),
    estatus              VARCHAR(10) NOT NULL CONSTRAINT df_rv_estatus DEFAULT ('PENDIENTE')
                         CONSTRAINT ck_rv_estatus CHECK (estatus IN ('PENDIENTE','ENVIADA','AUTORIZADA','RECHAZADA','CANCELADA')),
    solicitado_por       NVARCHAR(100) NOT NULL,
    fecha_solicitud      DATETIME2(0) NOT NULL CONSTRAINT df_rv_fecha_solicitud DEFAULT (SYSDATETIME()),
    fecha_envio          DATETIME2(0) NULL,
    fecha_respuesta      DATETIME2(0) NULL,
    respondido_por       NVARCHAR(254) NULL,
    comentario_respuesta NVARCHAR(1000) NULL,
    motivo_cancelacion   NVARCHAR(300) NULL
);
GO
IF INDEXPROPERTY(OBJECT_ID('gd.recert_vobo'), 'ux_rv_activa', 'IndexID') IS NULL
    CREATE UNIQUE INDEX ux_rv_activa ON gd.recert_vobo (evento_id, macro_area_id) WHERE estatus IN ('PENDIENTE','ENVIADA','AUTORIZADA');
GO

-- Bajas vigentes de una macro área en un evento (lo que el gerente autoriza).
CREATE OR ALTER VIEW gd.vw_recert_vobo_detalle
AS
SELECT  v.vobo_id, v.evento_id, e.evento, v.macro_area_id, e.macro_area, e.acceso_id, e.usuario, e.nombre_usuario, e.vista, e.rol,
        e.comentario, e.decidido_por, e.fecha_decision, e.ticket, e.fecha_ejecucion
FROM gd.recert_vobo AS v
JOIN gd.vw_recert_evidencia AS e ON e.evento_id = v.evento_id AND e.macro_area_id = v.macro_area_id AND e.decision = 'BAJA';
GO

-- Lo que Power Automate debe enviar al gerente.
CREATE OR ALTER VIEW gd.vw_recert_vobo_por_enviar
AS
SELECT  v.vobo_id, ev.nombre AS evento, m.nombre AS macro_area, v.gerente_nombre, v.gerente_correo, v.bajas,
        v.solicitado_por, v.fecha_solicitud
FROM gd.recert_vobo AS v
JOIN gd.recert_evento AS ev ON ev.evento_id = v.evento_id
JOIN gd.macro_area AS m ON m.macro_area_id = v.macro_area_id
WHERE v.estatus = 'PENDIENTE';
GO

-- Solicitud de VoBo: la macro área sin accesos por decidir (si el evento sigue abierto), con bajas y con gerente.
-- Reemplaza una solicitud pendiente o enviada; no se puede pedir otra si ya hay una autorizada vigente.
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
    SELECT @usuario_red = usuario_red, @nombre = nombre, @correo = correo FROM gd.macro_area_gerente WITH (UPDLOCK) WHERE macro_area_id = @macro_area_id;
    IF @usuario_red IS NULL THROW 50174, N'La macro área no tiene gerente asignado (Recertificación → Macro áreas).', 1;

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

-- Power Automate: la solicitud ya se envió al gerente (deja de aparecer en vw_recert_vobo_por_enviar).
CREATE OR ALTER PROCEDURE gd.sp_vobo_marcar_enviada
    @vobo_id BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE gd.recert_vobo SET estatus = 'ENVIADA', fecha_envio = SYSDATETIME() WHERE vobo_id = @vobo_id AND estatus = 'PENDIENTE';
    SELECT CAST(CASE WHEN @@ROWCOUNT = 1 THEN 1 ELSE 0 END AS BIT) AS actualizada;
END;
GO

-- Power Automate: respuesta del gerente. Si la solicitud ya se canceló (cambió una decisión), la respuesta se ignora
-- y se informa en el resultado; no falla para no romper el flujo.
CREATE OR ALTER PROCEDURE gd.sp_vobo_responder
    @vobo_id        BIGINT,
    @autorizada     BIT,
    @respondido_por NVARCHAR(254),
    @comentario     NVARCHAR(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE gd.recert_vobo
    SET estatus = CASE WHEN @autorizada = 1 THEN 'AUTORIZADA' ELSE 'RECHAZADA' END,
        fecha_respuesta = SYSDATETIME(), respondido_por = @respondido_por, comentario_respuesta = NULLIF(LTRIM(RTRIM(@comentario)), N''),
        fecha_envio = ISNULL(fecha_envio, SYSDATETIME())
    WHERE vobo_id = @vobo_id AND estatus IN ('PENDIENTE', 'ENVIADA');
    IF @@ROWCOUNT = 1
        SELECT CAST(1 AS BIT) AS registrada, N'Respuesta registrada.' AS mensaje;
    ELSE
        SELECT CAST(0 AS BIT) AS registrada,
               ISNULL((SELECT N'La solicitud está ' + estatus + N'; la respuesta no se registró.' FROM gd.recert_vobo WHERE vobo_id = @vobo_id),
                      N'La solicitud no existe.') AS mensaje;
END;
GO

-- Un cambio de decisión invalida el VoBo de su macro área (pendiente, enviado o autorizado).
CREATE OR ALTER TRIGGER gd.tr_recert_decision_vobo ON gd.recert_decision
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE v SET estatus = 'CANCELADA', motivo_cancelacion = N'Cambió una decisión de la macro área después de la solicitud'
    FROM gd.recert_vobo AS v
    WHERE v.estatus IN ('PENDIENTE', 'ENVIADA', 'AUTORIZADA')
      AND EXISTS (SELECT 1 FROM inserted AS i JOIN gd.recert_acceso AS a ON a.acceso_id = i.acceso_id
                  WHERE a.evento_id = v.evento_id AND a.macro_area_id = v.macro_area_id);
END;
GO

-- El ticket de la baja exige el VoBo autorizado del gerente de su macro área.
CREATE OR ALTER PROCEDURE gd.sp_registrar_baja_ejecucion
    @acceso_id       BIGINT,
    @ticket          NVARCHAR(60),
    @ejecutado_por   NVARCHAR(150),
    @fecha_ejecucion DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @fecha_ejecucion = ISNULL(@fecha_ejecucion, CAST(GETDATE() AS DATE));
    SET @ticket = NULLIF(LTRIM(RTRIM(@ticket)), N'');

    IF NOT EXISTS (SELECT 1 FROM gd.recert_acceso WHERE acceso_id = @acceso_id) THROW 50160, N'El acceso no existe.', 1;
    IF ISNULL((SELECT TOP (1) decision FROM gd.recert_decision WHERE acceso_id = @acceso_id ORDER BY decision_id DESC), '') <> 'BAJA'
        THROW 50161, N'El acceso no tiene una decisión vigente de Baja.', 1;
    IF EXISTS (SELECT 1 FROM gd.recert_baja_ejecucion WHERE acceso_id = @acceso_id) THROW 50162, N'La baja ya se registró como ejecutada.', 1;
    IF NOT EXISTS (SELECT 1 FROM gd.recert_acceso AS a
                   JOIN gd.recert_vobo AS v ON v.evento_id = a.evento_id AND v.macro_area_id = a.macro_area_id AND v.estatus = 'AUTORIZADA'
                   WHERE a.acceso_id = @acceso_id)
        THROW 50165, N'La baja requiere el VoBo autorizado del gerente de la macro área.', 1;
    IF @ticket IS NULL THROW 50163, N'Indica el ticket de la ejecución.', 1;
    IF @fecha_ejecucion > CAST(GETDATE() AS DATE) THROW 50164, N'La fecha de ejecución no puede ser futura.', 1;

    INSERT INTO gd.recert_baja_ejecucion (acceso_id, fecha_ejecucion, ticket, ejecutado_por)
    VALUES (@acceso_id, @fecha_ejecucion, @ticket, @ejecutado_por);
END;
GO

IF NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '011_recert_vobo_gerente')
    INSERT INTO gd.migracion (migracion_id, descripcion)
    VALUES ('011_recert_vobo_gerente', N'Gerente por macro área y VoBo de bajas vía Power Automate antes del ticket');
GO
