using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Pages.Shared;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GobiernoDatos.Web.Pages.Recertificacion;

public class BajasModel(IRecertificacionService recert, IUsuarioActual usuario) : PortalPageModel
{
    [BindProperty(SupportsGet = true)] public int? Evento { get; set; }
    [BindProperty(SupportsGet = true)] public string? Estatus { get; set; }     // null, PENDIENTES, EJECUTADAS
    [BindProperty(SupportsGet = true)] public long? Acceso { get; set; }
    [BindProperty(SupportsGet = true)] public string? Macro { get; set; }      // null = todas las macro áreas

    public IReadOnlyList<FilaHistorialEvento> Eventos { get; private set; } = [];
    public TableroBajas? Tablero { get; private set; }
    public IReadOnlyList<FilaBaja> Filas { get; private set; } = [];
    public FilaBaja? Seleccionada { get; private set; }
    public IReadOnlyList<PasoTraza> Traza { get; private set; } = [];
    // VoBo del gerente por macro área (con el filtro aplicado).
    public IReadOnlyList<BajasMacroArea> MacroAreas { get; private set; } = [];
    // Bajas de la macro área filtrada (todas si no hay filtro), para los indicadores.
    public IReadOnlyList<FilaBaja> DelArea { get; private set; } = [];
    public int Ejecutadas => DelArea.Count(f => f.Ejecucion is not null);
    public decimal? DiasPromedio
    {
        get
        {
            var dias = DelArea.Where(f => f.Ejecucion is not null)
                .Select(f => (decimal)(f.Ejecucion!.FechaEjecucion.DayNumber - DateOnly.FromDateTime(f.FechaDecision).DayNumber)).ToList();
            return dias.Count == 0 ? null : dias.Average();
        }
    }

    // Si la baja se puede ejecutar: requiere el VoBo autorizado de su macro área.
    public BajasMacroArea? AreaDe(FilaBaja f) => Tablero?.MacroAreas.FirstOrDefault(m => m.MacroArea == f.MacroArea);

    public IActionResult OnGet()
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        Eventos = recert.Historial();
        var id = Evento ?? recert.EventoActivo()?.EventoId;
        Tablero = id is null ? null : recert.Bajas(id.Value);
        if (Tablero is null) return Page();

        var delArea = Tablero.Filas.Where(f => Macro is null || f.MacroArea == Macro).ToList();
        DelArea = delArea;
        Filas = (Estatus switch
        {
            "PENDIENTES" => delArea.Where(f => f.Ejecucion is null),
            "EJECUTADAS" => delArea.Where(f => f.Ejecucion is not null),
            _ => delArea,
        }).ToList();
        MacroAreas = Tablero.MacroAreas.Where(m => Macro is null || m.MacroArea == Macro).ToList();
        Seleccionada = Tablero.Filas.FirstOrDefault(f => f.AccesoId == Acceso) ?? Filas.FirstOrDefault();
        if (Seleccionada is not null) Traza = recert.Traza(Seleccionada.AccesoId);
        return Page();
    }

    public IActionResult OnPostEjecutar(long accesoId, string? ticket, DateOnly? fecha)
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        return Ejecutar(() => recert.RegistrarEjecucion(accesoId, ticket ?? "", fecha, usuario.Nombre),
            "Ejecución de la baja registrada.", () => RedirectToPage(new { Evento, Estatus, Macro, acceso = accesoId }));
    }

    // Quita un ticket mal capturado (queda como anulado en la traza).
    public IActionResult OnPostAnular(long accesoId, string? motivo, string? ticket)
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        return Ejecutar(() => recert.AnularEjecucion(accesoId, motivo ?? "", usuario.Nombre),
            $"Ticket {ticket} quitado. La baja vuelve a pendiente de ejecución.", () => RedirectToPage(new { Evento, Estatus, Macro, acceso = accesoId }));
    }

    // Pide (o vuelve a pedir) el VoBo del gerente; Power Automate lo envía.
    public IActionResult OnPostSolicitarVoBo(int macroAreaId, string? macroArea)
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        return Ejecutar(() => recert.SolicitarVoBo(Evento!.Value, macroAreaId, usuario.Login),
            $"VoBo solicitado al gerente de {macroArea}. Power Automate le enviará la solicitud de aprobación.",
            () => RedirectToPage(null, null, new { Evento, Estatus, Macro }, "vobo"));
    }

    public IActionResult OnGetEvidencia(int evento)
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        var nombre = recert.Historial().FirstOrDefault(h => h.EventoId == evento)?.Nombre ?? "evento";
        var archivo = string.Concat(nombre.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));
        return File(Csv.ABytesExcel(recert.EvidenciaCsv(evento)), "text/csv", $"evidencia_recertificacion_{archivo}.csv");
    }
}
