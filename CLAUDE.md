# Gobierno de Datos – Seguimiento de Data Products, Calidad y Recertificación

Contexto para Claude Code. Léelo completo antes de proponer o escribir código.
El detalle está en `docs/`; este archivo resume lo esencial y las decisiones tomadas.

## 1. Qué es el proyecto

Herramienta interna (Mabe) para el equipo de gobierno de datos que hoy trabaja con
la plataforma Informatica (CDGC). Reemplaza un Excel y listas de SharePoint que eran
incómodas de llenar. **Prioridad de diseño: sencillez de captura.**

Tres módulos:

1. **Seguimiento de data products**: avance semanal por data product (DP) y por vista HANA,
   con histórico semana a semana y bitácora.
2. **Calidad de datos**: resultados de reglas de Informatica (registros evaluados / con error)
   reportados por vista, DP, macro área y compañía.
3. **Recertificación de accesos (auditoría)**: 2 a 4 eventos al año en los que el responsable
   de cada macro área decide si cada acceso usuario × vista **Continúa** o se da de **Baja**.

## 2. Glosario

| Término | Significado |
|---|---|
| Data product (DP) | Agrupa varias vistas HANA. Tiene rubros propios (Catalogado, Diseño Funcional, Arquitectura) |
| Vista gobernada | Vista HANA bajo gobierno. Llave: `ruta_tecnica` normalizada |
| Rubro | Elemento de avance. Nivel DP: Catalogado, DF, Arquitectura. Nivel vista: Glosario, Linaje, Perfilado, Reglas |
| Etapa | Estatus de flujo de una vista (13 valores, de *Por Iniciar* a *Completada*) que indica la tarea pendiente |
| Objeto BW | Cubo/ADSO que evalúan las reglas de calidad (PMATERIAL, ZSD_O21…). Los objetos alimentan vistas |
| Regla | Regla de calidad de Informatica (ID tipo `RL_1_1`), genera un CSV por ejecución |
| Macro área | "Área Responsable" del extracto de recertificación; su "Responsable" es quien certifica |
| Evento | Ciclo de recertificación; congela una foto del extracto |

## 3. Stack

- **Base de datos: SQL Server 2016+, esquema `gd`.** Script vigente: `db/gobierno_datos_ddl_v2.sql`.
- **Aplicación: ASP.NET Core Razor Pages (.NET 9)** en `src/GobiernoDatos.Web` (detalle en
  `docs/05_portal_razor.md`). Con `FuenteDatos = SqlServer` los cuatro módulos trabajan sobre
  el esquema `gd` (`Data/Sql/`, Dapper); recertificación delega sus reglas en los `sp_*`, CHECK y
  triggers de la v3. `Muestras` (memoria) sigue disponible para demos sin base.
- **Autenticación: Windows (IIS).** En producción `IUsuarioActual` = usuario de dominio autenticado
  por IIS; nombre visible desde AD. Administrador = cuentas/grupos en `GobiernoDatos:Administradores`;
  Equipo y Certificador = todo usuario autenticado (la base filtra por macro área). El certificador
  se identifica por usuario de red (`gd.responsable.usuario_red`, `DOMINIO\usuario`), no por nombre. La sesión demo
  solo existe en desarrollo y la app no arranca en producción con ella.
- **Despliegue:** IIS en SRVMABE602 (`http://srvmabe602:8010`) con `scripts/deploy.ps1`.
- **Reporteo: Power BI** (Memo desarrolla en Power BI).

## 4. Decisiones de modelo (no reabrir sin consultar)

**Captura por eventos, histórico calculado**
- Toda captura es un movimiento con fecha y usuario (`mov_avance_dp`, `mov_avance_vista`,
  `mov_etapa_vista`, `mov_etapa_regla`). El estado semanal se calcula en vistas SQL
  (último valor con fecha ≤ fin de semana). Semanas ISO, etiqueta `39/26`.

**Cálculo de avance**
- % DP = promedio ponderado (`rubro.peso`) de los 7 rubros.
- Rubro de vista a nivel DP = promedio de las vistas vigentes del DP.
- *No aplica* = 100% (ya se evaluó). Sin dato = 0%. Estado y % siempre coherentes
  (NO_INICIADO=0, EN_CURSO 1–99, CERRADO=100, NO_APLICA=100).

**Vistas y DP**
- Una vista pertenece a **un solo DP vigente** (`dp_vista` con vigencia). Las vistas se
  identifican primero y se pueden agregar al DP en cualquier momento; una vista nueva solo
  cuenta desde su `fecha_desde`.

**Etapas de vista – "el último evento manda"** (sustituyó a la opción C el 2026-10-15, a pedido del
usuario; pendiente de validar con Memo. Migración 005, `Domain/ReglaAvanceVista.cs`)
- Cada rubro de vista abarca un rango de etapas: Glosario 3–6, Linaje 7–8, Perfilado 10, Reglas 11.
- **Se puede capturar el % de cualquier rubro de vista en cualquier etapa.** Vale el último evento:
  la captura; el cruce hacia adelante del rango (→ 100 CERRADO); o el reingreso al rango después de
  haberlo cerrado (→ 0 EN_CURSO, reproceso). Sin eventos: 0 (EN_CURSO dentro del rango, NO_INICIADO fuera).
  Entrar al rango por primera vez o moverse entre etapas posteriores no borra capturas.
- Las vistas pueden reabrirse (Reescaneo, Perfilado, Glosarios por cambio de estructura).
  Un regreso de etapa exige **motivo**. Métricas: pasos, regresos, ciclos por vista.

**Calidad**
- Reglas evalúan objetos BW (N:M); objetos alimentan vistas (`vista_objeto`, con vigencia).
  **El nivel de medición es la vista**; el DP agrega por sus vistas.
- Resumen por regla × periodo (AAAAMM): evaluados y errores; válidos y % calculados.
  Agregar SIEMPRE como Σ válidos / Σ evaluados. Los niveles **no son aditivos**
  (una regla cuenta una vez por vista y una vez por DP).
- Carga vía `stg_resumen_calidad` + `sp_cargar_resumen_calidad`; la regla se identifica por
  `codigo` o `nombre_csv`. `escribe_log` (columna STAGE) indica si la regla escribe al log.
- `query_validacion` versionado automáticamente (trigger → `regla_query_hist`).
- Ciclo de vida de la regla como etapas (Definición → … → Operación PRD), no como 6 fechas.

**Recertificación de accesos (modelo v3, `db/migrations/001_v3_recertificacion.sql`)**
- Fuente: vista HANA de Recertificación. Grano: usuario × rol × vista con accesos de 3 meses.
- Columna GOBIERNO del extracto: **se omite**. "Área Responsable" = macro área; "Responsable"
  = certificador (puede haber delegados).
- **Cada vista tiene un rol dedicado (1 rol = 1 vista)** → la decisión es por usuario × vista.
- Evento = carga del extracto, **foto inmutable**. Decisiones Continúa/Baja con histórico
  (vale la última), **comentario obligatorio en Baja**, bloqueo al cerrar el evento.
- Decisión del evento anterior visible como referencia, **sin precargar**.
- Acción masiva "Continúa" con confirmación y registro individual por acceso.
- Pendientes al cierre se reportan, nunca se asume decisión.
- Ejecución de bajas con evidencia (`recert_baja_ejecucion`: fecha, ticket, ejecutor).
- La misma carga sincroniza el inventario de vistas: nuevas → etapa *Por Iniciar*;
  ausentes → se marcan, **no se borran**; uso por vista y evento (`vista_uso`).
- Validaciones de carga: rol con >1 vista, vista con >1 rol, filas rechazadas
  (el evento no abre con rechazos sin resolver).

**Llaves y datos**
- `ruta_tecnica` normalizada: `PAQUETE/VISTA` en mayúsculas, sin `"_SYS_BIC".` ni comillas.
  SharePoint trae `"_SYS_BIC"."MABE.X/Y"`; el extracto trae `MABE.X/Y`.
- Exportaciones de SharePoint venían en Windows-1252: importar con code page 1252 a NVARCHAR.
- El detalle por usuario solo existe por la auditoría: acceso restringido a esas tablas.

## 5. Estado actual

| Entregable | Estado | Archivo |
|---|---|---|
| Script BD v2 (seguimiento, etapas, calidad) | Aplicado en SRVMABE385 / GobiernoDatos (2026-10-02) | `db/gobierno_datos_ddl_v2.sql` |
| Carga inicial de muestras (v2) | Aplicada en SRVMABE385; sin rubros de vista del Excel ni recertificación | `db/seeds/001_muestras_v2.sql` |
| Modelo ER v3 (incluye recertificación) | Listo | `docs/Gobierno_Datos_ER_v3.pdf`, `docs/modelo_er_v3.mmd` |
| Migración v3 (recertificación) | Aplicada en SRVMABE385 (2026-10-02) | `db/migrations/001_v3_recertificacion.sql` |
| Migración 002 (necesidades del portal, fuera del ER: confirmación por macro área, descartar borrador, `@usuario` en procs v2) | Aplicada en SRVMABE385; **pendiente de validar con Memo** | `db/migrations/002_portal.sql` |
| Migración 003 (usuario de red del responsable) | Aplicada en SRVMABE385 (2026-10-13) | `db/migrations/003_responsable_usuario_red.sql` |
| Migración 004 (tareas por etapa de vista, pantalla Seguimiento tarea) | Aplicada en SRVMABE385 (2026-10-15) | `db/migrations/004_tarea_vista.sql` |
| Migración 005 (avance de vista: captura libre, último evento manda) | Aplicada en SRVMABE385 (2026-10-15) | `db/migrations/005_avance_vista_captura_libre.sql` |
| Migración 006 (carga de calidad con una sola marca de tiempo) | Aplicada en SRVMABE385 (2026-10-15) | `db/migrations/006_calidad_marca_de_carga.sql` |
| Extracto de recertificación de muestra | Cargado y después **limpiado** en SRVMABE385 (2026-10-16); se conservaron 3 vistas con trabajo capturado | `db/seeds/002_recertificacion_muestra.sql`, `db/mantenimiento/limpiar_muestra_recertificacion.sql` |
| Limpieza de datos de seguimiento, DP, vistas, calidad y tareas (catálogos, reglas e inventario conservados) | Ejecutada en SRVMABE385 (2026-10-19) | `db/mantenimiento/limpiar_seguimiento_vistas_calidad.sql` |
| Migración 007 (avance reportado por DP) + importación del Excel de seguimiento | Aplicada; carga desde Seguimiento semanal (los 54 DP de prueba se borraron el 2026-10-19 para cargar de nuevo) | `db/migrations/007_avance_reportado_dp.sql`, `Domain/ImportacionSeguimiento.cs` |
| Extracto real desde HANA (Eventos → Nuevo evento) | Publicado; DSN `Hana` presente en SRVMABE602 (confirmado por el usuario, 2026-10-16) | `Services/FuenteExtracto.cs` |
| Maqueta de 7 pantallas (pendiente de confirmar por Memo) | Lista | `mockups/*.html`, `mockups/Pantallas_maqueta.pdf` |
| Portal Razor Pages (7 pantallas + Data products), con pruebas | Conectado a SRVMABE385 (todos los módulos) | `src/GobiernoDatos.Web`, `tests/GobiernoDatos.Tests` |
| Muestras de datos reales | Listas | `data/muestras/*.csv` |

## 6. Cómo trabajar en este repo

- Responder en **español**, de forma directa y estructurada.
- Antes de cambiar el modelo, revisar `docs/02_modelo_y_reglas.md`; si una decisión de la
  sección 4 no encaja, **preguntar**, no reinterpretar.
- Nombres de objetos en español, snake_case, esquema `gd`. Constraints con nombre explícito
  (`pk_`, `fk_`, `ck_`, `uq_`, `ix_`, `ux_`).
- Escribir scripts SQL idempotentes por migración (`db/migrations/NNN_descripcion.sql`) a
  partir del v2, en lugar de editar el script monolítico. Orden de despliegue: DDL v2 →
  `db/migrations/*` en orden → `db/seeds/*`. Las aplicadas quedan en `gd.migracion`.
- Toda regla de negocio crítica (opción C, cierre de evento, comentario en Baja) debe tener
  prueba automatizada.
- No inventar datos: usar `data/muestras/` o marcadores `[ ]`.
