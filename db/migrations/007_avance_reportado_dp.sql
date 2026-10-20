/* =====================================================================
   Migración 007 · Avance reportado a nivel data product (importación del Excel de seguimiento)
   Resuelve la decisión abierta #1 (docs/05): el Excel trae Glosario, Linaje, Perfil y Reglas por data product.
     - gd.mov_avance_dp acepta también rubros de nivel VISTA ("avance reportado").
     - Rubro de vista de un DP: promedio de sus vistas vigentes; si el DP no tiene vistas asignadas en la semana,
       el último avance reportado (mov_avance_dp). Mismo criterio que el almacén en memoria (GobiernoStore).
     - data_product.vistas_reportadas: columna "Vistas" del Excel; se muestra mientras no haya vistas asignadas.
   Requiere 005 (vw_avance_vista_semana vigente). Idempotente. Ejecutar con QUOTED_IDENTIFIER ON (sqlcmd -I).
   ===================================================================== */
IF COL_LENGTH('gd.data_product', 'vistas_reportadas') IS NULL
    ALTER TABLE gd.data_product ADD vistas_reportadas INT NULL
        CONSTRAINT ck_dp_vistas_reportadas CHECK (vistas_reportadas IS NULL OR vistas_reportadas >= 0);
GO

-- El nivel del movimiento sigue amarrado al del rubro por fk_movdp_rubro (rubro_id, nivel).
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'ck_movdp_nivel' AND definition LIKE '%VISTA%')
BEGIN
    ALTER TABLE gd.mov_avance_dp DROP CONSTRAINT ck_movdp_nivel;
    ALTER TABLE gd.mov_avance_dp ADD CONSTRAINT ck_movdp_nivel CHECK (nivel IN ('DP', 'VISTA'));
END;
GO

CREATE OR ALTER VIEW gd.vw_avance_rubro_dp_semana
AS
WITH dp_semana AS (
    SELECT s.semana_id, s.etiqueta, s.fecha_fin, d.dp_id
    FROM gd.semana AS s
    JOIN gd.data_product AS d
      ON  s.fecha_fin    >= d.fecha_alta
      AND s.fecha_inicio <= ISNULL(d.fecha_cierre, CAST(GETDATE() AS DATE))
),
vista_agregado AS (
    SELECT semana_id, dp_id, rubro_id, AVG(pct) AS pct
    FROM gd.vw_avance_vista_semana
    WHERE dp_id IS NOT NULL
    GROUP BY semana_id, dp_id, rubro_id
)
SELECT  ds.semana_id, ds.etiqueta, ds.dp_id,
        r.rubro_id, r.nombre AS rubro, r.nivel, r.grupo_responsable, r.peso, r.orden,
        CAST(CASE WHEN r.nivel = 'DP'         THEN ISNULL(md.pct, 0)
                  WHEN va.pct IS NOT NULL      THEN va.pct          -- el DP tiene vistas: manda el promedio de sus vistas
                  ELSE ISNULL(md.pct, 0) END                         -- sin vistas: avance reportado
             AS DECIMAL(5,2)) AS pct
FROM dp_semana AS ds
CROSS JOIN gd.rubro AS r
OUTER APPLY (
    SELECT TOP (1) m.pct
    FROM gd.mov_avance_dp AS m
    WHERE m.dp_id = ds.dp_id AND m.rubro_id = r.rubro_id AND m.fecha <= ds.fecha_fin
    ORDER BY m.fecha DESC, m.mov_id DESC
) AS md
LEFT JOIN vista_agregado AS va
  ON r.nivel = 'VISTA' AND va.semana_id = ds.semana_id AND va.dp_id = ds.dp_id AND va.rubro_id = r.rubro_id;
GO

CREATE OR ALTER VIEW gd.vw_avance_dp_total_semana
AS
SELECT  a.semana_id, a.etiqueta, a.dp_id,
        d.nombre AS data_product, g.nombre AS gobierno, d.estatus,
        CAST(SUM(a.pct * a.peso) / SUM(a.peso) AS DECIMAL(5,2)) AS pct_avance,
        -- vistas asignadas en la semana; si no hay, las reportadas en el Excel
        CASE WHEN MAX(nv.num_vistas) > 0 THEN MAX(nv.num_vistas) ELSE ISNULL(d.vistas_reportadas, 0) END AS num_vistas
FROM gd.vw_avance_rubro_dp_semana AS a
JOIN gd.semana       AS s ON s.semana_id   = a.semana_id
JOIN gd.data_product AS d ON d.dp_id       = a.dp_id
JOIN gd.gobierno     AS g ON g.gobierno_id = d.gobierno_id
OUTER APPLY (
    SELECT COUNT(*) AS num_vistas
    FROM gd.dp_vista AS x
    JOIN gd.vista_gobernada AS v ON v.vista_id = x.vista_id AND v.activa = 1
    WHERE x.dp_id = a.dp_id
      AND x.fecha_desde <= s.fecha_fin
      AND (x.fecha_hasta IS NULL OR x.fecha_hasta >= s.fecha_fin)
) AS nv
GROUP BY a.semana_id, a.etiqueta, a.dp_id, d.nombre, g.nombre, d.estatus, d.vistas_reportadas;
GO

IF NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '007_avance_reportado_dp')
    INSERT INTO gd.migracion (migracion_id, descripcion)
    VALUES ('007_avance_reportado_dp', N'Avance reportado a nivel DP para rubros de vista y vistas reportadas (importación del Excel)');
GO
