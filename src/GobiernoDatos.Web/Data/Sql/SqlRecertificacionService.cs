using System.Data;
using Dapper;
using GobiernoDatos.Web.Domain;
using GobiernoDatos.Web.Services;
using Microsoft.Data.SqlClient;
using static GobiernoDatos.Web.Data.Sql.SqlAux;

namespace GobiernoDatos.Web.Data.Sql;

// Recertificación sobre el esquema gd v3. Las reglas viven en la base (procedimientos sp_*, CHECK y triggers):
// este servicio solo los invoca y traduce sus errores (THROW 50xxx) a mensajes de negocio.
// Seguridad por fila: el certificador se identifica por gd.responsable.usuario_red (DOMINIO\usuario) y solo ve las macro áreas
// donde es titular o delegado vigente (gd.macro_area_certificador).
public sealed class SqlRecertificacionService(SqlConexion db, IDirectorioUsuarios directorio, Microsoft.Extensions.Options.IOptions<GobiernoDatosOptions> opciones) : IRecertificacionService
{
    // ---- Eventos ----

    private sealed record EventoRow(int evento_id, string nombre, DateTime? fecha_extracto, DateTime? fecha_inicio, DateTime? fecha_limite,
                                    DateTime? fecha_cierre, string estatus, int val_roles_varias_vistas, int val_vistas_varios_roles,
                                    int val_roles_nuevos, int val_vistas_nuevas);
    private sealed record RechazoRow(int? fila, string? usuario, string? rol, string? vista, string motivo);
    private sealed record ConfirmacionRow(int macro_area_id, string usuario, DateTime fecha_registro);

    private static RecertEvento? CargarEvento(IDbConnection c, int eventoId)
    {
        using var m = c.QueryMultiple("""
            SELECT evento_id, nombre, fecha_extracto, fecha_inicio, fecha_limite, fecha_cierre, estatus,
                   val_roles_varias_vistas, val_vistas_varios_roles, val_roles_nuevos, val_vistas_nuevas
            FROM gd.recert_evento WHERE evento_id = @eventoId;
            SELECT fila, usuario, rol, vista, motivo FROM gd.recert_carga_rechazo WHERE evento_id = @eventoId ORDER BY fila;
            SELECT macro_area_id, usuario, fecha_registro FROM gd.recert_confirmacion WHERE evento_id = @eventoId;
            """, new { eventoId });
        var e = m.ReadSingleOrDefault<EventoRow>();
        if (e is null) return null;
        var rechazos = m.Read<RechazoRow>().ToList();
        var confirmaciones = m.Read<ConfirmacionRow>().ToList();

        var evento = new RecertEvento
        {
            EventoId = e.evento_id, Nombre = e.nombre, FechaExtracto = D(e.fecha_extracto), FechaInicio = D(e.fecha_inicio),
            FechaLimite = D(e.fecha_limite), FechaCierre = D(e.fecha_cierre), Estatus = Enum.Parse<EstatusEvento>(e.estatus),
            Validacion = new ValidacionCarga(e.val_roles_varias_vistas, e.val_vistas_varios_roles, e.val_roles_nuevos, e.val_vistas_nuevas, rechazos.Count),
        };
        evento.Rechazos.AddRange(rechazos.Select(r => new RecertCargaRechazo(r.fila ?? 0, r.usuario, r.rol, r.vista, r.motivo)));
        evento.Confirmaciones.AddRange(confirmaciones.Select(x => new ConfirmacionMacroArea(x.macro_area_id, x.usuario, x.fecha_registro)));
        return evento;
    }

    public RecertEvento? EventoActivo()
    {
        using var c = db.Abrir();
        var id = c.ExecuteScalar<int?>("""
            SELECT TOP (1) evento_id FROM gd.recert_evento
            ORDER BY CASE WHEN estatus <> 'CERRADO' THEN 0 ELSE 1 END, evento_id DESC
            """);
        return id is null ? null : CargarEvento(c, id.Value);
    }

    private sealed record HistorialRow(int evento_id, string nombre, DateTime? fecha_inicio, DateTime? fin, string estatus,
                                       int accesos, int bajas, int ejecutadas);

    public IReadOnlyList<FilaHistorialEvento> Historial()
    {
        using var c = db.Abrir();
        return c.Query<HistorialRow>("""
            SELECT e.evento_id, e.nombre, e.fecha_inicio, ISNULL(e.fecha_cierre, e.fecha_limite) AS fin, e.estatus,
                   (SELECT COUNT(*) FROM gd.recert_acceso AS a WHERE a.evento_id = e.evento_id) AS accesos,
                   (SELECT COUNT(*) FROM gd.vw_recert_evidencia AS x WHERE x.evento_id = e.evento_id AND x.decision = 'BAJA') AS bajas,
                   (SELECT COUNT(*) FROM gd.vw_recert_evidencia AS x
                    WHERE x.evento_id = e.evento_id AND x.decision = 'BAJA' AND x.fecha_ejecucion IS NOT NULL) AS ejecutadas
            FROM gd.recert_evento AS e
            ORDER BY e.evento_id DESC
            """).Select(h => new FilaHistorialEvento(h.evento_id, h.nombre, D(h.fecha_inicio), D(h.fin), Enum.Parse<EstatusEvento>(h.estatus),
                                                     h.accesos, h.bajas, h.ejecutadas)).ToList();
    }

    private sealed record AvanceRow(int macro_area_id, string macro_area, int accesos, int continua, int baja);
    private sealed record CertRow(int macro_area_id, string nombre, string tipo);

    public TableroEvento? Tablero(int eventoId)
    {
        using var c = db.Abrir();
        var evento = CargarEvento(c, eventoId);
        if (evento is null) return null;
        var macros = MacroAreas(c, eventoId, null, evento);
        return new TableroEvento(evento, new Conteo(macros.Sum(m => m.Conteo.Accesos), macros.Sum(m => m.Conteo.Continua), macros.Sum(m => m.Conteo.Baja)), macros);
    }

    private List<FilaMacroArea> MacroAreas(IDbConnection c, int eventoId, IReadOnlySet<int>? solo, RecertEvento evento)
    {
        var avance = c.Query<AvanceRow>("""
            SELECT macro_area_id, macro_area, accesos, continua, baja
            FROM gd.vw_recert_avance_macro_area WHERE evento_id = @eventoId
            """, new { eventoId });
        var certs = c.Query<CertRow>("""
            SELECT ca.macro_area_id, r.nombre, ca.tipo
            FROM gd.macro_area_certificador AS ca JOIN gd.responsable AS r ON r.responsable_id = ca.responsable_id
            WHERE ca.fecha_desde <= @hoy AND (ca.fecha_hasta IS NULL OR ca.fecha_hasta >= @hoy)
            ORDER BY ca.tipo DESC, r.nombre
            """, new { hoy = P(db.Hoy) }).ToLookup(x => x.macro_area_id, x => x.nombre);
        return avance.Where(a => solo is null || solo.Contains(a.macro_area_id))
            .Select(a => new FilaMacroArea(a.macro_area_id, a.macro_area, certs[a.macro_area_id].ToList(), new Conteo(a.accesos, a.continua, a.baja),
                                           evento.Confirmaciones.Any(x => x.MacroAreaId == a.macro_area_id)))
            .OrderBy(m => m.MacroArea).ToList();
    }

    public int CrearEvento(string nombre, DateOnly? fechaExtracto, DateOnly? fechaLimite, IReadOnlyList<Dictionary<string, string>> extracto)
    {
        var tabla = Staging(extracto);
        return Negocio(() => db.EnTransaccion((c, tx) => Cargar(c, tx, nombre, fechaExtracto, fechaLimite, tabla)));
    }

    // Columnas del extracto (CSV o HANA, ya normalizadas por la fuente) → filas de gd.stg_recertificacion.
    private static DataTable Staging(IReadOnlyList<Dictionary<string, string>> extracto)
    {
        if (extracto.Count == 0) throw new DomainException("El extracto no tiene filas.");
        if (!extracto[0].ContainsKey("USUARIO") || !extracto[0].ContainsKey("ROL") || !extracto[0].ContainsKey("VISTA"))
            throw new DomainException("El extracto no tiene las columnas de recertificación (USUARIO, ROL, VISTA…).");

        var t = new DataTable();
        foreach (var col in new[] { "area", "content", "usuario", "nombre", "rol", "vista", "accesos_3m", "area_responsable", "responsable", "gobierno" })
            t.Columns.Add(col, typeof(string));
        foreach (var f in extracto)
            t.Rows.Add(f.GetValueOrDefault("AREA"), f.GetValueOrDefault("CONTENT"), f.GetValueOrDefault("USUARIO"), f.GetValueOrDefault("NOMBRE"),
                       f.GetValueOrDefault("ROL"), f.GetValueOrDefault("VISTA"), f.GetValueOrDefault("ACCESO ULTIMOS 3 MESES"),
                       f.GetValueOrDefault("Área Responsable") ?? f.GetValueOrDefault("Area Responsable"), null,   // RESPONSABLE ya no se usa (010)
                       f.GetValueOrDefault("GOBIERNO"));
        return t;
    }

    // El staging es compartido: se vacía y se llena dentro de la misma transacción que la carga (SqlBulkCopy: miles de filas).
    private static int Cargar(IDbConnection c, IDbTransaction tx, string nombre, DateOnly? fechaExtracto, DateOnly? fechaLimite, DataTable tabla)
    {
        c.Execute("TRUNCATE TABLE gd.stg_recertificacion", transaction: tx);
        using (var bulk = new SqlBulkCopy((SqlConnection)c, SqlBulkCopyOptions.Default, (SqlTransaction)tx)
               { DestinationTableName = "gd.stg_recertificacion", BulkCopyTimeout = 300 })
        {
            foreach (DataColumn col in tabla.Columns) bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            bulk.WriteToServer(tabla);
        }
        return c.QueryFirst<int>("gd.sp_cargar_extracto_recertificacion",
            new { nombre, fecha_extracto = fechaExtracto?.ToDateTime(TimeOnly.MinValue), fecha_limite = fechaLimite?.ToDateTime(TimeOnly.MinValue), usuario = "portal" },
            tx, commandType: CommandType.StoredProcedure, commandTimeout: 300);
    }

    public void AbrirEvento(int eventoId) => Sp("gd.sp_abrir_evento", new { evento_id = eventoId });

    public int CerrarEvento(int eventoId) => Negocio(() =>
    {
        using var c = db.Abrir();
        return c.QuerySingle<int>("gd.sp_cerrar_evento", new { evento_id = eventoId }, commandType: CommandType.StoredProcedure);
    });

    public void DescartarBorrador(int eventoId) => Sp("gd.sp_descartar_evento", new { evento_id = eventoId });

    // ---- Mi recertificación ----

    private sealed record AccesoRow(long acceso_id, string usuario, string nombre_usuario, string vista, string rol, int macro_area_id,
                                    string macro_area, int accesos_3m, string decision, string? comentario, string? ticket, string? anterior);

    public MiRecertificacion MisAccesos(string certificador)
    {
        using var c = db.Abrir();
        var eventoId = c.ExecuteScalar<int?>("SELECT TOP (1) evento_id FROM gd.recert_evento WHERE estatus <> 'BORRADOR' ORDER BY evento_id DESC");
        if (eventoId is null) return new(null, [], []);
        var evento = CargarEvento(c, eventoId.Value)!;

        var accesos = c.Query<AccesoRow>("""
            DECLARE @anterior INT = (SELECT TOP (1) evento_id FROM gd.recert_evento
                                     WHERE estatus <> 'BORRADOR' AND evento_id < @eventoId ORDER BY evento_id DESC);
            SELECT x.acceso_id, x.usuario, x.nombre_usuario, x.vista, x.rol, x.macro_area_id, x.macro_area, x.accesos_3m,
                   x.decision, x.comentario, x.ticket, p.decision AS anterior
            FROM gd.vw_recert_evidencia AS x
            OUTER APPLY (SELECT TOP (1) pv.decision FROM gd.vw_recert_evidencia AS pv
                         WHERE pv.evento_id = @anterior AND pv.usuario = x.usuario AND pv.rol = x.rol AND pv.decision <> 'PENDIENTE') AS p
            WHERE x.evento_id = @eventoId
              AND EXISTS (SELECT 1 FROM gd.macro_area_certificador AS ca JOIN gd.responsable AS r ON r.responsable_id = ca.responsable_id
                          WHERE r.usuario_red = @certificador AND ca.macro_area_id = x.macro_area_id
                            AND ca.fecha_desde <= @hoy AND (ca.fecha_hasta IS NULL OR ca.fecha_hasta >= @hoy))
            ORDER BY x.vista, x.accesos_3m DESC
            """, new { eventoId, certificador = LoginParaBuscar(certificador), hoy = P(db.Hoy) }).ToList();

        var mias = accesos.Select(a => a.macro_area_id).ToHashSet();
        return new(evento, MacroAreas(c, eventoId.Value, mias, evento), accesos.Select(a => new AccesoCertificar(
            a.acceso_id, a.usuario, a.nombre_usuario, a.vista, a.rol, a.macro_area, a.accesos_3m, Decisión(a.anterior),
            Decisión(a.decision), a.comentario, a.ticket is not null)).ToList());
    }

    private sealed record AccesoClave(int evento_id, int macro_area_id);

    public ResultadoDecision Decidir(long accesoId, Decision decision, string? comentario, string certificador) => Negocio(() =>
    {
        using var c = db.Abrir();
        Registrar(c, null, accesoId, decision, comentario, certificador);
        var a = c.QuerySingle<AccesoClave>("SELECT evento_id, macro_area_id FROM gd.recert_acceso WHERE acceso_id = @accesoId", new { accesoId });
        var avance = c.QuerySingle<AvanceRow>("""
            SELECT macro_area_id, macro_area, accesos, continua, baja FROM gd.vw_recert_avance_macro_area
            WHERE evento_id = @evento_id AND macro_area_id = @macro_area_id
            """, new { a.evento_id, a.macro_area_id });
        return new ResultadoDecision(accesoId, decision, string.IsNullOrWhiteSpace(comentario) ? null : comentario.Trim(),
                                     new Conteo(avance.accesos, avance.continua, avance.baja));
    });

    // Acción masiva: una decisión individual por acceso pendiente, todo o nada.
    // Cada acceso se registra de forma individual con gd.sp_registrar_decision (reglas en la base). Para miles de accesos,
    // las decisiones existentes y el responsable de cada macro área se consultan una sola vez.
    public int ContinuaMasivo(IEnumerable<long> accesoIds, string certificador) => Negocio(() => db.EnTransaccion((c, tx) =>
    {
        var ids = accesoIds.Distinct().ToList();
        if (ids.Count == 0) return 0;
        var login = LoginParaBuscar(certificador);
        var accesos = c.Query<MasivoRow>("""
            SELECT a.acceso_id, a.macro_area_id,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM gd.recert_decision AS d WHERE d.acceso_id = a.acceso_id) THEN 1 ELSE 0 END AS BIT) AS decidido
            FROM gd.recert_acceso AS a
            WHERE a.acceso_id IN (SELECT CAST(value AS BIGINT) FROM STRING_SPLIT(@ids, ','))
            """, new { ids = string.Join(",", ids) }, tx).ToDictionary(a => a.acceso_id);
        var responsables = c.Query<ResponsableMacroRow>("""
            SELECT ca.macro_area_id, MIN(ca.responsable_id) AS responsable_id
            FROM gd.macro_area_certificador AS ca JOIN gd.responsable AS r ON r.responsable_id = ca.responsable_id
            WHERE r.usuario_red = @login AND ca.fecha_desde <= @hoy AND (ca.fecha_hasta IS NULL OR ca.fecha_hasta >= @hoy)
            GROUP BY ca.macro_area_id
            """, new { login, hoy = P(db.Hoy) }, tx).ToDictionary(r => r.macro_area_id, r => r.responsable_id);

        int n = 0;
        foreach (var id in ids)
        {
            if (!accesos.TryGetValue(id, out var a)) throw new DomainException("El acceso no existe.");
            if (a.decidido) continue;
            if (!responsables.TryGetValue(a.macro_area_id, out var responsableId))
                throw new DomainException("No eres certificador vigente de la macro área de este acceso.");
            c.Execute("gd.sp_registrar_decision",
                new { acceso_id = id, decision = "CONTINUA", comentario = "Acción masiva", responsable_id = responsableId, usuario = login },
                tx, commandType: CommandType.StoredProcedure);
            n++;
        }
        return n;
    }));

    private sealed record MasivoRow(long acceso_id, int macro_area_id, bool decidido);
    private sealed record ResponsableMacroRow(int macro_area_id, int responsable_id);

    private void Registrar(IDbConnection c, IDbTransaction? tx, long accesoId, Decision decision, string? comentario, string certificador)
    {
        var responsableId = ResponsableCertificador(c, tx, accesoId, certificador)
                            ?? throw new DomainException("No eres certificador vigente de la macro área de este acceso.");
        c.Execute("gd.sp_registrar_decision",
            new { acceso_id = accesoId, decision = decision.ToString(), comentario, responsable_id = responsableId, usuario = LoginParaBuscar(certificador) },
            tx, commandType: CommandType.StoredProcedure);
    }

    private int? ResponsableCertificador(IDbConnection c, IDbTransaction? tx, long accesoId, string certificador) =>
        c.ExecuteScalar<int?>("""
            SELECT TOP (1) ca.responsable_id
            FROM gd.recert_acceso AS a
            JOIN gd.macro_area_certificador AS ca ON ca.macro_area_id = a.macro_area_id
            JOIN gd.responsable AS r ON r.responsable_id = ca.responsable_id
            WHERE a.acceso_id = @accesoId AND r.usuario_red = @certificador
              AND ca.fecha_desde <= @hoy AND (ca.fecha_hasta IS NULL OR ca.fecha_hasta >= @hoy)
            """, new { accesoId, certificador = LoginParaBuscar(certificador), hoy = P(db.Hoy) }, tx);

    public void ConfirmarMacroArea(int macroAreaId, string certificador) => Negocio(() =>
    {
        using var c = db.Abrir();
        var eventoId = c.ExecuteScalar<int?>("SELECT TOP (1) evento_id FROM gd.recert_evento WHERE estatus = 'ABIERTO' ORDER BY evento_id DESC")
                       ?? throw new DomainException("No hay un evento abierto.");
        var responsableId = c.ExecuteScalar<int?>("""
            SELECT TOP (1) ca.responsable_id FROM gd.macro_area_certificador AS ca JOIN gd.responsable AS r ON r.responsable_id = ca.responsable_id
            WHERE ca.macro_area_id = @macroAreaId AND r.usuario_red = @certificador
              AND ca.fecha_desde <= @hoy AND (ca.fecha_hasta IS NULL OR ca.fecha_hasta >= @hoy)
            """, new { macroAreaId, certificador = LoginParaBuscar(certificador), hoy = P(db.Hoy) }) ?? throw new DomainException("No eres certificador de esa macro área.");
        c.Execute("gd.sp_confirmar_macro_area",
            new { evento_id = eventoId, macro_area_id = macroAreaId, responsable_id = responsableId, usuario = LoginParaBuscar(certificador) },
            commandType: CommandType.StoredProcedure);
        // El certificador terminó: si hay bajas y gerente, y no hay solicitud vigente, se pide el VoBo.
        var pedir = c.ExecuteScalar<int>("""
            SELECT CASE WHEN EXISTS (SELECT 1 FROM gd.recert_gerente)
                         AND EXISTS (SELECT 1 FROM gd.vw_recert_evidencia WHERE evento_id = @eventoId AND macro_area_id = @macroAreaId AND decision = 'BAJA')
                         AND NOT EXISTS (SELECT 1 FROM gd.recert_vobo WHERE evento_id = @eventoId AND macro_area_id = @macroAreaId
                                         AND estatus IN ('PENDIENTE','ENVIADA','AUTORIZADA')) THEN 1 ELSE 0 END
            """, new { eventoId, macroAreaId });
        if (pedir == 1)
            c.Execute("gd.sp_vobo_solicitar", new { evento_id = eventoId, macro_area_id = macroAreaId, solicitado_por = LoginParaBuscar(certificador) },
                      commandType: CommandType.StoredProcedure);
        return 0;
    });

    // ---- Bajas y evidencia ----

    private sealed record BajaRow(long acceso_id, string usuario, string nombre_usuario, string vista, string rol, string macro_area,
                                  string? decidido_por, DateTime fecha_decision, string? comentario, string? ticket,
                                  DateTime? fecha_ejecucion, string? ejecutado_por);

    public TableroBajas? Bajas(int eventoId)
    {
        using var c = db.Abrir();
        var evento = CargarEvento(c, eventoId);
        if (evento is null) return null;
        var filas = c.Query<BajaRow>("""
            SELECT acceso_id, usuario, nombre_usuario, vista, rol, macro_area, decidido_por, fecha_decision, comentario,
                   ticket, fecha_ejecucion, ejecutado_por
            FROM gd.vw_recert_evidencia WHERE evento_id = @eventoId AND decision = 'BAJA'
            """, new { eventoId }).Select(b => new FilaBaja(b.acceso_id, b.usuario, b.nombre_usuario, b.vista, b.rol, b.macro_area,
                b.decidido_por ?? "", b.fecha_decision, b.comentario,
                b.fecha_ejecucion is null ? null : new RecertBajaEjecucion
                {
                    AccesoId = b.acceso_id, FechaEjecucion = D(b.fecha_ejecucion.Value), Ticket = b.ticket!, EjecutadoPor = b.ejecutado_por!,
                }))
            .OrderBy(f => f.Ejecucion is not null).ThenBy(f => f.FechaDecision).ToList();

        var ejecutadas = filas.Where(f => f.Ejecucion is not null).ToList();
        decimal? dias = ejecutadas.Count == 0 ? null
            : (decimal)ejecutadas.Average(f => f.Ejecucion!.FechaEjecucion.DayNumber - DateOnly.FromDateTime(f.FechaDecision).DayNumber);
        return new TableroBajas(evento, filas, filas.Count, ejecutadas.Count, dias, MacroAreasBajas(c, eventoId));
    }

    private sealed record MacroBajasRow(int macro_area_id, string macro_area, int bajas, int ejecutadas, int por_decidir);
    private sealed record GerenteRow(string usuario_red, string nombre, string correo);
    private sealed record VoBoRow(long vobo_id, int evento_id, int macro_area_id, string gerente_nombre, string gerente_correo, int bajas,
                                  string estatus, string solicitado_por, DateTime fecha_solicitud, DateTime? fecha_envio, DateTime? fecha_respuesta,
                                  string? respondido_por, string? comentario_respuesta, string? motivo_cancelacion);

    private sealed record InfraRow(long asignacion_id, int evento_id, int macro_area_id, string responsable_usuario_red, string responsable_nombre,
                                   string responsable_correo, string cambio, int bajas, string asignado_por, DateTime fecha_asignacion,
                                   bool notificado, string? error_notificacion);

    private static AsignacionInfra Infra(InfraRow i) => new(i.asignacion_id, i.evento_id, i.macro_area_id, i.responsable_usuario_red,
        i.responsable_nombre, i.responsable_correo, i.cambio, i.bajas, i.asignado_por, i.fecha_asignacion, i.notificado, i.error_notificacion);

    private static VoBoGerente VoBo(VoBoRow v) => new(v.vobo_id, v.evento_id, v.macro_area_id, v.gerente_nombre, v.gerente_correo, v.bajas,
        Enum.Parse<EstatusVoBo>(v.estatus), v.solicitado_por, v.fecha_solicitud, v.fecha_envio, v.fecha_respuesta, v.respondido_por,
        v.comentario_respuesta, v.motivo_cancelacion);

    // Macro áreas con bajas en el evento: conteos, gerente y última solicitud de VoBo.
    private static List<BajasMacroArea> MacroAreasBajas(IDbConnection c, int eventoId)
    {
        using var m = c.QueryMultiple("""
            SELECT e.macro_area_id, e.macro_area,
                   SUM(CASE WHEN e.decision = 'BAJA' THEN 1 ELSE 0 END) AS bajas,
                   SUM(CASE WHEN e.decision = 'BAJA' AND e.ticket IS NOT NULL THEN 1 ELSE 0 END) AS ejecutadas,
                   SUM(CASE WHEN e.decision = 'PENDIENTE' THEN 1 ELSE 0 END) AS por_decidir
            FROM gd.vw_recert_evidencia AS e
            WHERE e.evento_id = @eventoId
            GROUP BY e.macro_area_id, e.macro_area
            HAVING SUM(CASE WHEN e.decision = 'BAJA' THEN 1 ELSE 0 END) > 0
            ORDER BY e.macro_area;

            SELECT v.vobo_id, v.evento_id, v.macro_area_id, v.gerente_nombre, v.gerente_correo, v.bajas, v.estatus, v.solicitado_por,
                   v.fecha_solicitud, v.fecha_envio, v.fecha_respuesta, v.respondido_por, v.comentario_respuesta, v.motivo_cancelacion
            FROM gd.recert_vobo AS v
            WHERE v.evento_id = @eventoId
              AND v.vobo_id = (SELECT MAX(x.vobo_id) FROM gd.recert_vobo AS x WHERE x.evento_id = v.evento_id AND x.macro_area_id = v.macro_area_id);

            SELECT usuario_red, nombre, correo FROM gd.recert_gerente;

            SELECT i.asignacion_id, i.evento_id, i.macro_area_id, i.responsable_usuario_red, i.responsable_nombre, i.responsable_correo,
                   i.cambio, i.bajas, i.asignado_por, i.fecha_asignacion, i.notificado, i.error_notificacion
            FROM gd.recert_asignacion_infra AS i
            WHERE i.evento_id = @eventoId
              AND i.asignacion_id = (SELECT MAX(x.asignacion_id) FROM gd.recert_asignacion_infra AS x
                                     WHERE x.evento_id = i.evento_id AND x.macro_area_id = i.macro_area_id);
            """, new { eventoId });
        var macros = m.Read<MacroBajasRow>().ToList();
        var vobos = m.Read<VoBoRow>().ToDictionary(v => v.macro_area_id, VoBo);
        var gerente = m.ReadSingleOrDefault<GerenteRow>() is { } g ? new GerenteVoBo(g.usuario_red, g.nombre, g.correo) : null;
        var infra = m.Read<InfraRow>().ToDictionary(i => i.macro_area_id, Infra);
        return macros.Select(x => new BajasMacroArea(x.macro_area_id, x.macro_area, x.bajas, x.ejecutadas, x.por_decidir,
            gerente, vobos.GetValueOrDefault(x.macro_area_id), infra.GetValueOrDefault(x.macro_area_id))).ToList();
    }

    private sealed record IncluidoRow(string nombre, DateTime fecha, int accesos_3m);
    private sealed record DecisionRow(string decision, string? comentario, string nombre, DateTime fecha_registro);
    private sealed record EjecucionRow(DateTime fecha_ejecucion, string ticket, string ejecutado_por);
    private sealed record AnuladaRow(DateTime fecha_ejecucion, string ticket, string ejecutado_por, string motivo, string anulado_por, DateTime fecha_anulacion);

    public IReadOnlyList<PasoTraza> Traza(long accesoId)
    {
        using var c = db.Abrir();
        using var m = c.QueryMultiple("""
            SELECT e.nombre, COALESCE(CAST(e.fecha_inicio AS DATETIME2), CAST(e.fecha_extracto AS DATETIME2), e.fecha_registro) AS fecha, a.accesos_3m
            FROM gd.recert_acceso AS a JOIN gd.recert_evento AS e ON e.evento_id = a.evento_id WHERE a.acceso_id = @accesoId;
            SELECT d.decision, d.comentario, r.nombre, d.fecha_registro
            FROM gd.recert_decision AS d JOIN gd.responsable AS r ON r.responsable_id = d.decidido_por
            WHERE d.acceso_id = @accesoId ORDER BY d.decision_id;
            SELECT fecha_ejecucion, ticket, ejecutado_por FROM gd.recert_baja_ejecucion WHERE acceso_id = @accesoId;
            SELECT fecha_ejecucion, ticket, ejecutado_por, motivo, anulado_por, fecha_anulacion
            FROM gd.recert_baja_ejecucion_anulada WHERE acceso_id = @accesoId ORDER BY anulacion_id;
            """, new { accesoId });
        var incluido = m.ReadSingleOrDefault<IncluidoRow>();
        if (incluido is null) return [];
        var pasos = new List<PasoTraza> { new(incluido.fecha, "Acceso incluido en el evento", $"{incluido.nombre} · {incluido.accesos_3m:N0} accesos en 3 meses") };
        Decision? previa = null;
        foreach (var d in m.Read<DecisionRow>())
        {
            var actual = Enum.Parse<Decision>(d.decision);
            var titulo = previa is null ? $"Decisión: {ReglasRecertificacion.Etiqueta(actual)}"
                       : previa == actual ? $"Decisión ratificada: {ReglasRecertificacion.Etiqueta(actual)}"
                       : $"Decisión cambiada a {ReglasRecertificacion.Etiqueta(actual)}";
            pasos.Add(new(d.fecha_registro, titulo, d.nombre + (d.comentario is null ? "" : " · " + d.comentario)));
            previa = actual;
        }
        var ejecucion = m.ReadSingleOrDefault<EjecucionRow>();
        foreach (var an in m.Read<AnuladaRow>())
        {
            pasos.Add(new(an.fecha_ejecucion, "Rol retirado", $"{an.ejecutado_por} · ticket {an.ticket}"));
            pasos.Add(new(an.fecha_anulacion, "Ticket anulado", $"{an.anulado_por} · ticket {an.ticket} · {an.motivo}"));
        }
        if (ejecucion is { } ej)
            pasos.Add(new(ej.fecha_ejecucion, "Rol retirado", $"{ej.ejecutado_por} · ticket {ej.ticket}"));
        return pasos;
    }

    public void RegistrarEjecucion(long accesoId, string ticket, DateOnly? fecha, string usuario) =>
        Sp("gd.sp_registrar_baja_ejecucion",
           new { acceso_id = accesoId, ticket, ejecutado_por = usuario, fecha_ejecucion = fecha?.ToDateTime(TimeOnly.MinValue) });

    public void AnularEjecucion(long accesoId, string motivo, string usuario) =>
        Sp("gd.sp_anular_baja_ejecucion", new { acceso_id = accesoId, motivo, anulado_por = usuario });

    private sealed record EvidenciaRow(string evento, string estatus_evento, string macro_area, string usuario, string nombre_usuario,
                                       string rol, string vista, int accesos_3m, string decision, string? comentario, string? decidido_por,
                                       DateTime? fecha_decision, string? ticket, DateTime? fecha_ejecucion, string? ejecutado_por);

    public string EvidenciaCsv(int eventoId)
    {
        using var c = db.Abrir();
        var filas = c.Query<EvidenciaRow>("""
            SELECT evento, estatus_evento, macro_area, usuario, nombre_usuario, rol, vista, accesos_3m, decision, comentario,
                   decidido_por, fecha_decision, ticket, fecha_ejecucion, ejecutado_por
            FROM gd.vw_recert_evidencia WHERE evento_id = @eventoId ORDER BY macro_area, vista, usuario
            """, new { eventoId });
        return Csv.Escribir(["Evento", "Estatus evento", "Macro área", "Usuario", "Nombre", "Rol", "Vista", "Accesos 3 meses",
                             "Decisión", "Comentario", "Decidió", "Fecha decisión", "Ticket", "Fecha ejecución", "Ejecutó"],
            filas.Select(f => new object?[]
            {
                f.evento, ReglasRecertificacion.Etiqueta(Enum.Parse<EstatusEvento>(f.estatus_evento)), f.macro_area, f.usuario, f.nombre_usuario,
                f.rol, f.vista, f.accesos_3m, Decisión(f.decision) is { } d ? ReglasRecertificacion.Etiqueta(d) : "PENDIENTE",
                f.comentario, f.decidido_por, f.fecha_decision?.ToString("yyyy-MM-dd HH:mm"), f.ticket,
                f.fecha_ejecucion?.ToString("yyyy-MM-dd"), f.ejecutado_por,
            }));
    }

    private sealed record AuditoriaRow(long acceso_id, int evento_id, string evento, string? area, string? content, string rol, string vista,
                                       string usuario, string nombre_usuario, string? gobierno, string macro_area, string? responsable,
                                       string decision, string? comentario, int accesos_3m);

    public IReadOnlyList<FilaAuditoria> Auditoria(int? eventoId)
    {
        using var c = db.Abrir();
        return c.Query<AuditoriaRow>("""
            SELECT acceso_id, evento_id, evento, area, content, rol, vista, usuario, nombre_usuario, gobierno, macro_area, responsable,
                   decision, comentario, accesos_3m
            FROM gd.vw_recert_evidencia
            WHERE @eventoId IS NULL OR evento_id = @eventoId
            ORDER BY evento_id DESC, macro_area, vista, usuario
            """, new { eventoId })
            .Select(f => new FilaAuditoria(f.acceso_id, f.evento_id, f.evento, f.area, f.content, f.rol, f.vista, f.usuario, f.nombre_usuario,
                f.gobierno, f.macro_area, f.responsable, Decisión(f.decision), f.comentario, f.accesos_3m)).ToList();
    }

    // ---- Recordatorios ----

    private sealed record PendienteRow(string certificador, string? usuario_red, string? correo, string macro_area, string tipo, int pendientes);

    public IReadOnlyList<CertificadorPendiente> PendientesPorCertificador(int eventoId)
    {
        using var c = db.Abrir();
        return c.Query<PendienteRow>("""
            SELECT certificador, usuario_red, correo, macro_area, tipo, pendientes
            FROM gd.vw_recert_pendientes_certificador
            WHERE evento_id = @eventoId
            ORDER BY macro_area, tipo DESC, certificador
            """, new { eventoId })
            .Select(p => new CertificadorPendiente(p.certificador, p.usuario_red, p.correo, p.macro_area, p.tipo, p.pendientes)).ToList();
    }

    public void RegistrarRecordatorio(RecertRecordatorio r)
    {
        using var c = db.Abrir();
        c.Execute("""
            INSERT INTO gd.recert_recordatorio (evento_id, certificador, usuario_red, correo, macro_areas, pendientes, enviado, error, enviado_por, fecha)
            VALUES (@EventoId, @Certificador, @UsuarioRed, @Correo, @MacroAreas, @Pendientes, @Enviado, LEFT(@Error, 400), @EnviadoPor, @Fecha)
            """, r);
    }

    private sealed record RecordatorioRow(int evento_id, string certificador, string? usuario_red, string? correo, string macro_areas,
                                          int pendientes, bool enviado, string? error, string enviado_por, DateTime fecha);

    public IReadOnlyList<RecertRecordatorio> Recordatorios(int eventoId)
    {
        using var c = db.Abrir();
        return c.Query<RecordatorioRow>("""
            SELECT evento_id, certificador, usuario_red, correo, macro_areas, pendientes, enviado, error, enviado_por, fecha
            FROM gd.recert_recordatorio WHERE evento_id = @eventoId ORDER BY fecha DESC, recordatorio_id DESC
            """, new { eventoId })
            .Select(r => new RecertRecordatorio(r.evento_id, r.certificador, r.usuario_red, r.correo, r.macro_areas, r.pendientes,
                                                r.enviado, r.error, r.enviado_por, r.fecha)).ToList();
    }

    // ---- Catálogo GOBIERNO → macro área (migración 010) ----

    private sealed record MapeoRow(string gobierno, string macro_area, string usuario, DateTime fecha_registro);

    public IReadOnlyList<MapeoGobierno> MapeosGobierno()
    {
        using var c = db.Abrir();
        return c.Query<MapeoRow>("""
            SELECT gm.gobierno, m.nombre AS macro_area, gm.usuario, gm.fecha_registro
            FROM gd.recert_gobierno_macro_area AS gm JOIN gd.macro_area AS m ON m.macro_area_id = gm.macro_area_id
            ORDER BY gm.gobierno
            """).Select(r => new MapeoGobierno(r.gobierno, r.macro_area, r.usuario, r.fecha_registro)).ToList();
    }

    public void CrearMapeoGobierno(string gobierno, string macroArea, string usuario)
    {
        var (g, m) = ReglasRecertificacion.MapeoGobierno(gobierno, macroArea);
        db.EnTransaccion((c, tx) =>
        {
            if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM gd.recert_gobierno_macro_area WITH (UPDLOCK, HOLDLOCK) WHERE gobierno = @g", new { g }, tx) > 0)
                throw new DomainException($"El gobierno {g} ya tiene macro área; cámbiala en su renglón.");
            c.Execute("INSERT INTO gd.recert_gobierno_macro_area (gobierno, macro_area_id, usuario) VALUES (@g, @id, @usuario)",
                      new { g, id = MacroAreaId(c, tx, m), usuario }, tx);
        });
    }

    public void CambiarMapeoGobierno(string gobierno, string macroArea, string usuario)
    {
        var (g, m) = ReglasRecertificacion.MapeoGobierno(gobierno, macroArea);
        db.EnTransaccion((c, tx) =>
        {
            var n = c.Execute("""
                UPDATE gd.recert_gobierno_macro_area SET macro_area_id = @id, usuario = @usuario, fecha_registro = SYSDATETIME() WHERE gobierno = @g
                """, new { g, id = MacroAreaId(c, tx, m), usuario }, tx);
            if (n == 0) throw new DomainException($"El gobierno {g} no está en el catálogo.");
        });
    }

    public void EliminarMapeoGobierno(string gobierno)
    {
        using var c = db.Abrir();
        if (c.Execute("DELETE FROM gd.recert_gobierno_macro_area WHERE gobierno = @g", new { g = gobierno.Trim() }) == 0)
            throw new DomainException($"El gobierno {gobierno} no está en el catálogo.");
    }

    // ---- Gerentes y VoBo (migración 011) ----

    public GerenteVoBo? Gerente()
    {
        using var c = db.Abrir();
        return c.QuerySingleOrDefault<GerenteRow>("SELECT usuario_red, nombre, correo FROM gd.recert_gerente") is { } g
            ? new GerenteVoBo(g.usuario_red, g.nombre, g.correo) : null;
    }

    public void AsignarGerente(string usuarioRed, string usuario)
    {
        var login = Login(usuarioRed);
        var nombre = directorio.BuscarNombre(login) ?? throw new DomainException($"El usuario {login} no existe en Active Directory.");
        var correo = directorio.BuscarCorreo(login) ?? throw new DomainException($"{nombre} no tiene correo en Active Directory: Power Automate no podría pedirle el VoBo.");
        using var c = db.Abrir();
        c.Execute("""
            MERGE gd.recert_gerente WITH (HOLDLOCK) AS t
            USING (SELECT CAST(1 AS TINYINT) AS gerente_id) AS s ON t.gerente_id = s.gerente_id
            WHEN MATCHED THEN UPDATE SET usuario_red = @login, nombre = @nombre, correo = @correo, usuario = @usuario, fecha_registro = SYSDATETIME()
            WHEN NOT MATCHED THEN INSERT (gerente_id, usuario_red, nombre, correo, usuario) VALUES (1, @login, @nombre, @correo, @usuario);
            """, new { login, nombre, correo, usuario });
    }

    public void QuitarGerente()
    {
        using var c = db.Abrir();
        if (c.Execute("DELETE FROM gd.recert_gerente") == 0) throw new DomainException("No hay gerente asignado.");
    }

    public long SolicitarVoBo(int eventoId, int macroAreaId, string usuario) => Negocio(() =>
    {
        using var c = db.Abrir();
        return c.ExecuteScalar<long>("gd.sp_vobo_solicitar", new { evento_id = eventoId, macro_area_id = macroAreaId, solicitado_por = usuario },
                                     commandType: CommandType.StoredProcedure);
    });

    public GerenteVoBo? ResponsableInfra()
    {
        using var c = db.Abrir();
        return c.QuerySingleOrDefault<GerenteRow>("SELECT usuario_red, nombre, correo FROM gd.recert_responsable_infra") is { } r
            ? new GerenteVoBo(r.usuario_red, r.nombre, r.correo) : null;
    }

    public void AsignarResponsableInfra(string usuarioRed, string usuario)
    {
        var login = Login(usuarioRed);
        var nombre = directorio.BuscarNombre(login) ?? throw new DomainException($"El usuario {login} no existe en Active Directory.");
        var correo = directorio.BuscarCorreo(login) ?? throw new DomainException($"{nombre} no tiene correo en Active Directory: no se le podría avisar.");
        using var c = db.Abrir();
        c.Execute("""
            MERGE gd.recert_responsable_infra WITH (HOLDLOCK) AS t
            USING (SELECT CAST(1 AS TINYINT) AS responsable_id) AS s ON t.responsable_id = s.responsable_id
            WHEN MATCHED THEN UPDATE SET usuario_red = @login, nombre = @nombre, correo = @correo, usuario = @usuario, fecha_registro = SYSDATETIME()
            WHEN NOT MATCHED THEN INSERT (responsable_id, usuario_red, nombre, correo, usuario) VALUES (1, @login, @nombre, @correo, @usuario);
            """, new { login, nombre, correo, usuario });
    }

    public void QuitarResponsableInfra()
    {
        using var c = db.Abrir();
        if (c.Execute("DELETE FROM gd.recert_responsable_infra") == 0) throw new DomainException("No hay responsable de infraestructura asignado.");
    }

    public AsignacionInfra AsignarInfraestructura(int eventoId, int macroAreaId, string cambio, string usuario)
    {
        cambio = ReglasRecertificacion.Cambio(cambio);
        return db.EnTransaccion((c, tx) =>
        {
            var responsable = c.QuerySingleOrDefault<GerenteRow>("SELECT usuario_red, nombre, correo FROM gd.recert_responsable_infra", transaction: tx)
                ?? throw new DomainException("No hay responsable de infraestructura asignado (Recertificación → Macro áreas).");
            var (login, nombre, correo) = (responsable.usuario_red, responsable.nombre, responsable.correo);
            if (c.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM gd.recert_vobo WITH (UPDLOCK, HOLDLOCK)
                WHERE evento_id = @eventoId AND macro_area_id = @macroAreaId AND estatus = 'AUTORIZADA'
                """, new { eventoId, macroAreaId }, tx) == 0)
                throw new DomainException("Las bajas de la macro área necesitan el VoBo autorizado del gerente antes de asignarlas.");
            var pendientes = c.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM gd.vw_recert_evidencia
                WHERE evento_id = @eventoId AND macro_area_id = @macroAreaId AND decision = 'BAJA' AND ticket IS NULL
                """, new { eventoId, macroAreaId }, tx);
            if (pendientes == 0) throw new DomainException("La macro área no tiene bajas pendientes de ejecutar.");
            var row = c.QuerySingle<InfraRow>("""
                INSERT INTO gd.recert_asignacion_infra (evento_id, macro_area_id, responsable_usuario_red, responsable_nombre, responsable_correo,
                                                        cambio, bajas, asignado_por)
                OUTPUT inserted.asignacion_id, inserted.evento_id, inserted.macro_area_id, inserted.responsable_usuario_red, inserted.responsable_nombre,
                       inserted.responsable_correo, inserted.cambio, inserted.bajas, inserted.asignado_por, inserted.fecha_asignacion,
                       inserted.notificado, inserted.error_notificacion
                VALUES (@eventoId, @macroAreaId, @login, @nombre, @correo, @cambio, @pendientes, @usuario)
                """, new { eventoId, macroAreaId, login, nombre, correo, cambio, pendientes, usuario }, tx);
            return Infra(row);
        });
    }

    public void RegistrarNotificacionInfra(long asignacionId, bool enviado, string? error)
    {
        using var c = db.Abrir();
        c.Execute("UPDATE gd.recert_asignacion_infra SET notificado = @enviado, error_notificacion = LEFT(@error, 400) WHERE asignacion_id = @asignacionId",
                  new { asignacionId, enviado, error });
    }

    public bool ResponderVoBo(long voboId, bool autorizada, string respondidoPor, string? comentario)
    {
        using var c = db.Abrir();
        return c.QuerySingle<bool>("gd.sp_vobo_responder",
            new { vobo_id = voboId, autorizada, respondido_por = respondidoPor, comentario }, commandType: CommandType.StoredProcedure);
    }

    // La macro área se busca por nombre y se da de alta si no existe.
    private static int MacroAreaId(IDbConnection c, IDbTransaction tx, string nombre) =>
        c.ExecuteScalar<int?>("SELECT macro_area_id FROM gd.macro_area WHERE nombre = @nombre", new { nombre }, tx)
        ?? c.ExecuteScalar<int>("INSERT INTO gd.macro_area (nombre) OUTPUT inserted.macro_area_id VALUES (@nombre)", new { nombre }, tx);

    // ---- Certificadores ----

    private sealed record CertificadorRow(int macro_area_id, string macro_area, string? nombre, string? usuario_red, string? tipo);

    public IReadOnlyList<CertificadoresMacroArea> Certificadores()
    {
        using var c = db.Abrir();
        return c.Query<CertificadorRow>("""
            SELECT m.macro_area_id, m.nombre AS macro_area, r.nombre, r.usuario_red, ca.tipo
            FROM gd.macro_area AS m
            LEFT JOIN gd.macro_area_certificador AS ca ON ca.macro_area_id = m.macro_area_id
                 AND ca.fecha_desde <= @hoy AND (ca.fecha_hasta IS NULL OR ca.fecha_hasta >= @hoy)
            LEFT JOIN gd.responsable AS r ON r.responsable_id = ca.responsable_id
            WHERE m.activo = 1
            """, new { hoy = P(db.Hoy) })
            .GroupBy(x => (x.macro_area_id, x.macro_area))
            .Select(g => new CertificadoresMacroArea(g.Key.macro_area_id, g.Key.macro_area,
                g.Where(x => x.nombre is not null).OrderByDescending(x => x.tipo).ThenBy(x => x.nombre)
                 .Select(x => new CertificadorVigente(x.nombre!, x.usuario_red, x.tipo!)).ToList()))
            .OrderBy(m => m.MacroArea).ToList();
    }

    // El certificador se identifica por su usuario de red; el nombre sale de Active Directory.
    // La PK del ER (macro_area_id, responsable_id) guarda una vigencia por persona: reasignar reabre la misma fila.
    public void AsignarCertificador(int macroAreaId, string usuarioRed, string tipo)
    {
        if (tipo is not ("TITULAR" or "DELEGADO")) throw new DomainException("El tipo debe ser titular o delegado.");
        var login = Login(usuarioRed);
        var nombre = directorio.BuscarNombre(login) ?? throw new DomainException($"El usuario {login} no existe en Active Directory.");
        db.EnTransaccion((c, tx) =>
        {
            if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM gd.macro_area WHERE macro_area_id = @macroAreaId", new { macroAreaId }, tx) == 0)
                throw new DomainException("La macro área no existe.");

            // Responsable con ese usuario de red; si no hay, uno con el mismo nombre sin usuario de red (p. ej. del extracto); si no, uno nuevo.
            var responsableId = c.ExecuteScalar<int?>("SELECT responsable_id FROM gd.responsable WITH (UPDLOCK) WHERE usuario_red = @login", new { login }, tx);
            if (responsableId is null)
            {
                responsableId = c.ExecuteScalar<int?>("""
                    SELECT TOP (1) responsable_id FROM gd.responsable WITH (UPDLOCK)
                    WHERE nombre = @nombre AND usuario_red IS NULL ORDER BY responsable_id
                    """, new { nombre }, tx);
                if (responsableId is not null)
                    c.Execute("UPDATE gd.responsable SET usuario_red = @login WHERE responsable_id = @responsableId", new { login, responsableId }, tx);
                else
                    responsableId = c.ExecuteScalar<int>("INSERT INTO gd.responsable (nombre, usuario_red) OUTPUT inserted.responsable_id VALUES (@nombre, @login)",
                                                         new { nombre, login }, tx);
            }
            c.Execute("""
                IF EXISTS (SELECT 1 FROM gd.macro_area_certificador WHERE macro_area_id = @macroAreaId AND responsable_id = @responsableId)
                    UPDATE gd.macro_area_certificador SET tipo = @tipo, fecha_hasta = NULL
                    WHERE macro_area_id = @macroAreaId AND responsable_id = @responsableId;
                ELSE
                    INSERT INTO gd.macro_area_certificador (macro_area_id, responsable_id, tipo, fecha_desde)
                    VALUES (@macroAreaId, @responsableId, @tipo, @hoy);
                """, new { macroAreaId, responsableId, tipo, hoy = P(db.Hoy) }, tx);
        });
    }

    // Identificador = usuario de red, o el nombre si el certificador aún no tiene usuario de red.
    public void RetirarCertificador(int macroAreaId, string identificador) =>
        db.EnTransaccion((c, tx) =>
        {
            var n = c.Execute("""
                DELETE ca FROM gd.macro_area_certificador AS ca JOIN gd.responsable AS r ON r.responsable_id = ca.responsable_id
                WHERE ca.macro_area_id = @macroAreaId AND ca.fecha_desde >= @hoy
                  AND (r.usuario_red = @identificador OR (r.usuario_red IS NULL AND r.nombre = @identificador));

                UPDATE ca SET fecha_hasta = DATEADD(DAY, -1, @hoy)
                FROM gd.macro_area_certificador AS ca JOIN gd.responsable AS r ON r.responsable_id = ca.responsable_id
                WHERE ca.macro_area_id = @macroAreaId AND ca.fecha_hasta IS NULL
                  AND (r.usuario_red = @identificador OR (r.usuario_red IS NULL AND r.nombre = @identificador));
                """, new { macroAreaId, identificador, hoy = P(db.Hoy) }, tx);
            if (n == 0) throw new DomainException("No es certificador de esa macro área.");
        });

    private string Login(string usuarioRed) => UsuarioRed.Normalizar(usuarioRed, opciones.Value.DominioPredeterminado);

    // Para buscar: una identidad que no es usuario de red no coincide con nadie (no ve nada), en vez de fallar.
    private string LoginParaBuscar(string identidad)
    {
        try { return Login(identidad); }
        catch (DomainException) { return identidad.Trim(); }
    }

    // ---- auxiliares ----

    private static Decision? Decisión(string? d) => d is "CONTINUA" or "BAJA" ? Enum.Parse<Decision>(d) : null;

    private void Sp(string nombre, object parametros) => Negocio(() =>
    {
        using var c = db.Abrir();
        c.Execute(nombre, parametros, commandType: CommandType.StoredProcedure);
        return 0;
    });

    // Los procedimientos y triggers de la v3 señalan reglas de negocio con THROW 50xxx.
    private static T Negocio<T>(Func<T> accion)
    {
        try
        {
            return accion();
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            throw new DomainException(ex.Message);
        }
    }
}
