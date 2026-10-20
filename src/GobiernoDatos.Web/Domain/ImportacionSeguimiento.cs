using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GobiernoDatos.Web.Domain;

public sealed record NotaImportada(Semana Semana, string Texto);

// Una fila del Excel de seguimiento: data product, gobierno, vistas reportadas, % por rubro y notas por semana.
public sealed record FilaSeguimientoImportada(int Linea, string Gobierno, string DataProduct, int? Vistas,
                                              IReadOnlyDictionary<int, decimal> Rubros, IReadOnlyList<NotaImportada> Notas);

public sealed record LecturaSeguimiento(IReadOnlyList<FilaSeguimientoImportada> Filas, IReadOnlyList<string> Advertencias);

// Lee el Excel de seguimiento guardado como CSV (Gobierno, Data Product, Avance, Vistas, Glosarios, Catalogado, DF, Arq.,
// Linaje, Perfil, Reglas de Calidad, …, Comentarios). Tolera renglones de grupo arriba del encabezado y columnas repetidas
// (se toma la primera aparición). "Avance" no se importa: el portal lo calcula.
public static partial class ImportacionSeguimiento
{
    // Encabezados aceptados por rubro (normalizados: minúsculas, sin acentos ni puntos).
    private static readonly (int RubroId, string[] Nombres)[] ColumnasRubro =
    [
        (1, ["glosarios", "glosario"]),
        (2, ["catalogado"]),
        (3, ["df", "diseno funcional"]),
        (4, ["arq", "arquitectura"]),
        (5, ["linaje"]),
        (6, ["perfil", "perfilado"]),
        (7, ["reglas de calidad", "reglas"]),
    ];

    public static LecturaSeguimiento Leer(IReadOnlyList<IReadOnlyList<string>> filas, Semana corte)
    {
        var advertencias = new List<string>();
        int iEnc = -1;
        for (int i = 0; i < filas.Count && iEnc < 0; i++)
        {
            var normal = filas[i].Select(Normalizar).ToList();
            if (normal.Contains("gobierno") && normal.Contains("data product")) iEnc = i;
        }
        if (iEnc < 0) throw new DomainException("No se encontró el encabezado con las columnas «Gobierno» y «Data Product».");

        var enc = filas[iEnc].Select(Normalizar).ToList();
        int Col(params string[] nombres) =>
            enc.Select((h, i) => (h, i)).Where(x => nombres.Contains(x.h)).Select(x => x.i).DefaultIfEmpty(-1).Min();
        int cGob = Col("gobierno"), cDp = Col("data product"), cVistas = Col("vistas"), cCom = Col("comentarios");
        var cRubros = ColumnasRubro.Select(r => (r.RubroId, Indice: Col(r.Nombres))).Where(r => r.Indice >= 0).ToList();
        foreach (var faltante in ColumnasRubro.Where(r => cRubros.All(c => c.RubroId != r.RubroId)))
            advertencias.Add($"No se encontró la columna «{faltante.Nombres[0]}»; ese rubro no se importa.");

        string Celda(IReadOnlyList<string> f, int i) => i >= 0 && i < f.Count ? f[i].Trim() : "";

        var resultado = new List<FilaSeguimientoImportada>();
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = iEnc + 1; i < filas.Count; i++)
        {
            var f = filas[i];
            int linea = i + 1;
            string gobierno = Celda(f, cGob), dp = Celda(f, cDp);
            if (gobierno == "" && dp == "") continue;
            if (gobierno == "" || dp == "") { advertencias.Add($"Línea {linea}: falta gobierno o data product; se omite."); continue; }
            if (!vistos.Add(dp)) { advertencias.Add($"Línea {linea}: «{dp}» aparece repetido; se usa la primera fila."); continue; }

            var rubros = new Dictionary<int, decimal>();
            foreach (var (rubroId, indice) in cRubros)
            {
                var texto = Celda(f, indice);
                if (texto == "") continue;   // sin dato: no se registra movimiento
                if (Porcentaje(texto) is decimal pct) rubros[rubroId] = pct;
                else advertencias.Add($"Línea {linea} («{dp}»): valor «{texto}» no es un porcentaje válido; se omite.");
            }

            var vistasTxt = Celda(f, cVistas);
            int? vistas = int.TryParse(vistasTxt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;
            if (vistasTxt != "" && vistas is null) advertencias.Add($"Línea {linea} («{dp}»): «Vistas» no es un número; se omite.");

            resultado.Add(new FilaSeguimientoImportada(linea, gobierno, dp, vistas, rubros, SepararNotas(Celda(f, cCom), corte)));
        }
        return new LecturaSeguimiento(resultado, advertencias);
    }

    // "42/26 - texto. 41/26 - texto | 40/26 texto" → una nota por semana. Las marcas que no son una semana válida
    // (futura, de otro año o mal escrita, p. ej. 38/52) se dejan como texto. Lo que no tiene marca va a la semana de corte.
    public static IReadOnlyList<NotaImportada> SepararNotas(string comentario, Semana corte)
    {
        comentario = (comentario ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (comentario == "") return [];

        var marcas = MarcaSemana().Matches(comentario)
            .Select(m => (m.Index, m.Length, Semana: SemanaValida(m, corte)))
            .Where(m => m.Semana is not null)
            .ToList();

        var notas = new List<NotaImportada>();
        void Agregar(Semana s, string texto)
        {
            texto = texto.Trim().Trim('-', '.', '|', '*', ' ').Trim();
            if (texto.Length > 0) notas.Add(new NotaImportada(s, texto.Length > 2000 ? texto[..2000] : texto));
        }

        if (marcas.Count == 0) { Agregar(corte, comentario); return notas; }
        if (marcas[0].Index > 0) Agregar(corte, comentario[..marcas[0].Index]);
        for (int k = 0; k < marcas.Count; k++)
        {
            int inicio = marcas[k].Index + marcas[k].Length;
            int fin = k + 1 < marcas.Count ? marcas[k + 1].Index : comentario.Length;
            Agregar(marcas[k].Semana!.Value, comentario[inicio..fin]);
        }
        return notas;
    }

    private static Semana? SemanaValida(Match m, Semana corte)
    {
        int num = int.Parse(m.Groups[1].Value), anio = 2000 + int.Parse(m.Groups[2].Value);
        if (num < 1 || num > 53 || anio < corte.Anio - 2 || anio > corte.Anio) return null;
        var s = new Semana(anio, num);
        try { _ = s.Inicio; } catch (ArgumentOutOfRangeException) { return null; }   // semana 53 inexistente
        return s.Id > corte.Id ? null : s;
    }

    public static decimal? Porcentaje(string texto)
    {
        var t = texto.Trim().TrimEnd('%').Trim();
        if (!decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) return null;
        if (v > 0 && v <= 1 && texto.Contains('.') && !texto.Contains('%')) v *= 100;   // 0.86 → 86
        return v is >= 0 and <= 100 ? Math.Round(v, 2) : null;
    }

    private static string Normalizar(string s)
    {
        var d = (s ?? "").Trim().ToLowerInvariant().Replace(".", "").Normalize(NormalizationForm.FormD);
        return new string(d.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }

    // 42/26, 35/26*, 29-26 (sin dígitos pegados antes ni después).
    [GeneratedRegex(@"(?<![\d/])(\d{1,2})\s*[/-]\s*(\d{2})(?!\d)\**")]
    private static partial Regex MarcaSemana();
}
