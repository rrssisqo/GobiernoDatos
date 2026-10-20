using System.IO.Compression;
using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;
using GobiernoDatos.Web.Pages.Shared;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GobiernoDatos.Web.Pages.Auditoria;

// Auditoría de la recertificación con las columnas de la consulta HANA (CONT_TI_RECERTIFICACION), en su orden;
// OKBAJA y COMENTARIOS = decisión y comentario capturados en el portal.
// Filtros por evento (0 = todos), área responsable (macro área), OKBAJA y texto; exportación a Excel completa o un archivo por macro área.
public class IndexModel(IRecertificacionService recert, IUsuarioActual usuario) : PortalPageModel
{
    public const int TamanoPagina = 100;

    [BindProperty(SupportsGet = true)] public int? Evento { get; set; }
    [BindProperty(SupportsGet = true)] public string? Macro { get; set; }
    [BindProperty(SupportsGet = true)] public string? Decision { get; set; }   // PENDIENTE, CONTINUA, BAJA
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public int Pagina { get; set; } = 1;

    public IReadOnlyList<FilaHistorialEvento> Eventos { get; private set; } = [];
    public IReadOnlyList<string> MacroAreas { get; private set; } = [];
    public IReadOnlyList<FilaAuditoria> Filas { get; private set; } = [];       // filtradas, todas las páginas
    public IReadOnlyList<FilaAuditoria> PaginaActual { get; private set; } = [];
    public Conteo Conteo { get; private set; } = new(0, 0, 0);
    public int Paginas => Math.Max(1, (Filas.Count + TamanoPagina - 1) / TamanoPagina);

    public IActionResult OnGet()
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        var todas = Cargar();
        Filas = Filtrar(todas, Macro);
        Conteo = new(Filas.Count, Filas.Count(f => f.OkBaja == Domain.Decision.CONTINUA), Filas.Count(f => f.OkBaja == Domain.Decision.BAJA));
        Pagina = Math.Clamp(Pagina, 1, Paginas);
        PaginaActual = Filas.Skip((Pagina - 1) * TamanoPagina).Take(TamanoPagina).ToList();
        return Page();
    }

    public IActionResult OnGetExportar()
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        var filas = Filtrar(Cargar(), Macro);
        var sufijo = Macro is null ? "" : "_" + EscritorExcel.NombreArchivo(Macro);
        return File(Libro("Auditoría", filas), TipoXlsx, $"auditoria_recertificacion_{NombreEvento()}{sufijo}.xlsx");
    }

    // Un .xlsx por macro área, en un .zip (los navegadores no descargan varios archivos de una sola respuesta).
    public IActionResult OnGetExportarPorMacro()
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        var filas = Filtrar(Cargar(), null);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var g in filas.GroupBy(f => f.AreaResponsable).OrderBy(g => g.Key))
            {
                var entrada = zip.CreateEntry($"auditoria_{EscritorExcel.NombreArchivo(g.Key)}.xlsx", CompressionLevel.Fastest);
                using var destino = entrada.Open();
                destino.Write(Libro(g.Key, g));
            }
        }
        return File(ms.ToArray(), "application/zip", $"auditoria_por_macro_area_{NombreEvento()}.zip");
    }

    private const string TipoXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // Mismas columnas y orden que la consulta HANA.
    public static readonly string[] ColumnasHana =
        ["AREA", "CONTENT", "ROL", "VISTA", "USUARIO", "NAME", "GOBIERNO", "AREARESPONSABLE", "RESPONSABLE", "OKBAJA", "COMENTARIOS", "ACCESOS"];

    public static object?[] Valores(FilaAuditoria f) =>
        [f.Area, f.Content, f.Rol, f.Vista, f.Usuario, f.Name, f.Gobierno, f.AreaResponsable, f.Responsable, OkBaja(f.OkBaja), f.Comentarios, f.Accesos];

    private static byte[] Libro(string hoja, IEnumerable<FilaAuditoria> filas) =>
        EscritorExcel.Libro(hoja, ColumnasHana, filas.Select(Valores));

    // Valores de la columna OKBAJA: OK (continúa) o BAJA; vacío mientras no hay decisión.
    public static string? OkBaja(Decision? d) => d switch { Domain.Decision.CONTINUA => "OK", Domain.Decision.BAJA => "BAJA", _ => null };

    // Sin evento en la URL: el evento activo; 0 = todos los eventos.
    private IReadOnlyList<FilaAuditoria> Cargar()
    {
        Eventos = recert.Historial();
        Evento ??= recert.EventoActivo()?.EventoId ?? 0;
        var filas = recert.Auditoria(Evento == 0 ? null : Evento);
        MacroAreas = filas.Select(f => f.AreaResponsable).Distinct().Order().ToList();
        return filas;
    }

    private List<FilaAuditoria> Filtrar(IEnumerable<FilaAuditoria> filas, string? macro)
    {
        var q = Q?.Trim();
        return filas.Where(f =>
                (macro is null || f.AreaResponsable == macro) &&
                (Decision switch
                {
                    "PENDIENTE" => f.OkBaja is null,
                    "CONTINUA" => f.OkBaja == Domain.Decision.CONTINUA,
                    "BAJA" => f.OkBaja == Domain.Decision.BAJA,
                    _ => true,
                }) &&
                (string.IsNullOrEmpty(q) || new[] { f.Area, f.Content, f.Rol, f.Vista, f.Usuario, f.Name, f.Gobierno, f.Responsable, f.Comentarios }
                    .Any(t => t?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)))
            .ToList();
    }

    private string NombreEvento() =>
        Evento is null or 0 ? "todos_los_eventos"
        : EscritorExcel.NombreArchivo(Eventos.FirstOrDefault(e => e.EventoId == Evento)?.Nombre ?? $"evento_{Evento}");
}
