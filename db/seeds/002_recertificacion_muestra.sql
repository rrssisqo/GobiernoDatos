/* =====================================================================
   Extracto de recertificación de muestra (data/muestras/recertificacion_extracto_muestra.csv).
   Requiere la migración 001. Crea el evento en BORRADOR: abrirlo a certificadores es decisión del administrador.
   Idempotente: no hace nada si el evento ya existe.
   El extracto no trae "Responsable": las macro áreas quedan sin certificador hasta asignarlo.
   ===================================================================== */
SET NOCOUNT ON;

IF EXISTS (SELECT 1 FROM gd.recert_evento WHERE nombre = N'[Evento muestra]')
BEGIN
    PRINT N'El evento de muestra ya existe; no se carga de nuevo.';
    RETURN;
END;

TRUNCATE TABLE gd.stg_recertificacion;

INSERT INTO gd.stg_recertificacion (area, content, usuario, nombre, rol, vista, accesos_3m, area_responsable, responsable) VALUES
 (N'GENERICO',     N'ROL.GENERICO',     N'BFRMEXFN', N'Fernandez, Beatriz (MEX, PAL, CV)', N'CON_GEN_CALENDARIO_FISCAL_GOVERNANCE',        N'MABE.AGENERICO.GENERICO/CONTINENTAL_GENERICO_CALENDARIOFISCAL',        N'1673', N'TI',                      NULL),
 (N'CADENA_VALOR', N'ROL.CADENA_VALOR', N'CASCOSSC', N'Arias, Cristhian (CRI, HER, PP)',   N'CON_CDV_ENTRADAS0MATERIAL0PT_PLANINV',        N'MABE.CADENA_VALOR.ENTREGAS.ENTRANTES/CONTINENTAL_ENTREGAS_ENTRANTES',   N'1440', N'Cadena de Valor',         NULL),
 (N'GENERICO',     N'ROL.GENERICO',     N'YAZMEXCV', N'Ayala, Yareth (MEX, PAL, CV)',      N'CON_GEN_CALENDARIO_FISCAL_GOVERNANCE',        N'MABE.AGENERICO.GENERICO/CONTINENTAL_GENERICO_CALENDARIOFISCAL',        N'1012', N'TI',                      NULL),
 (N'CADENA_VALOR', N'ROL.CADENA_VALOR', N'CASCOSSC', N'Arias, Cristhian (CRI, HER, PP)',   N'CON_CDV_ORDENES0COMPRA0PT_PLANPROD',          N'MABE.CADENA_VALOR.COMPRAS/CONTINENTAL_COMPRAS_GOVERNANCE',             N'921',  N'Cadena de Valor',         NULL),
 (N'GENERICO',     N'ROL.GENERICO',     N'MJGMEXCV', N'Jaramillo, Mauricio (MEX, PAL, CV)', N'CON_GEN_DM_MATERIAL_GOVERNANCE',             N'MABE.AGENERICO.GENERICO/DM_MATERIAL',                                  N'869',  N'Datos Maestros Material', NULL),
 (N'GENERICO',     N'ROL.GENERICO',     N'CASCOSSC', N'Arias, Cristhian (CRI, HER, PP)',   N'CON_GEN_CALENDARIO_FISCAL_GOVERNANCE',        N'MABE.AGENERICO.GENERICO/CONTINENTAL_GENERICO_CALENDARIOFISCAL',        N'859',  N'TI',                      NULL),
 (N'CSC',          N'ROL.CSC',          N'CASCOSSC', N'Arias, Cristhian (CRI, HER, PP)',   N'CON_CSC_TIPOCAMBIO',                          N'MABE.AGENERICO.GENERICO/CONTINENTAL_GENERICO_TIPOCAMBIO',              N'859',  N'Finanzas',                NULL),
 (N'CADENA_VALOR', N'ROL.CADENA_VALOR', N'CASCOSSC', N'Arias, Cristhian (CRI, HER, PP)',   N'CON_CDV_INVENTARIO0FINANCIERO0COSTO_PLANINV', N'MABE.CADENA_VALOR.INVENTARIOS.COSTEADO/CONTINENTAL_COSTEADO_GOVERNANCE', N'827',  N'Cadena de Valor',         NULL),
 (N'GENERICO',     N'ROL.GENERICO',     N'KCCMEXFI', N'Canche, Kathia (MEX, PAL, FI)',     N'CON_GEN_DM_REGIONSOCIEDAD_GOV',               N'MABE.AGENERICO.GENERICO/DM_GENERICO_REGIONSOCIEDAD',                   N'809',  N'TI',                      NULL);

EXEC gd.sp_cargar_extracto_recertificacion @nombre = N'[Evento muestra]', @usuario = N'migración';
