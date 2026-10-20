using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;

namespace GobiernoDatos.Web.Services;

public sealed record Medicion(long Evaluados, long Errores)
{
    public long Validos => Evaluados - Errores;
    // Agregar SIEMPRE como Σ válidos / Σ evaluados.
    public decimal? Calidad => Evaluados == 0 ? null : (decimal)Validos / Evaluados;
    public static Medicion Sumar(IEnumerable<Medicion> m) { var l = m.ToList(); return new(l.Sum(x => x.Evaluados), l.Sum(x => x.Errores)); }
}

public sealed record FilaGobiernoCalidad(string Gobierno, Medicion Medicion);

public sealed record VistaFuente(int VistaId, string Nombre);

public sealed record FilaReglaCalidad(int ReglaId, string Codigo, string Descripcion, string? Gobierno, string? Dimension,
                                      IReadOnlyList<string> Objetos, IReadOnlyList<VistaFuente> Vistas, Medicion? Medicion);

public sealed record TableroCalidad(int Periodo, Medicion Total, decimal? DeltaVsAnterior, int ReglasEvaluadas,
                                    int ReglasActivas, IReadOnlyList<FilaGobiernoCalidad> PorGobierno,
                                    IReadOnlyList<FilaReglaCalidad> PorRegla, IReadOnlyList<CargaRechazo> Rechazos,
                                    DateTime? FechaUltimaCarga);

public sealed record ResultadoCarga(int Cargados, int Rechazados);

public interface ICalidadService
{
    IReadOnlyList<int> Periodos();
    IReadOnlyList<string> Gobiernos();
    TableroCalidad Tablero(int periodo, string? gobierno);
    ResultadoCarga CargarResumen(IReadOnlyList<FilaResumenCalidad> filas);
}

public sealed class CalidadService(GobiernoStore s) : ICalidadService
{
    public IReadOnlyList<int> Periodos()
    {
        lock (s.Sync)
        {
            var actual = s.Hoy.Year * 100 + s.Hoy.Month;
            return s.Resumenes.Select(r => r.Periodo).Append(actual).Distinct().OrderDescending().ToList();
        }
    }

    public IReadOnlyList<string> Gobiernos()
    {
        lock (s.Sync) return s.Reglas.Select(r => r.Gobierno).OfType<string>().Distinct().Order().ToList();
    }

    public TableroCalidad Tablero(int periodo, string? gobierno)
    {
        lock (s.Sync)
        {
            var reglas = s.Reglas.Where(r => gobierno is null || r.Gobierno == gobierno).ToList();
            var ids = reglas.Select(r => r.ReglaId).ToHashSet();
            Medicion? Med(int reglaId, int p) =>
                s.Resumenes.FirstOrDefault(x => x.ReglaId == reglaId && x.Periodo == p) is { } x ? new(x.Evaluados, x.Errores) : null;

            var res = s.Resumenes.Where(r => r.Periodo == periodo && ids.Contains(r.ReglaId)).ToList();
            var total = Medicion.Sumar(res.Select(r => new Medicion(r.Evaluados, r.Errores)));

            var anterior = s.Resumenes.Where(r => r.Periodo < periodo && ids.Contains(r.ReglaId)).Select(r => r.Periodo).DefaultIfEmpty().Max();
            var totalAnt = Medicion.Sumar(s.Resumenes.Where(r => r.Periodo == anterior && ids.Contains(r.ReglaId))
                                                     .Select(r => new Medicion(r.Evaluados, r.Errores)));
            decimal? delta = total.Calidad is decimal c && totalAnt.Calidad is decimal ca ? c - ca : null;

            var porGobierno = res.GroupBy(r => s.Reglas.First(x => x.ReglaId == r.ReglaId).Gobierno ?? "[sin gobierno]")
                                 .Select(g => new FilaGobiernoCalidad(g.Key, Medicion.Sumar(g.Select(r => new Medicion(r.Evaluados, r.Errores)))))
                                 .OrderBy(f => f.Gobierno).ToList();

            // Vistas fuente: objetos de la regla → vista_objeto vigente en el periodo (gd.vw_calidad_vista).
            var ini = new DateOnly(periodo / 100, periodo % 100, 1);
            var fin = ini.AddMonths(1).AddDays(-1);
            var porRegla = reglas.Select(r => new FilaReglaCalidad(r.ReglaId, r.Codigo, r.Descripcion, r.Gobierno, r.DimensionDq,
                    r.Objetos.Select(o => s.Objetos.First(x => x.ObjetoId == o).Nombre).ToList(),
                    s.VistaObjetos.Where(vo => r.Objetos.Contains(vo.ObjetoId) && vo.FechaDesde <= fin && (vo.FechaHasta is null || vo.FechaHasta >= ini))
                                  .Select(vo => vo.VistaId).Distinct()
                                  .Select(id => new VistaFuente(id, s.Vistas.First(v => v.VistaId == id).NombreNegocio)).ToList(),
                    Med(r.ReglaId, periodo)))
                .OrderBy(f => f.Medicion?.Calidad ?? 2).ThenBy(f => f.Codigo).ToList();

            var ultimaCarga = s.Resumenes.Select(r => (DateTime?)r.FechaCarga).Concat(s.RechazosCalidad.Select(r => (DateTime?)r.FechaCarga)).Max();
            var rechazos = s.RechazosCalidad.Where(r => r.FechaCarga == ultimaCarga).ToList();

            return new TableroCalidad(periodo, total, delta, res.Count, reglas.Count(r => r.Activa), porGobierno, porRegla, rechazos, ultimaCarga);
        }
    }

    // Espejo de gd.sp_cargar_resumen_calidad: valida, registra rechazos y hace upsert por regla y periodo.
    public ResultadoCarga CargarResumen(IReadOnlyList<FilaResumenCalidad> filas)
    {
        lock (s.Sync)
        {
            var ahora = s.Ahora;
            var validadas = filas.Select(f =>
            {
                var rc = f.ReglaCodigo is null ? null : s.Reglas.FirstOrDefault(r => r.Codigo.Equals(f.ReglaCodigo, StringComparison.OrdinalIgnoreCase));
                var rn = f.NombreCsv is null ? null : s.Reglas.FirstOrDefault(r => r.NombreCsv?.Equals(f.NombreCsv, StringComparison.OrdinalIgnoreCase) == true);
                var regla = rc ?? rn;
                string? motivo =
                    f.ReglaCodigo is null && f.NombreCsv is null ? "Sin identificador de regla" :
                    f.Periodo is null || f.Evaluados is null || f.Errores is null ? "Campos obligatorios vacíos" :
                    regla is null ? "Regla no registrada" :
                    rc is not null && rn is not null && rc != rn ? "Código y CSV apuntan a reglas distintas" :
                    !regla.Activa ? "Regla inactiva" :
                    f.Periodo is < 200001 or > 209912 || f.Periodo % 100 is < 1 or > 12 ? "Periodo inválido (AAAAMM)" :
                    f.Evaluados < 0 || f.Errores < 0 || f.Errores > f.Evaluados ? "Conteos inconsistentes" :
                    null;
                return (Fila: f, Regla: regla, Motivo: motivo);
            }).ToList();

            var duplicados = validadas.Where(v => v.Motivo is null)
                                      .GroupBy(v => (v.Regla!.ReglaId, v.Fila.Periodo)).Where(g => g.Count() > 1)
                                      .SelectMany(g => g).ToHashSet();
            validadas = validadas.Select(v => duplicados.Contains(v) ? v with { Motivo = "Regla y periodo duplicados en la carga" } : v).ToList();

            foreach (var v in validadas.Where(v => v.Motivo is not null))
                s.RechazosCalidad.Add(new CargaRechazo { Fila = v.Fila, Motivo = v.Motivo!, FechaCarga = ahora });

            var cargados = 0;
            foreach (var v in validadas.Where(v => v.Motivo is null))
            {
                var existente = s.Resumenes.FirstOrDefault(r => r.ReglaId == v.Regla!.ReglaId && r.Periodo == v.Fila.Periodo);
                if (existente is null)
                    s.Resumenes.Add(new ResumenCalidad { ReglaId = v.Regla!.ReglaId, Periodo = v.Fila.Periodo!.Value, Evaluados = v.Fila.Evaluados!.Value, Errores = v.Fila.Errores!.Value, FechaCarga = ahora });
                else { existente.Evaluados = v.Fila.Evaluados!.Value; existente.Errores = v.Fila.Errores!.Value; existente.FechaCarga = ahora; }
                cargados++;
            }
            return new ResultadoCarga(cargados, validadas.Count - cargados);
        }
    }
}
