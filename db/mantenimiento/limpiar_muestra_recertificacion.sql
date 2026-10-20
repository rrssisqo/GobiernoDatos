/* =====================================================================
   Limpieza de la recertificación de muestra (db/seeds/002_recertificacion_muestra.sql), antes de la
   primera carga real desde HANA. Se ejecuta una vez (2026-10-16, a pedido del usuario).
   Borra: evento [Evento muestra] y su foto (accesos, rechazos, uso, confirmaciones), certificadores,
          macro áreas, usuarios y roles HANA, y las vistas creadas por ese extracto SIN trabajo capturado.
   Conserva: vistas del extracto con trabajo (avances, DP, etapas movidas por usuarios, tareas, objetos, notas):
             son vistas reales que el extracto de HANA volverá a traer (se reconocen por ruta técnica).
   Candados: aborta si hay decisiones de certificación, bajas ejecutadas o algún evento distinto al de muestra.
   ===================================================================== */
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF EXISTS (SELECT 1 FROM gd.recert_evento WHERE nombre <> N'[Evento muestra]')
    THROW 50900, N'Hay eventos distintos al de muestra: no se limpia.', 1;
IF EXISTS (SELECT 1 FROM gd.recert_decision) OR EXISTS (SELECT 1 FROM gd.recert_baja_ejecucion)
    THROW 50901, N'Hay decisiones o bajas registradas (evidencia de auditoría): no se limpia.', 1;

BEGIN TRANSACTION;

-- Vistas que trajo el extracto de muestra, separadas por si tienen trabajo capturado.
SELECT v.vista_id,
       CASE WHEN EXISTS (SELECT 1 FROM gd.mov_avance_vista a WHERE a.vista_id = v.vista_id)
              OR EXISTS (SELECT 1 FROM gd.dp_vista d WHERE d.vista_id = v.vista_id)
              OR EXISTS (SELECT 1 FROM gd.mov_etapa_vista m WHERE m.vista_id = v.vista_id AND m.usuario <> N'migración')
              OR EXISTS (SELECT 1 FROM gd.tarea_vista t WHERE t.vista_id = v.vista_id)
              OR EXISTS (SELECT 1 FROM gd.vista_objeto o WHERE o.vista_id = v.vista_id)
              OR EXISTS (SELECT 1 FROM gd.log_semanal l WHERE l.vista_id = v.vista_id)
            THEN 1 ELSE 0 END AS con_trabajo
INTO #vistas_muestra
FROM gd.vista_gobernada AS v
WHERE v.fecha_primera_aparicion IS NOT NULL;

-- La foto solo se puede tocar en borrador (tr_recert_acceso_inmutable).
UPDATE gd.recert_evento SET estatus = 'BORRADOR', fecha_inicio = NULL, fecha_cierre = NULL;

DELETE FROM gd.recert_confirmacion;
DELETE FROM gd.recert_carga_rechazo;
DELETE FROM gd.vista_uso;
DELETE FROM gd.recert_acceso;
DELETE FROM gd.recert_evento;
DELETE FROM gd.macro_area_certificador;
DELETE FROM gd.macro_area;
DELETE FROM gd.rol_hana;
DELETE FROM gd.usuario_hana;

DELETE m FROM gd.mov_etapa_vista AS m JOIN #vistas_muestra AS x ON x.vista_id = m.vista_id AND x.con_trabajo = 0;
DELETE v FROM gd.vista_gobernada AS v JOIN #vistas_muestra AS x ON x.vista_id = v.vista_id AND x.con_trabajo = 0;

-- Las conservadas quedan como vistas sin extracto hasta la carga desde HANA.
UPDATE v SET en_ultima_recertificacion = 0, fecha_primera_aparicion = NULL, fecha_ultima_aparicion = NULL
FROM gd.vista_gobernada AS v JOIN #vistas_muestra AS x ON x.vista_id = v.vista_id AND x.con_trabajo = 1;

COMMIT TRANSACTION;

SELECT (SELECT COUNT(*) FROM #vistas_muestra WHERE con_trabajo = 0) AS vistas_borradas,
       (SELECT COUNT(*) FROM #vistas_muestra WHERE con_trabajo = 1) AS vistas_conservadas,
       (SELECT COUNT(*) FROM gd.recert_evento) AS eventos,
       (SELECT COUNT(*) FROM gd.recert_acceso) AS accesos,
       (SELECT COUNT(*) FROM gd.macro_area) AS macro_areas,
       (SELECT COUNT(*) FROM gd.vista_gobernada) AS vistas_en_inventario;
