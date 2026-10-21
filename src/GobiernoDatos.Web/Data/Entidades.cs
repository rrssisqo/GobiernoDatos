using GobiernoDatos.Web.Domain;

namespace GobiernoDatos.Web.Data;

// Entidades del almacén en memoria. Nombres y columnas siguen el modelo ER v3 (docs/modelo_er_v3.mmd).

public sealed class DataProduct
{
    public int DpId { get; init; }
    public required string Nombre { get; set; }
    public required string Gobierno { get; set; }
    public string? Responsable { get; set; }
    public string Estatus { get; set; } = "EN_CURSO";       // PLANEADO, EN_CURSO, CERRADO, CANCELADO
    public DateOnly? FechaAlta { get; set; }
    public DateOnly? FechaCierre { get; set; }
    public int? VistasIniciales { get; set; }               // Columna "Vistas" del Excel (migración inicial)
}

public sealed class VistaGobernada
{
    public int VistaId { get; init; }
    public required string RutaTecnica { get; init; }       // normalizada
    public required string NombreNegocio { get; set; }
    public string? Gobierno { get; set; }                   // NULL hasta clasificar
    public string? Subdominio { get; set; }
    public string? Responsable { get; set; }
    public string? NivelSeguridad { get; set; }
    public DateOnly FechaAlta { get; init; }
    public DateOnly? FechaPrimeraAparicion { get; set; }
    public DateOnly? FechaUltimaAparicion { get; set; }
    public bool EnUltimaRecertificacion { get; set; }
    public bool Activa { get; set; } = true;
}

public sealed class DpVista
{
    public int DpId { get; init; }
    public int VistaId { get; init; }
    public DateOnly FechaDesde { get; init; }
    public DateOnly? FechaHasta { get; set; }
    public bool VigenteEn(DateOnly f) => FechaDesde <= f && (FechaHasta is null || FechaHasta >= f);
}

public sealed class MovEtapaVista
{
    public long MovId { get; init; }
    public int VistaId { get; init; }
    public int EtapaOrden { get; init; }
    public DateOnly Fecha { get; init; }
    public int? MotivoId { get; init; }
    public bool EsRegreso { get; init; }
    public string? Comentario { get; init; }
    public required string Usuario { get; init; }
}

// Captura de % por rubro. VistaId = mov_avance_vista; DpId = mov_avance_dp.
// EsInicial marca los valores de rubros de vista que el Excel trae a nivel DP (ver README del portal).
public sealed class MovAvance
{
    public long MovId { get; init; }
    public int? DpId { get; init; }
    public int? VistaId { get; init; }
    public int RubroId { get; init; }
    public DateOnly Fecha { get; init; }
    public decimal Pct { get; init; }
    public EstadoAvance Estado { get; init; }
    public string? Comentario { get; init; }
    public required string Usuario { get; init; }
    public bool EsInicial { get; init; }
}

public sealed class NotaSemanal
{
    public long LogId { get; init; }
    public int DpId { get; init; }
    public int SemanaId { get; init; }
    public int? VistaId { get; init; }
    public int? RubroId { get; init; }
    public required string Comentario { get; init; }
    public required string Usuario { get; init; }
    public DateTime FechaRegistro { get; init; }
}

// Tarea pendiente de la etapa de una vista, asignada a un usuario de red (gd.tarea_vista).
public sealed class TareaVista
{
    public int TareaId { get; init; }
    public int VistaId { get; init; }
    public int EtapaOrden { get; init; }
    public required string AsignadoA { get; init; }
    public required string AsignadoNombre { get; init; }
    public required string AsignadoPor { get; init; }
    public DateTime FechaAsignacion { get; init; }
    public string Estatus { get; set; } = "ABIERTA";
    public DateTime? FechaCierre { get; set; }
    public string? CerradoPor { get; set; }
    public string? MotivoCierre { get; set; }
}

public sealed class ObjetoFuente
{
    public int ObjetoId { get; init; }
    public required string Nombre { get; init; }
}

public sealed class VistaObjeto
{
    public int VistaId { get; init; }
    public int ObjetoId { get; init; }
    public DateOnly FechaDesde { get; init; }
    public DateOnly? FechaHasta { get; set; }
}

public sealed class Regla
{
    public int ReglaId { get; init; }
    public required string Codigo { get; init; }
    public string? TipoRegla { get; set; }
    public required string Descripcion { get; set; }
    public string? ReglaFuncional { get; set; }
    public string? Gobierno { get; set; }
    public string? Responsable { get; set; }
    public string? CamposEvaluados { get; set; }
    public string? DimensionDq { get; set; }
    public int? NoCsv { get; set; }
    public string? NombreCsv { get; set; }
    public bool EscribeLog { get; set; }
    public string? Etapa { get; set; }
    public bool Activa { get; set; } = true;
    public List<int> Objetos { get; } = [];
    public string? PropositoNegocio { get; set; }
    public string? TablaStg { get; set; }
    public string? Mapping { get; set; }
    public string? Frecuencia { get; set; }
    public string? CorreoAsunto { get; set; }
    public string? QueryValidacion { get; set; }
    public List<string> Destinatarios { get; } = [];
    public DateOnly? FechaAlta { get; set; }
}

public sealed class ResumenCalidad
{
    public int ReglaId { get; init; }
    public int Periodo { get; init; }                        // AAAAMM
    public long Evaluados { get; set; }
    public long Errores { get; set; }
    public long Validos => Evaluados - Errores;
    public DateTime FechaCarga { get; set; }
}

public sealed record FilaResumenCalidad(string? ReglaCodigo, string? NombreCsv, int? Periodo, long? Evaluados, long? Errores);

public sealed class CargaRechazo
{
    public required FilaResumenCalidad Fila { get; init; }
    public required string Motivo { get; init; }
    public DateTime FechaCarga { get; init; }
}

// ---- Recertificación (v3) ----

public sealed class MacroArea
{
    public int MacroAreaId { get; init; }
    public required string Nombre { get; init; }
    public List<CertificadorMacro> Certificadores { get; } = [];   // titular y delegados (macro_area_certificador)
}

// UsuarioRed nulo = responsable que llegó por nombre (columna "Responsable" del extracto): nadie puede actuar como él
// hasta asignarle su usuario de red.
public sealed record CertificadorMacro(string? UsuarioRed, string Nombre, string Tipo);

public sealed class UsuarioHana
{
    public required string Codigo { get; init; }
    public required string Nombre { get; set; }
}

public sealed class RolHana
{
    public required string Nombre { get; init; }
    public int VistaId { get; init; }                        // 1 rol = 1 vista
    public string? Area { get; set; }
    public string? Content { get; set; }
}

public sealed class RecertEvento
{
    public int EventoId { get; init; }
    public required string Nombre { get; init; }
    public DateOnly? FechaExtracto { get; init; }
    public DateOnly? FechaInicio { get; set; }
    public DateOnly? FechaLimite { get; init; }
    public DateOnly? FechaCierre { get; set; }
    public EstatusEvento Estatus { get; set; } = EstatusEvento.BORRADOR;
    public ValidacionCarga Validacion { get; set; } = new();
    public List<RecertCargaRechazo> Rechazos { get; } = [];
    public List<ConfirmacionMacroArea> Confirmaciones { get; } = [];
}

public sealed record ValidacionCarga(int RolesConVariasVistas = 0, int VistasConVariosRoles = 0,
                                     int RolesNuevos = 0, int VistasNuevas = 0, int FilasRechazadas = 0);

public sealed record RecertCargaRechazo(int Fila, string? Usuario, string? Rol, string? Vista, string Motivo);

public sealed record ConfirmacionMacroArea(int MacroAreaId, string Usuario, DateTime Fecha);

public sealed class RecertAcceso
{
    public long AccesoId { get; init; }
    public int EventoId { get; init; }
    public required string UsuarioCodigo { get; init; }
    public required string Rol { get; init; }
    public int VistaId { get; init; }
    public int MacroAreaId { get; init; }
    public int Accesos3m { get; init; }
    // Tal como llegaron en el extracto (columnas de la consulta HANA).
    public string? Area { get; init; }
    public string? Content { get; init; }
    public string? Gobierno { get; init; }
    public string? Responsable { get; init; }
}

public sealed class RecertDecision
{
    public long DecisionId { get; init; }
    public long AccesoId { get; init; }
    public Decision Decision { get; init; }
    public string? Comentario { get; init; }
    public required string DecididoPor { get; init; }
    public DateTime FechaRegistro { get; init; }
}

// Renglón del catálogo GOBIERNO (HANA) → macro área.
public sealed record MapeoGobierno(string Gobierno, string MacroArea, string Usuario, DateTime Fecha);

// Recordatorio por correo a un certificador (gd.recert_recordatorio, migración 009).
public sealed record RecertRecordatorio(int EventoId, string Certificador, string? UsuarioRed, string? Correo, string MacroAreas,
                                        int Pendientes, bool Enviado, string? Error, string EnviadoPor, DateTime Fecha);

// Ticket de baja anulado (gd.recert_baja_ejecucion_anulada, migración 013).
public sealed record RecertBajaAnulada(long AccesoId, string Ticket, DateOnly FechaEjecucion, string EjecutadoPor, string Motivo,
                                       string AnuladoPor, DateTime FechaAnulacion);

public sealed class RecertBajaEjecucion
{
    public long AccesoId { get; init; }
    public DateOnly FechaEjecucion { get; init; }
    public required string Ticket { get; init; }
    public required string EjecutadoPor { get; init; }
}

public sealed class VistaUso
{
    public int EventoId { get; init; }
    public int VistaId { get; init; }
    public int Accesos3m { get; set; }
    public int Usuarios { get; set; }
    public int Roles { get; set; }
}
