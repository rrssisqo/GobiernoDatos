/* =====================================================================
   Migración 002 · Necesidades del portal que no están en el ER v3
   (docs/05_portal_razor.md, "Decisiones que tomé y conviene confirmar"). Requiere 001.
   Idempotente. Ejecutar con QUOTED_IDENTIFIER ON (sqlcmd -I).
   ---------------------------------------------------------------------
     1. recert_confirmacion: "Confirmar certificación" por macro área (decisión 2)
     2. sp_descartar_evento: resolver rechazos descartando el borrador (decisión 4)
     3. Usuario del portal en sp_mover_etapa_vista y sp_registrar_avance_vista
        (los v2 graban SUSER_SNAME(), que con la aplicación es el usuario técnico)
   ===================================================================== */
IF OBJECT_ID('gd.migracion') IS NULL OR NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '001_v3_recertificacion')
    THROW 50000, N'Aplica primero db/migrations/001_v3_recertificacion.sql.', 1;
GO

/* =====================================================================
   1. CONFIRMACIÓN DE LA MACRO ÁREA
   ===================================================================== */
IF OBJECT_ID('gd.recert_confirmacion') IS NULL
CREATE TABLE gd.recert_confirmacion (
    evento_id      INT NOT NULL CONSTRAINT fk_rc_evento     REFERENCES gd.recert_evento (evento_id),
    macro_area_id  INT NOT NULL CONSTRAINT fk_rc_macro_area REFERENCES gd.macro_area (macro_area_id),
    responsable_id INT NOT NULL CONSTRAINT fk_rc_responsable REFERENCES gd.responsable (responsable_id),
    usuario        NVARCHAR(100) NOT NULL CONSTRAINT df_rc_usuario DEFAULT (SUSER_SNAME()),
    fecha_registro DATETIME2(0)  NOT NULL CONSTRAINT df_rc_fecha_reg DEFAULT (SYSDATETIME()),
    CONSTRAINT pk_recert_confirmacion PRIMARY KEY (evento_id, macro_area_id)
);
GO

-- Solo con el evento abierto, por un certificador vigente y sin accesos pendientes.
CREATE OR ALTER PROCEDURE gd.sp_confirmar_macro_area
    @evento_id      INT,
    @macro_area_id  INT,
    @responsable_id INT,
    @usuario        NVARCHAR(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @hoy DATE = CAST(GETDATE() AS DATE);
    IF ISNULL((SELECT estatus FROM gd.recert_evento WHERE evento_id = @evento_id), '') <> 'ABIERTO'
        THROW 50170, N'No hay un evento abierto.', 1;
    IF NOT EXISTS (SELECT 1 FROM gd.fn_macro_areas_certificador(@responsable_id, @hoy) WHERE macro_area_id = @macro_area_id)
        THROW 50171, N'No eres certificador de esa macro área.', 1;
    DECLARE @pendientes INT = (SELECT COUNT(*) FROM gd.recert_acceso AS a
                               WHERE a.evento_id = @evento_id AND a.macro_area_id = @macro_area_id
                                 AND NOT EXISTS (SELECT 1 FROM gd.recert_decision AS d WHERE d.acceso_id = a.acceso_id));
    IF @pendientes > 0
    BEGIN
        DECLARE @msg NVARCHAR(200) = CONCAT(N'Faltan ', @pendientes, N' accesos por decidir.');
        THROW 50172, @msg, 1;
    END;

    DELETE FROM gd.recert_confirmacion WHERE evento_id = @evento_id AND macro_area_id = @macro_area_id;
    INSERT INTO gd.recert_confirmacion (evento_id, macro_area_id, responsable_id, usuario)
    VALUES (@evento_id, @macro_area_id, @responsable_id, ISNULL(@usuario, SUSER_SNAME()));
END;
GO

-- Una decisión posterior invalida la confirmación de su macro área.
CREATE OR ALTER TRIGGER gd.tr_recert_decision_invalida_confirmacion ON gd.recert_decision
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE c
    FROM gd.recert_confirmacion AS c
    JOIN gd.recert_acceso AS a ON a.evento_id = c.evento_id AND a.macro_area_id = c.macro_area_id
    JOIN inserted AS i ON i.acceso_id = a.acceso_id;
END;
GO

/* =====================================================================
   2. DESCARTAR UN BORRADOR
      Las vistas nuevas que trajo el extracto se conservan en el inventario; las marcas de
      "en la última recertificación" se recalculan con el evento anterior.
   ===================================================================== */
CREATE OR ALTER PROCEDURE gd.sp_descartar_evento
    @evento_id INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @estatus VARCHAR(10) = (SELECT estatus FROM gd.recert_evento WHERE evento_id = @evento_id);
    IF @estatus IS NULL THROW 50180, N'El evento no existe.', 1;
    IF @estatus <> 'BORRADOR' THROW 50181, N'Solo se puede descartar un evento en borrador.', 1;

    BEGIN TRANSACTION;
    DELETE FROM gd.recert_carga_rechazo WHERE evento_id = @evento_id;
    DELETE FROM gd.vista_uso WHERE evento_id = @evento_id;
    DELETE FROM gd.recert_acceso WHERE evento_id = @evento_id;
    DELETE FROM gd.recert_evento WHERE evento_id = @evento_id;

    DECLARE @anterior INT = (SELECT MAX(evento_id) FROM gd.recert_evento);
    UPDATE v SET en_ultima_recertificacion = CASE WHEN u.vista_id IS NULL THEN 0 ELSE 1 END
    FROM gd.vista_gobernada AS v
    LEFT JOIN gd.vista_uso AS u ON u.evento_id = @anterior AND u.vista_id = v.vista_id;
    COMMIT TRANSACTION;
END;
GO

/* =====================================================================
   3. USUARIO DEL PORTAL EN LOS PROCEDIMIENTOS v2 (parámetro opcional: compatible con llamadas existentes)
   ===================================================================== */
CREATE OR ALTER PROCEDURE gd.sp_mover_etapa_vista
    @vista_id   INT,
    @etapa_id   INT,
    @motivo_id  INT = NULL,
    @comentario NVARCHAR(1000) = NULL,
    @fecha      DATE = NULL,
    @usuario    NVARCHAR(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @fecha = ISNULL(@fecha, CAST(GETDATE() AS DATE));

    DECLARE @tiene_mov BIT = CASE WHEN EXISTS (SELECT 1 FROM gd.mov_etapa_vista WHERE vista_id = @vista_id) THEN 1 ELSE 0 END;
    DECLARE @orden_actual INT = ISNULL((
        SELECT TOP (1) e.orden
        FROM gd.mov_etapa_vista AS m
        JOIN gd.etapa_vista AS e ON e.etapa_id = m.etapa_id
        WHERE m.vista_id = @vista_id
        ORDER BY m.fecha DESC, m.mov_id DESC), 0);
    DECLARE @orden_nueva INT = (SELECT orden FROM gd.etapa_vista WHERE etapa_id = @etapa_id);

    IF @orden_nueva IS NULL
        THROW 50001, N'La etapa indicada no existe.', 1;
    IF @tiene_mov = 1 AND @orden_nueva = @orden_actual
        THROW 50002, N'La vista ya se encuentra en esa etapa.', 1;

    DECLARE @es_regreso BIT = CASE WHEN @orden_nueva < @orden_actual THEN 1 ELSE 0 END;
    IF @es_regreso = 1 AND @motivo_id IS NULL
        THROW 50003, N'Un regreso de etapa requiere indicar el motivo.', 1;

    INSERT INTO gd.mov_etapa_vista (vista_id, etapa_id, fecha, motivo_id, es_regreso, comentario, usuario)
    VALUES (@vista_id, @etapa_id, @fecha, ISNULL(@motivo_id, CASE WHEN @es_regreso = 0 THEN 1 END), @es_regreso, @comentario,
            ISNULL(@usuario, SUSER_SNAME()));
END;
GO

CREATE OR ALTER PROCEDURE gd.sp_registrar_avance_vista
    @vista_id   INT,
    @rubro_id   INT,
    @estado     VARCHAR(12),
    @pct        DECIMAL(5,2) = NULL,
    @comentario NVARCHAR(1000) = NULL,
    @fecha      DATE = NULL,
    @usuario    NVARCHAR(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @fecha = ISNULL(@fecha, CAST(GETDATE() AS DATE));

    DECLARE @orden INT = ISNULL((
        SELECT TOP (1) e.orden
        FROM gd.mov_etapa_vista AS m
        JOIN gd.etapa_vista AS e ON e.etapa_id = m.etapa_id
        WHERE m.vista_id = @vista_id AND m.fecha <= @fecha
        ORDER BY m.fecha DESC, m.mov_id DESC), 0);

    IF NOT EXISTS (SELECT 1 FROM gd.rubro
                   WHERE rubro_id = @rubro_id AND nivel = 'VISTA'
                     AND @orden BETWEEN etapa_orden_ini AND etapa_orden_fin)
        THROW 50010, N'El rubro no está activo en la etapa actual de la vista.', 1;

    SET @pct = CASE @estado WHEN 'NO_INICIADO' THEN 0 WHEN 'CERRADO' THEN 100 WHEN 'NO_APLICA' THEN 100 ELSE @pct END;

    INSERT INTO gd.mov_avance_vista (vista_id, rubro_id, fecha, pct, estado, comentario, usuario)
    VALUES (@vista_id, @rubro_id, @fecha, @pct, @estado, @comentario, ISNULL(@usuario, SUSER_SNAME()));
END;
GO

IF NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '002_portal')
    INSERT INTO gd.migracion (migracion_id, descripcion)
    VALUES ('002_portal', N'Portal: confirmación por macro área, descartar borrador, usuario en procedimientos v2');
GO
