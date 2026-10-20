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

    public IReadOnlyList<FilaHistorialEvento> Eventos { get; private set; } = [];
    public TableroBajas? Tablero { get; private set; }
    public IReadOnlyList<FilaBaja> Filas { get; private set; } = [];
    public FilaBaja? Seleccionada { get; private set; }
    public IReadOnlyList<PasoTraza> Traza { get; private set; } = [];

    public IActionResult OnGet()
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        Eventos = recert.Historial();
        var id = Evento ?? recert.EventoActivo()?.EventoId;
        Tablero = id is null ? null : recert.Bajas(id.Value);
        if (Tablero is null) return Page();

        Filas = Estatus switch
        {
            "PENDIENTES" => Tablero.Filas.Where(f => f.Ejecucion is null).ToList(),
            "EJECUTADAS" => Tablero.Filas.Where(f => f.Ejecucion is not null).ToList(),
            _ => Tablero.Filas,
        };
        Seleccionada = Tablero.Filas.FirstOrDefault(f => f.AccesoId == Acceso) ?? Filas.FirstOrDefault();
        if (Seleccionada is not null) Traza = recert.Traza(Seleccionada.AccesoId);
        return Page();
    }

    public IActionResult OnPostEjecutar(long accesoId, string? ticket, DateOnly? fecha)
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        return Ejecutar(() => recert.RegistrarEjecucion(accesoId, ticket ?? "", fecha, usuario.Nombre),
            "Ejecución de la baja registrada.", () => RedirectToPage(new { Evento, Estatus, acceso = accesoId }));
    }

    public IActionResult OnGetEvidencia(int evento)
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        var nombre = recert.Historial().FirstOrDefault(h => h.EventoId == evento)?.Nombre ?? "evento";
        var archivo = string.Concat(nombre.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));
        return File(Csv.ABytesExcel(recert.EvidenciaCsv(evento)), "text/csv", $"evidencia_recertificacion_{archivo}.csv");
    }
}
