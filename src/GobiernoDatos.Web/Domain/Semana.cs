using System.Globalization;

namespace GobiernoDatos.Web.Domain;

// Semana ISO. Id = AAAASS (gd.semana.semana_id); etiqueta "39/26" como en el Excel.
public readonly record struct Semana(int Anio, int Numero)
{
    public int Id => Anio * 100 + Numero;
    public string Etiqueta => $"{Numero:00}/{Anio % 100:00}";
    public DateOnly Inicio => DateOnly.FromDateTime(ISOWeek.ToDateTime(Anio, Numero, DayOfWeek.Monday));
    public DateOnly Fin => Inicio.AddDays(6);

    public static Semana De(DateOnly fecha)
    {
        var dt = fecha.ToDateTime(TimeOnly.MinValue);
        return new Semana(ISOWeek.GetYear(dt), ISOWeek.GetWeekOfYear(dt));
    }

    public static Semana DeId(int id) => new(id / 100, id % 100);

    public static bool TryParseEtiqueta(string etiqueta, out Semana semana)
    {
        semana = default;
        var partes = etiqueta.Split('/');
        if (partes.Length != 2 || !int.TryParse(partes[0], out var num) || !int.TryParse(partes[1], out var anio)) return false;
        semana = new Semana(2000 + anio, num);
        return true;
    }

    public Semana Anterior() => De(Inicio.AddDays(-7));

    public string Rango()
    {
        var es = CultureInfo.GetCultureInfo("es-MX");
        return Inicio.Month == Fin.Month
            ? $"{Inicio.Day}–{Fin.Day} {Fin.ToString("MMM", es).TrimEnd('.')}"
            : $"{Inicio.Day} {Inicio.ToString("MMM", es).TrimEnd('.')}–{Fin.Day} {Fin.ToString("MMM", es).TrimEnd('.')}";
    }
}
