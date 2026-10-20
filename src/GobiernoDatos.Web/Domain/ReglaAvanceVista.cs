namespace GobiernoDatos.Web.Domain;

public sealed record MovEtapaRef(long MovId, DateOnly Fecha, int Orden);
public sealed record CapturaRef(long MovId, DateOnly Fecha, decimal Pct, EstadoAvance Estado);
public sealed record ResultadoRubro(decimal Pct, EstadoAvance Estado, string Origen);

// % de un rubro de vista al corte: "el último evento manda" (espejo de gd.vw_avance_vista_semana, migración 005).
// Sustituye a la opción C (2026-10-15): el % de cualquier rubro de vista se puede capturar en cualquier etapa.
//   - Captura del rubro (cualquier etapa)                               → lo capturado
//   - La vista cruza hacia adelante el rango del rubro                   → 100 CERRADO
//   - La vista vuelve a entrar al rango después de haberlo cerrado       → 0 EN_CURSO (reproceso)
//   - Sin eventos: 0 EN_CURSO si la vista está dentro del rango; si no, 0 NO_INICIADO.
// Entrar al rango por primera vez no borra capturas previas; moverse entre etapas posteriores tampoco.
// Orden de eventos: fecha, luego secuencia de registro; en empate gana la captura.
public static class ReglaAvanceVista
{
    public static ResultadoRubro Calcular(Rubro rubro, IEnumerable<MovEtapaRef> etapas, IEnumerable<CapturaRef> capturas, DateOnly corte)
    {
        if (rubro.Nivel != NivelRubro.VISTA) throw new ArgumentException("Solo aplica a rubros de vista.", nameof(rubro));
        int ini = rubro.EtapaOrdenIni!.Value, fin = rubro.EtapaOrdenFin!.Value;
        bool EnRango(int o) => o >= ini && o <= fin;

        var movs = etapas.Where(m => m.Fecha <= corte).OrderBy(m => m.Fecha).ThenBy(m => m.MovId).ToList();
        var eventos = new List<(DateOnly Fecha, long Secuencia, int Tipo, ResultadoRubro Valor)>();

        int? previa = null;
        bool cerradoPorEtapa = false;
        foreach (var m in movs)
        {
            if (m.Orden > fin && (previa is null || previa <= fin))
            {
                eventos.Add((m.Fecha, m.MovId, 0, new(100, EstadoAvance.CERRADO, "ETAPA")));
                cerradoPorEtapa = true;
            }
            else if (EnRango(m.Orden) && (previa is null || !EnRango(previa.Value)) && cerradoPorEtapa)
            {
                eventos.Add((m.Fecha, m.MovId, 0, new(0, EstadoAvance.EN_CURSO, "ETAPA")));
            }
            previa = m.Orden;
        }
        eventos.AddRange(capturas.Where(c => c.Fecha <= corte)
                                 .Select(c => (c.Fecha, c.MovId, 1, new ResultadoRubro(c.Pct, c.Estado, "CAPTURA"))));

        if (eventos.Count == 0)
        {
            var actual = movs.LastOrDefault()?.Orden ?? 0;
            return new(0, EnRango(actual) ? EstadoAvance.EN_CURSO : EstadoAvance.NO_INICIADO, "ETAPA");
        }
        return eventos.OrderBy(e => e.Fecha).ThenBy(e => e.Secuencia).ThenBy(e => e.Tipo).Last().Valor;
    }
}
