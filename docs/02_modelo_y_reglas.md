# 02 · Modelo de datos y reglas de negocio

Ver ER: `Gobierno_Datos_ER_v3.pdf` (fuente Mermaid: `modelo_er_v3.mmd`).
Script implementado: `../db/gobierno_datos_ddl_v2.sql` (secciones 1–10).

## Dominios
| Dominio | Tablas |
|---|---|
| Catálogos | gobierno, subdominio, responsable, nivel_seguridad, etapa_vista, rubro, motivo, tipo_regla, etapa_regla, semana |
| Núcleo | data_product, vista_gobernada, dp_vista |
| Flujo y avance | mov_etapa_vista, mov_avance_vista, mov_avance_dp, log_semanal |
| Calidad | objeto_fuente, vista_objeto, regla, regla_objeto, regla_contacto, regla_query_hist, mov_etapa_regla, resumen_calidad, stg_resumen_calidad, carga_rechazo |
| Recertificación (v3) | macro_area, macro_area_certificador, usuario_hana, rol_hana, recert_evento, recert_acceso, recert_decision, recert_baja_ejecucion, recert_carga_rechazo, vista_uso |

## Etapas de vista
| Orden | Etapa | Rubro que alimenta |
|---|---|---|
| 0 | Por Iniciar | — |
| 1 | Escaneo Inicial (lanzar escaneo de metadatos) | — |
| 2 | Asignación Stake Holders | — |
| 3 | Generación Glosarios Copilot | Glosario |
| 4 | Import de Glosarios | Glosario |
| 5 | Reescaneo | (dentro del rango de Glosario) |
| 6 | Certificación Glosarios | Glosario |
| 7 | Asignación Relaciones | Linaje |
| 8 | Linaje | Linaje |
| 9 | Data Product (documentar la vista en el DP; NO es la asignación) | — |
| 10 | Perfilado | Perfilado |
| 11 | Calidad de Datos | Reglas |
| 12 | Completada | — |

## Regla de cálculo del avance por vista: "el último evento manda"
Sustituye a la opción C desde el 2026-10-15 (migración 005; pendiente de validar con Memo). El % de cualquier
rubro de vista se puede capturar en cualquier etapa. Por rubro, al cierre de la semana vale el último evento:

| Evento | % | Estado |
|---|---|---|
| Captura del rubro (cualquier etapa) | lo capturado | lo capturado |
| La vista cruza hacia adelante el rango del rubro | 100 | CERRADO |
| La vista vuelve a entrar al rango después de haberlo cerrado (reproceso) | 0 | EN_CURSO |
| Sin eventos y la vista dentro del rango / fuera del rango | 0 | EN_CURSO / NO_INICIADO |

Entrar al rango por primera vez no borra capturas previas; moverse entre etapas posteriores tampoco.
Orden: fecha y luego registro; en empate gana la captura. Implementado en `gd.vw_avance_vista_semana`
(columna `origen` = ETAPA o CAPTURA) y en C# `Domain/ReglaAvanceVista.cs` (almacén en memoria).

## Calidad
- Cadena: resumen_calidad → regla → regla_objeto → objeto_fuente → vista_objeto → vista → dp_vista → DP.
- Una regla cuenta para una vista si evalúa al menos un objeto vigente de esa vista en el periodo.
- Vistas: `vw_calidad_regla` (compañía/gobierno), `vw_calidad_vista`, `vw_calidad_dp`.
- Power BI: `DIVIDE(SUM(reg_validos), SUM(reg_evaluados))`; no sumar niveles entre sí.

## Recertificación (v3)
- `recert_acceso` UK (evento, usuario, rol); vista vía `rol_hana.vista_id` (UNIQUE).
- `recert_decision`: insert-only; la vigente es la última por acceso. CHECK/proc: comentario
  obligatorio si BAJA. Trigger: rechazar inserts si el evento está CERRADO.
- Estatus de evento: BORRADOR → (carga y validación) → ABIERTO → CERRADO.
- Seguridad por fila: un certificador ve accesos de sus macro áreas (titular o delegado vigente).

## Validaciones (vw_validaciones y futuras)
Vista con gobierno ≠ al de su DP · DP activo sin vistas · DP cerrado con vista reabierta ·
regla STAGE=SI sin resumen del último periodo · regla sin objetos · objeto con reglas sin vista ·
regla/vista sin responsable · (v3) vista de DP fuera de la última recertificación o con 0 accesos ·
rol con más de una vista · vista con más de un rol.
