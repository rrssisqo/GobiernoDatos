using GobiernoDatos.Web.Pages.Shared;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GobiernoDatos.Web.Pages.DataProducts;

public class IndexModel(ISeguimientoService seguimiento, IUsuarioActual usuario) : PortalPageModel
{
    public IReadOnlyList<ResumenDp> DataProducts { get; private set; } = [];
    public IReadOnlyList<string> Gobiernos { get; private set; } = [];
    public IReadOnlyList<string> Responsables { get; private set; } = [];

    [BindProperty] public string Nombre { get; set; } = "";
    [BindProperty] public string Gobierno { get; set; } = "";
    [BindProperty] public string? Responsable { get; set; }

    public void OnGet()
    {
        DataProducts = seguimiento.DataProducts();
        Gobiernos = seguimiento.Gobiernos();
        Responsables = seguimiento.Responsables();
    }

    public IActionResult OnPostCrear()
    {
        int id = 0;
        return Ejecutar(() => id = seguimiento.CrearDataProduct(Nombre, Gobierno, Responsable, usuario.Login),
            $"Se creó el data product «{Nombre}». Asígnale vistas desde Vistas gobernadas.",
            () => id == 0 ? RedirectToPage() : RedirectToPage("Detalle", new { id }));
    }
}
