/* =====================================================================
   Carga inicial con data/muestras sobre el esquema v2.
   Idempotente: cada bloque inserta solo lo que no existe (por llave natural).
   Mapeos: docs/01_contexto_y_fuentes.md.

   Fuera de esta carga:
     - Rubros de vista del Excel (Glosario, Linaje, Perfil, Reglas) a nivel DP:
       el v2 solo admite rubros de nivel DP en mov_avance_dp (ver docs/05, decisión 1).
     - Extracto de recertificación: requiere las tablas de la migración v3.
   Fecha de migración = cierre de la semana 39/26 (corte del Excel): 2026-09-27.
   ===================================================================== */
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @fecha DATE = '2026-09-27';
DECLARE @usuario NVARCHAR(100) = N'migración';

/* ---- Gobiernos: Excel (CDV, Finanzas, Comercial, Industrial), SharePoint "Área" y GOBIERNO de reglas.
        Catálogo definitivo pendiente (docs/04, decisión abierta 3). ---- */
INSERT INTO gd.gobierno (nombre)
SELECT g.nombre FROM (VALUES (N'CDV'), (N'Finanzas'), (N'Comercial'), (N'Industrial'), (N'Nominas'), (N'RRHH'),
                             (N'Datos Maestros'), (N'Cadena de Valor')) AS g(nombre)
WHERE NOT EXISTS (SELECT 1 FROM gd.gobierno x WHERE x.nombre = g.nombre);

/* ---- Subdominios (SharePoint SubDominioPadre por Área) ---- */
INSERT INTO gd.subdominio (gobierno_id, nombre)
SELECT g.gobierno_id, s.nombre
FROM (VALUES (N'Industrial', N'Gestión de Activos'), (N'Nominas', N'Costo Nómina'),
             (N'RRHH', N'HeadCount'), (N'Industrial', N'OEE')) AS s(gobierno, nombre)
JOIN gd.gobierno g ON g.nombre = s.gobierno
WHERE NOT EXISTS (SELECT 1 FROM gd.subdominio x WHERE x.gobierno_id = g.gobierno_id AND x.nombre = s.nombre);

/* ---- Responsables (SharePoint Vistas y Reglas) ---- */
INSERT INTO gd.responsable (nombre)
SELECT r.nombre FROM (VALUES (N'Eli Villalpando'), (N'Fernando Sosa'), (N'Andres Díaz'), (N'Manuel Lopez')) AS r(nombre)
WHERE NOT EXISTS (SELECT 1 FROM gd.responsable x WHERE x.nombre = r.nombre);

/* ---- Data products (Excel de seguimiento 39/26) ---- */
INSERT INTO gd.data_product (nombre, gobierno_id, estatus, fecha_alta)
SELECT d.nombre, g.gobierno_id, 'EN_CURSO', @fecha
FROM (VALUES (N'CDV',        N'MOM - Tránsitos - Entrega Entrante'),
             (N'CDV',        N'MOM - Demand Variation'),
             (N'Finanzas',   N'Auxiliar de Inventarios'),
             (N'Comercial',  N'Importaciones'),
             (N'Finanzas',   N'Costo Tipo'),
             (N'Industrial', N'Rotación y Ausentismo')) AS d(gobierno, nombre)
JOIN gd.gobierno g ON g.nombre = d.gobierno
WHERE NOT EXISTS (SELECT 1 FROM gd.data_product x WHERE x.nombre = d.nombre);

/* ---- Avance de rubros de nivel DP (Catalogado=2, Diseño Funcional=3, Arquitectura=4). Vacío = sin dato. ---- */
INSERT INTO gd.mov_avance_dp (dp_id, rubro_id, fecha, pct, estado, comentario, usuario)
SELECT d.dp_id, a.rubro_id, @fecha, a.pct,
       CASE WHEN a.pct = 0 THEN 'NO_INICIADO' WHEN a.pct = 100 THEN 'CERRADO' ELSE 'EN_CURSO' END,
       N'Migración Excel 39/26', @usuario
FROM (VALUES (N'MOM - Tránsitos - Entrega Entrante', 2,  98), (N'MOM - Tránsitos - Entrega Entrante', 3, 100),
             (N'MOM - Demand Variation',             2,  20),
             (N'Auxiliar de Inventarios',            2, 100), (N'Auxiliar de Inventarios', 3, 100), (N'Auxiliar de Inventarios', 4, 100),
             (N'Importaciones',                      2, 100), (N'Importaciones', 3, 100),
             (N'Costo Tipo',                         2, 100), (N'Costo Tipo', 3, 100), (N'Costo Tipo', 4, 50),
             (N'Rotación y Ausentismo',              2,  35), (N'Rotación y Ausentismo', 3, 100)) AS a(dp, rubro_id, pct)
JOIN gd.data_product d ON d.nombre = a.dp
WHERE NOT EXISTS (SELECT 1 FROM gd.mov_avance_dp x WHERE x.dp_id = d.dp_id AND x.rubro_id = a.rubro_id AND x.usuario = @usuario);

/* ---- Bitácora: comentarios del Excel separados por semana ("39/26 - ... 38/26 - ...") ---- */
INSERT INTO gd.log_semanal (dp_id, semana_id, comentario, usuario, fecha_registro)
SELECT d.dp_id, n.semana_id, n.comentario, @usuario, s.fecha_fin
FROM (VALUES
    (N'MOM - Tránsitos - Entrega Entrante', 202639, N'Se cierran Glosarios al 100%, se cierra catálogado, pendiente Doc. Arqu… (truncado en captura)'),
    (N'MOM - Demand Variation',             202639, N'Se inicia Catalogado del Data Product. Se cierra Linaje del Dashboard. (truncado)'),
    (N'Auxiliar de Inventarios',            202639, N'Se concluye Catalogado de Data Product.'),
    (N'Auxiliar de Inventarios',            202638, N'Se confirman linajes y… (truncado)'),
    (N'Importaciones',                      202639, N'Se cierran glosarios, Catalogado, Diseños Funcionales.'),
    (N'Importaciones',                      202638, N'Se Agreg… (truncado)'),
    (N'Costo Tipo',                         202639, N'Se avanza documento de Arquitectura, estamos en espera de retro de g… (truncado)'),
    (N'Rotación y Ausentismo',              202639, N'Se crean perfilados de 3 de las vistas, y se revisan con Abi, sin reglas ha… (truncado)')
) AS n(dp, semana_id, comentario)
JOIN gd.data_product d ON d.nombre = n.dp
JOIN gd.semana s ON s.semana_id = n.semana_id
WHERE NOT EXISTS (SELECT 1 FROM gd.log_semanal x WHERE x.dp_id = d.dp_id AND x.semana_id = n.semana_id AND x.usuario = @usuario);

/* ---- Vistas gobernadas (SharePoint "Vistas HANA Gobernadas"); ruta técnica normalizada PAQUETE/VISTA ---- */
DECLARE @vistas TABLE (ruta NVARCHAR(400), nombre NVARCHAR(200), gobierno NVARCHAR(100), subdominio NVARCHAR(100),
                       responsable NVARCHAR(150), nivel TINYINT, etapa NVARCHAR(60));
INSERT INTO @vistas VALUES
 (N'MABE.INDUSTRIAL.MANTENIMIENTO/IND_CON_MANTENIMIENTO_ORDENES_TRABAJO', N'Ordenes Trabajo Mantenimiento', N'Industrial', N'Gestión de Activos', N'Eli Villalpando', 1, N'Completada'),
 (N'MABE.CSC.NOMINAS.COSTONOMINA/CONT_NOMINAS_COSTONOMINA',               N'Costo de Nomina',               N'Nominas',    N'Costo Nómina',       N'Fernando Sosa',   2, N'Certificación Glosarios'),
 (N'MABE.RH.COMPENSACIONES.HEADCOUNT/CONT_RH_HEADCOUNT_PBI',              N'Head Count PBI',                N'RRHH',       N'HeadCount',          N'Andres Díaz',     1, N'Reescaneo'),
 (N'MABE.INDUSTRIAL.OEE/CONT_IND_OEE_CONSPRODUCCIONXHORA',                N'Consolidado Producción por Hora', N'Industrial', N'OEE',              N'Eli Villalpando', 1, N'Escaneo Inicial'),
 (N'MABE.INDUSTRIAL.OEE/CONT_IND_OEE_PARADAS',                            N'OEE Paradas',                   N'Industrial', N'OEE',                N'Eli Villalpando', 1, N'Escaneo Inicial'),
 (N'MABE.INDUSTRIAL.OEE/CONT_IND_OEE_CONSTIEMPOSXHORA',                   N'Consolidado Tiempos por Hora',  N'Industrial', N'OEE',                N'Eli Villalpando', 1, N'Escaneo Inicial'),
 (N'MABE.INDUSTRIAL.OEE/CONT_IND_OEE_PRODUCCION',                         N'OEE Producción',                N'Industrial', N'OEE',                N'Eli Villalpando', 1, N'Escaneo Inicial');

INSERT INTO gd.vista_gobernada (ruta_tecnica, nombre_negocio, gobierno_id, subdominio_id, responsable_id, nivel_seguridad_id, fecha_alta)
SELECT v.ruta, v.nombre, g.gobierno_id, sd.subdominio_id, r.responsable_id, v.nivel, @fecha
FROM @vistas v
JOIN gd.gobierno g ON g.nombre = v.gobierno
LEFT JOIN gd.subdominio sd ON sd.gobierno_id = g.gobierno_id AND sd.nombre = v.subdominio
LEFT JOIN gd.responsable r ON r.nombre = v.responsable
WHERE NOT EXISTS (SELECT 1 FROM gd.vista_gobernada x WHERE x.ruta_tecnica = v.ruta);

-- "Estatus" de SharePoint = etapa actual de la vista (movimiento inicial, motivo Flujo inicial).
INSERT INTO gd.mov_etapa_vista (vista_id, etapa_id, fecha, motivo_id, es_regreso, comentario, usuario)
SELECT vg.vista_id, e.etapa_id, @fecha, 1, 0, N'Etapa migrada de SharePoint', @usuario
FROM @vistas v
JOIN gd.vista_gobernada vg ON vg.ruta_tecnica = v.ruta
JOIN gd.etapa_vista e ON e.nombre = v.etapa
WHERE NOT EXISTS (SELECT 1 FROM gd.mov_etapa_vista x WHERE x.vista_id = vg.vista_id);

/* ---- Tipos de regla (Rl_Null y Rl_Nulos son el mismo tipo) ---- */
INSERT INTO gd.tipo_regla (nombre, codigo_informatica)
SELECT t.nombre, t.codigo FROM (VALUES (N'Nulos', N'Rl_Nulos'), (N'Catálogo de plantas', N'Rl_Cat_Plantas')) AS t(nombre, codigo)
WHERE NOT EXISTS (SELECT 1 FROM gd.tipo_regla x WHERE x.nombre = t.nombre);

/* ---- Objetos BW (NOMBRE_CUBOS, separados por ';') ---- */
INSERT INTO gd.objeto_fuente (nombre)
SELECT o.nombre FROM (VALUES (N'PMATERIAL'), (N'ZMM_O09'), (N'PVEN_COMPC'), (N'ZSD_O21'), (N'MAT_PLANT')) AS o(nombre)
WHERE NOT EXISTS (SELECT 1 FROM gd.objeto_fuente x WHERE x.nombre = o.nombre);

/* ---- Reglas (SharePoint "Reglas") ---- */
DECLARE @reglas TABLE (codigo VARCHAR(20), tipo NVARCHAR(80), descripcion NVARCHAR(300), funcional NVARCHAR(2000),
                       proposito NVARCHAR(2000), gobierno NVARCHAR(100), responsable NVARCHAR(150), campos NVARCHAR(400),
                       tabla_stg NVARCHAR(128), mapping NVARCHAR(128), no_csv INT, nombre_csv NVARCHAR(260), asunto NVARCHAR(200),
                       pase_prd DATE, operacion_prd DATE);
INSERT INTO @reglas VALUES
 ('RL_1_1', N'Nulos', N'Gestión de Materiales',
  N'Regla de validación que incluya todos los campos que no deberían tener registros nulos en MATL_TYPE y MATL_GROUP de PMATERIAL. Generar un correo con todos los registros encontrados con error (nulos). Considerar todos los registros.',
  N'Regla de negocio encargada de agrupar materiales para dar visibilidad sobre los mismos. Validando que los campos Tipo de Material y Grupo de Material no sean vacíos.',
  N'Datos Maestros', N'Manuel Lopez', N'MATL_TYPE; MATL_GROUP', N'STG_0MATERIAL', N'm_DQ_PMATERIAL', 1, N'01PMATERIAL_RL_01.csv',
  N'Dato Maestro de Material', '2024-06-17', NULL),
 ('RL_1_2', N'Nulos', N'Libro de pedidos (contratos de compra, proveedor, material y fecha inicio y fin)',
  N'Regla 18: ZMM_O09 no debe existir nulo en _0VENDOR y _0PLANT_COMP (planta suministradora) en el mismo momento.',
  N'Regla de negocio encargada de validar contratos llenados de manera completa y vigentes para la generación de pedidos.',
  N'Industrial', NULL, N'_0VENDOR;_0PLANT_COMP', N'STG_ZMM_O09', NULL, 2, N'02ZMM_O09_RL_01.csv',
  N'Libro de Pedidos', '2024-06-17', '2024-08-08'),
 ('RL_2_1', N'Nulos', N'Dato maestro proveedor por sociedad',
  N'El campo COMP_CODE del cubo PVEN_COMPC no puede ser nulo.',
  N'Regla de negocio encargada de validar la relación de proveedor sociedad.',
  N'Datos Maestros', NULL, N'COMP_CODE', N'STG_PVEN_COMPC', N'm_DQ_PVEN_COMPC', 3, N'03PVEN_COMPC_RL_02.csv',
  N'Dato Maestro Proveedor Sociedad', '2024-06-17', '2024-08-08'),
 ('RL_2_2', N'Nulos', N'Datos transaccionales de Ordenes',
  N'El campo _0ACCNT_ASGN del cubo Prf_ZSD_O21 no puede viajar vacío, caso contrario levantar una alerta.',
  N'Regla de negocio para grupo de imputación para clientes.',
  N'Cadena de Valor', NULL, N'_0ACCNT_ASGN', N'STG_ZSD_O21', N'm_DQ_ZSD_O21', 4, N'04ZSD_O21_RL_02.csv',
  N'Datos de Pedidos - Detalle', '2024-06-17', '2024-08-08'),
 ('RL_3_1', N'Catálogo de plantas', N'Relación entre plantas y materiales Gestión de materiales',
  N'Mat_Plant no debe existir registros sin centro (plant) en los tipos de material ZMAB, ZCKD, ZIKD.',
  N'Regla de negocio encargada de validar las relaciones de los centros y los materiales para los tipos de Material ZMAB, ZCKD y ZIKD.',
  N'Datos Maestros', NULL, N'PLANT;MATL_TYPE', N'STG_0MAT_PLANT', N'm_DQ_MAT_PLANT_PMATERIAL', 5, N'05MAT_PLANT_RL_3_1.csv',
  N'Dato Maestro Material Planta', '2024-06-17', '2024-08-08'),
 ('RL_3_2', N'Catálogo de plantas', N'Relación entre plantas y materiales Gestión de materiales',
  N'Mat_Plant no puede tener categoría de valoración (_BIC_ZCAT_VALO) en nulo en los tipos de material ZMAB, ZCKD, ZIKD.',
  N'Regla de negocio que se encarga de validar la afectación de inventarios financieros y días de inventario.',
  N'Datos Maestros', NULL, N'_BIC_ZCAT_VALO; MATL_TYPE', N'STG_0MAT_PLANT', N'm_DQ_MAT_PLANT_PMATERIAL', 6, N'06MAT_PLANT_RL_3_2.csv',
  N'Dato Maestro Material Planta', '2024-06-17', '2024-08-08');

INSERT INTO gd.regla (codigo, tipo_regla_id, descripcion, regla_funcional, proposito_negocio, dimension_dq, gobierno_id,
                      responsable_id, campos_evaluados, tabla_stg, mapping, ruta, no_csv, nombre_csv, escribe_log, correo_asunto)
SELECT r.codigo, t.tipo_regla_id, r.descripcion, r.funcional, r.proposito, 'VALIDEZ', g.gobierno_id,
       rs.responsable_id, r.campos, r.tabla_stg, r.mapping, N'MABE/06. Mapas de Reglas de Calidad Deltas', r.no_csv, r.nombre_csv, 1, r.asunto
FROM @reglas r
JOIN gd.tipo_regla t ON t.nombre = r.tipo
JOIN gd.gobierno g ON g.nombre = r.gobierno
LEFT JOIN gd.responsable rs ON rs.nombre = r.responsable
WHERE NOT EXISTS (SELECT 1 FROM gd.regla x WHERE x.codigo = r.codigo);

INSERT INTO gd.regla_objeto (regla_id, objeto_id)
SELECT rg.regla_id, o.objeto_id
FROM (VALUES ('RL_1_1', N'PMATERIAL'), ('RL_1_2', N'ZMM_O09'), ('RL_2_1', N'PVEN_COMPC'), ('RL_2_2', N'ZSD_O21'),
             ('RL_3_1', N'MAT_PLANT'), ('RL_3_1', N'PMATERIAL'), ('RL_3_2', N'MAT_PLANT'), ('RL_3_2', N'PMATERIAL')) AS ro(codigo, objeto)
JOIN gd.regla rg ON rg.codigo = ro.codigo
JOIN gd.objeto_fuente o ON o.nombre = ro.objeto
WHERE NOT EXISTS (SELECT 1 FROM gd.regla_objeto x WHERE x.regla_id = rg.regla_id AND x.objeto_id = o.objeto_id);

INSERT INTO gd.regla_contacto (regla_id, correo, tipo)
SELECT rg.regla_id, c.correo, c.tipo
FROM (VALUES ('RL_1_1', N'manuel.lopez@cia.com', 'CONTACTO'),
             ('RL_1_1', N'correo_destinatario1@cia.com', 'DESTINATARIO'),
             ('RL_1_1', N'destinatario2@cia.com', 'DESTINATARIO')) AS c(codigo, correo, tipo)
JOIN gd.regla rg ON rg.codigo = c.codigo
WHERE NOT EXISTS (SELECT 1 FROM gd.regla_contacto x WHERE x.regla_id = rg.regla_id AND x.correo = c.correo AND x.tipo = c.tipo);

-- Ciclo de vida: FECHA_PASE_A_PRD → "Pase a PRD"; FECHA_OPERACION_PRD → "Operación PRD".
INSERT INTO gd.mov_etapa_regla (regla_id, etapa_regla_id, fecha, motivo_id, comentario, usuario)
SELECT rg.regla_id, e.etapa_regla_id, m.fecha, 1, N'Migrado de SharePoint', @usuario
FROM @reglas r
JOIN gd.regla rg ON rg.codigo = r.codigo
CROSS APPLY (VALUES (N'Pase a PRD', r.pase_prd), (N'Operación PRD', r.operacion_prd)) AS m(etapa, fecha)
JOIN gd.etapa_regla e ON e.nombre = m.etapa
WHERE m.fecha IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM gd.mov_etapa_regla x WHERE x.regla_id = rg.regla_id AND x.etapa_regla_id = e.etapa_regla_id);

COMMIT TRANSACTION;

SELECT 'gobierno' AS tabla, COUNT(*) AS filas FROM gd.gobierno
UNION ALL SELECT 'data_product', COUNT(*) FROM gd.data_product
UNION ALL SELECT 'mov_avance_dp', COUNT(*) FROM gd.mov_avance_dp
UNION ALL SELECT 'log_semanal', COUNT(*) FROM gd.log_semanal
UNION ALL SELECT 'vista_gobernada', COUNT(*) FROM gd.vista_gobernada
UNION ALL SELECT 'mov_etapa_vista', COUNT(*) FROM gd.mov_etapa_vista
UNION ALL SELECT 'regla', COUNT(*) FROM gd.regla
UNION ALL SELECT 'regla_objeto', COUNT(*) FROM gd.regla_objeto
UNION ALL SELECT 'regla_contacto', COUNT(*) FROM gd.regla_contacto
UNION ALL SELECT 'mov_etapa_regla', COUNT(*) FROM gd.mov_etapa_regla;
