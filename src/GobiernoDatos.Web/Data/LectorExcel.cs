using ClosedXML.Excel;
using GobiernoDatos.Web.Domain;

namespace GobiernoDatos.Web.Data;

// Lee un libro de Excel (.xlsx) como filas de texto, con cada celda tal como se ve en Excel
// (un 100% con formato de porcentaje llega como "100%", no como 1).
public static class LectorExcel
{
    // Hoja a usar: la primera que contenga el encabezado buscado; si ninguna, la primera del libro.
    public static List<List<string>> LeerFilas(Stream xlsx, string encabezadoBuscado)
    {
        XLWorkbook libro;
        try
        {
            libro = new XLWorkbook(xlsx);
        }
        catch (Exception ex) when (ex is not DomainException)
        {
            throw new DomainException("El archivo no es un libro de Excel válido (.xlsx).");
        }

        using (libro)
        {
            var hojas = libro.Worksheets.Select(Leer).ToList();
            if (hojas.Count == 0) throw new DomainException("El libro de Excel no tiene hojas.");
            return hojas.FirstOrDefault(h => h.Any(f => f.Any(c => c.Equals(encabezadoBuscado, StringComparison.OrdinalIgnoreCase))))
                   ?? hojas[0];
        }
    }

    private static List<List<string>> Leer(IXLWorksheet hoja)
    {
        var usado = hoja.RangeUsed();
        if (usado is null) return [];
        int ultimaColumna = usado.LastColumn().ColumnNumber();
        var filas = new List<List<string>>();
        for (int r = 1; r <= usado.LastRow().RowNumber(); r++)
        {
            var fila = new List<string>(ultimaColumna);
            for (int c = 1; c <= ultimaColumna; c++) fila.Add(Texto(hoja.Cell(r, c)));
            filas.Add(fila);
        }
        return filas;
    }

    // Texto con el formato de la celda. Si una fórmula no se puede evaluar, se usa el último valor que guardó Excel.
    private static string Texto(IXLCell celda)
    {
        try
        {
            return celda.GetFormattedString().Trim();
        }
        catch
        {
            return celda.CachedValue.ToString()?.Trim() ?? "";
        }
    }
}
