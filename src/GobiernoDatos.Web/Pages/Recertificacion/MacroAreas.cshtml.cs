using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Pages.Shared;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GobiernoDatos.Web.Pages.Recertificacion;

// ABC del catálogo GOBIERNO (extracto HANA) → macro área (antes una tabla fija dentro de la vista HANA)
// y del gerente que da el VoBo de las bajas de todas las macro áreas antes del ticket (Power Automate).
public class MacroAreasModel(IRecertificacionService recert, IUsuarioActual usuario) : PortalPageModel
{
    public IReadOnlyList<MapeoGobierno> Mapeos { get; private set; } = [];
    public IReadOnlyList<string> MacroAreas { get; private set; } = [];
    public GerenteVoBo? Gerente { get; private set; }

    public IActionResult OnGet()
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        Mapeos = recert.MapeosGobierno();
        MacroAreas = recert.Certificadores().Select(m => m.MacroArea).ToList();
        Gerente = recert.Gerente();
        return Page();
    }

    public IActionResult OnPostCrear(string? gobierno, string? macroArea) =>
        Admin(() => recert.CrearMapeoGobierno(gobierno ?? "", macroArea ?? "", usuario.Login), $"{gobierno?.Trim()} → {macroArea?.Trim()} agregado.");

    public IActionResult OnPostCambiar(string gobierno, string? macroArea) =>
        Admin(() => recert.CambiarMapeoGobierno(gobierno, macroArea ?? "", usuario.Login), $"{gobierno} ahora va a {macroArea?.Trim()}.");

    public IActionResult OnPostEliminar(string gobierno) =>
        Admin(() => recert.EliminarMapeoGobierno(gobierno), $"{gobierno} eliminado del catálogo.");

    public IActionResult OnPostAsignarGerente(string? usuarioRed) =>
        Admin(() => recert.AsignarGerente(usuarioRed ?? "", usuario.Login), "Gerente asignado: autoriza las bajas de todas las macro áreas.", "gerente");

    public IActionResult OnPostQuitarGerente() =>
        Admin(() => recert.QuitarGerente(), "Se quitó el gerente.", "gerente");

    private IActionResult Admin(Action accion, string ok, string? fragmento = null)
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        return Ejecutar(accion, ok, () => RedirectToPage(null, null, null, fragmento));
    }
}
