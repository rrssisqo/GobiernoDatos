using System.Globalization;
using GobiernoDatos.Web.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GobiernoDatos.Web.Pages.Shared;

// Formatos comunes. Lo que las fuentes no traen se muestra entre corchetes (convención de la maqueta).
public static class Ui
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-MX");

    public static string Pct(decimal pct) => $"{Math.Round(pct, 0):0}%";
    public static string Calidad(decimal? c) => c is null ? "—" : (c.Value * 100).ToString("0.0", Es) + "%";
    public static string Num(long? n, string vacio = "—") => n is null ? vacio : n.Value.ToString("N0", Es);
    public static string Fecha(DateOnly? f, string vacio = "[fecha]") => f is null ? vacio : f.Value.ToString("d MMM yyyy", Es).Replace(".", "");
    public static string FechaHora(DateTime f) => f.ToString("d MMM yyyy HH:mm", Es).Replace(".", "");
    public static string Texto(string? t, string vacio) => string.IsNullOrWhiteSpace(t) ? vacio : t;

    public static string Periodo(int p) =>
        $"{p} · {new DateTime(p / 100, p % 100, 1).ToString("MMMM yyyy", Es)}";

    // Escala de color de la maqueta: 100 / 50–99 / 1–49 / 0.
    public static string ClaseRubro(decimal pct) => pct switch
    {
        >= 100 => "c-ok",
        >= 50 => "c-medio",
        > 0 => "c-bajo",
        _ => "c-cero",
    };

    public static string ClaseCalidad(decimal? c) => c switch
    {
        null => "c-cero",
        >= 0.98m => "c-ok",
        >= 0.90m => "c-medio",
        _ => "c-bajo",
    };

    public static string ClaseEstatus(EstatusEvento e) => e switch
    {
        EstatusEvento.ABIERTO => "badge-ok",
        EstatusEvento.BORRADOR => "badge-aviso",
        _ => "badge-neutro",
    };

    public static string EstatusDp(string e) => e switch
    {
        "EN_CURSO" => "En curso",
        "PLANEADO" => "Planeado",
        "CERRADO" => "Cerrado",
        _ => "Cancelado",
    };
}

public abstract class PortalPageModel : PageModel
{
    [TempData] public string? MensajeOk { get; set; }
    [TempData] public string? MensajeError { get; set; }

    // Sin esquema de autenticación todavía, Forbid() no aplica: se responde 403 directo.
    protected IActionResult SinPermiso() => StatusCode(StatusCodes.Status403Forbidden);

    // Ejecuta una escritura: errores de negocio vuelven como mensaje, sin perder la navegación.
    protected IActionResult Ejecutar(Action accion, string ok, Func<IActionResult> despues)
    {
        try
        {
            accion();
            if (ok.Length > 0) MensajeOk = ok;   // vacío: la acción arma su propio mensaje
        }
        catch (DomainException ex)
        {
            MensajeError = ex.Message;
        }
        return despues();
    }
}
