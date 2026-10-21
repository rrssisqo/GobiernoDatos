# 06 · VoBo del gerente con Power Automate

Antes de que TI registre el ticket de una baja, **el gerente** debe autorizar las bajas que decidió el certificador. Es una sola persona para todas las macro áreas; la solicitud se hace por macro área, cuando su certificador termina. El portal crea la solicitud. **Power Automate** la envía al gerente como aprobación y registra su respuesta. El portal muestra el resultado y solo permite capturar el ticket cuando la solicitud está **autorizada**.

Objetos de la base `GobiernoDatos` (SRVMABE385), creados por `db/migrations/011_recert_vobo_gerente.sql` y `012_recert_gerente_unico.sql`.

## Cómo se crea una solicitud

1. En el portal, **Recertificación → Macro áreas → Gerente que autoriza las bajas**: se asigna el gerente. Se busca en Entra ID y se guardan su usuario de red, nombre y correo (`gd.recert_gerente`). La solicitud guarda el gerente vigente al momento de crearla.
2. El certificador termina su macro área (sin accesos por decidir) y la confirma. Si hay bajas y hay gerente asignado, se crea la solicitud automáticamente.
3. El administrador también puede pedirla, o volver a pedirla, desde **Bajas y evidencia → VoBo del gerente por macro área → "Solicitar VoBo"**. También se permite con el evento ya cerrado.

## Estados de `gd.recert_vobo.estatus`

| Estatus | Significado | Quién lo pone |
|---|---|---|
| `PENDIENTE` | Creada, falta enviarla al gerente | Portal |
| `ENVIADA` | Power Automate ya la envió; se espera la respuesta | Power Automate (`sp_vobo_marcar_enviada`) |
| `AUTORIZADA` | El gerente autorizó: TI ya puede registrar los tickets | Power Automate (`sp_vobo_responder`) |
| `RECHAZADA` | El gerente rechazó; se puede volver a pedir | Power Automate (`sp_vobo_responder`) |
| `CANCELADA` | Cambió una decisión de la macro área después de pedirla, o se reemplazó por otra solicitud | Base de datos (trigger) o portal |

Solo hay una solicitud activa (`PENDIENTE`, `ENVIADA` o `AUTORIZADA`) por evento y macro área. Si la solicitud ya se canceló, una respuesta del gerente **no se registra**: el procedimiento devuelve `registrada = 0` y un mensaje, sin marcar error.

## Lo que usa el flujo

| Objeto | Para qué |
|---|---|
| `gd.vw_recert_vobo_por_enviar` | Solicitudes por enviar. Columnas: `vobo_id`, `evento`, `macro_area`, `gerente_nombre`, `gerente_correo`, `bajas`, `solicitado_por`, `fecha_solicitud`. |
| `gd.vw_recert_vobo_detalle` | Bajas de cada solicitud (filtrar por `vobo_id`): `usuario`, `nombre_usuario`, `vista`, `rol`, `comentario`, `decidido_por`, `fecha_decision`. |
| `EXEC gd.sp_vobo_marcar_enviada @vobo_id` | Marca la solicitud como enviada para que no se envíe dos veces. Devuelve `actualizada` (1/0). |
| `EXEC gd.sp_vobo_responder @vobo_id, @autorizada, @respondido_por, @comentario` | Registra la respuesta. `@autorizada` = 1 o 0; `@respondido_por` = correo de quien respondió. Devuelve `registrada` y `mensaje`. |

## Flujo sugerido

Requiere la **puerta de enlace de datos local** (on-premises data gateway) con acceso a SRVMABE385, y una conexión de SQL Server con permisos limitados (`db/mantenimiento/permisos_power_automate.sql`).

1. **Desencadenador**: *Periodicidad* (Recurrence), cada 15 minutos.
2. **SQL Server – Obtener filas (V2)** de `gd.vw_recert_vobo_por_enviar`.
3. **Aplicar a cada uno** (Apply to each) por fila:
   1. **SQL Server – Ejecutar procedimiento almacenado (V2)** `gd.sp_vobo_marcar_enviada` con `vobo_id`. Si `actualizada` = 0, terminar esa iteración (otra ejecución ya la tomó).
   2. **SQL Server – Obtener filas (V2)** de `gd.vw_recert_vobo_detalle` con el filtro `vobo_id eq <vobo_id>`.
   3. **Crear tabla HTML** con `nombre_usuario`, `usuario`, `vista`, `rol` y `comentario`.
   4. **Aprobaciones – Iniciar una aprobación y esperar** (*Aprobar/Rechazar – Primero en responder*):
      - Asignado a: `gerente_correo`.
      - Título: `VoBo de bajas · <macro_area> · evento <evento> (<bajas> bajas)`.
      - Detalles: la tabla HTML y una nota de que, al aprobar, TI retirará esos roles.
   5. **SQL Server – Ejecutar procedimiento almacenado (V2)** `gd.sp_vobo_responder`:
      - `vobo_id`: el de la fila.
      - `autorizada`: `if(equals(outputs('Iniciar_una_aprobación')?['body/outcome'], 'Approve'), 1, 0)`.
      - `respondido_por`: el correo del primer elemento de `responses` (`responder/email`).
      - `comentario`: el comentario del primer elemento de `responses`.

La aprobación puede esperar días; Power Automate mantiene la ejecución abierta hasta 30 días. Si el gerente no responde en ese plazo, la solicitud se queda en `ENVIADA`. En ese caso, desde el portal se usa "Volver a solicitar", que cancela la anterior y crea una nueva `PENDIENTE`.

## Probar sin Power Automate

```sql
SELECT * FROM gd.vw_recert_vobo_por_enviar;
EXEC gd.sp_vobo_marcar_enviada @vobo_id = 1;
EXEC gd.sp_vobo_responder @vobo_id = 1, @autorizada = 1, @respondido_por = N'gerente@mabe.com.mx', @comentario = N'Prueba';
```

En el portal, la macro área cambia a **Autorizado** y aparece el campo del ticket en sus bajas.
