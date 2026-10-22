using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;
using GobiernoDatos.Web.Pages.Shared;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GobiernoDatos.Web.Pages.Auditoria;

// Evidencia de la respuesta de cada macro área: por acceso, OKBAJA (OK = continúa, BAJA) y COMENTARIOS que capturó el
// certificador en el portal, con quién respondió y cuándo. Filtros por evento, macro área, respuesta y texto; exportación a Excel.
public class RespuestasModel(IRecertificacionService recert, IUsuarioActual usuario) : PortalPageModel
{
    public const int TamanoPagina = 100;

    [BindProperty(SupportsGet = true)] public int? Evento { get; set; }
    [BindProperty(SupportsGet = true)] public string? Macro { get; set; }
    [BindProperty(SupportsGet = true)] public string? Respuesta { get; set; }   // OK, BAJA, PENDIENTE
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public int Pagina { get; set; } = 1;

    public IReadOnlyList<FilaHistorialEvento> Eventos { get; private set; } = [];
    public IReadOnlyList<string> MacroAreas { get; private set; } = [];
    public IReadOnlyList<FilaAuditoria> Filas { get; private set; } = [];
    public IReadOnlyList<FilaAuditoria> PaginaActual { get; private set; } = [];
    public int Paginas => Math.Max(1, (Filas.Count + TamanoPagina - 1) / TamanoPagina);
    // Resumen por macro área del evento (sin filtros de respuesta ni texto).
    public IReadOnlyList<(string MacroArea, int Total, int Ok, int Baja, int Pendientes)> Resumen { get; private set; } = [];

    public static readonly string[] Columnas =
        ["EVENTO", "AREARESPONSABLE", "USUARIO", "NAME", "ROL", "VISTA", "OKBAJA", "COMENTARIOS", "RESPONDIÓ", "FECHA RESPUESTA"];

    public static string? OkBaja(Decision? d) => d switch { Decision.CONTINUA => "OK", Decision.BAJA => "BAJA", _ => null };

    public IActionResult OnGet()
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        Filas = Filtrar(Cargar());
        Pagina = Math.Clamp(Pagina, 1, Paginas);
        PaginaActual = Filas.Skip((Pagina - 1) * TamanoPagina).Take(TamanoPagina).ToList();
        return Page();
    }

    public IActionResult OnGetExportar()
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        var filas = Filtrar(Cargar());
        var evento = Evento is null or 0 ? "todos_los_eventos"
            : EscritorExcel.NombreArchivo(Eventos.FirstOrDefault(e => e.EventoId == Evento)?.Nombre ?? $"evento_{Evento}");
        var sufijo = Macro is null ? "" : "_" + EscritorExcel.NombreArchivo(Macro);
        var libro = EscritorExcel.Libro(
        [
            ("Respuestas", Columnas, filas.Select(f => new object?[]
                { f.Evento, f.AreaResponsable, f.Usuario, f.Name, f.Rol, f.Vista, OkBaja(f.OkBaja), f.Comentarios, f.DecididoPor, f.FechaDecision })),
            ("Resumen por macro área", ["AREARESPONSABLE", "ACCESOS", "OK", "BAJA", "PENDIENTES"],
             Resumen.Select(r => new object?[] { r.MacroArea, r.Total, r.Ok, r.Baja, r.Pendientes })),
        ]);
        return File(libro, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"evidencia_respuestas_{evento}{sufijo}.xlsx");
    }

    // Sin evento en la URL: el evento activo; 0 = todos los eventos.
    private IReadOnlyList<FilaAuditoria> Cargar()
    {
        Eventos = recert.Historial();
        Evento ??= recert.EventoActivo()?.EventoId ?? 0;
        var filas = recert.Auditoria(Evento == 0 ? null : Evento);
        MacroAreas = filas.Select(f => f.AreaResponsable).Distinct().Order().ToList();
        Resumen = filas.Where(f => Macro is null || f.AreaResponsable == Macro).GroupBy(f => f.AreaResponsable).OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Count(), g.Count(f => f.OkBaja == Decision.CONTINUA), g.Count(f => f.OkBaja == Decision.BAJA), g.Count(f => f.OkBaja is null)))
            .ToList();
        return filas;
    }

    private List<FilaAuditoria> Filtrar(IEnumerable<FilaAuditoria> filas)
    {
        var q = Q?.Trim();
        return filas.Where(f =>
                (Macro is null || f.AreaResponsable == Macro) &&
                (Respuesta switch
                {
                    "OK" => f.OkBaja == Decision.CONTINUA,
                    "BAJA" => f.OkBaja == Decision.BAJA,
                    "PENDIENTE" => f.OkBaja is null,
                    _ => true,
                }) &&
                (string.IsNullOrEmpty(q) || new[] { f.Usuario, f.Name, f.Rol, f.Vista, f.Comentarios, f.DecididoPor }
                    .Any(t => t?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)))
            .ToList();
    }
}
