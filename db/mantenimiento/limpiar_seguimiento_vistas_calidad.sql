/* =====================================================================
   Limpieza de datos capturados en Seguimiento semanal, Data products, Vistas gobernadas, Calidad y
   Seguimiento tarea (2026-10-19, a pedido del usuario). No toca catálogos ni recertificación.
   Borra:
     - Data products completos: avances de DP, notas de bitácora, asignaciones vista→DP y los DP.
     - Sobre las vistas: avances de rubro, cambios de etapa hechos por personas, tareas, objetos BW relacionados,
       y datos de gobierno (gobierno, subdominio, responsable, nivel de seguridad; nombre de negocio = nombre técnico).
     - Resultados y rechazos de cargas de calidad.
   Conserva:
     - Catálogos: gobierno, subdominio, responsable, nivel_seguridad, etapa_vista, rubro, motivo, tipo_regla,
       etapa_regla, semana; reglas, objetos BW, contactos, historial de query y ciclo de vida de reglas.
     - El inventario de vistas (lo usa el evento de recertificación abierto) y sus movimientos de carga
       (usuario 'migración' o 'portal'): cada vista queda en la etapa con que llegó.
     - Todo lo de recertificación.
   ===================================================================== */
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

DECLARE @r TABLE (tabla NVARCHAR(60), filas INT);

DELETE FROM gd.tarea_vista;              INSERT @r VALUES (N'tarea_vista', @@ROWCOUNT);
DELETE FROM gd.vista_objeto;             INSERT @r VALUES (N'vista_objeto', @@ROWCOUNT);
DELETE FROM gd.mov_avance_vista;         INSERT @r VALUES (N'mov_avance_vista', @@ROWCOUNT);
DELETE FROM gd.mov_avance_dp;            INSERT @r VALUES (N'mov_avance_dp', @@ROWCOUNT);
DELETE FROM gd.log_semanal;              INSERT @r VALUES (N'log_semanal', @@ROWCOUNT);
DELETE FROM gd.dp_vista;                 INSERT @r VALUES (N'dp_vista', @@ROWCOUNT);
DELETE FROM gd.data_product;             INSERT @r VALUES (N'data_product', @@ROWCOUNT);

-- Cambios de etapa hechos por personas; se conservan los de carga (SharePoint y extractos).
DELETE FROM gd.mov_etapa_vista WHERE usuario NOT IN (N'migración', N'portal');
INSERT @r VALUES (N'mov_etapa_vista (personas)', @@ROWCOUNT);

UPDATE gd.vista_gobernada
SET gobierno_id = NULL, subdominio_id = NULL, responsable_id = NULL, nivel_seguridad_id = NULL,
    nombre_negocio = RIGHT(ruta_tecnica, CHARINDEX(N'/', REVERSE(ruta_tecnica) + N'/') - 1)
WHERE gobierno_id IS NOT NULL OR subdominio_id IS NOT NULL OR responsable_id IS NOT NULL OR nivel_seguridad_id IS NOT NULL
   OR nombre_negocio <> RIGHT(ruta_tecnica, CHARINDEX(N'/', REVERSE(ruta_tecnica) + N'/') - 1);
INSERT @r VALUES (N'vista_gobernada (datos de gobierno)', @@ROWCOUNT);

DELETE FROM gd.resumen_calidad;          INSERT @r VALUES (N'resumen_calidad', @@ROWCOUNT);
DELETE FROM gd.carga_rechazo;            INSERT @r VALUES (N'carga_rechazo', @@ROWCOUNT);
DELETE FROM gd.stg_resumen_calidad;      INSERT @r VALUES (N'stg_resumen_calidad', @@ROWCOUNT);

COMMIT TRANSACTION;

SELECT tabla, filas FROM @r;
SELECT (SELECT COUNT(*) FROM gd.vista_gobernada) AS vistas_en_inventario,
       (SELECT COUNT(*) FROM gd.regla) AS reglas, (SELECT COUNT(*) FROM gd.objeto_fuente) AS objetos_bw,
       (SELECT COUNT(*) FROM gd.recert_acceso) AS accesos_recertificacion;
