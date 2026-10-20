using GobiernoDatos.Web.Pages.Shared;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GobiernoDatos.Web.Pages.Tareas;

public class IndexModel(ITareasService tareas, IVistasService vistas, IUsuarioActual usuario) : PortalPageModel
{
    // Estatus: ABIERTA (por omisión), TERMINADA, CANCELADA o "TODAS".
    [BindProperty(SupportsGet = true)] public string? Estatus { get; set; }
    [BindProperty(SupportsGet = true)] public bool Mias { get; set; }
    [BindProperty(SupportsGet = true)] public int? Dp { get; set; }

    public IReadOnlyList<FilaTarea> Filas { get; private set; } = [];
    public IReadOnlyList<OpcionDp> DataProducts { get; private set; } = [];
    public int Abiertas { get; private set; }
    public int MiasAbiertas { get; private set; }
    public int Terminadas { get; private set; }
    public decimal? DiasPromedioAbiertas { get; private set; }

    public string EstatusEfectivo => Estatus ?? EstatusTarea.Abierta;

    public void OnGet()
    {
        var todas = tareas.Listar(new FiltroTareas(null, null, Dp));
        var abiertas = todas.Where(t => t.Estatus == EstatusTarea.Abierta).ToList();
        Abiertas = abiertas.Count;
        MiasAbiertas = abiertas.Count(t => EsMia(t));
        Terminadas = todas.Count(t => t.Estatus == EstatusTarea.Terminada);
        DiasPromedioAbiertas = abiertas.Count == 0 ? null : (decimal)abiertas.Average(t => t.DiasTranscurridos);

        Filas = todas.Where(t => EstatusEfectivo == "TODAS" || t.Estatus == EstatusEfectivo)
                     .Where(t => !Mias || EsMia(t))
                     .ToList();
        DataProducts = vistas.DataProductsAbiertos();
    }

    public bool EsMia(FilaTarea t) => string.Equals(t.AsignadoA, usuario.Login, StringComparison.OrdinalIgnoreCase);

    public IActionResult OnPostTerminar(int tareaId) =>
        Ejecutar(() => tareas.Terminar(tareaId, usuario.Login), "Tarea marcada como terminada.",
                 () => RedirectToPage(new { Estatus, Mias = Mias ? true : (bool?)null, Dp }));
}
