using System.Globalization;
using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;
using GobiernoDatos.Web.Pages.Shared;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GobiernoDatos.Web.Pages.Calidad;

public class IndexModel(ICalidadService calidad) : PortalPageModel
{
    [BindProperty(SupportsGet = true)] public int? Periodo { get; set; }
    [BindProperty(SupportsGet = true)] public string? Gob { get; set; }

    public IReadOnlyList<int> Periodos { get; private set; } = [];
    public IReadOnlyList<string> Gobiernos { get; private set; } = [];
    public TableroCalidad Tablero { get; private set; } = null!;

    public void OnGet()
    {
        Periodos = calidad.Periodos();
        Gobiernos = calidad.Gobiernos();
        Tablero = calidad.Tablero(Periodos.Contains(Periodo ?? 0) ? Periodo!.Value : Periodos[0], Gob);
    }

    // Archivo con la salida de la vista de resumen: regla_codigo, nombre_csv, periodo, reg_evaluados, reg_errores.
    public async Task<IActionResult> OnPostCargarAsync(IFormFile? archivo)
    {
        if (archivo is null || archivo.Length == 0)
        {
            MensajeError = "Selecciona el archivo CSV del resumen.";
            return RedirectToPage(new { Periodo, Gob });
        }
        using var ms = new MemoryStream();
        await archivo.CopyToAsync(ms);
        var filas = Csv.Leer(Csv.LeerTexto(ms.ToArray()));
        if (filas.Count > 0 && !filas[0].ContainsKey("reg_evaluados"))
        {
            MensajeError = "El archivo debe tener las columnas regla_codigo, nombre_csv, periodo, reg_evaluados y reg_errores.";
            return RedirectToPage(new { Periodo, Gob });
        }

        var datos = filas.Select(f => new FilaResumenCalidad(
            Nulo(f.GetValueOrDefault("regla_codigo")), Nulo(f.GetValueOrDefault("nombre_csv")),
            (int?)Largo(f.GetValueOrDefault("periodo")), Largo(f.GetValueOrDefault("reg_evaluados")), Largo(f.GetValueOrDefault("reg_errores"))))
            .ToList();
        var r = calidad.CargarResumen(datos);
        if (r.Rechazados > 0) MensajeError = $"Carga terminada: {r.Cargados} registros cargados y {r.Rechazados} rechazados. Revisa los rechazos abajo.";
        else MensajeOk = $"Carga terminada: {r.Cargados} registros cargados.";
        return RedirectToPage(new { Periodo = datos.Select(d => d.Periodo).Where(p => p is not null).Max() ?? Periodo, Gob });
    }

    private static string? Nulo(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    private static long? Largo(string? v) => long.TryParse(v?.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}
