# 04 · Pendientes y backlog

## Decisiones abiertas (resolver con Memo)
1. Stack de la aplicación, autenticación (¿identidad corporativa?) y hosting.
2. Confirmar pantallas de la maqueta (en especial el módulo de recertificación).
3. Catálogo definitivo de gobiernos y cómo se deriva el gobierno de la ruta de la vista
   (Excel usa CDV/Finanzas/Comercial/Industrial; SharePoint Industrial/Nominas/RRHH; reglas Datos Maestros/Cadena de Valor).
4. Rol ejecutor por etapa de vista (columna `etapa_vista.rol_ejecutor`).
5. Si la macro área de un acceso puede diferir entre filas de la misma vista.

## Deltas v2 → v3 (migraciones) — aplicados en `db/migrations/001_v3_recertificacion.sql`
- `vista_gobernada`: + fecha_primera_aparicion, fecha_ultima_aparicion, en_ultima_recertificacion;
  gobierno_id NULL hasta clasificar; quitar fecha_recertificacion (se deriva de vista_uso).
- Nuevas: macro_area, macro_area_certificador, usuario_hana, rol_hana (vista_id UNIQUE),
  recert_evento, recert_acceso, recert_decision, recert_baja_ejecucion, recert_carga_rechazo,
  vista_uso, stg_recertificacion.
- Procs: sp_cargar_extracto_recertificacion (staging → validación → usuarios/roles/vistas →
  recert_acceso + vista_uso), sp_abrir_evento, sp_cerrar_evento, sp_registrar_decision,
  sp_registrar_baja_ejecucion.
- Trigger: bloqueo de decisiones en evento cerrado. Vistas: avance por macro área,
  pendientes por certificador, bajas por ejecutar, evidencia de auditoría.
- Función de normalización de ruta técnica.

## Backlog sugerido
| Fase | Alcance |
|---|---|
| F0 | Repo, decisión de stack, entorno SQL Server de desarrollo |
| F1 | Migraciones v2 + v3, seeds de catálogos, pruebas de reglas críticas |
| F2 | Cargas: extracto de recertificación, resumen de calidad, migración inicial Excel/SharePoint |
| F3 | App: Vistas gobernadas, Seguimiento, Detalle DP |
| F4 | App: módulo de recertificación (Eventos, Mi recertificación, Bajas) con seguridad por fila |
| F5 | Modelo Power BI sobre las vistas `gd.vw_*` |
