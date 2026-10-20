using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;
using GobiernoDatos.Web.Pages.Shared;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GobiernoDatos.Web.Pages;

public class IndexModel(ISeguimientoService seguimiento, IUsuarioActual usuario) : PortalPageModel
{
    [BindProperty(SupportsGet = true)] public int? Semana { get; set; }
    [BindProperty(SupportsGet = true)] public string? Gob { get; set; }

    public IReadOnlyList<Semana> Semanas { get; private set; } = [];
    public TableroSeguimiento Tablero { get; private set; } = null!;
    public IReadOnlyList<string> Gobiernos { get; private set; } = [];
    // Semanas para "Cargar archivo": las 12 más recientes; por omisión la anterior (el Excel se cierra en la sesión semanal).
    public IReadOnlyList<Semana> SemanasCorte { get; private set; } = [];
    // Para el diálogo "Editar data product".
    public IReadOnlyList<string> CatalogoGobiernos { get; private set; } = [];
    public IReadOnlyList<string> Responsables { get; private set; } = [];
    public IReadOnlyDictionary<int, string?> ResponsablePorDp { get; private set; } = new Dictionary<int, string?>();

    public void OnGet()
    {
        Semanas = seguimiento.Semanas();
        var semana = Semanas.FirstOrDefault(s => s.Id == Semana, Semanas[0]);
        Tablero = seguimiento.Tablero(semana, Gob);
        Gobiernos = seguimiento.Tablero(semana, null).Filas.Select(f => f.Gobierno).Distinct().Order().ToList();
        var actual = seguimiento.SemanaActual();
        SemanasCorte = Enumerable.Range(0, 12).Select(i => Domain.Semana.De(actual.Inicio.AddDays(-7 * i))).ToList();
        CatalogoGobiernos = seguimiento.Gobiernos();
        Responsables = seguimiento.Responsables();
        ResponsablePorDp = seguimiento.DataProducts().ToDictionary(d => d.DpId, d => d.Responsable);
    }

    public IActionResult OnPostEditar(int dpId, string? nombre, string? gobierno, string? responsable) =>
        Ejecutar(() =>
            {
                seguimiento.EditarDataProduct(dpId, nombre ?? "", gobierno ?? "", responsable, usuario.Login);
                MensajeOk = $"Data product «{nombre!.Trim()}» actualizado.";
            },
            "", () => RedirectToPage(new { semana = Semana, gob = Gob }));

    public IActionResult OnPostEliminar(int dpId) =>
        Ejecutar(() =>
            {
                var r = seguimiento.EliminarDataProduct(dpId, usuario.Login);
                MensajeOk = $"Data product «{r.Nombre}» eliminado junto con {r.Avances} avances, {r.Notas} comentarios" +
                            (r.Vistas > 0 ? $" y {r.Vistas} asignaciones de vistas." : ".");
            },
            "", () => RedirectToPage(new { semana = Semana, gob = Gob }));

    public IActionResult OnGetExportar()
    {
        var semanas = seguimiento.Semanas();
        var semana = semanas.FirstOrDefault(s => s.Id == Semana, semanas[0]);
        var t = seguimiento.Tablero(semana, Gob);
        var csv = Csv.Escribir(
            new[] { "Semana", "Gobierno", "Data Product", "Avance", "Vistas" }.Concat(Catalogos.Rubros.Select(r => r.Corto)).Append("Comentarios"),
            t.Filas.Select(f => new object?[] { semana.Etiqueta, f.Gobierno, f.DataProduct, Ui.Pct(f.Avance), f.Vistas }
                .Concat(f.Rubros.Select(r => (object?)Ui.Pct(r.Pct))).Append(f.HistorialTexto)));
        return File(Csv.ABytesExcel(csv), "text/csv", $"seguimiento_{semana.Etiqueta.Replace('/', '_')}.csv");
    }

    // Excel (.xlsx) como formato principal; CSV se sigue aceptando.
    private static List<List<string>> FilasDelArchivo(string nombre, MemoryStream contenido)
    {
        var extension = Path.GetExtension(nombre).ToLowerInvariant();
        contenido.Position = 0;
        return extension switch
        {
            ".xlsx" => LectorExcel.LeerFilas(contenido, "Data Product"),
            ".csv" => Csv.LeerFilas(Csv.LeerTexto(contenido.ToArray())),
            _ => throw new DomainException("El archivo debe ser de Excel (.xlsx). Si es un .xls antiguo, guárdalo como .xlsx."),
        };
    }

    // Carga del Excel de seguimiento (.xlsx o CSV): data products, avances de los 7 rubros y comentarios separados por semana.
    public async Task<IActionResult> OnPostImportarAsync(IFormFile? archivo, int semanaCorte)
    {
        if (archivo is null || archivo.Length == 0)
        {
            MensajeError = "Selecciona el archivo de seguimiento (.xlsx).";
            return RedirectToPage();
        }
        using var ms = new MemoryStream();
        await archivo.CopyToAsync(ms);
        var corte = Domain.Semana.DeId(semanaCorte);
        return Ejecutar(() =>
            {
                var lectura = ImportacionSeguimiento.Leer(FilasDelArchivo(archivo.FileName, ms), corte);
                if (lectura.Filas.Count == 0) throw new DomainException("El archivo no tiene data products para importar.");
                var r = seguimiento.ImportarSeguimiento(lectura.Filas, corte, usuario.Login);
                MensajeOk = $"Archivo cargado ({corte.Etiqueta}): {r.DataProductsNuevos} data products nuevos, {r.DataProductsActualizados} actualizados, " +
                            $"{r.Avances} avances y {r.Notas} comentarios por semana registrados." +
                            (r.GobiernosNuevos.Count > 0 ? $" Gobiernos dados de alta: {string.Join(", ", r.GobiernosNuevos)}." : "") +
                            (lectura.Advertencias.Count > 0 ? $" Advertencias: {string.Join(" ", lectura.Advertencias.Take(10))}" +
                                                              (lectura.Advertencias.Count > 10 ? $" (y {lectura.Advertencias.Count - 10} más)" : "") : "");
            },
            "", () => RedirectToPage(new { semana = corte.Id }));
    }
}
