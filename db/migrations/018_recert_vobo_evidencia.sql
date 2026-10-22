/* =====================================================================
   Migración 018 · Evidencia del VoBo del gerente
     - gd.recert_vobo_baja: copia de las bajas incluidas en cada solicitud de VoBo, tomada al solicitarla.
       Es lo que el gerente autoriza; queda como evidencia aunque después cambien las decisiones.
     - gd.sp_vobo_solicitar: guarda la copia.
     - gd.vw_recert_vobo_detalle: lee la copia (mismas columnas que antes; ticket y fecha de ejecución actuales).
     - Las solicitudes anteriores a esta migración se completan con las bajas vigentes de su macro área.
   Requiere 012. Idempotente. Ejecutar con: sqlcmd -I -b -f 65001.
   ===================================================================== */
IF OBJECT_ID('gd.recert_vobo_baja') IS NULL
CREATE TABLE gd.recert_vobo_baja (
    vobo_id        BIGINT NOT NULL CONSTRAINT fk_rvb_vobo REFERENCES gd.recert_vobo (vobo_id),
    acceso_id      BIGINT NOT NULL CONSTRAINT fk_rvb_acceso REFERENCES gd.recert_acceso (acceso_id),
    usuario        NVARCHAR(30)   NOT NULL,
    nombre_usuario NVARCHAR(150)  NOT NULL,
    rol            NVARCHAR(128)  NOT NULL,
    vista          NVARCHAR(400)  NOT NULL,
    comentario     NVARCHAR(1000) NULL,
    decidido_por   NVARCHAR(150)  NULL,
    fecha_decision DATETIME2(0)   NULL,
    CONSTRAINT pk_recert_vobo_baja PRIMARY KEY (vobo_id, acceso_id)
);
GO

INSERT INTO gd.recert_vobo_baja (vobo_id, acceso_id, usuario, nombre_usuario, rol, vista, comentario, decidido_por, fecha_decision)
SELECT v.vobo_id, e.acceso_id, e.usuario, e.nombre_usuario, e.rol, e.vista, e.comentario, e.decidido_por, e.fecha_decision
FROM gd.recert_vobo AS v
JOIN gd.vw_recert_evidencia AS e ON e.evento_id = v.evento_id AND e.macro_area_id = v.macro_area_id AND e.decision = 'BAJA'
WHERE NOT EXISTS (SELECT 1 FROM gd.recert_vobo_baja AS x WHERE x.vobo_id = v.vobo_id);
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

    -- Copia de las bajas que se piden autorizar (evidencia y detalle para Power Automate).
    INSERT INTO gd.recert_vobo_baja (vobo_id, acceso_id, usuario, nombre_usuario, rol, vista, comentario, decidido_por, fecha_decision)
    SELECT @vobo_id, acceso_id, usuario, nombre_usuario, rol, vista, comentario, decidido_por, fecha_decision
    FROM gd.vw_recert_evidencia
    WHERE evento_id = @evento_id AND macro_area_id = @macro_area_id AND decision = 'BAJA';

    COMMIT TRANSACTION;
    SELECT @vobo_id AS vobo_id;
END;
GO

CREATE OR ALTER VIEW gd.vw_recert_vobo_detalle
AS
SELECT  v.vobo_id, v.evento_id, ev.nombre AS evento, v.macro_area_id, m.nombre AS macro_area, b.acceso_id, b.usuario, b.nombre_usuario,
        b.vista, b.rol, b.comentario, b.decidido_por, b.fecha_decision, ej.ticket, ej.fecha_ejecucion
FROM gd.recert_vobo AS v
JOIN gd.recert_vobo_baja AS b ON b.vobo_id = v.vobo_id
JOIN gd.recert_evento AS ev ON ev.evento_id = v.evento_id
JOIN gd.macro_area AS m ON m.macro_area_id = v.macro_area_id
LEFT JOIN gd.recert_baja_ejecucion AS ej ON ej.acceso_id = b.acceso_id;
GO

IF NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '018_recert_vobo_evidencia')
    INSERT INTO gd.migracion (migracion_id, descripcion)
    VALUES ('018_recert_vobo_evidencia', N'Copia de las bajas de cada solicitud de VoBo como evidencia del gerente');
GO
