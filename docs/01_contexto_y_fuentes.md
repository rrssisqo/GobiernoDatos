# 01 · Contexto y fuentes de datos

## Proceso actual
- Gobierno de datos con Informatica (CDGC). Se gestiona por **data products**; cada DP agrupa
  vistas HANA (fuentes de datos).
- Cada DP debe tener al menos un Diseño Funcional, un documento de Arquitectura y su Catalogado
  (propósito, nivel de privacidad, permisos). Cada vista debe tener glosarios, linaje,
  perfilado de calidad y, si se requiere, reglas de calidad.
- Sesiones semanales de seguimiento por DP hasta su cierre. Hoy se registra en Excel
  (% por rubro + notas concatenadas por semana, p. ej. `39/26 - ... 38/26 - ...`).
- Antes se usaron listas de SharePoint (Vistas HANA Gobernadas, Reglas); se abandonaron por
  incómodas: demasiadas columnas, booleanos, avance capturado a mano que no cuadraba.

## Fuentes y cómo se mapean

### Excel de seguimiento (`data/muestras/excel_seguimiento_sem39_26.csv`)
- Columnas: Gobierno, Data Product, Avance, Vistas, Glosario, Catalogado, DF, Arq., Linaje,
  Perfil, Reglas, Comentarios. Grupos: Owner (Glosario, Catalogado, DF), Funcional (Arq., Linaje),
  Owner-Funcional (Perfil, Reglas).
- El "Avance" del Excel es el promedio simple de los 7 rubros con vacíos = 0 (verificado en las 6 filas).

### Lista SharePoint "Vistas HANA Gobernadas" (`sharepoint_vistas_hana_gobernadas.csv`)
| Columna | Destino |
|---|---|
| Vista | `vista_gobernada.nombre_negocio` |
| Vista Técnica | `ruta_tecnica` (normalizar) |
| Responsable | `vista_gobernada.responsable_id` (responsable operativo de la vista) |
| Nivel de Seguridad | `nivel_seguridad` |
| Proceso / SubDominioPadre | se unifican en `subdominio` |
| Área | gobierno (revisar catálogo) |
| Estatus | etapa actual de la vista (`mov_etapa_vista` inicial) |
| Linaje, Glosarios, Reglas (bool), Avance, ID Regla | se reemplazan por el modelo nuevo |

### Lista SharePoint "Reglas" (`sharepoint_reglas.csv`)
| Columna | Destino |
|---|---|
| ID | `regla.codigo` |
| NOMBRE_REGLA | `tipo_regla` (normalizar Rl_Null / Rl_Nulos) |
| DESCRIPCION / DESCRIPCION_FUNCIONAL / CORREO_PROPOSITO | `descripcion` / `regla_funcional` / `proposito_negocio` |
| NOMBRE_CUBOS (puede traer varios, separados por `;`) | `objeto_fuente` + `regla_objeto` |
| NOMBRE_CAMPO | `campos_evaluados` |
| TABLA_STG, MAPPING, TASK_FLOW, RUTA | atributos técnicos |
| NO_CSV, NOMBRE_CSV | únicos; prefijo y nombre del archivo que genera la regla |
| STAGE (SI/NO) | `escribe_log` |
| Query | `query_validacion` (con histórico) |
| CORREO_CONTACTOS, CORREO_ENVIAR | `regla_contacto` |
| ESTATUS + FECHA_* (6) | `mov_etapa_regla` |
- Nota: el sufijo `_RL_xx` del CSV NO coincide con el ID; el match es por NOMBRE_CSV/NO_CSV.
- Los textos de la muestra se limpiaron de codificación y algunos se abreviaron.

### Resultados de reglas (Informatica)
- Informatica genera detalle variable (evaluados y errores). Memo generará una vista de
  resumen con: regla, periodo, registros evaluados, número de errores → `stg_resumen_calidad`.

### Vista HANA de Recertificación (`recertificacion_extracto_muestra.csv`)
- Grano: usuario × rol × vista. Columnas: AREA, CONTENT, USUARIO, NOMBRE, ROL, VISTA,
  ACCESO ULTIMOS 3 MESES, GOBIERNO (se omite), Área Responsable (macro área),
  Responsable (certificador de la macro área), OK-BAJA., Comentarios. (estas dos se capturan
  ahora en la herramienta).
- Se usa en cada evento de recertificación (2–4 por año) y para sincronizar el inventario de
  vistas gobernadas (≈223 vistas en uso; ≈109 asignadas a DP al momento de la conversación).
