using GobiernoDatos.Web.Domain;
using GobiernoDatos.Web.Pages.Shared;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GobiernoDatos.Web.Pages.Recertificacion;

public class MiModel(IRecertificacionService recert, IUsuarioActual usuario) : PortalPageModel
{
    public MiRecertificacion Datos { get; private set; } = null!;

    public IActionResult OnGet()
    {
        if (!usuario.TieneRol(RolesPortal.Certificador)) return SinPermiso();
        Datos = recert.MisAccesos(usuario.Login);
        return Page();
    }

    // Cada decisión se guarda al momento (llamada desde la tabla con fetch).
    public IActionResult OnPostDecidir(long accesoId, Decision decision, string? comentario)
    {
        if (!usuario.TieneRol(RolesPortal.Certificador)) return SinPermiso();
        try
        {
            var r = recert.Decidir(accesoId, decision, comentario, usuario.Login);
            return new JsonResult(new { ok = true, decision = r.Decision.ToString(), r.Comentario, decididos = r.Conteo.Decididos, total = r.Conteo.Accesos });
        }
        catch (DomainException ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message }) { StatusCode = StatusCodes.Status422UnprocessableEntity };
        }
    }

    // ids: lista separada por comas (un solo campo, sin importar cuántos accesos se marquen).
    public IActionResult OnPostMasivo(string? ids)
    {
        if (!usuario.TieneRol(RolesPortal.Certificador)) return SinPermiso();
        var lista = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                               .Select(x => long.TryParse(x, out var v) ? v : 0).Where(v => v > 0).ToList();
        int n = 0;
        return Ejecutar(() => n = recert.ContinuaMasivo(lista, usuario.Login), "", () =>
        {
            if (MensajeError is null) MensajeOk = $"Se registró «Continúa» en {n} accesos pendientes.";
            return RedirectToPage();
        });
    }

    public IActionResult OnPostConfirmar(int macroAreaId)
    {
        if (!usuario.TieneRol(RolesPortal.Certificador)) return SinPermiso();
        return Ejecutar(() => recert.ConfirmarMacroArea(macroAreaId, usuario.Login),
            "Certificación de la macro área confirmada. Puedes cambiar decisiones hasta el cierre del evento.", () => RedirectToPage());
    }
}
