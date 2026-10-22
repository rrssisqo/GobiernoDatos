using GobiernoDatos.Web.Pages.Shared;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GobiernoDatos.Web.Pages.Reglas;

// Catálogo de reglas de calidad (ABC): listado, baja lógica y reactivación. Alta y cambio en Editar.
public class IndexModel(IReglasService reglas, IUsuarioActual usuario) : PortalPageModel
{
    [BindProperty(SupportsGet = true)] public string? Estado { get; set; }   // null = activas, BAJAS, TODAS

    public IReadOnlyList<FilaRegla> Todas { get; private set; } = [];
    public IReadOnlyList<FilaRegla> Filas { get; private set; } = [];
    public IReadOnlyList<string> Frecuencias { get; private set; } = [];

    public void OnGet()
    {
        Todas = reglas.Listar();
        Frecuencias = reglas.Catalogos().Frecuencias;
        Filas = Estado switch
        {
            "BAJAS" => Todas.Where(r => !r.Activa).ToList(),
            "TODAS" => Todas,
            _ => Todas.Where(r => r.Activa).ToList(),
        };
    }

    public IActionResult OnPostBaja(int reglaId, string? motivo, string? codigo) =>
        Ejecutar(() => reglas.DarDeBaja(reglaId, motivo ?? "", usuario.Login),
                 $"Regla {codigo} dada de baja. Conserva su historial de resultados y se puede reactivar.", () => RedirectToPage(new { Estado }));

    public IActionResult OnPostCrearFrecuencia(string? nombre) =>
        Ejecutar(() => reglas.CrearFrecuencia(nombre ?? ""), $"Frecuencia «{nombre?.Trim()}» agregada al catálogo.",
                 () => RedirectToPage(null, null, new { Estado }, "frecuencias"));

    public IActionResult OnPostEliminarFrecuencia(string nombre) =>
        Ejecutar(() => reglas.EliminarFrecuencia(nombre), $"Frecuencia «{nombre}» eliminada del catálogo.",
                 () => RedirectToPage(null, null, new { Estado }, "frecuencias"));

    public IActionResult OnPostReactivar(int reglaId, string? codigo) =>
        Ejecutar(() => reglas.Reactivar(reglaId, usuario.Login), $"Regla {codigo} reactivada.", () => RedirectToPage(new { Estado }));
}
