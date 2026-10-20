namespace GobiernoDatos.Web.Domain;

public enum EstadoAvance { NO_INICIADO, EN_CURSO, CERRADO, NO_APLICA }

public static class Avance
{
    // Estado y % siempre coherentes (ck_movvista_coherencia / ck_movdp_coherencia).
    public static decimal NormalizarPct(EstadoAvance estado, decimal? pct) => estado switch
    {
        EstadoAvance.NO_INICIADO => 0,
        EstadoAvance.CERRADO or EstadoAvance.NO_APLICA => 100,
        _ when pct is > 0 and < 100 => pct.Value,
        _ => throw new DomainException("En curso requiere un porcentaje entre 1 y 99."),
    };

    // % DP = promedio ponderado por rubro.peso de los 7 rubros. Sin dato = 0.
    public static decimal PromedioPonderado(IEnumerable<(decimal Pct, decimal Peso)> rubros)
    {
        var lista = rubros.ToList();
        var peso = lista.Sum(r => r.Peso);
        return peso == 0 ? 0 : Math.Round(lista.Sum(r => r.Pct * r.Peso) / peso, 2);
    }

    public static string Etiqueta(EstadoAvance estado) => estado switch
    {
        EstadoAvance.NO_INICIADO => "No iniciado",
        EstadoAvance.EN_CURSO => "En curso",
        EstadoAvance.CERRADO => "Cerrado",
        _ => "No aplica",
    };
}

public sealed class DomainException(string message) : Exception(message);
