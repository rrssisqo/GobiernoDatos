using System.Text;

namespace GobiernoDatos.Web.Data;

// Lector CSV mínimo (RFC 4180: comillas dobles, comas y saltos de línea dentro de comillas).
public static class Csv
{
    static Csv() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    // Las exportaciones de SharePoint venían en Windows-1252: si el archivo no es UTF-8 válido, se lee como 1252.
    public static string LeerTexto(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    public static List<Dictionary<string, string>> Leer(string texto)
    {
        var filas = Parsear(texto);
        if (filas.Count == 0) return [];
        var encabezado = filas[0].Select(h => h.Trim()).ToArray();
        return filas.Skip(1)
            .Where(f => f.Any(c => c.Length > 0))
            .Select(f =>
            {
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < encabezado.Length; i++) d[encabezado[i]] = i < f.Count ? f[i].Trim() : "";
                return d;
            })
            .ToList();
    }

    // Filas crudas, sin interpretar encabezados (para archivos con encabezados de grupo o columnas repetidas).
    public static List<List<string>> LeerFilas(string texto) => Parsear(texto);

    private static List<List<string>> Parsear(string texto)
    {
        var filas = new List<List<string>>();
        var fila = new List<string>();
        var campo = new StringBuilder();
        bool enComillas = false;

        for (int i = 0; i < texto.Length; i++)
        {
            char c = texto[i];
            if (enComillas)
            {
                if (c == '"' && i + 1 < texto.Length && texto[i + 1] == '"') { campo.Append('"'); i++; }
                else if (c == '"') enComillas = false;
                else campo.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': enComillas = true; break;
                case ',': fila.Add(campo.ToString()); campo.Clear(); break;
                case '\r': break;
                case '\n': fila.Add(campo.ToString()); campo.Clear(); filas.Add(fila); fila = []; break;
                default: campo.Append(c); break;
            }
        }
        if (campo.Length > 0 || fila.Count > 0) { fila.Add(campo.ToString()); filas.Add(fila); }
        return filas;
    }

    public static string Escribir(IEnumerable<string> encabezado, IEnumerable<IEnumerable<object?>> filas)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", encabezado.Select(Escapar)));
        foreach (var f in filas) sb.AppendLine(string.Join(",", f.Select(v => Escapar(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? ""))));
        return sb.ToString();
    }

    private static string Escapar(string v) =>
        v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;

    // Excel abre bien un CSV UTF-8 solo si lleva BOM.
    public static byte[] ABytesExcel(string csv) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv)];
}
