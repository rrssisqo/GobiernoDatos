# 05 · Portal (ASP.NET Core Razor Pages)

Implementación de las 7 pantallas de la maqueta (`mockups/`) más un listado de data products.

## Fuentes de datos

`GobiernoDatos:FuenteDatos` elige la fuente:

| Valor | Todos los módulos |
|---|---|
| `Muestras` (valor del repo) | Memoria, con `data/muestras/`; los cambios se pierden al reiniciar |
| `SqlServer` | Esquema `gd` v3 (`Data/Sql/`) |

Recertificación en SQL (`SqlRecertificacionService`) no duplica reglas: llama a `sp_cargar_extracto_recertificacion`,
`sp_abrir_evento`, `sp_cerrar_evento`, `sp_registrar_decision`, `sp_confirmar_macro_area`, `sp_descartar_evento` y
`sp_registrar_baja_ejecucion`, y traduce sus `THROW 50xxx` a mensajes. El certificador se identifica por
`gd.responsable.nombre` = nombre del usuario del portal (con autenticación corporativa conviene usar el correo).
Los certificadores se asignan en Eventos → "Certificadores por macro área".

Con `SqlServer`, el avance se lee de las vistas `gd.vw_avance_*` (el cálculo vive en SQL) y la carga de
calidad pasa por `stg_resumen_calidad` + `gd.sp_cargar_resumen_calidad`. Las escrituras de avance y etapa
replican las validaciones de `sp_registrar_avance_vista` / `sp_mover_etapa_vista` en vez de llamarlos,
porque los procedimientos v2 graban `SUSER_SNAME()` (el usuario técnico) y no el usuario del portal.
**Delta para v3:** agregar `@usuario` a esos procedimientos.

La cadena de conexión nunca va en `appsettings.json`. En desarrollo, con user-secrets:

```
cd src/GobiernoDatos.Web
dotnet user-secrets set "ConnectionStrings:GobiernoDatos" "Server=srvmabe385;Database=GobiernoDatos;User Id=...;Password=...;Encrypt=True;TrustServerCertificate=True"
dotnet user-secrets set "GobiernoDatos:FuenteDatos" "SqlServer"
```

En el servidor: variables de entorno `ConnectionStrings__GobiernoDatos` y `GobiernoDatos__FuenteDatos`.

Con la v3, Vistas gobernadas muestra rol, macro área, uso y apariciones del último extracto, y el gobierno
de una vista es opcional hasta clasificarla. Los rubros de vista de un DP sin vistas cuentan 0%.

## Cómo ejecutarlo

```
dotnet run --project src/GobiernoDatos.Web      # https://localhost:7180
dotnet test                                      # opción C, recertificación, cargas

# Pruebas de los servicios SQL: crean y recrean la base GobiernoDatos_Pruebas en el servidor indicado.
# Usar LocalDB o un servidor de pruebas, nunca la base compartida.
$env:GD_SQL_TEST = "Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true"
dotnet test                                      # 48 pruebas; sin la variable, las 5 de SQL se omiten
```

Abrir `GobiernoDatos.sln` en Visual Studio también funciona. Los cambios se pierden al reiniciar
(almacén en memoria).

## Migraciones de base de datos

Los scripts de `db/migrations` están en UTF-8 y tienen textos con acentos (mensajes de error, catálogos). Se ejecutan **en orden** con:

```
sqlcmd -S SRVMABE385 -d GobiernoDatos -U <usuario> -P <contraseña> -C -I -b -f 65001 -i db\migrations\0NN_nombre.sql
```

`-f 65001` es obligatorio: sin él, `sqlcmd` lee el archivo como ANSI y los acentos quedan dañados en procedimientos y datos ("área" → "Ã¡rea"). `-I` activa QUOTED_IDENTIFIER (lo exigen los índices filtrados). Todas las migraciones se pueden volver a ejecutar sin efectos secundarios.

## Despliegue (IIS en SRVMABE602)

El portal corre en `http://srvmabe602:8010/`. Archivos en `\\SRVMABE602\Aplicación web\GobiernoDatos`:
publicación self-contained win-x64 (incluye el runtime .NET 9), hosting in-process con `AspNetCoreModuleV2`.

```
.\scripts\deploy.ps1 -Simular      # qué subiría, sin tocar el servidor
.\scripts\deploy.ps1               # pruebas → publish → copia de lo cambiado → prueba de humo
```

- La contraseña se pide al ejecutar (no se guarda). Opciones: `-SinPruebas`, `-Url`, `-Usuario`, `-Destino`.
- Sube los archivos propios de la app y los que cambian de tamaño; usa `app_offline.htm` durante la copia y
  verifica la DLL antes de volver a poner la app en línea. Si la verificación falla, la deja fuera de línea.
- **Nunca toca `web.config`**: el del servidor tiene `ASPNETCORE_ENVIRONMENT=Production` y la cadena de conexión
  (`ConnectionStrings__GobiernoDatos`). Para cambiar la contraseña de la base, se edita ahí.
- No borra archivos del servidor; los que sobran se reportan.
- Logs de arranque: `logs\stdout_*.log` en la carpeta del servidor.

### Autenticación y roles

| Configuración (`GobiernoDatos:`) | Desarrollo | Producción (`appsettings.Production.json`) |
|---|---|---|
| `Autenticacion` | `Demo` (usuario de `UsuarioDemo`) | `Windows`: usuario autenticado por IIS (anónima deshabilitada) |
| `Administradores` | — | Cuentas o grupos de AD: `MABENET\usuario`, `MABENET\Grupo` |

- Administrador: Eventos, Bajas y evidencia, y Auditoría. Equipo y Certificador: cualquier usuario autenticado.
- El nombre visible sale de Active Directory (formato `Apellido, Nombre (PAÍS, UBICACIÓN, ÁREA)`).
- **Un certificador se reconoce por su usuario de red** (`gd.responsable.usuario_red`, migración 003). En Eventos se
  asigna escribiendo `MABENET\usuario` (o solo `usuario`; se completa con `GobiernoDatos:DominioPredeterminado`).
  El portal lo valida contra AD y guarda el nombre de AD para mostrarlo. Si ya existía un responsable con ese nombre
  sin usuario de red (p. ej. de la columna "Responsable" del extracto), se le asigna en vez de duplicarlo.
- Un responsable que llegó solo por nombre aparece como "sin usuario de red" y no puede certificar hasta asignárselo.
- Los movimientos guardan la cuenta (`MABENET\usuario`) como usuario.
- En producción la app no arranca si `Autenticacion` no es `Windows`.

## Estructura

| Carpeta | Contenido |
|---|---|
| `Domain/` | Catálogos (espejo de los INSERT del DDL v2), opción C, semanas ISO, normalización de ruta técnica, reglas de recertificación |
| `Data/` | Entidades con la forma del ER v3, almacén en memoria, cargador de muestras, carga del extracto de recertificación, lector CSV (UTF-8 o 1252) |
| `Services/` | Un servicio por módulo (`ISeguimientoService`, `IVistasService`, `ICalidadService`, `IRecertificacionService`) e `IUsuarioActual` |
| `Pages/` | Una Razor Page por pantalla; `Shared/` tiene el layout y los formatos comunes |
| `wwwroot/` | CSS con los tokens de la maqueta y JS mínimo (filtros, confirmaciones, decisiones con fetch) |

Las interfaces de `Services/` son la frontera con la base de datos: cada método corresponde a una
vista `gd.vw_*` o a un procedimiento `gd.sp_*`. Para pasar a SQL Server se escribe otra
implementación de esas interfaces (p. ej. con Dapper) y se registra en `Program.cs` según
`GobiernoDatos:FuenteDatos`. Las páginas no cambian.

## Pantallas

| Ruta | Pantalla | Qué hace hoy |
|---|---|---|
| `/` | Seguimiento semanal | Semana ISO, KPIs, tabla DP × 7 rubros por grupo responsable, filtro por gobierno, búsqueda, exportar CSV para Excel, editar (nombre, gobierno, responsable) o eliminar un DP con todas sus dependencias, con confirmación |
| `/DataProducts` | Data products | Listado y alta de DP |
| `/DataProducts/Detalle` | Detalle de DP | Rubros DP, avance por vista con etapa, bitácora unificada, registrar avance (DP o vista), nota semanal, cerrar DP |
| `/Vistas` | Vistas gobernadas | Pestañas Todas / Nuevas / Fuera de recertificación, uso por vista, datos del extracto (lectura) y de gobierno (editables), mover de etapa con motivo en regresos, objetos BW |
| `/Tareas` | Seguimiento tarea | Tareas de la etapa de cada vista asignadas a un usuario de red: data product, vista, usuario, etapa, asignación, estatus, días transcurridos (se congelan al cerrar) y check para marcarla terminada |
| `/Calidad` | Calidad | Periodo y gobierno, KPIs, por gobierno, por regla y vista fuente, carga del resumen con rechazos; enlace al catálogo de reglas |
| `/Reglas` | Reglas | ABC del catálogo de reglas: listado (activas, dadas de baja, todas; búsqueda), alta y cambio en `/Reglas/Editar` (el código no cambia), baja lógica con motivo (inactiva + etapa "Baja"; conserva su historial de resultados) y reactivación a la etapa anterior. Objetos BW y tipos nuevos se crean; contactos y destinatarios (correos) en `gd.regla_contacto` (CONTACTO / DESTINATARIO; el contacto sustituye al responsable); task flow, hora de ejecución, fecha de producción y comentarios; frecuencia del catálogo `gd.frecuencia_regla`, que se administra en el mismo menú (migración 017); cambios de etapa en `gd.mov_etapa_regla`. Validación en `Domain/DefinicionRegla.cs` |
| `/Recertificacion/Eventos` | Eventos | Carga del extracto → validación → abrir → cerrar, avance por macro área, historial |
| `/Recertificacion/MacroAreas` | Macro áreas | Solo administradores. Gerente único que da el VoBo de las bajas y responsable de infraestructura único que las ejecuta, para todas las macro áreas (búsqueda en Entra ID; migraciones 012 y 015). ABC del catálogo GOBIERNO (extracto HANA) → macro área (`gd.recert_gobierno_macro_area`, migración 010); antes era una tabla fija dentro de la vista HANA. La carga del extracto toma la macro área de aquí (la columna Área Responsable solo si el gobierno no está); un gobierno sin macro área se rechaza. El RESPONSABLE del extracto ya no se usa: los certificadores se administran en Eventos |
| `/Recertificacion/Mi` | Mi recertificación | Accesos de las macro áreas del certificador, Continúa/Baja al momento, comentario obligatorio en Baja, acción masiva, confirmación |
| `/Recertificacion/Bajas` | Bajas y evidencia | Filtro por macro área; VoBo del gerente por macro área (solicitud, estatus y "Volver a solicitar"); asignación a infraestructura en el panel de bajas (número de cambio por macro área; correo con las bajas en Excel al responsable único; migraciones 014 y 015); registrar ejecución con ticket (prellenado con el cambio) solo con VoBo autorizado y asignación, y "Quitar ticket" con motivo (queda como anulado en la traza; migraciones 011 a 013, ver `docs/06_power_automate_vobo.md`); traza de auditoría; exportar evidencia CSV |
| `/Auditoria` | Auditoría | Solo administradores. Accesos de recertificación (un evento o todos) con las columnas de la consulta HANA en su orden, sin GOBIERNO, RESPONSABLE ni COMENTARIOS (AREA, CONTENT, ROL, VISTA, USUARIO, NAME, AREARESPONSABLE, OKBAJA vacía, ACCESOS); filtros por área responsable y texto; paginado de 100; exportar a Excel (.xlsx, respeta los filtros) y exportar por macro área (.zip con un .xlsx por macro área, `Data/EscritorExcel.cs`). La migración 008 guarda además GOBIERNO y RESPONSABLE por acceso |
| `/Auditoria/Respuestas` | Auditoría · Evidencia de la respuesta | Solo administradores. Por acceso: AREARESPONSABLE, usuario, nombre, rol, vista, OKBAJA (OK = continúa, BAJA), COMENTARIOS, quién respondió y cuándo; resumen por macro área (OK, BAJA, sin respuesta, avance); filtros por evento, macro área, respuesta y texto; exportar a Excel (hojas Respuestas y Resumen por macro área, con los filtros) |
| `/Auditoria/Notificaciones` | Auditoría · Notificaciones | Solo administradores. Evidencia de la solicitud de validación a los responsables de área: por macro área si se notificó (recordatorios de Eventos), envíos, último envío y descarga del correo `.eml` tal como salió (migración 016); detalle de cada envío; VoBo del gerente por macro área con su constancia en correo .eml generada por el portal (solicitud, envío por Power Automate, respuesta y la copia de las bajas que se pidió autorizar, migración 018); correo de cierre del responsable de infraestructura por macro área: cuando todas las bajas de la macro área tienen ticket aparece la columna para que el administrador suba el .eml o .msg con el que infraestructura avisa que terminó (máximo 10 MB; se conservan todos, migración 019) y se descarga con su nombre original; "Descargar evidencia" (.zip con resumen en Excel, los correos, las constancias de VoBo en .eml y los correos de cierre de infraestructura) |

## Reglas implementadas (con prueba)

- Avance de rubros de vista "el último evento manda" (`Domain/ReglaAvanceVista.cs`, espejo de `gd.vw_avance_vista_semana`, migración 005).
- Cualquier rubro de vista se captura en cualquier etapa; un regreso de etapa exige motivo.
- % DP = promedio ponderado de los 7 rubros; sin dato = 0. Reproduce el Avance de las 6 filas del Excel.
- Una vista tiene un solo DP vigente; reasignar cierra la vigencia anterior.
- Calidad agregada como Σ válidos ÷ Σ evaluados; validaciones de `sp_cargar_resumen_calidad`.
- Recertificación: comentario obligatorio en Baja, decisiones insert-only (vale la última), bloqueo con el evento cerrado, pendientes reportados al cierre, 1 rol = 1 vista en la carga, el evento no abre con rechazos, seguridad por fila por macro área.

## Extracto de recertificación desde HANA (Nuevo evento)

- **Eventos → Nuevo evento** (nombre, fecha del extracto —hoy si se deja vacía— y fecha límite) ejecuta por ODBC la
  consulta de `_SYS_BIC."MABE.TI.ANALYTICS/CONT_TI_RECERTIFICACION"` (`Services/FuenteExtracto.cs`) y crea el evento en
  borrador. Solo está disponible si no hay un evento en borrador o abierto; para recargar un borrador (p. ej. tras
  corregir datos en HANA) se descarta y se vuelve a crear.
- Mapeo: `NAME`→nombre, `AREARESPONSABLE`→macro área, `RESPONSABLE`→certificador (por nombre: hay que asignarle
  su usuario de red en el panel), `ACCESOS` nulo → 0. `GOBIERNO`, `OKBAJA` y `COMENTARIOS` no se cargan.
- La carga usa SqlBulkCopy al staging y `sp_cargar_extracto_recertificacion` (≈25 s para ~5,700 filas).
- Conexión: `ConnectionStrings:Hana` (`DSN=Hana;UID=...;PWD=...`) en user-secrets / `web.config` del servidor.
  El servidor que ejecuta el portal necesita el **DSN de sistema `Hana` de 64 bits** (driver HDBODBC).
- La regla 1 rol = 1 vista se mantiene: las filas que la rompen se rechazan y el evento no abre hasta corregirlas en HANA.
  En la primera lectura (2026-10-16): `CON_CSC_CXP` (3 vistas), `GE_FIN_VENTAS_ANALISTA` (2 vistas) y la vista
  `...SERVICIO_AL_CTE/MEXICO_VENTASYCUOTAS_SERVIPLUS0POLIZAS` (2 roles) → 7 filas rechazadas.
- En desarrollo (fuente `Muestras`) el botón usa `data/muestras/recertificacion_extracto_muestra.csv`.

## Importación del Excel de seguimiento (Seguimiento semanal → Cargar archivo)

- Archivo de Excel semanal **.xlsx** (también CSV; `Data/LectorExcel.cs` con ClosedXML lee cada celda como se ve en Excel,
  p. ej. 100% y no 1; usa la hoja que tenga el encabezado «Data Product»), interpretado por `Domain/ImportacionSeguimiento.cs`: detecta el encabezado aunque haya renglones de grupo y toma
  la primera aparición de cada columna (Glosarios, Catalogado, DF, Arq., Linaje, Perfil, Reglas de Calidad, Vistas,
  Comentarios). "Avance" no se importa: el portal lo calcula.
- Crea los data products que no existen (gobierno del archivo; da de alta gobiernos nuevos), actualiza gobierno y vistas
  reportadas, registra solo los avances que cambiaron y separa los comentarios «42/26 - …» en notas por semana
  (marcas inválidas como 38/52 quedan como texto). Reimportar el mismo archivo no duplica nada.
- Los rubros de vista se guardan como **avance reportado** del DP (migración 007): cuentan mientras el DP no tenga vistas
  asignadas; con vistas, manda el promedio de sus vistas. Resuelve la decisión abierta #1.
- En Seguimiento semanal, cada data product muestra la nota de la semana y un desplegable "Comentarios por semana"; la
  exportación a Excel incluye los comentarios en el mismo formato del archivo («42/26 - … 41/26 - …»).
- Primera importación (2026-10-19, corte 42/26): 54 DP, 213 avances, 167 notas; el avance calculado coincide con el
  Excel en 53 de 54 (en «6 Reglas de Calidad» el Excel trae 80% capturado a mano).

## Tareas por etapa (migración 004)

- La tarea de la etapa actual de una vista se asigna a un usuario de red validado contra AD: al **mover de etapa**
  (campo opcional) o desde el bloque **Tarea de la etapa actual** en Vistas gobernadas. La etapa final no tiene tarea.
- Una sola tarea abierta por vista (índice único). Estatus: Abierta → Terminada (check en Seguimiento tarea; guarda
  fecha y usuario) o Cancelada (reasignación o cambio de etapa, con motivo).
- Días transcurridos = desde la asignación hasta hoy; al cerrarse se congelan. `gd.vw_seguimiento_tarea` lo expone para Power BI.
- **Decisión a confirmar:** al cambiar de etapa con la tarea abierta, se cancela ("Cambio de etapa"), no se da por terminada.

## Recordatorios por correo (migración 009)

- En **Eventos** (evento abierto): "Recordar a pendientes" (todas las macro áreas) o "Recordar" por macro área; ambos piden confirmación.
- Un correo por certificador vigente (titular o delegado) con pendientes, aunque certifique varias macro áreas: sus macro áreas con pendientes, la fecha límite y la liga a Mi recertificación (`Correo:UrlPortal`).
- Destinatario: `gd.responsable.correo` si está capturado; si no, el correo de Active Directory del **usuario de red** del certificador. Sin usuario de red ni correo no se envía y se reporta.
- Cada intento queda en `gd.recert_recordatorio` (a quién, correo, macro áreas, pendientes, enviado o error, quién lo envió, fecha) y se muestra en Eventos → "Recordatorios enviados".
- "Enviar correo de prueba" (Eventos) manda un correo al administrador que lo pulsa, para validar el relay.
- Configuración (`GobiernoDatos:Correo` en `appsettings.Production.json`): relay `relay.mabenet.corpmabe.com:25`, cuenta `mabenet\sacmexalt`, remitente `administrador.analytics@mabe.com.mx`. La contraseña **no** está en el repositorio: variable `GobiernoDatos__Correo__Contrasena` en el `web.config` del servidor (user-secrets en local). Sin `Correo:Servidor` fuera de producción el envío es simulado (`EnviadorSimulado`).

## Búsqueda de certificadores en Entra ID

- Eventos → Certificadores → "Asignar": al escribir nombre, correo o usuario de red, el portal busca en Entra ID (Microsoft Graph, `Services/BuscadorUsuarios.cs`) y muestra nombre, usuario de red, correo, puesto y área. Al elegir, el campo queda con `MABENET\usuario` y se asigna como siempre (se valida contra el AD local).
- Solo aparecen cuentas activas con cuenta de Windows (`onPremisesSamAccountName`); el dominio NetBIOS sale de `onPremisesDomainName` (`mabenet.corpmabe.com` → `MABENET`). Las cuentas solo de nube o externas no pueden entrar al portal con autenticación de Windows.
- Token por usuario y contraseña de la cuenta de servicio (ROPC) sobre la aplicación registrada: `GobiernoDatos:Entra` (TenantId, ClientId, Usuario) en `appsettings.Production.json`; la contraseña en la variable `GobiernoDatos__Entra__Contrasena` del `web.config`. Si la cuenta llega a requerir MFA, ROPC deja de funcionar: habría que cambiar a un secreto de aplicación con permiso `User.Read.All` de aplicación.
- Requiere salida del servidor a `login.microsoftonline.com` y `graph.microsoft.com`. Si la búsqueda falla, se puede escribir el usuario de red a mano.

## Decisiones que tomé y conviene confirmar

1. **Rubros de vista de DP sin vistas asignadas** (resuelta con la migración 007: avance reportado). El Excel trae Glosario, Linaje, Perfil y Reglas a nivel DP, pero no dice qué vistas tiene cada DP. Los migré como valor inicial del DP y se usan solo mientras el DP no tenga vistas asignadas; al asignar la primera, el cálculo pasa a ser el promedio de sus vistas. Alternativa: asignar las vistas reales antes de migrar.
2. **Confirmación de la macro área.** La maqueta tiene el botón "Confirmar certificación", pero el ER v3 no tiene dónde guardarla. Se agregó `gd.recert_confirmacion` en `db/migrations/002_portal.sql` (fuera del ER, pendiente de validar); una decisión posterior la invalida.
3. **Un solo evento activo a la vez.** No se puede crear un evento mientras otro esté en borrador o abierto.
4. **Filas rechazadas.** Se resuelven corrigiendo el extracto, descartando el borrador y volviendo a cargarlo. La sincronización del inventario ocurre al cargar (antes de abrir).
5. **Bajas ejecutadas.** Una vez registrada la ejecución, la decisión de ese acceso ya no puede cambiar.
6. **Recordatorios.** Se envían a mano desde Eventos (no hay envío automático); ver "Recordatorios por correo".

## Pendiente

- Autenticación corporativa (Windows / Entra ID): sustituir `UsuarioDemo` por una implementación que lea `HttpContext.User` y mapee el usuario a `responsable`. Los roles del portal son Equipo, Administrador y Certificador.
- Los certificadores por macro área se administran en Eventos y el catálogo de reglas en el menú Reglas.
