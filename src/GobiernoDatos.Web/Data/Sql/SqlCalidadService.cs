using Dapper;
using GobiernoDatos.Web.Services;

namespace GobiernoDatos.Web.Data.Sql;

// Calidad sobre gd.resumen_calidad. La carga usa el flujo del modelo: stg_resumen_calidad + gd.sp_cargar_resumen_calidad.
public sealed class SqlCalidadService(SqlConexion db) : ICalidadService
{
    public IReadOnlyList<int> Periodos()
    {
        using var c = db.Abrir();
        var actual = db.Hoy.Year * 100 + db.Hoy.Month;
        return c.Query<int>("SELECT DISTINCT periodo FROM gd.resumen_calidad").Append(actual).Distinct().OrderDescending().ToList();
    }

    public IReadOnlyList<string> Gobiernos()
    {
        using var c = db.Abrir();
        return c.Query<string>("SELECT DISTINCT g.nombre FROM gd.regla AS r JOIN gd.gobierno AS g ON g.gobierno_id = r.gobierno_id ORDER BY g.nombre").ToList();
    }

    private sealed record ReglaRow(int regla_id, string codigo, string descripcion, string? gobierno, string? dimension_dq, bool activa);
    private sealed record ObjetoRow(int regla_id, string nombre);
    private sealed record VistaRow(int regla_id, int vista_id, string nombre_negocio);
    private sealed record ResumenRow(int regla_id, int periodo, long reg_evaluados, long reg_errores);
    private sealed record RechazoRow(string? regla_codigo, string? nombre_csv, int? periodo, long? reg_evaluados, long? reg_errores, string motivo, DateTime fecha_carga);

    public TableroCalidad Tablero(int periodo, string? gobierno)
    {
        var ini = new DateTime(periodo / 100, periodo % 100, 1);
        using var c = db.Abrir();
        using var m = c.QueryMultiple("""
            SELECT r.regla_id, r.codigo, r.descripcion, g.nombre AS gobierno, r.dimension_dq, r.activa
            FROM gd.regla AS r LEFT JOIN gd.gobierno AS g ON g.gobierno_id = r.gobierno_id
            WHERE @gobierno IS NULL OR g.nombre = @gobierno;

            SELECT ro.regla_id, o.nombre FROM gd.regla_objeto AS ro JOIN gd.objeto_fuente AS o ON o.objeto_id = ro.objeto_id;

            -- Vistas fuente: objetos de la regla → vista_objeto vigente en el periodo (gd.vw_calidad_vista).
            SELECT DISTINCT ro.regla_id, v.vista_id, v.nombre_negocio
            FROM gd.regla_objeto AS ro
            JOIN gd.vista_objeto AS vo ON vo.objeto_id = ro.objeto_id
             AND vo.fecha_desde <= EOMONTH(@ini) AND (vo.fecha_hasta IS NULL OR vo.fecha_hasta >= @ini)
            JOIN gd.vista_gobernada AS v ON v.vista_id = vo.vista_id;

            SELECT regla_id, periodo, reg_evaluados, reg_errores FROM gd.resumen_calidad WHERE periodo <= @periodo;

            SELECT MAX(f) FROM (SELECT MAX(fecha_carga) AS f FROM gd.resumen_calidad UNION ALL SELECT MAX(fecha_carga) FROM gd.carga_rechazo) AS x;

            SELECT regla_codigo, nombre_csv, periodo, reg_evaluados, reg_errores, motivo, fecha_carga
            FROM gd.carga_rechazo
            WHERE fecha_carga = (SELECT MAX(f) FROM (SELECT MAX(fecha_carga) AS f FROM gd.resumen_calidad
                                                     UNION ALL SELECT MAX(fecha_carga) FROM gd.carga_rechazo) AS x);
            """, new { gobierno, periodo, ini });

        var reglas = m.Read<ReglaRow>().ToList();
        var objetos = m.Read<ObjetoRow>().ToLookup(o => o.regla_id, o => o.nombre);
        var vistas = m.Read<VistaRow>().ToLookup(v => v.regla_id);
        var ids = reglas.Select(r => r.regla_id).ToHashSet();
        var resumen = m.Read<ResumenRow>().Where(r => ids.Contains(r.regla_id)).ToList();
        var ultimaCarga = m.ReadSingle<DateTime?>();
        var rechazos = m.Read<RechazoRow>().Select(r => new CargaRechazo
        {
            Fila = new FilaResumenCalidad(r.regla_codigo, r.nombre_csv, r.periodo, r.reg_evaluados, r.reg_errores),
            Motivo = r.motivo, FechaCarga = r.fecha_carga,
        }).ToList();

        static Medicion Med(IEnumerable<ResumenRow> rs) => Medicion.Sumar(rs.Select(r => new Medicion(r.reg_evaluados, r.reg_errores)));
        var delPeriodo = resumen.Where(r => r.periodo == periodo).ToList();
        var total = Med(delPeriodo);
        var anterior = resumen.Where(r => r.periodo < periodo).Select(r => r.periodo).DefaultIfEmpty().Max();
        var totalAnt = Med(resumen.Where(r => r.periodo == anterior));
        decimal? delta = total.Calidad is decimal ca && totalAnt.Calidad is decimal cb ? ca - cb : null;

        var gobiernoDe = reglas.ToDictionary(r => r.regla_id, r => r.gobierno ?? "[sin gobierno]");
        var porGobierno = delPeriodo.GroupBy(r => gobiernoDe[r.regla_id])
            .Select(g => new FilaGobiernoCalidad(g.Key, Med(g))).OrderBy(f => f.Gobierno).ToList();

        var porRegla = reglas.Select(r =>
        {
            var res = delPeriodo.FirstOrDefault(x => x.regla_id == r.regla_id);
            return new FilaReglaCalidad(r.regla_id, r.codigo, r.descripcion, r.gobierno, r.dimension_dq, objetos[r.regla_id].ToList(),
                vistas[r.regla_id].Select(v => new VistaFuente(v.vista_id, v.nombre_negocio)).ToList(),
                res is null ? null : new Medicion(res.reg_evaluados, res.reg_errores));
        }).OrderBy(f => f.Medicion?.Calidad ?? 2).ThenBy(f => f.Codigo).ToList();

        return new TableroCalidad(periodo, total, delta, delPeriodo.Count, reglas.Count(r => r.activa), porGobierno, porRegla, rechazos, ultimaCarga);
    }

    private sealed record ResultadoSp(int registros_cargados, int registros_rechazados);

    public ResultadoCarga CargarResumen(IReadOnlyList<FilaResumenCalidad> filas) =>
        db.EnTransaccion((c, tx) =>
        {
            c.Execute("""
                INSERT INTO gd.stg_resumen_calidad (regla_codigo, nombre_csv, periodo, reg_evaluados, reg_errores)
                VALUES (@ReglaCodigo, @NombreCsv, @Periodo, @Evaluados, @Errores)
                """, filas, tx);
            var r = c.QuerySingle<ResultadoSp>("gd.sp_cargar_resumen_calidad", transaction: tx, commandType: System.Data.CommandType.StoredProcedure);
            return new ResultadoCarga(r.registros_cargados, r.registros_rechazados);
        });
}
