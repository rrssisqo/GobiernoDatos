/* =====================================================================
   Migración 005 · Avance por rubro de vista: "el último evento manda"
   Sustituye la opción C (decisión del 2026-10-15): el % de cualquier rubro de vista se puede capturar
   en cualquier etapa. Por rubro y vista, al cierre de cada semana vale el último evento:
     - Captura del rubro (gd.mov_avance_vista), en cualquier etapa        → lo capturado
     - La vista cruza hacia adelante el rango del rubro                     → 100 CERRADO
     - La vista vuelve a entrar al rango después de haberlo cerrado         → 0 EN_CURSO (reproceso)
     - Sin eventos: 0 EN_CURSO si la vista está dentro del rango; si no, 0 NO_INICIADO
   Entrar al rango por primera vez no borra capturas previas; moverse entre etapas posteriores tampoco.
   Orden: fecha, luego fecha_registro; en empate gana la captura.
   Espejo en C#: src/GobiernoDatos.Web/Domain/ReglaAvanceVista.cs.
   Requiere 002 (parámetro @usuario). Idempotente. Ejecutar con QUOTED_IDENTIFIER ON (sqlcmd -I).
   ===================================================================== */
IF OBJECT_ID('gd.migracion') IS NULL OR NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '002_portal')
    THROW 50000, N'Aplica primero db/migrations/002_portal.sql.', 1;
GO

CREATE OR ALTER VIEW gd.vw_avance_vista_semana
AS
WITH vista_semana AS (
    SELECT s.semana_id, s.etiqueta, s.fecha_fin, v.vista_id, v.nombre_negocio
    FROM gd.semana AS s
    JOIN gd.vista_gobernada AS v
      ON  s.fecha_fin    >= v.fecha_alta
      AND s.fecha_inicio <= CAST(GETDATE() AS DATE)
    WHERE v.activa = 1
),
movs AS (
    SELECT  m.vista_id, m.mov_id, m.fecha, m.fecha_registro, e.orden,
            LAG(e.orden) OVER (PARTITION BY m.vista_id ORDER BY m.fecha, m.mov_id) AS orden_prev
    FROM gd.mov_etapa_vista AS m
    JOIN gd.etapa_vista AS e ON e.etapa_id = m.etapa_id
),
cierres AS (   -- la vista cruza hacia adelante el rango del rubro
    SELECT mv.vista_id, r.rubro_id, mv.mov_id, mv.fecha, mv.fecha_registro
    FROM movs AS mv
    JOIN gd.rubro AS r ON r.nivel = 'VISTA'
    WHERE mv.orden > r.etapa_orden_fin
      AND (mv.orden_prev IS NULL OR mv.orden_prev <= r.etapa_orden_fin)
),
eventos AS (
    SELECT c.vista_id, c.rubro_id, c.fecha, c.fecha_registro, 0 AS tipo,
           CAST(100 AS DECIMAL(5,2)) AS pct, CAST('CERRADO' AS VARCHAR(12)) AS estado, CAST('ETAPA' AS VARCHAR(7)) AS origen
    FROM cierres AS c

    UNION ALL   -- reproceso: vuelve a entrar al rango después de haberlo cerrado por etapa
    SELECT mv.vista_id, r.rubro_id, mv.fecha, mv.fecha_registro, 0, CAST(0 AS DECIMAL(5,2)), 'EN_CURSO', 'ETAPA'
    FROM movs AS mv
    JOIN gd.rubro AS r ON r.nivel = 'VISTA'
    WHERE mv.orden BETWEEN r.etapa_orden_ini AND r.etapa_orden_fin
      AND (mv.orden_prev IS NULL OR mv.orden_prev NOT BETWEEN r.etapa_orden_ini AND r.etapa_orden_fin)
      AND EXISTS (SELECT 1 FROM cierres AS c
                  WHERE c.vista_id = mv.vista_id AND c.rubro_id = r.rubro_id
                    AND (c.fecha < mv.fecha OR (c.fecha = mv.fecha AND c.mov_id < mv.mov_id)))

    UNION ALL   -- capturas, en cualquier etapa
    SELECT a.vista_id, a.rubro_id, a.fecha, a.fecha_registro, 1, a.pct, a.estado, 'CAPTURA'
    FROM gd.mov_avance_vista AS a
)
SELECT  vs.semana_id,
        vs.etiqueta,
        vs.vista_id,
        vs.nombre_negocio              AS vista,
        dv.dp_id,
        r.rubro_id,
        r.nombre                       AS rubro,
        ISNULL(ea.orden, 0)            AS etapa_orden_actual,
        calc.pct,
        calc.estado,
        calc.origen
FROM vista_semana AS vs
CROSS JOIN gd.rubro AS r
OUTER APPLY (   -- DP vigente al cierre de la semana
    SELECT TOP (1) x.dp_id
    FROM gd.dp_vista AS x
    WHERE x.vista_id = vs.vista_id
      AND x.fecha_desde <= vs.fecha_fin
      AND (x.fecha_hasta IS NULL OR x.fecha_hasta >= vs.fecha_fin)
    ORDER BY x.fecha_desde DESC
) AS dv
OUTER APPLY (   -- etapa actual al cierre de la semana
    SELECT TOP (1) e.orden
    FROM gd.mov_etapa_vista AS m
    JOIN gd.etapa_vista AS e ON e.etapa_id = m.etapa_id
    WHERE m.vista_id = vs.vista_id AND m.fecha <= vs.fecha_fin
    ORDER BY m.fecha DESC, m.mov_id DESC
) AS ea
OUTER APPLY (   -- último evento del rubro al cierre de la semana
    SELECT TOP (1) ev.pct, ev.estado, ev.origen
    FROM eventos AS ev
    WHERE ev.vista_id = vs.vista_id AND ev.rubro_id = r.rubro_id AND ev.fecha <= vs.fecha_fin
    ORDER BY ev.fecha DESC, ev.fecha_registro DESC, ev.tipo DESC
) AS ult
CROSS APPLY (
    SELECT
        pct    = CAST(ISNULL(ult.pct, 0) AS DECIMAL(5,2)),
        estado = ISNULL(ult.estado, CASE WHEN ISNULL(ea.orden, 0) BETWEEN r.etapa_orden_ini AND r.etapa_orden_fin
                                         THEN 'EN_CURSO' ELSE 'NO_INICIADO' END),
        origen = ISNULL(ult.origen, 'ETAPA')
) AS calc
WHERE r.nivel = 'VISTA';
GO

-- Registra el % de cualquier rubro de vista, en cualquier etapa.
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

    IF NOT EXISTS (SELECT 1 FROM gd.rubro WHERE rubro_id = @rubro_id AND nivel = 'VISTA')
        THROW 50010, N'El rubro no es de nivel vista.', 1;

    SET @pct = CASE @estado WHEN 'NO_INICIADO' THEN 0 WHEN 'CERRADO' THEN 100 WHEN 'NO_APLICA' THEN 100 ELSE @pct END;

    INSERT INTO gd.mov_avance_vista (vista_id, rubro_id, fecha, pct, estado, comentario, usuario)
    VALUES (@vista_id, @rubro_id, @fecha, @pct, @estado, @comentario, ISNULL(@usuario, SUSER_SNAME()));
END;
GO

IF NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '005_avance_vista_captura_libre')
    INSERT INTO gd.migracion (migracion_id, descripcion)
    VALUES ('005_avance_vista_captura_libre', N'Avance de rubros de vista: se captura en cualquier etapa; el último evento manda (sustituye la opción C)');
GO
