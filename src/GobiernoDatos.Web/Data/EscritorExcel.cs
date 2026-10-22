using ClosedXML.Excel;

namespace GobiernoDatos.Web.Data;

// Libros .xlsx (una o varias hojas): encabezado congelado con filtro, fechas y números con su tipo de Excel.
public static class EscritorExcel
{
    public static byte[] Libro(string hoja, IEnumerable<string> encabezados, IEnumerable<IEnumerable<object?>> filas) =>
        Libro([(hoja, encabezados, filas)]);

    // Varias hojas en un mismo libro, cada una con su encabezado.
    public static byte[] Libro(IEnumerable<(string Hoja, IEnumerable<string> Encabezados, IEnumerable<IEnumerable<object?>> Filas)> hojas)
    {
        using var libro = new XLWorkbook();
        foreach (var (hoja, encabezados, filas) in hojas) Hoja(libro, hoja, encabezados, filas);
        using var ms = new MemoryStream();
        libro.SaveAs(ms);
        return ms.ToArray();
    }

    private static void Hoja(XLWorkbook libro, string hoja, IEnumerable<string> encabezados, IEnumerable<IEnumerable<object?>> filas)
    {
        var h = libro.AddWorksheet(NombreHoja(hoja));
        var enc = encabezados.ToList();
        for (int c = 0; c < enc.Count; c++) h.Cell(1, c + 1).Value = enc[c];

        var r = 1;
        foreach (var fila in filas)
        {
            r++;
            var c = 0;
            foreach (var valor in fila)
            {
                var celda = h.Cell(r, ++c);
                switch (valor)
                {
                    case null: break;
                    case DateTime f:
                        celda.Value = f;
                        celda.Style.NumberFormat.Format = f.TimeOfDay == TimeSpan.Zero ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm";
                        break;
                    case DateOnly f:
                        celda.Value = f.ToDateTime(TimeOnly.MinValue);
                        celda.Style.NumberFormat.Format = "yyyy-mm-dd";
                        break;
                    case int or long or decimal or double: celda.Value = Convert.ToDouble(valor); break;
                    default: celda.Value = valor.ToString(); break;
                }
            }
        }

        var encabezado = h.Range(1, 1, 1, enc.Count);
        encabezado.Style.Font.Bold = true;
        encabezado.Style.Fill.BackgroundColor = XLColor.FromHtml("#E6F0FA");
        h.Range(1, 1, Math.Max(r, 1), enc.Count).SetAutoFilter();
        h.SheetView.FreezeRows(1);
        h.Columns(1, enc.Count).AdjustToContents(1, Math.Min(r, 300));   // ancho según las primeras filas: rápido con miles
        foreach (var col in h.Columns(1, enc.Count)) if (col.Width > 60) col.Width = 60;
    }

    // Excel limita el nombre de la hoja a 31 caracteres y no admite : \ / ? * [ ]
    private static string NombreHoja(string nombre)
    {
        var limpio = new string(nombre.Select(ch => ":\\/?*[]".Contains(ch) ? '_' : ch).ToArray()).Trim();
        return limpio.Length == 0 ? "Hoja1" : limpio[..Math.Min(31, limpio.Length)];
    }

    // Nombre de archivo seguro para Windows.
    public static string NombreArchivo(string texto) =>
        string.Concat(texto.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
