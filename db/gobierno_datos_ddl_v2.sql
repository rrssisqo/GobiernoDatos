/* =====================================================================
   GOBIERNO DE DATOS - Seguimiento de Data Products y Calidad   (v2)
   Motor: SQL Server 2016+        Esquema: gd
   ---------------------------------------------------------------------
   Cambios vs v1
     - Vista: ruta técnica única, nombre de negocio, responsable,
       subdominio, nivel de seguridad. La relación con el DP pasa a
       dp_vista con vigencia (se agregan vistas sobre la marcha).
     - Flujo de etapas por vista (13 estatus) con reaperturas y motivo.
     - Opción C: el % de rubros de vista se deriva de la etapa y solo se
       captura el % del rubro activo.
     - Reglas: llave interna + código, NO_CSV / NOMBRE_CSV, atributos de
       Informatica, query con histórico, contactos, ciclo de vida.
     - Objetos BW: las reglas evalúan objetos; los objetos alimentan
       vistas. La calidad se mide por vista y sube al DP por sus vistas.
   ---------------------------------------------------------------------
   Contenido
     1. Esquema
     2. Catálogos
     3. Núcleo: data_product, vista_gobernada, dp_vista
     4. Flujo y avance
     5. Objetos y reglas
     6. Resultados de calidad y carga
     7. Procedimientos
     8. Vistas de avance y flujo
     9. Vistas de calidad
    10. Validaciones
   ===================================================================== */

/* =====================================================================
   1. ESQUEMA
   ===================================================================== */
IF SCHEMA_ID('gd') IS NULL EXEC('CREATE SCHEMA gd');
GO

/* =====================================================================
   2. CATÁLOGOS
   ===================================================================== */
CREATE TABLE gd.gobierno (
    gobierno_id  INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_gobierno PRIMARY KEY,
    nombre       NVARCHAR(100) NOT NULL CONSTRAINT uq_gobierno_nombre UNIQUE,
    activo       BIT NOT NULL CONSTRAINT df_gobierno_activo DEFAULT (1)
);
GO

CREATE TABLE gd.subdominio (
    subdominio_id INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_subdominio PRIMARY KEY,
    gobierno_id   INT NOT NULL CONSTRAINT fk_subdominio_gobierno REFERENCES gd.gobierno (gobierno_id),
    nombre        NVARCHAR(100) NOT NULL,
    CONSTRAINT uq_subdominio UNIQUE (gobierno_id, nombre)
);
GO

CREATE TABLE gd.responsable (
    responsable_id INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_responsable PRIMARY KEY,
    nombre         NVARCHAR(150) NOT NULL,
    correo         NVARCHAR(254) NULL,
    activo         BIT NOT NULL CONSTRAINT df_responsable_activo DEFAULT (1)
);
GO
CREATE UNIQUE INDEX ux_responsable_correo ON gd.responsable (correo) WHERE correo IS NOT NULL;
GO

CREATE TABLE gd.nivel_seguridad (
    nivel_seguridad_id TINYINT NOT NULL CONSTRAINT pk_nivel_seguridad PRIMARY KEY,
    nombre             NVARCHAR(50) NOT NULL CONSTRAINT uq_nivel_seguridad UNIQUE
);
GO
INSERT INTO gd.nivel_seguridad VALUES (1, N'Interno/Confidencial'), (2, N'Restringido/Privado');
GO

-- Flujo de trabajo de la vista. orden 0 = inicio, es_final = cierre.
CREATE TABLE gd.etapa_vista (
    etapa_id        INT NOT NULL CONSTRAINT pk_etapa_vista PRIMARY KEY,
    orden           TINYINT NOT NULL CONSTRAINT uq_etapa_vista_orden UNIQUE,
    nombre          NVARCHAR(60) NOT NULL CONSTRAINT uq_etapa_vista_nombre UNIQUE,
    tarea_pendiente NVARCHAR(200) NULL,
    rol_ejecutor    NVARCHAR(60) NULL,
    es_final        BIT NOT NULL CONSTRAINT df_etapa_vista_final DEFAULT (0)
);
GO
INSERT INTO gd.etapa_vista (etapa_id, orden, nombre, tarea_pendiente, es_final) VALUES
 ( 1,  0, N'Por Iniciar',                  N'Priorizar e iniciar la vista', 0),
 ( 2,  1, N'Escaneo Inicial',              N'Lanzar escaneo de metadatos en Informatica', 0),
 ( 3,  2, N'Asignación Stake Holders',     N'Asignar owners y stakeholders', 0),
 ( 4,  3, N'Generación Glosarios Copilot', N'Generar glosarios con Copilot', 0),
 ( 5,  4, N'Import de Glosarios',          N'Importar glosarios a Informatica', 0),
 ( 6,  5, N'Reescaneo',                    N'Relanzar escaneo de metadatos', 0),
 ( 7,  6, N'Certificación Glosarios',      N'Certificación de glosarios por el owner', 0),
 ( 8,  7, N'Asignación Relaciones',        N'Relacionar términos con columnas', 0),
 ( 9,  8, N'Linaje',                       N'Completar linaje', 0),
 (10,  9, N'Data Product',                 N'Documentar la vista dentro del data product', 0),
 (11, 10, N'Perfilado',                    N'Perfilar la vista', 0),
 (12, 11, N'Calidad de Datos',             N'Definir y crear reglas de calidad', 0),
 (13, 12, N'Completada',                   NULL, 1);
GO

-- Rubros. Los de nivel VISTA abarcan un rango de etapas (opción C).
CREATE TABLE gd.rubro (
    rubro_id          INT NOT NULL CONSTRAINT pk_rubro PRIMARY KEY,
    nombre            NVARCHAR(50) NOT NULL CONSTRAINT uq_rubro_nombre UNIQUE,
    nivel             VARCHAR(5) NOT NULL CONSTRAINT ck_rubro_nivel CHECK (nivel IN ('DP','VISTA')),
    grupo_responsable NVARCHAR(30) NOT NULL,
    peso              DECIMAL(5,2) NOT NULL CONSTRAINT df_rubro_peso DEFAULT (1) CONSTRAINT ck_rubro_peso CHECK (peso > 0),
    orden             TINYINT NOT NULL,
    etapa_orden_ini   TINYINT NULL CONSTRAINT fk_rubro_etapa_ini REFERENCES gd.etapa_vista (orden),
    etapa_orden_fin   TINYINT NULL CONSTRAINT fk_rubro_etapa_fin REFERENCES gd.etapa_vista (orden),
    CONSTRAINT uq_rubro_id_nivel UNIQUE (rubro_id, nivel),
    CONSTRAINT ck_rubro_rango CHECK (
           (nivel = 'DP'    AND etapa_orden_ini IS NULL AND etapa_orden_fin IS NULL)
        OR (nivel = 'VISTA' AND etapa_orden_ini IS NOT NULL AND etapa_orden_fin IS NOT NULL
                            AND etapa_orden_ini <= etapa_orden_fin))
);
GO
INSERT INTO gd.rubro (rubro_id, nombre, nivel, grupo_responsable, peso, orden, etapa_orden_ini, etapa_orden_fin) VALUES
 (1, N'Glosario',         'VISTA', N'Owner',           1, 1,    3,    6),
 (2, N'Catalogado',       'DP',    N'Owner',           1, 2, NULL, NULL),
 (3, N'Diseño Funcional', 'DP',    N'Owner',           1, 3, NULL, NULL),
 (4, N'Arquitectura',     'DP',    N'Funcional',       1, 4, NULL, NULL),
 (5, N'Linaje',           'VISTA', N'Funcional',       1, 5,    7,    8),
 (6, N'Perfilado',        'VISTA', N'Owner-Funcional', 1, 6,   10,   10),
 (7, N'Reglas',           'VISTA', N'Owner-Funcional', 1, 7,   11,   11);
GO

CREATE TABLE gd.motivo (
    motivo_id INT NOT NULL CONSTRAINT pk_motivo PRIMARY KEY,
    nombre    NVARCHAR(60) NOT NULL CONSTRAINT uq_motivo UNIQUE,
    aplica_a  VARCHAR(5) NOT NULL CONSTRAINT ck_motivo_aplica CHECK (aplica_a IN ('VISTA','REGLA','AMBOS'))
);
GO
INSERT INTO gd.motivo VALUES
 (1, N'Flujo inicial',          'AMBOS'),
 (2, N'Cambio de estructura',   'AMBOS'),
 (3, N'Corrección',             'AMBOS'),
 (4, N'Solicitud del owner',    'AMBOS'),
 (5, N'Cambio de query',        'REGLA'),
 (9, N'Otro',                   'AMBOS');
GO

CREATE TABLE gd.tipo_regla (
    tipo_regla_id      INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_tipo_regla PRIMARY KEY,
    nombre             NVARCHAR(80) NOT NULL CONSTRAINT uq_tipo_regla UNIQUE,  -- ej. Nulos, Catálogo de plantas
    codigo_informatica NVARCHAR(60) NULL                                       -- ej. Rl_Nulos
);
GO

-- Ciclo de vida de la regla (reemplaza ESTATUS y las 6 columnas de fecha).
CREATE TABLE gd.etapa_regla (
    etapa_regla_id INT NOT NULL CONSTRAINT pk_etapa_regla PRIMARY KEY,
    orden          TINYINT NOT NULL CONSTRAINT uq_etapa_regla_orden UNIQUE,
    nombre         NVARCHAR(40) NOT NULL CONSTRAINT uq_etapa_regla_nombre UNIQUE,
    es_operativa   BIT NOT NULL CONSTRAINT df_etapa_regla_operativa DEFAULT (0)
);
GO
INSERT INTO gd.etapa_regla VALUES
 (1, 1, N'Definición',    0),
 (2, 2, N'Desarrollo',    0),
 (3, 3, N'Pruebas DEV',   0),
 (4, 4, N'Validación',    0),
 (5, 5, N'Pase a PRD',    0),
 (6, 6, N'Operación PRD', 1),
 (7, 7, N'Suspendida',    0),
 (8, 8, N'Baja',          0);
GO

-- Semanas ISO. semana_id = AAAASS; etiqueta con formato del Excel (39/26).
CREATE TABLE gd.semana (
    semana_id    INT NOT NULL CONSTRAINT pk_semana PRIMARY KEY,
    anio         SMALLINT NOT NULL,
    num_semana   TINYINT NOT NULL,
    fecha_inicio DATE NOT NULL,
    fecha_fin    DATE NOT NULL,
    etiqueta     AS (RIGHT('0' + CAST(num_semana AS VARCHAR(2)), 2) + '/' + RIGHT(CAST(anio AS VARCHAR(4)), 2)) PERSISTED
);
GO
WITH s AS (
    SELECT CAST('2024-12-30' AS DATE) AS lunes          -- lunes de la semana 1 de 2025
    UNION ALL
    SELECT DATEADD(DAY, 7, lunes) FROM s WHERE lunes < '2028-12-25'
)
INSERT INTO gd.semana (semana_id, anio, num_semana, fecha_inicio, fecha_fin)
SELECT YEAR(DATEADD(DAY, 3, lunes)) * 100 + DATEPART(ISO_WEEK, lunes),
       YEAR(DATEADD(DAY, 3, lunes)), DATEPART(ISO_WEEK, lunes), lunes, DATEADD(DAY, 6, lunes)
FROM s
OPTION (MAXRECURSION 0);
GO

/* =====================================================================
   3. NÚCLEO
   ===================================================================== */
CREATE TABLE gd.data_product (
    dp_id        INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_data_product PRIMARY KEY,
    nombre       NVARCHAR(150) NOT NULL CONSTRAINT uq_dp_nombre UNIQUE,
    gobierno_id  INT NOT NULL CONSTRAINT fk_dp_gobierno REFERENCES gd.gobierno (gobierno_id),
    responsable_id INT NULL CONSTRAINT fk_dp_responsable REFERENCES gd.responsable (responsable_id),
    estatus      VARCHAR(15) NOT NULL CONSTRAINT df_dp_estatus DEFAULT ('EN_CURSO')
                 CONSTRAINT ck_dp_estatus CHECK (estatus IN ('PLANEADO','EN_CURSO','CERRADO','CANCELADO')),
    fecha_alta   DATE NOT NULL CONSTRAINT df_dp_fecha_alta DEFAULT (CAST(GETDATE() AS DATE)),
    fecha_cierre DATE NULL,
    CONSTRAINT ck_dp_fechas CHECK (fecha_cierre IS NULL OR fecha_cierre >= fecha_alta)
);
GO

CREATE TABLE gd.vista_gobernada (
    vista_id              INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_vista_gobernada PRIMARY KEY,
    ruta_tecnica          NVARCHAR(400) NOT NULL CONSTRAINT uq_vista_ruta UNIQUE,  -- "_SYS_BIC"."MABE..../VISTA"
    nombre_negocio        NVARCHAR(200) NOT NULL,
    gobierno_id           INT NOT NULL CONSTRAINT fk_vista_gobierno REFERENCES gd.gobierno (gobierno_id),
    subdominio_id         INT NULL     CONSTRAINT fk_vista_subdominio REFERENCES gd.subdominio (subdominio_id),
    responsable_id        INT NULL     CONSTRAINT fk_vista_responsable REFERENCES gd.responsable (responsable_id),
    nivel_seguridad_id    TINYINT NULL CONSTRAINT fk_vista_nivel_seg REFERENCES gd.nivel_seguridad (nivel_seguridad_id),
    fecha_recertificacion DATE NULL,
    fecha_alta            DATE NOT NULL CONSTRAINT df_vista_fecha_alta DEFAULT (CAST(GETDATE() AS DATE)),
    activa                BIT NOT NULL CONSTRAINT df_vista_activa DEFAULT (1)
);
GO

-- Asignación vista -> data product con vigencia. Un solo DP vigente por vista.
-- fecha_hasta = último día vigente (inclusive); NULL = vigente.
CREATE TABLE gd.dp_vista (
    dp_vista_id INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_dp_vista PRIMARY KEY,
    dp_id       INT NOT NULL CONSTRAINT fk_dpvista_dp    REFERENCES gd.data_product (dp_id),
    vista_id    INT NOT NULL CONSTRAINT fk_dpvista_vista REFERENCES gd.vista_gobernada (vista_id),
    fecha_desde DATE NOT NULL CONSTRAINT df_dpvista_desde DEFAULT (CAST(GETDATE() AS DATE)),
    fecha_hasta DATE NULL,
    CONSTRAINT ck_dpvista_fechas CHECK (fecha_hasta IS NULL OR fecha_hasta >= fecha_desde)
);
GO
CREATE UNIQUE INDEX ux_dpvista_vigente ON gd.dp_vista (vista_id) WHERE fecha_hasta IS NULL;
CREATE INDEX ix_dpvista_dp ON gd.dp_vista (dp_id, fecha_desde) INCLUDE (vista_id, fecha_hasta);
GO

/* =====================================================================
   4. FLUJO Y AVANCE
   ===================================================================== */
-- Bitácora de etapas de la vista. Usar gd.sp_mover_etapa_vista para insertar.
CREATE TABLE gd.mov_etapa_vista (
    mov_id         BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT pk_mov_etapa_vista PRIMARY KEY,
    vista_id       INT NOT NULL CONSTRAINT fk_movetapa_vista  REFERENCES gd.vista_gobernada (vista_id),
    etapa_id       INT NOT NULL CONSTRAINT fk_movetapa_etapa  REFERENCES gd.etapa_vista (etapa_id),
    fecha          DATE NOT NULL CONSTRAINT df_movetapa_fecha DEFAULT (CAST(GETDATE() AS DATE)),
    motivo_id      INT NULL     CONSTRAINT fk_movetapa_motivo REFERENCES gd.motivo (motivo_id),
    es_regreso     BIT NOT NULL CONSTRAINT df_movetapa_regreso DEFAULT (0),
    comentario     NVARCHAR(1000) NULL,
    usuario        NVARCHAR(100) NOT NULL CONSTRAINT df_movetapa_usuario DEFAULT (SUSER_SNAME()),
    fecha_registro DATETIME2(0) NOT NULL CONSTRAINT df_movetapa_fecha_reg DEFAULT (SYSDATETIME()),
    CONSTRAINT ck_movetapa_motivo CHECK (es_regreso = 0 OR motivo_id IS NOT NULL)
);
GO
CREATE INDEX ix_movetapa_vista ON gd.mov_etapa_vista (vista_id, fecha DESC, mov_id DESC) INCLUDE (etapa_id);
GO

-- Estados y % válidos: NO_INICIADO=0, EN_CURSO 1-99, CERRADO=100, NO_APLICA=100.
CREATE TABLE gd.mov_avance_vista (
    mov_id         BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT pk_mov_avance_vista PRIMARY KEY,
    vista_id       INT NOT NULL CONSTRAINT fk_movvista_vista REFERENCES gd.vista_gobernada (vista_id),
    rubro_id       INT NOT NULL,
    nivel          VARCHAR(5) NOT NULL CONSTRAINT df_movvista_nivel DEFAULT ('VISTA') CONSTRAINT ck_movvista_nivel CHECK (nivel = 'VISTA'),
    fecha          DATE NOT NULL CONSTRAINT df_movvista_fecha DEFAULT (CAST(GETDATE() AS DATE)),
    pct            DECIMAL(5,2) NOT NULL,
    estado         VARCHAR(12) NOT NULL,
    comentario     NVARCHAR(1000) NULL,
    usuario        NVARCHAR(100) NOT NULL CONSTRAINT df_movvista_usuario DEFAULT (SUSER_SNAME()),
    fecha_registro DATETIME2(0) NOT NULL CONSTRAINT df_movvista_fecha_reg DEFAULT (SYSDATETIME()),
    CONSTRAINT fk_movvista_rubro FOREIGN KEY (rubro_id, nivel) REFERENCES gd.rubro (rubro_id, nivel),
    CONSTRAINT ck_movvista_estado CHECK (estado IN ('NO_INICIADO','EN_CURSO','CERRADO','NO_APLICA')),
    CONSTRAINT ck_movvista_coherencia CHECK (
           (estado = 'NO_INICIADO' AND pct = 0)
        OR (estado = 'EN_CURSO' AND pct > 0 AND pct < 100)
        OR (estado IN ('CERRADO','NO_APLICA') AND pct = 100))
);
GO
CREATE INDEX ix_movvista_ultimo ON gd.mov_avance_vista (vista_id, rubro_id, fecha DESC, mov_id DESC) INCLUDE (pct, estado);
GO

CREATE TABLE gd.mov_avance_dp (
    mov_id         BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT pk_mov_avance_dp PRIMARY KEY,
    dp_id          INT NOT NULL CONSTRAINT fk_movdp_dp REFERENCES gd.data_product (dp_id),
    rubro_id       INT NOT NULL,
    nivel          VARCHAR(5) NOT NULL CONSTRAINT df_movdp_nivel DEFAULT ('DP') CONSTRAINT ck_movdp_nivel CHECK (nivel = 'DP'),
    fecha          DATE NOT NULL CONSTRAINT df_movdp_fecha DEFAULT (CAST(GETDATE() AS DATE)),
    pct            DECIMAL(5,2) NOT NULL,
    estado         VARCHAR(12) NOT NULL,
    comentario     NVARCHAR(1000) NULL,
    usuario        NVARCHAR(100) NOT NULL CONSTRAINT df_movdp_usuario DEFAULT (SUSER_SNAME()),
    fecha_registro DATETIME2(0) NOT NULL CONSTRAINT df_movdp_fecha_reg DEFAULT (SYSDATETIME()),
    CONSTRAINT fk_movdp_rubro FOREIGN KEY (rubro_id, nivel) REFERENCES gd.rubro (rubro_id, nivel),
    CONSTRAINT ck_movdp_estado CHECK (estado IN ('NO_INICIADO','EN_CURSO','CERRADO','NO_APLICA')),
    CONSTRAINT ck_movdp_coherencia CHECK (
           (estado = 'NO_INICIADO' AND pct = 0)
        OR (estado = 'EN_CURSO' AND pct > 0 AND pct < 100)
        OR (estado IN ('CERRADO','NO_APLICA') AND pct = 100))
);
GO
CREATE INDEX ix_movdp_ultimo ON gd.mov_avance_dp (dp_id, rubro_id, fecha DESC, mov_id DESC) INCLUDE (pct, estado);
GO

CREATE TABLE gd.log_semanal (
    log_id         BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT pk_log_semanal PRIMARY KEY,
    dp_id          INT NOT NULL CONSTRAINT fk_log_dp     REFERENCES gd.data_product (dp_id),
    semana_id      INT NOT NULL CONSTRAINT fk_log_semana REFERENCES gd.semana (semana_id),
    vista_id       INT NULL     CONSTRAINT fk_log_vista  REFERENCES gd.vista_gobernada (vista_id),
    rubro_id       INT NULL     CONSTRAINT fk_log_rubro  REFERENCES gd.rubro (rubro_id),
    comentario     NVARCHAR(2000) NOT NULL,
    usuario        NVARCHAR(100) NOT NULL CONSTRAINT df_log_usuario DEFAULT (SUSER_SNAME()),
    fecha_registro DATETIME2(0) NOT NULL CONSTRAINT df_log_fecha_reg DEFAULT (SYSDATETIME())
);
GO
CREATE INDEX ix_log_dp_semana ON gd.log_semanal (dp_id, semana_id);
GO

/* =====================================================================
   5. OBJETOS Y REGLAS
   ===================================================================== */
-- Objetos BW evaluados por las reglas (cubos, ADSO, InfoObjetos...).
CREATE TABLE gd.objeto_fuente (
    objeto_id   INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_objeto_fuente PRIMARY KEY,
    nombre      NVARCHAR(100) NOT NULL CONSTRAINT uq_objeto_nombre UNIQUE,  -- ej. PMATERIAL, ZSD_O21
    tipo        VARCHAR(12) NOT NULL CONSTRAINT df_objeto_tipo DEFAULT ('CUBO')
                CONSTRAINT ck_objeto_tipo CHECK (tipo IN ('CUBO','ADSO','DSO','INFOOBJETO','TABLA','OTRO')),
    descripcion NVARCHAR(300) NULL,
    activo      BIT NOT NULL CONSTRAINT df_objeto_activo DEFAULT (1)
);
GO

-- Qué objetos alimentan cada vista (nivel de medición de la calidad). Con vigencia.
CREATE TABLE gd.vista_objeto (
    vista_objeto_id INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_vista_objeto PRIMARY KEY,
    vista_id        INT NOT NULL CONSTRAINT fk_vo_vista  REFERENCES gd.vista_gobernada (vista_id),
    objeto_id       INT NOT NULL CONSTRAINT fk_vo_objeto REFERENCES gd.objeto_fuente (objeto_id),
    fecha_desde     DATE NOT NULL CONSTRAINT df_vo_desde DEFAULT (CAST(GETDATE() AS DATE)),
    fecha_hasta     DATE NULL,
    CONSTRAINT ck_vo_fechas CHECK (fecha_hasta IS NULL OR fecha_hasta >= fecha_desde)
);
GO
CREATE UNIQUE INDEX ux_vo_vigente ON gd.vista_objeto (vista_id, objeto_id) WHERE fecha_hasta IS NULL;
CREATE INDEX ix_vo_objeto ON gd.vista_objeto (objeto_id) INCLUDE (vista_id, fecha_desde, fecha_hasta);
GO

CREATE TABLE gd.regla (
    regla_id          INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_regla PRIMARY KEY,
    codigo            VARCHAR(20) NOT NULL CONSTRAINT uq_regla_codigo UNIQUE,     -- ID: RL_1_1
    tipo_regla_id     INT NULL CONSTRAINT fk_regla_tipo REFERENCES gd.tipo_regla (tipo_regla_id),
    descripcion       NVARCHAR(300) NOT NULL,                                     -- DESCRIPCION
    regla_funcional   NVARCHAR(2000) NULL,                                        -- DESCRIPCION_FUNCIONAL
    proposito_negocio NVARCHAR(2000) NULL,                                        -- CORREO_PROPOSITO
    dimension_dq      VARCHAR(15) NULL CONSTRAINT ck_regla_dimension CHECK (dimension_dq IN
                          ('COMPLETITUD','VALIDEZ','UNICIDAD','CONSISTENCIA','EXACTITUD','OPORTUNIDAD')),
    gobierno_id       INT NULL CONSTRAINT fk_regla_gobierno REFERENCES gd.gobierno (gobierno_id),
    responsable_id    INT NULL CONSTRAINT fk_regla_responsable REFERENCES gd.responsable (responsable_id),
    campos_evaluados  NVARCHAR(400) NULL,                                         -- NOMBRE_CAMPO
    tabla_stg         NVARCHAR(128) NULL,
    mapping           NVARCHAR(128) NULL,
    task_flow         NVARCHAR(128) NULL,
    ruta              NVARCHAR(400) NULL,
    no_csv            INT NULL,
    nombre_csv        NVARCHAR(260) NULL,
    frecuencia        VARCHAR(15) NULL CONSTRAINT ck_regla_frecuencia CHECK (frecuencia IN ('DIARIA','SEMANAL','MENSUAL','TRIMESTRAL')),
    escribe_log       BIT NOT NULL CONSTRAINT df_regla_log DEFAULT (0),          -- STAGE
    query_validacion  NVARCHAR(MAX) NULL,                                         -- Query
    correo_asunto     NVARCHAR(200) NULL,
    activa            BIT NOT NULL CONSTRAINT df_regla_activa DEFAULT (1),
    fecha_alta        DATE NOT NULL CONSTRAINT df_regla_fecha_alta DEFAULT (CAST(GETDATE() AS DATE))
);
GO
CREATE UNIQUE INDEX ux_regla_no_csv     ON gd.regla (no_csv)     WHERE no_csv IS NOT NULL;
CREATE UNIQUE INDEX ux_regla_nombre_csv ON gd.regla (nombre_csv) WHERE nombre_csv IS NOT NULL;
GO

CREATE TABLE gd.regla_objeto (
    regla_id  INT NOT NULL CONSTRAINT fk_ro_regla  REFERENCES gd.regla (regla_id),
    objeto_id INT NOT NULL CONSTRAINT fk_ro_objeto REFERENCES gd.objeto_fuente (objeto_id),
    CONSTRAINT pk_regla_objeto PRIMARY KEY (regla_id, objeto_id)
);
GO

CREATE TABLE gd.regla_contacto (
    regla_id INT NOT NULL CONSTRAINT fk_rc_regla REFERENCES gd.regla (regla_id),
    correo   NVARCHAR(254) NOT NULL,
    tipo     VARCHAR(12) NOT NULL CONSTRAINT ck_rc_tipo CHECK (tipo IN ('CONTACTO','DESTINATARIO')),
    CONSTRAINT pk_regla_contacto PRIMARY KEY (regla_id, correo, tipo)
);
GO

-- Versiones del query: permite saber con qué definición se calculó cada periodo.
CREATE TABLE gd.regla_query_hist (
    hist_id          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT pk_regla_query_hist PRIMARY KEY,
    regla_id         INT NOT NULL CONSTRAINT fk_rqh_regla REFERENCES gd.regla (regla_id),
    query_validacion NVARCHAR(MAX) NOT NULL,
    vigente_desde    DATETIME2(0) NOT NULL CONSTRAINT df_rqh_desde DEFAULT (SYSDATETIME()),
    usuario          NVARCHAR(100) NOT NULL CONSTRAINT df_rqh_usuario DEFAULT (SUSER_SNAME())
);
GO

CREATE OR ALTER TRIGGER gd.tr_regla_query_hist ON gd.regla
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO gd.regla_query_hist (regla_id, query_validacion)
    SELECT i.regla_id, i.query_validacion
    FROM inserted AS i
    LEFT JOIN deleted AS d ON d.regla_id = i.regla_id
    WHERE i.query_validacion IS NOT NULL
      AND (d.regla_id IS NULL OR ISNULL(d.query_validacion, N'') <> i.query_validacion);
END;
GO

CREATE TABLE gd.mov_etapa_regla (
    mov_id         BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT pk_mov_etapa_regla PRIMARY KEY,
    regla_id       INT NOT NULL CONSTRAINT fk_mer_regla  REFERENCES gd.regla (regla_id),
    etapa_regla_id INT NOT NULL CONSTRAINT fk_mer_etapa  REFERENCES gd.etapa_regla (etapa_regla_id),
    fecha          DATE NOT NULL CONSTRAINT df_mer_fecha DEFAULT (CAST(GETDATE() AS DATE)),
    motivo_id      INT NULL     CONSTRAINT fk_mer_motivo REFERENCES gd.motivo (motivo_id),
    comentario     NVARCHAR(1000) NULL,
    usuario        NVARCHAR(100) NOT NULL CONSTRAINT df_mer_usuario DEFAULT (SUSER_SNAME()),
    fecha_registro DATETIME2(0) NOT NULL CONSTRAINT df_mer_fecha_reg DEFAULT (SYSDATETIME())
);
GO
CREATE INDEX ix_mer_regla ON gd.mov_etapa_regla (regla_id, fecha DESC, mov_id DESC) INCLUDE (etapa_regla_id);
GO

/* =====================================================================
   6. RESULTADOS DE CALIDAD Y CARGA
   ===================================================================== */
CREATE TABLE gd.resumen_calidad (
    regla_id      INT NOT NULL CONSTRAINT fk_resumen_regla REFERENCES gd.regla (regla_id),
    periodo       INT NOT NULL,                                -- AAAAMM
    reg_evaluados BIGINT NOT NULL,
    reg_errores   BIGINT NOT NULL,
    reg_validos   AS (reg_evaluados - reg_errores) PERSISTED,
    pct_calidad   AS (CAST(CASE WHEN reg_evaluados = 0 THEN NULL
                                ELSE (reg_evaluados - reg_errores) * 1.0 / reg_evaluados END AS DECIMAL(9,6))) PERSISTED,
    fecha_carga   DATETIME2(0) NOT NULL CONSTRAINT df_resumen_fecha_carga DEFAULT (SYSDATETIME()),
    CONSTRAINT pk_resumen_calidad PRIMARY KEY (regla_id, periodo),
    CONSTRAINT ck_resumen_periodo CHECK (periodo BETWEEN 200001 AND 209912 AND periodo % 100 BETWEEN 1 AND 12),
    CONSTRAINT ck_resumen_conteos CHECK (reg_evaluados >= 0 AND reg_errores >= 0 AND reg_errores <= reg_evaluados)
);
GO
CREATE INDEX ix_resumen_periodo ON gd.resumen_calidad (periodo) INCLUDE (reg_evaluados, reg_errores);
GO

-- Staging: la salida de tu vista de resumen. Identifica la regla por código o por nombre de CSV.
CREATE TABLE gd.stg_resumen_calidad (
    regla_codigo  VARCHAR(20)   NULL,
    nombre_csv    NVARCHAR(260) NULL,
    periodo       INT    NULL,
    reg_evaluados BIGINT NULL,
    reg_errores   BIGINT NULL
);
GO

CREATE TABLE gd.carga_rechazo (
    rechazo_id    BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT pk_carga_rechazo PRIMARY KEY,
    regla_codigo  VARCHAR(20)   NULL,
    nombre_csv    NVARCHAR(260) NULL,
    periodo       INT    NULL,
    reg_evaluados BIGINT NULL,
    reg_errores   BIGINT NULL,
    motivo        NVARCHAR(200) NOT NULL,
    fecha_carga   DATETIME2(0) NOT NULL CONSTRAINT df_rechazo_fecha DEFAULT (SYSDATETIME())
);
GO

/* =====================================================================
   7. PROCEDIMIENTOS
   ===================================================================== */
-- Mueve una vista de etapa. Un regreso (orden menor) exige motivo.
CREATE OR ALTER PROCEDURE gd.sp_mover_etapa_vista
    @vista_id   INT,
    @etapa_id   INT,
    @motivo_id  INT = NULL,
    @comentario NVARCHAR(1000) = NULL,
    @fecha      DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @fecha = ISNULL(@fecha, CAST(GETDATE() AS DATE));

    DECLARE @tiene_mov BIT = CASE WHEN EXISTS (SELECT 1 FROM gd.mov_etapa_vista WHERE vista_id = @vista_id) THEN 1 ELSE 0 END;
    DECLARE @orden_actual INT = ISNULL((
        SELECT TOP (1) e.orden
        FROM gd.mov_etapa_vista AS m
        JOIN gd.etapa_vista AS e ON e.etapa_id = m.etapa_id
        WHERE m.vista_id = @vista_id
        ORDER BY m.fecha DESC, m.mov_id DESC), 0);
    DECLARE @orden_nueva INT = (SELECT orden FROM gd.etapa_vista WHERE etapa_id = @etapa_id);

    IF @orden_nueva IS NULL
    BEGIN
        THROW 50001, N'La etapa indicada no existe.', 1;
    END;

    IF @tiene_mov = 1 AND @orden_nueva = @orden_actual
    BEGIN
        THROW 50002, N'La vista ya se encuentra en esa etapa.', 1;
    END;

    DECLARE @es_regreso BIT = CASE WHEN @orden_nueva < @orden_actual THEN 1 ELSE 0 END;

    IF @es_regreso = 1 AND @motivo_id IS NULL
    BEGIN
        THROW 50003, N'Un regreso de etapa requiere indicar el motivo.', 1;
    END;

    INSERT INTO gd.mov_etapa_vista (vista_id, etapa_id, fecha, motivo_id, es_regreso, comentario)
    VALUES (@vista_id, @etapa_id, @fecha, ISNULL(@motivo_id, CASE WHEN @es_regreso = 0 THEN 1 END), @es_regreso, @comentario);
END;
GO

-- Registra % de un rubro de vista. Solo se permite si el rubro está activo en la etapa actual.
CREATE OR ALTER PROCEDURE gd.sp_registrar_avance_vista
    @vista_id   INT,
    @rubro_id   INT,
    @estado     VARCHAR(12),
    @pct        DECIMAL(5,2) = NULL,
    @comentario NVARCHAR(1000) = NULL,
    @fecha      DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @fecha = ISNULL(@fecha, CAST(GETDATE() AS DATE));

    DECLARE @orden INT = ISNULL((
        SELECT TOP (1) e.orden
        FROM gd.mov_etapa_vista AS m
        JOIN gd.etapa_vista AS e ON e.etapa_id = m.etapa_id
        WHERE m.vista_id = @vista_id AND m.fecha <= @fecha
        ORDER BY m.fecha DESC, m.mov_id DESC), 0);

    IF NOT EXISTS (SELECT 1 FROM gd.rubro
                   WHERE rubro_id = @rubro_id AND nivel = 'VISTA'
                     AND @orden BETWEEN etapa_orden_ini AND etapa_orden_fin)
    BEGIN
        THROW 50010, N'El rubro no está activo en la etapa actual de la vista.', 1;
    END;

    SET @pct = CASE @estado WHEN 'NO_INICIADO' THEN 0 WHEN 'CERRADO' THEN 100 WHEN 'NO_APLICA' THEN 100 ELSE @pct END;

    INSERT INTO gd.mov_avance_vista (vista_id, rubro_id, fecha, pct, estado, comentario)
    VALUES (@vista_id, @rubro_id, @fecha, @pct, @estado, @comentario);
END;
GO

-- Carga del resumen de calidad desde staging (upsert por regla y periodo).
--   INSERT INTO gd.stg_resumen_calidad (regla_codigo, nombre_csv, periodo, reg_evaluados, reg_errores) SELECT ...;
--   EXEC gd.sp_cargar_resumen_calidad;
CREATE OR ALTER PROCEDURE gd.sp_cargar_resumen_calidad
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @cargados INT, @rechazados INT;

    BEGIN TRANSACTION;

    SELECT s.regla_codigo, s.nombre_csv, s.periodo, s.reg_evaluados, s.reg_errores,
           COALESCE(rc.regla_id, rn.regla_id) AS regla_id,
           v.motivo
    INTO #validado
    FROM gd.stg_resumen_calidad AS s
    LEFT JOIN gd.regla AS rc ON rc.codigo     = s.regla_codigo
    LEFT JOIN gd.regla AS rn ON rn.nombre_csv = s.nombre_csv
    CROSS APPLY (SELECT motivo = CASE
        WHEN s.regla_codigo IS NULL AND s.nombre_csv IS NULL               THEN N'Sin identificador de regla'
        WHEN s.periodo IS NULL OR s.reg_evaluados IS NULL
          OR s.reg_errores IS NULL                                         THEN N'Campos obligatorios vacíos'
        WHEN COALESCE(rc.regla_id, rn.regla_id) IS NULL                    THEN N'Regla no registrada'
        WHEN rc.regla_id IS NOT NULL AND rn.regla_id IS NOT NULL
          AND rc.regla_id <> rn.regla_id                                   THEN N'Código y CSV apuntan a reglas distintas'
        WHEN COALESCE(rc.activa, rn.activa) = 0                            THEN N'Regla inactiva'
        WHEN s.periodo NOT BETWEEN 200001 AND 209912
          OR s.periodo % 100 NOT BETWEEN 1 AND 12                          THEN N'Periodo inválido (AAAAMM)'
        WHEN s.reg_evaluados < 0 OR s.reg_errores < 0
          OR s.reg_errores > s.reg_evaluados                               THEN N'Conteos inconsistentes'
    END) AS v;

    -- Duplicados de regla y periodo dentro de la misma carga
    UPDATE x SET motivo = N'Regla y periodo duplicados en la carga'
    FROM #validado AS x
    WHERE x.motivo IS NULL
      AND EXISTS (SELECT 1 FROM #validado AS y
                  WHERE y.motivo IS NULL AND y.regla_id = x.regla_id AND y.periodo = x.periodo
                  GROUP BY y.regla_id, y.periodo HAVING COUNT(*) > 1);

    INSERT INTO gd.carga_rechazo (regla_codigo, nombre_csv, periodo, reg_evaluados, reg_errores, motivo)
    SELECT regla_codigo, nombre_csv, periodo, reg_evaluados, reg_errores, motivo
    FROM #validado WHERE motivo IS NOT NULL;
    SET @rechazados = @@ROWCOUNT;

    MERGE gd.resumen_calidad AS t
    USING (SELECT regla_id, periodo, reg_evaluados, reg_errores FROM #validado WHERE motivo IS NULL) AS s
       ON t.regla_id = s.regla_id AND t.periodo = s.periodo
    WHEN MATCHED THEN
        UPDATE SET reg_evaluados = s.reg_evaluados, reg_errores = s.reg_errores, fecha_carga = SYSDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (regla_id, periodo, reg_evaluados, reg_errores)
        VALUES (s.regla_id, s.periodo, s.reg_evaluados, s.reg_errores);
    SET @cargados = @@ROWCOUNT;

    TRUNCATE TABLE gd.stg_resumen_calidad;

    COMMIT TRANSACTION;

    SELECT @cargados AS registros_cargados, @rechazados AS registros_rechazados;
END;
GO

/* =====================================================================
   8. VISTAS DE AVANCE Y FLUJO
   ---------------------------------------------------------------------
   Regla C por vista y rubro, al cierre de cada semana:
     - La vista nunca entró al rango del rubro:
         la rebasó (salto)  -> 100 CERRADO      | no la ha rebasado -> 0 NO_INICIADO
     - Entró al rango y después avanzó más allá del rango -> 100 CERRADO
     - Entró al rango y sigue ahí (o regresó antes)       -> % capturado desde la entrada
   Un rubro solo se reabre cuando la vista vuelve a entrar a una etapa de su rango.
   Nota: si el volumen crece, materializar estas vistas semanalmente en tablas.
   ===================================================================== */
CREATE OR ALTER VIEW gd.vw_avance_vista_semana
AS
WITH vista_semana AS (
    SELECT s.semana_id, s.etiqueta, s.fecha_fin, v.vista_id, v.nombre_negocio
    FROM gd.semana AS s
    JOIN gd.vista_gobernada AS v
      ON  s.fecha_fin    >= v.fecha_alta
      AND s.fecha_inicio <= CAST(GETDATE() AS DATE)
    WHERE v.activa = 1
)
SELECT  vs.semana_id,
        vs.etiqueta,
        vs.vista_id,
        vs.nombre_negocio              AS vista,
        dv.dp_id,
        r.rubro_id,
        r.nombre                       AS rubro,
        ISNULL(ea.orden, 0)            AS etapa_orden_actual,
        calc.pct,
        calc.estado,
        calc.origen
FROM vista_semana AS vs
CROSS JOIN gd.rubro AS r
OUTER APPLY (   -- DP vigente al cierre de la semana
    SELECT TOP (1) x.dp_id
    FROM gd.dp_vista AS x
    WHERE x.vista_id = vs.vista_id
      AND x.fecha_desde <= vs.fecha_fin
      AND (x.fecha_hasta IS NULL OR x.fecha_hasta >= vs.fecha_fin)
    ORDER BY x.fecha_desde DESC
) AS dv
OUTER APPLY (   -- etapa actual al cierre de la semana
    SELECT TOP (1) e.orden
    FROM gd.mov_etapa_vista AS m
    JOIN gd.etapa_vista AS e ON e.etapa_id = m.etapa_id
    WHERE m.vista_id = vs.vista_id AND m.fecha <= vs.fecha_fin
    ORDER BY m.fecha DESC, m.mov_id DESC
) AS ea
OUTER APPLY (   -- última entrada a una etapa del rango del rubro
    SELECT TOP (1) m.fecha, m.mov_id
    FROM gd.mov_etapa_vista AS m
    JOIN gd.etapa_vista AS e ON e.etapa_id = m.etapa_id
    WHERE m.vista_id = vs.vista_id AND m.fecha <= vs.fecha_fin
      AND e.orden BETWEEN r.etapa_orden_ini AND r.etapa_orden_fin
    ORDER BY m.fecha DESC, m.mov_id DESC
) AS ent
OUTER APPLY (   -- ¿avanzó más allá del rango después de esa entrada (o sin haber entrado)?
    SELECT CASE WHEN EXISTS (
        SELECT 1
        FROM gd.mov_etapa_vista AS m
        JOIN gd.etapa_vista AS e ON e.etapa_id = m.etapa_id
        WHERE m.vista_id = vs.vista_id AND m.fecha <= vs.fecha_fin
          AND e.orden > r.etapa_orden_fin
          AND (ent.mov_id IS NULL OR m.fecha > ent.fecha OR (m.fecha = ent.fecha AND m.mov_id > ent.mov_id))
    ) THEN 1 ELSE 0 END AS rebasado
) AS fwd
OUTER APPLY (   -- último % capturado desde la entrada al rango
    SELECT TOP (1) a.pct, a.estado
    FROM gd.mov_avance_vista AS a
    WHERE ent.mov_id IS NOT NULL
      AND a.vista_id = vs.vista_id AND a.rubro_id = r.rubro_id
      AND a.fecha >= ent.fecha AND a.fecha <= vs.fecha_fin
    ORDER BY a.fecha DESC, a.mov_id DESC
) AS cap
CROSS APPLY (
    SELECT
        pct    = CAST(CASE WHEN fwd.rebasado = 1 THEN 100
                           WHEN ent.mov_id IS NULL THEN 0
                           ELSE ISNULL(cap.pct, 0) END AS DECIMAL(5,2)),
        estado = CASE WHEN fwd.rebasado = 1 THEN 'CERRADO'
                      WHEN ent.mov_id IS NULL THEN 'NO_INICIADO'
                      ELSE ISNULL(cap.estado, 'EN_CURSO') END,
        origen = CASE WHEN fwd.rebasado = 0 AND ent.mov_id IS NOT NULL AND cap.pct IS NOT NULL
                      THEN 'CAPTURA' ELSE 'ETAPA' END
) AS calc
WHERE r.nivel = 'VISTA';
GO

-- % por rubro y DP al cierre de cada semana (una fila del Excel por rubro).
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
        CAST(CASE WHEN r.nivel = 'DP' THEN ISNULL(md.pct, 0) ELSE ISNULL(va.pct, 0) END AS DECIMAL(5,2)) AS pct
FROM dp_semana AS ds
CROSS JOIN gd.rubro AS r
OUTER APPLY (
    SELECT TOP (1) m.pct
    FROM gd.mov_avance_dp AS m
    WHERE r.nivel = 'DP' AND m.dp_id = ds.dp_id AND m.rubro_id = r.rubro_id AND m.fecha <= ds.fecha_fin
    ORDER BY m.fecha DESC, m.mov_id DESC
) AS md
LEFT JOIN vista_agregado AS va
  ON r.nivel = 'VISTA' AND va.semana_id = ds.semana_id AND va.dp_id = ds.dp_id AND va.rubro_id = r.rubro_id;
GO

-- % total por DP y semana (columna "Avance" del Excel).
CREATE OR ALTER VIEW gd.vw_avance_dp_total_semana
AS
SELECT  a.semana_id, a.etiqueta, a.dp_id,
        d.nombre AS data_product, g.nombre AS gobierno, d.estatus,
        CAST(SUM(a.pct * a.peso) / SUM(a.peso) AS DECIMAL(5,2)) AS pct_avance,
        MAX(nv.num_vistas) AS num_vistas
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
GROUP BY a.semana_id, a.etiqueta, a.dp_id, d.nombre, g.nombre, d.estatus;
GO

-- Etapa actual de cada vista.
CREATE OR ALTER VIEW gd.vw_vista_etapa_actual
AS
SELECT  v.vista_id, v.nombre_negocio, v.ruta_tecnica, v.gobierno_id, v.responsable_id,
        COALESCE(e.etapa_id, e0.etapa_id)               AS etapa_id,
        ISNULL(e.orden, 0)                              AS etapa_orden,
        COALESCE(e.nombre, e0.nombre)                   AS etapa,
        COALESCE(e.tarea_pendiente, e0.tarea_pendiente) AS tarea_pendiente,
        COALESCE(e.rol_ejecutor, e0.rol_ejecutor)       AS rol_ejecutor,
        ISNULL(e.es_final, 0)                           AS es_final,
        ISNULL(u.fecha, v.fecha_alta)                   AS fecha_entrada,
        DATEDIFF(DAY, ISNULL(u.fecha, v.fecha_alta), CAST(GETDATE() AS DATE)) AS dias_en_etapa,
        u.motivo_id
FROM gd.vista_gobernada AS v
OUTER APPLY (
    SELECT TOP (1) m.etapa_id, m.fecha, m.motivo_id
    FROM gd.mov_etapa_vista AS m
    WHERE m.vista_id = v.vista_id
    ORDER BY m.fecha DESC, m.mov_id DESC
) AS u
LEFT JOIN gd.etapa_vista AS e ON e.etapa_id = u.etapa_id
CROSS JOIN (SELECT etapa_id, nombre, tarea_pendiente, rol_ejecutor FROM gd.etapa_vista WHERE orden = 0) AS e0
WHERE v.activa = 1;
GO

-- Pasos, regresos y ciclos por vista (para comparar cuánto trabajo requirió cada una).
CREATE OR ALTER VIEW gd.vw_metricas_flujo_vista
AS
WITH m AS (
    SELECT  mv.vista_id, mv.fecha, mv.es_regreso,
            LAG(e.es_final) OVER (PARTITION BY mv.vista_id ORDER BY mv.fecha, mv.mov_id) AS prev_final
    FROM gd.mov_etapa_vista AS mv
    JOIN gd.etapa_vista AS e ON e.etapa_id = mv.etapa_id
)
SELECT  vista_id,
        COUNT(*)                                           AS pasos_totales,
        SUM(CAST(es_regreso AS INT))                       AS regresos,
        1 + SUM(CASE WHEN prev_final = 1 THEN 1 ELSE 0 END) AS ciclos,
        MIN(fecha)                                         AS fecha_primer_mov,
        MAX(fecha)                                         AS fecha_ultimo_mov,
        DATEDIFF(DAY, MIN(fecha), CAST(GETDATE() AS DATE)) AS dias_desde_inicio
FROM m
GROUP BY vista_id;
GO

-- Bandeja de pendientes del equipo.
CREATE OR ALTER VIEW gd.vw_pendientes
AS
SELECT  ea.vista_id,
        ea.nombre_negocio            AS vista,
        ea.ruta_tecnica,
        g.nombre                     AS gobierno,
        d.nombre                     AS data_product,
        ea.etapa,
        ea.tarea_pendiente,
        ea.rol_ejecutor,
        r.nombre                     AS responsable,
        ea.fecha_entrada,
        ea.dias_en_etapa,
        ISNULL(mf.ciclos, 1)         AS ciclo,
        mo.nombre                    AS motivo_ultimo_mov
FROM gd.vw_vista_etapa_actual AS ea
JOIN gd.gobierno AS g ON g.gobierno_id = ea.gobierno_id
LEFT JOIN gd.responsable AS r ON r.responsable_id = ea.responsable_id
LEFT JOIN gd.dp_vista AS dv ON dv.vista_id = ea.vista_id AND dv.fecha_hasta IS NULL
LEFT JOIN gd.data_product AS d ON d.dp_id = dv.dp_id
LEFT JOIN gd.vw_metricas_flujo_vista AS mf ON mf.vista_id = ea.vista_id
LEFT JOIN gd.motivo AS mo ON mo.motivo_id = ea.motivo_id
WHERE ea.es_final = 0;
GO

-- Línea de tiempo unificada por vista y DP.
CREATE OR ALTER VIEW gd.vw_bitacora_actividades
AS
SELECT  m.fecha, N'Cambio de etapa' AS tipo, dv.dp_id, m.vista_id,
        CONCAT(e.nombre, CASE WHEN m.es_regreso = 1 THEN N' (regreso)' ELSE N'' END,
               N' · ', mo.nombre, ISNULL(N' · ' + m.comentario, N'')) AS detalle,
        m.usuario
FROM gd.mov_etapa_vista AS m
JOIN gd.etapa_vista AS e ON e.etapa_id = m.etapa_id
LEFT JOIN gd.motivo AS mo ON mo.motivo_id = m.motivo_id
OUTER APPLY (SELECT TOP (1) x.dp_id FROM gd.dp_vista AS x
             WHERE x.vista_id = m.vista_id AND x.fecha_desde <= m.fecha
               AND (x.fecha_hasta IS NULL OR x.fecha_hasta >= m.fecha)
             ORDER BY x.fecha_desde DESC) AS dv

UNION ALL
SELECT  a.fecha, N'Avance de rubro', dv.dp_id, a.vista_id,
        CONCAT(r.nombre, N': ', a.pct, N'% ', a.estado, ISNULL(N' · ' + a.comentario, N'')),
        a.usuario
FROM gd.mov_avance_vista AS a
JOIN gd.rubro AS r ON r.rubro_id = a.rubro_id
OUTER APPLY (SELECT TOP (1) x.dp_id FROM gd.dp_vista AS x
             WHERE x.vista_id = a.vista_id AND x.fecha_desde <= a.fecha
               AND (x.fecha_hasta IS NULL OR x.fecha_hasta >= a.fecha)
             ORDER BY x.fecha_desde DESC) AS dv

UNION ALL
SELECT  a.fecha, N'Avance de rubro DP', a.dp_id, NULL,
        CONCAT(r.nombre, N': ', a.pct, N'% ', a.estado, ISNULL(N' · ' + a.comentario, N'')),
        a.usuario
FROM gd.mov_avance_dp AS a
JOIN gd.rubro AS r ON r.rubro_id = a.rubro_id

UNION ALL
SELECT  x.fecha_desde, N'Vista agregada al DP', x.dp_id, x.vista_id, v.nombre_negocio, NULL
FROM gd.dp_vista AS x
JOIN gd.vista_gobernada AS v ON v.vista_id = x.vista_id

UNION ALL
SELECT  x.fecha_hasta, N'Vista retirada del DP', x.dp_id, x.vista_id, v.nombre_negocio, NULL
FROM gd.dp_vista AS x
JOIN gd.vista_gobernada AS v ON v.vista_id = x.vista_id
WHERE x.fecha_hasta IS NOT NULL

UNION ALL
SELECT  CAST(l.fecha_registro AS DATE), N'Nota semanal', l.dp_id, l.vista_id,
        CONCAT(s.etiqueta, N' · ', l.comentario), l.usuario
FROM gd.log_semanal AS l
JOIN gd.semana AS s ON s.semana_id = l.semana_id;
GO

/* =====================================================================
   9. VISTAS DE CALIDAD
   ---------------------------------------------------------------------
   Nivel de medición: la vista. Una regla cuenta para una vista si evalúa
   al menos un objeto BW vigente de esa vista en el periodo; cuenta una sola
   vez por vista y una sola vez por DP.
   Los niveles NO son aditivos (un objeto puede alimentar varias vistas):
     - Compañía y gobierno de la regla -> vw_calidad_regla
     - Vista -> vw_calidad_vista        - DP -> vw_calidad_dp
   En Power BI: calidad = DIVIDE(SUM(reg_validos), SUM(reg_evaluados)).
   ===================================================================== */
CREATE OR ALTER VIEW gd.vw_calidad_regla
AS
SELECT  rc.periodo, r.regla_id, r.codigo, r.descripcion, tr.nombre AS tipo_regla, r.dimension_dq,
        g.nombre AS gobierno_regla, resp.nombre AS responsable,
        rc.reg_evaluados, rc.reg_errores, rc.reg_validos, rc.pct_calidad, rc.fecha_carga
FROM gd.resumen_calidad AS rc
JOIN gd.regla AS r ON r.regla_id = rc.regla_id
LEFT JOIN gd.tipo_regla  AS tr   ON tr.tipo_regla_id    = r.tipo_regla_id
LEFT JOIN gd.gobierno    AS g    ON g.gobierno_id       = r.gobierno_id
LEFT JOIN gd.responsable AS resp ON resp.responsable_id = r.responsable_id;
GO

CREATE OR ALTER VIEW gd.vw_calidad_vista
AS
WITH res AS (
    SELECT regla_id, periodo, DATEFROMPARTS(periodo / 100, periodo % 100, 1) AS ini_mes
    FROM gd.resumen_calidad
),
vr AS (
    SELECT DISTINCT res.periodo, vo.vista_id, res.regla_id
    FROM res
    JOIN gd.regla_objeto AS ro ON ro.regla_id = res.regla_id
    JOIN gd.vista_objeto AS vo ON vo.objeto_id = ro.objeto_id
     AND vo.fecha_desde <= EOMONTH(res.ini_mes)
     AND (vo.fecha_hasta IS NULL OR vo.fecha_hasta >= res.ini_mes)
)
SELECT  vr.periodo, v.vista_id, v.nombre_negocio AS vista, v.ruta_tecnica, g.nombre AS gobierno,
        r.codigo AS regla, r.descripcion, r.dimension_dq,
        rc.reg_evaluados, rc.reg_errores, rc.reg_validos, rc.pct_calidad
FROM vr
JOIN gd.resumen_calidad  AS rc ON rc.regla_id = vr.regla_id AND rc.periodo = vr.periodo
JOIN gd.vista_gobernada  AS v  ON v.vista_id  = vr.vista_id
JOIN gd.gobierno         AS g  ON g.gobierno_id = v.gobierno_id
JOIN gd.regla            AS r  ON r.regla_id  = vr.regla_id;
GO

CREATE OR ALTER VIEW gd.vw_calidad_dp
AS
WITH res AS (
    SELECT regla_id, periodo, DATEFROMPARTS(periodo / 100, periodo % 100, 1) AS ini_mes
    FROM gd.resumen_calidad
),
dr AS (
    SELECT DISTINCT res.periodo, dv.dp_id, res.regla_id
    FROM res
    JOIN gd.regla_objeto AS ro ON ro.regla_id = res.regla_id
    JOIN gd.vista_objeto AS vo ON vo.objeto_id = ro.objeto_id
     AND vo.fecha_desde <= EOMONTH(res.ini_mes)
     AND (vo.fecha_hasta IS NULL OR vo.fecha_hasta >= res.ini_mes)
    JOIN gd.dp_vista AS dv ON dv.vista_id = vo.vista_id
     AND dv.fecha_desde <= EOMONTH(res.ini_mes)
     AND (dv.fecha_hasta IS NULL OR dv.fecha_hasta >= res.ini_mes)
)
SELECT  dr.periodo, d.dp_id, d.nombre AS data_product, g.nombre AS gobierno,
        r.codigo AS regla, r.descripcion, r.dimension_dq,
        rc.reg_evaluados, rc.reg_errores, rc.reg_validos, rc.pct_calidad
FROM dr
JOIN gd.resumen_calidad AS rc ON rc.regla_id = dr.regla_id AND rc.periodo = dr.periodo
JOIN gd.data_product    AS d  ON d.dp_id      = dr.dp_id
JOIN gd.gobierno        AS g  ON g.gobierno_id = d.gobierno_id
JOIN gd.regla           AS r  ON r.regla_id   = dr.regla_id;
GO

/* =====================================================================
   10. VALIDACIONES
   ===================================================================== */
CREATE OR ALTER VIEW gd.vw_validaciones
AS
SELECT N'Vista con gobierno distinto al de su DP' AS validacion, v.nombre_negocio AS objeto,
       CONCAT(N'Vista: ', gv.nombre, N' / DP: ', gdp.nombre) AS detalle
FROM gd.vista_gobernada AS v
JOIN gd.dp_vista     AS x   ON x.vista_id = v.vista_id AND x.fecha_hasta IS NULL
JOIN gd.data_product AS d   ON d.dp_id = x.dp_id
JOIN gd.gobierno     AS gv  ON gv.gobierno_id  = v.gobierno_id
JOIN gd.gobierno     AS gdp ON gdp.gobierno_id = d.gobierno_id
WHERE v.gobierno_id <> d.gobierno_id

UNION ALL
SELECT N'Data product activo sin vistas vigentes', d.nombre, d.estatus
FROM gd.data_product AS d
WHERE d.estatus IN ('PLANEADO','EN_CURSO')
  AND NOT EXISTS (SELECT 1 FROM gd.dp_vista AS x WHERE x.dp_id = d.dp_id AND x.fecha_hasta IS NULL)

UNION ALL
SELECT N'Data product cerrado con vista reabierta', d.nombre, CONCAT(ea.nombre_negocio, N' en ', ea.etapa)
FROM gd.data_product AS d
JOIN gd.dp_vista AS x ON x.dp_id = d.dp_id AND x.fecha_hasta IS NULL
JOIN gd.vw_vista_etapa_actual AS ea ON ea.vista_id = x.vista_id
WHERE d.estatus = 'CERRADO' AND ea.es_final = 0

UNION ALL
SELECT N'Regla con STAGE = SI sin resumen en el último periodo', r.codigo, CAST(p.periodo AS NVARCHAR(10))
FROM gd.regla AS r
CROSS JOIN (SELECT MAX(periodo) AS periodo FROM gd.resumen_calidad) AS p
WHERE r.activa = 1 AND r.escribe_log = 1 AND p.periodo IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM gd.resumen_calidad AS rc WHERE rc.regla_id = r.regla_id AND rc.periodo = p.periodo)

UNION ALL
SELECT N'Regla activa sin objetos BW', r.codigo, r.descripcion
FROM gd.regla AS r
WHERE r.activa = 1 AND NOT EXISTS (SELECT 1 FROM gd.regla_objeto AS ro WHERE ro.regla_id = r.regla_id)

UNION ALL
SELECT N'Objeto con reglas sin vista vigente (su calidad no llega a ninguna vista)', o.nombre, NULL
FROM gd.objeto_fuente AS o
WHERE EXISTS (SELECT 1 FROM gd.regla_objeto AS ro WHERE ro.objeto_id = o.objeto_id)
  AND NOT EXISTS (SELECT 1 FROM gd.vista_objeto AS vo WHERE vo.objeto_id = o.objeto_id AND vo.fecha_hasta IS NULL)

UNION ALL
SELECT N'Regla activa sin responsable', r.codigo, r.descripcion
FROM gd.regla AS r
WHERE r.activa = 1 AND r.responsable_id IS NULL

UNION ALL
SELECT N'Vista sin responsable', v.nombre_negocio, v.ruta_tecnica
FROM gd.vista_gobernada AS v
WHERE v.activa = 1 AND v.responsable_id IS NULL;
GO
