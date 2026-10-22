using Dapper;
using GobiernoDatos.Web.Domain;
using GobiernoDatos.Web.Services;
using static GobiernoDatos.Web.Data.Sql.SqlAux;

namespace GobiernoDatos.Web.Data.Sql;

// Seguimiento sobre el esquema gd. El avance se lee de las vistas gd.vw_avance_* (cálculo en SQL).
// Las escrituras validan lo mismo que gd.sp_registrar_avance_vista y graban el usuario del portal;
// los procedimientos v2 no reciben usuario (toman SUSER_SNAME), por eso no se usan aquí.
public sealed class SqlSeguimientoService(SqlConexion db) : ISeguimientoService
{
    public Semana SemanaActual() => Semana.De(db.Hoy);

    public IReadOnlyList<Semana> Semanas()
    {
        using var c = db.Abrir();
        var primera = c.ExecuteScalar<DateTime?>("""
            SELECT MIN(f) FROM (
                SELECT MIN(fecha_alta) AS f FROM gd.data_product
                UNION ALL SELECT MIN(fecha) FROM gd.mov_avance_dp
                UNION ALL SELECT MIN(s.fecha_inicio) FROM gd.log_semanal AS l JOIN gd.semana AS s ON s.semana_id = l.semana_id) AS x
            """);
        var actual = SemanaActual();
        var desde = primera is null ? actual.Inicio : D(primera.Value);
        var lista = new List<Semana>();
        for (var w = actual; w.Fin >= desde && lista.Count < 104; w = w.Anterior()) lista.Add(w);
        return lista;
    }

    public IReadOnlyList<string> Gobiernos()
    {
        using var c = db.Abrir();
        return c.Query<string>("SELECT nombre FROM gd.gobierno WHERE activo = 1 ORDER BY nombre").ToList();
    }

    public IReadOnlyList<string> Responsables()
    {
        using var c = db.Abrir();
        return c.Query<string>("SELECT DISTINCT nombre FROM gd.responsable WHERE activo = 1 ORDER BY nombre").ToList();
    }

    private sealed record TotalRow(int dp_id, string data_product, string gobierno, string estatus, decimal pct_avance, int num_vistas);
    private sealed record RubroRow(int dp_id, int rubro_id, decimal pct);
    private sealed record NotaRow(int dp_id, int semana_id, string comentario);

    public TableroSeguimiento Tablero(Semana semana, string? gobierno)
    {
        using var c = db.Abrir();
        using var m = c.QueryMultiple("""
            SELECT dp_id, data_product, gobierno, estatus, pct_avance, num_vistas
            FROM gd.vw_avance_dp_total_semana
            WHERE semana_id = @s AND estatus <> 'CANCELADO' AND (@gobierno IS NULL OR gobierno = @gobierno);

            SELECT dp_id, rubro_id, pct FROM gd.vw_avance_rubro_dp_semana WHERE semana_id = @s;

            SELECT dp_id, semana_id, comentario FROM gd.log_semanal WHERE semana_id <= @s ORDER BY dp_id, semana_id, log_id;

            SELECT COUNT(*) FROM gd.vista_gobernada WHERE activa = 1;

            -- Data products con cambio en la semana: avance del DP, avance de una de sus vistas (vigente en esa fecha) o nota de la semana.
            SELECT dp_id FROM gd.mov_avance_dp WHERE fecha BETWEEN @ini AND @fin
            UNION
            SELECT dv.dp_id FROM gd.mov_avance_vista AS mv
            JOIN gd.dp_vista AS dv ON dv.vista_id = mv.vista_id AND mv.fecha >= dv.fecha_desde AND (dv.fecha_hasta IS NULL OR mv.fecha <= dv.fecha_hasta)
            WHERE mv.fecha BETWEEN @ini AND @fin
            UNION
            SELECT dp_id FROM gd.log_semanal WHERE semana_id = @s;
            """, new { s = semana.Id, gobierno, ini = P(semana.Inicio), fin = P(semana.Fin) });

        var totales = m.Read<TotalRow>().ToList();
        var rubros = m.Read<RubroRow>().ToLookup(r => r.dp_id);
        var notas = m.Read<NotaRow>().ToLookup(n => n.dp_id);
        var inventario = m.ReadSingle<int>();
        var conCambio = m.Read<int>().ToHashSet();

        var filas = totales.Select(t =>
        {
            var nota = notas[t.dp_id].LastOrDefault();
            var celdas = Catalogos.Rubros.Select(r => new CeldaRubro(r.RubroId,
                rubros[t.dp_id].FirstOrDefault(x => x.rubro_id == r.RubroId)?.pct ?? 0)).ToList();
            return new FilaSeguimiento(t.dp_id, t.gobierno, t.data_product, t.estatus, t.pct_avance, t.num_vistas, celdas,
                nota?.comentario, nota is null || nota.semana_id == semana.Id ? null : Semana.DeId(nota.semana_id).Etiqueta,
                FilaSeguimiento.Agrupar(notas[t.dp_id].Select(n => (n.semana_id, n.comentario))), conCambio.Contains(t.dp_id));
        }).OrderBy(f => f.Gobierno).ThenBy(f => f.DataProduct).ToList();

        var activos = filas.Where(f => f.Estatus is "EN_CURSO" or "PLANEADO").ToList();
        return new TableroSeguimiento(semana, filas,
            activos.Count == 0 ? 0 : Math.Round(activos.Average(f => f.Avance), 0),
            filas.Sum(f => f.Vistas), inventario,
            filas.Where(f => f.Avance >= 100).Select(f => f.DataProduct).ToList(),
            filas.Sum(f => f.Rubros.Count(r => r.Pct < 100)), filas.Count * Catalogos.Rubros.Count);
    }

    private sealed record DpRow(int dp_id, string nombre, string gobierno, string? responsable, string estatus,
                                DateTime fecha_alta, DateTime? fecha_cierre, decimal? pct_avance, int? num_vistas);

    public IReadOnlyList<ResumenDp> DataProducts()
    {
        using var c = db.Abrir();
        return c.Query<DpRow>("""
            SELECT d.dp_id, d.nombre, g.nombre AS gobierno, r.nombre AS responsable, d.estatus, d.fecha_alta, d.fecha_cierre,
                   t.pct_avance, t.num_vistas
            FROM gd.data_product AS d
            JOIN gd.gobierno AS g ON g.gobierno_id = d.gobierno_id
            LEFT JOIN gd.responsable AS r ON r.responsable_id = d.responsable_id
            OUTER APPLY (SELECT TOP (1) pct_avance, num_vistas FROM gd.vw_avance_dp_total_semana AS t
                         WHERE t.dp_id = d.dp_id ORDER BY t.semana_id DESC) AS t
            ORDER BY g.nombre, d.nombre
            """).Select(d => new ResumenDp(d.dp_id, d.nombre, d.gobierno, d.estatus, d.responsable, d.pct_avance ?? 0, d.num_vistas ?? 0)).ToList();
    }

    private sealed record RubroDpRow(int rubro_id, decimal? pct, string? estado, DateTime? fecha);
    private sealed record VistaSemanaRow(int vista_id, string vista, string ruta_tecnica, int rubro_id, decimal pct, string estado, int etapa_orden_actual);
    private sealed record EtapaRow(int vista_id, int etapa_orden);
    private sealed record ReglasRow(int vista_id, int n);
    private sealed record BitacoraRow(DateTime fecha, string tipo, string detalle, string? usuario, string? vista);
    private sealed record LogRow(int semana_id, DateTime fecha_registro, string comentario, int? rubro_id, string usuario, string? vista);

    public DetalleDp? Detalle(int dpId, Semana semana)
    {
        using var c = db.Abrir();
        using var m = c.QueryMultiple("""
            SELECT d.dp_id, d.nombre, g.nombre AS gobierno, r.nombre AS responsable, d.estatus, d.fecha_alta, d.fecha_cierre,
                   CAST(NULL AS DECIMAL(5,2)) AS pct_avance, CAST(NULL AS INT) AS num_vistas
            FROM gd.data_product AS d
            JOIN gd.gobierno AS g ON g.gobierno_id = d.gobierno_id
            LEFT JOIN gd.responsable AS r ON r.responsable_id = d.responsable_id
            WHERE d.dp_id = @id;

            SELECT ru.rubro_id, mv.pct, mv.estado, mv.fecha
            FROM gd.rubro AS ru
            OUTER APPLY (SELECT TOP (1) pct, estado, fecha FROM gd.mov_avance_dp AS m
                         WHERE m.dp_id = @id AND m.rubro_id = ru.rubro_id AND m.fecha <= @fin
                         ORDER BY m.fecha DESC, m.mov_id DESC) AS mv
            WHERE ru.nivel = 'DP';

            SELECT a.vista_id, a.vista, v.ruta_tecnica, a.rubro_id, a.pct, a.estado, CAST(a.etapa_orden_actual AS INT) AS etapa_orden_actual
            FROM gd.vw_avance_vista_semana AS a
            JOIN gd.vista_gobernada AS v ON v.vista_id = a.vista_id
            WHERE a.semana_id = @s AND a.dp_id = @id;

            SELECT ea.vista_id, CAST(ea.etapa_orden AS INT) AS etapa_orden
            FROM gd.vw_vista_etapa_actual AS ea
            WHERE ea.vista_id IN (SELECT vista_id FROM gd.dp_vista WHERE dp_id = @id);

            SELECT vo.vista_id, COUNT(DISTINCT ro.regla_id) AS n
            FROM gd.vista_objeto AS vo
            JOIN gd.regla_objeto AS ro ON ro.objeto_id = vo.objeto_id
            JOIN gd.regla AS r ON r.regla_id = ro.regla_id AND r.activa = 1
            WHERE vo.fecha_hasta IS NULL AND vo.vista_id IN (SELECT vista_id FROM gd.dp_vista WHERE dp_id = @id)
            GROUP BY vo.vista_id;

            SELECT dp_id, rubro_id, pct FROM gd.vw_avance_rubro_dp_semana WHERE semana_id = @s AND dp_id = @id;

            SELECT ISNULL((SELECT pct_avance FROM gd.vw_avance_dp_total_semana WHERE semana_id = @s AND dp_id = @id), 0);

            SELECT b.fecha, b.tipo,
                   CASE b.tipo WHEN N'Vista agregada al DP' THEN N'Vista agregada al data product'
                               WHEN N'Vista retirada del DP' THEN N'Vista retirada del data product' ELSE b.detalle END AS detalle,
                   b.usuario, v.nombre_negocio AS vista
            FROM gd.vw_bitacora_actividades AS b
            LEFT JOIN gd.vista_gobernada AS v ON v.vista_id = b.vista_id
            WHERE b.dp_id = @id AND b.tipo <> N'Nota semanal' AND b.fecha IS NOT NULL;

            SELECT l.semana_id, l.fecha_registro, l.comentario, l.rubro_id, l.usuario, v.nombre_negocio AS vista
            FROM gd.log_semanal AS l LEFT JOIN gd.vista_gobernada AS v ON v.vista_id = l.vista_id WHERE l.dp_id = @id;

            SELECT COUNT(*) FROM gd.mov_avance_dp WHERE dp_id = @id AND nivel = 'VISTA' AND fecha <= @fin;
            """, new { id = dpId, s = semana.Id, fin = P(semana.Fin) });

        var dp = m.ReadSingleOrDefault<DpRow>();
        if (dp is null) return null;
        var rubrosDp = m.Read<RubroDpRow>().Select(r => new RubroDpDetalle(Catalogos.Rubro(r.rubro_id), r.pct ?? 0,
            r.estado is null ? null : Enum.Parse<EstadoAvance>(r.estado), D(r.fecha))).OrderBy(r => r.Rubro.Orden).ToList();
        var vistaSemana = m.Read<VistaSemanaRow>().ToList();
        var etapaHoy = m.Read<EtapaRow>().ToDictionary(e => e.vista_id, e => e.etapa_orden);
        var reglas = m.Read<ReglasRow>().ToDictionary(r => r.vista_id, r => r.n);
        var promedio = m.Read<RubroRow>().ToDictionary(r => r.rubro_id, r => r.pct);
        var avance = m.ReadSingle<decimal>();
        var bitacora = m.Read<BitacoraRow>().ToList();
        var notas = m.Read<LogRow>().ToList();
        var reportados = m.ReadSingle<int>();

        var rubrosVista = Catalogos.Rubros.Where(r => r.Nivel == NivelRubro.VISTA).ToList();
        var vistas = vistaSemana.GroupBy(v => v.vista_id).Select(g =>
        {
            var f = g.First();
            return new VistaEnDp(f.vista_id, f.vista, f.ruta_tecnica, Catalogos.EtapaPorOrden(f.etapa_orden_actual).Nombre, f.etapa_orden_actual,
                rubrosVista.Select(r => g.FirstOrDefault(x => x.rubro_id == r.RubroId) is { } x
                    ? new CeldaRubro(r.RubroId, x.pct, Enum.Parse<EstadoAvance>(x.estado))
                    : new CeldaRubro(r.RubroId, 0)).ToList(),
                reglas.GetValueOrDefault(f.vista_id),
                etapaHoy.TryGetValue(f.vista_id, out var orden) ? Catalogos.RubroActivo(orden) : null);
        }).OrderBy(v => v.Nombre).ToList();

        var entradas = bitacora.Select(b => new EntradaBitacora(Semana.De(D(b.fecha)), D(b.fecha), b.tipo, b.detalle, b.usuario ?? "", b.vista))
            .Concat(notas.Select(n => new EntradaBitacora(Semana.DeId(n.semana_id), D(n.fecha_registro), "Nota semanal",
                n.rubro_id is null ? n.comentario : $"{Catalogos.Rubro(n.rubro_id.Value).Nombre} · {n.comentario}", n.usuario, n.vista)))
            .OrderByDescending(e => e.Semana.Id).ThenByDescending(e => e.Fecha).ToList();

        var entidad = new DataProduct
        {
            DpId = dp.dp_id, Nombre = dp.nombre, Gobierno = dp.gobierno, Responsable = dp.responsable, Estatus = dp.estatus,
            FechaAlta = D(dp.fecha_alta), FechaCierre = D(dp.fecha_cierre),
        };
        return new DetalleDp(entidad, semana, avance, rubrosDp, vistas,
            rubrosVista.Select(r => new CeldaRubro(r.RubroId, promedio.GetValueOrDefault(r.RubroId))).ToList(),
            vistas.Count == 0 && reportados > 0, entradas);
    }

    public int CrearDataProduct(string nombre, string gobierno, string? responsable, string usuario)
    {
        nombre = nombre.Trim();
        if (nombre.Length == 0) throw new DomainException("Indica el nombre del data product.");
        return db.EnTransaccion((c, tx) =>
        {
            if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM gd.data_product WHERE nombre = @nombre", new { nombre }, tx) > 0)
                throw new DomainException("Ya existe un data product con ese nombre.");
            return c.ExecuteScalar<int>("""
                INSERT INTO gd.data_product (nombre, gobierno_id, responsable_id, estatus, fecha_alta)
                OUTPUT inserted.dp_id VALUES (@nombre, @gobiernoId, @responsableId, 'EN_CURSO', @hoy)
                """, new { nombre, gobiernoId = GobiernoId(c, tx, gobierno), responsableId = ResponsableId(c, tx, responsable), hoy = P(db.Hoy) }, tx);
        });
    }

    public void RegistrarAvanceDp(int dpId, int rubroId, EstadoAvance estado, decimal? pct, string? nota, string usuario)
    {
        var rubro = Catalogos.Rubros.FirstOrDefault(r => r.RubroId == rubroId && r.Nivel == NivelRubro.DP)
                    ?? throw new DomainException("El rubro no es de nivel data product.");
        var valor = Avance.NormalizarPct(estado, pct);
        db.EnTransaccion((c, tx) =>
        {
            DpAbierto(c, tx, dpId);
            c.Execute("""
                INSERT INTO gd.mov_avance_dp (dp_id, rubro_id, fecha, pct, estado, comentario, usuario)
                VALUES (@dpId, @rubroId, @hoy, @pct, @estado, @comentario, @usuario)
                """, new { dpId, rubroId = rubro.RubroId, hoy = P(db.Hoy), pct = valor, estado = estado.ToString(), comentario = Limpiar(nota), usuario }, tx);
            NotaSemanal(c, tx, dpId, db.Hoy, nota, rubro.RubroId, null, usuario);
        });
    }

    public void RegistrarAvanceVista(int dpId, int vistaId, int rubroId, EstadoAvance estado, decimal? pct, string? nota, string usuario)
    {
        var valor = Avance.NormalizarPct(estado, pct);
        db.EnTransaccion((c, tx) =>
        {
            DpAbierto(c, tx, dpId);
            var hoy = P(db.Hoy);
            var pertenece = c.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM gd.dp_vista
                WHERE dp_id = @dpId AND vista_id = @vistaId AND fecha_desde <= @hoy AND (fecha_hasta IS NULL OR fecha_hasta >= @hoy)
                """, new { dpId, vistaId, hoy }, tx);
            if (pertenece == 0) throw new DomainException("La vista no pertenece al data product.");

            // Misma regla que gd.sp_registrar_avance_vista (migración 005): cualquier rubro de vista, en cualquier etapa.
            var rubro = Catalogos.Rubros.FirstOrDefault(r => r.RubroId == rubroId && r.Nivel == NivelRubro.VISTA)
                        ?? throw new DomainException("El rubro no es de nivel vista.");

            c.Execute("""
                INSERT INTO gd.mov_avance_vista (vista_id, rubro_id, fecha, pct, estado, comentario, usuario)
                VALUES (@vistaId, @rubroId, @hoy, @pct, @estado, @comentario, @usuario)
                """, new { vistaId, rubroId = rubro.RubroId, hoy, pct = valor, estado = estado.ToString(), comentario = Limpiar(nota), usuario }, tx);
            NotaSemanal(c, tx, dpId, db.Hoy, nota, rubro.RubroId, vistaId, usuario);
        });
    }

    public void AgregarNota(int dpId, string nota, string usuario)
    {
        if (string.IsNullOrWhiteSpace(nota)) throw new DomainException("La nota está vacía.");
        db.EnTransaccion((c, tx) =>
        {
            DpAbierto(c, tx, dpId);
            NotaSemanal(c, tx, dpId, db.Hoy, nota, null, null, usuario);
        });
    }

    public void CerrarDataProduct(int dpId, string usuario) =>
        db.EnTransaccion((c, tx) =>
        {
            DpAbierto(c, tx, dpId);
            c.Execute("UPDATE gd.data_product SET estatus = 'CERRADO', fecha_cierre = @hoy WHERE dp_id = @dpId", new { dpId, hoy = P(db.Hoy) }, tx);
            NotaSemanal(c, tx, dpId, db.Hoy, "Se cierra el data product.", null, null, usuario);
        });

    public void EditarDataProduct(int dpId, string nombre, string gobierno, string? responsable, string usuario)
    {
        nombre = nombre.Trim();
        if (nombre.Length == 0) throw new DomainException("Indica el nombre del data product.");
        db.EnTransaccion((c, tx) =>
        {
            if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM gd.data_product WITH (UPDLOCK) WHERE dp_id = @dpId", new { dpId }, tx) == 0)
                throw new DomainException("El data product no existe.");
            if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM gd.data_product WHERE nombre = @nombre AND dp_id <> @dpId", new { nombre, dpId }, tx) > 0)
                throw new DomainException("Ya existe un data product con ese nombre.");
            c.Execute("UPDATE gd.data_product SET nombre = @nombre, gobierno_id = @gobiernoId, responsable_id = @responsableId WHERE dp_id = @dpId",
                new { dpId, nombre, gobiernoId = GobiernoId(c, tx, gobierno), responsableId = ResponsableId(c, tx, responsable) }, tx);
        });
    }

    // Las tablas que dependen de gd.data_product: mov_avance_dp, log_semanal y dp_vista (las tareas son por vista).
    public ResultadoEliminacion EliminarDataProduct(int dpId, string usuario) =>
        db.EnTransaccion((c, tx) =>
        {
            var nombre = c.ExecuteScalar<string?>("SELECT nombre FROM gd.data_product WITH (UPDLOCK) WHERE dp_id = @dpId", new { dpId }, tx)
                         ?? throw new DomainException("El data product no existe.");
            var avances = c.Execute("DELETE FROM gd.mov_avance_dp WHERE dp_id = @dpId", new { dpId }, tx);
            var notas = c.Execute("DELETE FROM gd.log_semanal WHERE dp_id = @dpId", new { dpId }, tx);
            var vistas = c.Execute("DELETE FROM gd.dp_vista WHERE dp_id = @dpId", new { dpId }, tx);
            c.Execute("DELETE FROM gd.data_product WHERE dp_id = @dpId", new { dpId }, tx);
            return new ResultadoEliminacion(nombre, avances, notas, vistas);
        });

    public ResultadoImportacion ImportarSeguimiento(IReadOnlyList<FilaSeguimientoImportada> filas, Semana corte, string usuario) =>
        db.EnTransaccion((c, tx) =>
        {
            var fecha = P(corte.Fin < db.Hoy ? corte.Fin : db.Hoy);
            int nuevos = 0, actualizados = 0, avances = 0, notas = 0;
            var gobiernosNuevos = new List<string>();
            foreach (var f in filas)
            {
                var gobiernoId = c.ExecuteScalar<int?>("SELECT gobierno_id FROM gd.gobierno WHERE nombre = @Gobierno", f, tx);
                if (gobiernoId is null)
                {
                    gobiernoId = c.ExecuteScalar<int>("INSERT INTO gd.gobierno (nombre) OUTPUT inserted.gobierno_id VALUES (@Gobierno)", f, tx);
                    gobiernosNuevos.Add(f.Gobierno);
                }
                var dpId = c.ExecuteScalar<int?>("SELECT dp_id FROM gd.data_product WITH (UPDLOCK) WHERE nombre = @DataProduct", f, tx);
                if (dpId is null)
                {
                    dpId = c.ExecuteScalar<int>("""
                        INSERT INTO gd.data_product (nombre, gobierno_id, estatus, fecha_alta, vistas_reportadas)
                        OUTPUT inserted.dp_id VALUES (@nombre, @gobiernoId, 'EN_CURSO', @alta, @vistas)
                        """, new { nombre = f.DataProduct, gobiernoId, alta = P(corte.Inicio), vistas = f.Vistas }, tx);
                    nuevos++;
                }
                else
                {
                    c.Execute("UPDATE gd.data_product SET gobierno_id = @gobiernoId, vistas_reportadas = @vistas WHERE dp_id = @dpId",
                              new { dpId, gobiernoId, vistas = f.Vistas }, tx);
                    actualizados++;
                }

                foreach (var (rubroId, pct) in f.Rubros)
                {
                    var actual = c.ExecuteScalar<decimal?>("""
                        SELECT TOP (1) pct FROM gd.mov_avance_dp WHERE dp_id = @dpId AND rubro_id = @rubroId AND fecha <= @fecha
                        ORDER BY fecha DESC, mov_id DESC
                        """, new { dpId, rubroId, fecha }, tx);
                    if (actual == pct) continue;   // sin cambio
                    c.Execute("""
                        INSERT INTO gd.mov_avance_dp (dp_id, rubro_id, nivel, fecha, pct, estado, comentario, usuario)
                        VALUES (@dpId, @rubroId, @nivel, @fecha, @pct, @estado, @comentario, @usuario)
                        """, new
                    {
                        dpId, rubroId, nivel = Catalogos.Rubro(rubroId).Nivel.ToString(), fecha, pct,
                        estado = SeguimientoService.EstadoPorPct(pct).ToString(), comentario = $"Importación seguimiento {corte.Etiqueta}", usuario,
                    }, tx);
                    avances++;
                }
                foreach (var n in f.Notas)
                {
                    notas += c.Execute("""
                        INSERT INTO gd.log_semanal (dp_id, semana_id, comentario, usuario, fecha_registro)
                        SELECT @dpId, @semanaId, @texto, @usuario, @registro
                        WHERE NOT EXISTS (SELECT 1 FROM gd.log_semanal WHERE dp_id = @dpId AND semana_id = @semanaId AND comentario = @texto)
                        """, new { dpId, semanaId = n.Semana.Id, texto = n.Texto, usuario, registro = P(n.Semana.Fin) }, tx);
                }
            }
            return new ResultadoImportacion(nuevos, actualizados, avances, notas, gobiernosNuevos);
        });

    private static void DpAbierto(System.Data.IDbConnection c, System.Data.IDbTransaction tx, int dpId)
    {
        var estatus = c.ExecuteScalar<string?>("SELECT estatus FROM gd.data_product WITH (UPDLOCK) WHERE dp_id = @dpId", new { dpId }, tx)
                      ?? throw new DomainException("El data product no existe.");
        if (estatus is "CERRADO" or "CANCELADO") throw new DomainException("El data product está cerrado.");
    }

    private static string? Limpiar(string? t) => string.IsNullOrWhiteSpace(t) ? null : t.Trim();
}
