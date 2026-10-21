using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Pages.Shared;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GobiernoDatos.Web.Pages.Recertificacion;

// ABC del catálogo GOBIERNO (extracto HANA) → macro área; antes era una tabla fija dentro de la vista HANA.
public class MacroAreasModel(IRecertificacionService recert, IUsuarioActual usuario) : PortalPageModel
{
    public IReadOnlyList<MapeoGobierno> Mapeos { get; private set; } = [];
    public IReadOnlyList<string> MacroAreas { get; private set; } = [];

    public IActionResult OnGet()
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        Mapeos = recert.MapeosGobierno();
        MacroAreas = recert.Certificadores().Select(m => m.MacroArea).ToList();
        return Page();
    }

    public IActionResult OnPostCrear(string? gobierno, string? macroArea) =>
        Admin(() => recert.CrearMapeoGobierno(gobierno ?? "", macroArea ?? "", usuario.Login), $"{gobierno?.Trim()} → {macroArea?.Trim()} agregado.");

    public IActionResult OnPostCambiar(string gobierno, string? macroArea) =>
        Admin(() => recert.CambiarMapeoGobierno(gobierno, macroArea ?? "", usuario.Login), $"{gobierno} ahora va a {macroArea?.Trim()}.");

    public IActionResult OnPostEliminar(string gobierno) =>
        Admin(() => recert.EliminarMapeoGobierno(gobierno), $"{gobierno} eliminado del catálogo.");

    private IActionResult Admin(Action accion, string ok)
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        return Ejecutar(accion, ok, () => RedirectToPage());
    }
}
