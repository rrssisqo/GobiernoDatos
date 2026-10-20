using Dapper;
using GobiernoDatos.Web.Domain;
using GobiernoDatos.Web.Services;
using static GobiernoDatos.Web.Data.Sql.SqlAux;

namespace GobiernoDatos.Web.Data.Sql;

// Inventario de vistas sobre el esquema gd (v3): sincronizado con el último extracto de recertificación.
public sealed class SqlVistasService(SqlConexion db) : IVistasService
{
    private sealed record FilaRow(int vista_id, string ruta_tecnica, string nombre_negocio, int? responsable_id, int etapa_orden,
                                  string? data_product, bool en_ultima_recertificacion, int? accesos_3m, int? usuarios, string? macro_area);
    private sealed record EventoRow(string nombre, DateTime fecha);

    // Último evento con extracto cargado (cualquier estatus): es el que sincronizó el inventario.
    private const string UltimoEvento = "(SELECT MAX(evento_id) FROM gd.recert_evento)";

    public InventarioVistas Inventario(FiltroVistas filtro)
    {
        using var c = db.Abrir();
        using var m = c.QueryMultiple($"""
            SELECT v.vista_id, v.ruta_tecnica, v.nombre_negocio, v.responsable_id, CAST(ea.etapa_orden AS INT) AS etapa_orden,
                   d.nombre AS data_product, v.en_ultima_recertificacion, u.accesos_3m, u.usuarios, ma.macro_area
            FROM gd.vista_gobernada AS v
            JOIN gd.vw_vista_etapa_actual AS ea ON ea.vista_id = v.vista_id
            LEFT JOIN gd.dp_vista AS dv ON dv.vista_id = v.vista_id AND dv.fecha_hasta IS NULL
            LEFT JOIN gd.data_product AS d ON d.dp_id = dv.dp_id
            LEFT JOIN gd.vista_uso AS u ON u.vista_id = v.vista_id AND u.evento_id = {UltimoEvento}
            OUTER APPLY (SELECT TOP (1) mc.nombre AS macro_area
                         FROM gd.rol_hana AS r
                         JOIN gd.recert_acceso AS a ON a.rol_id = r.rol_id AND a.evento_id = {UltimoEvento}
                         JOIN gd.macro_area AS mc ON mc.macro_area_id = a.macro_area_id
                         WHERE r.vista_id = v.vista_id
                         GROUP BY mc.nombre ORDER BY COUNT(*) DESC) AS ma
            WHERE v.activa = 1;

            SELECT TOP (1) nombre, ISNULL(CAST(fecha_extracto AS DATETIME2), fecha_registro) AS fecha
            FROM gd.recert_evento ORDER BY evento_id DESC;
            """);
        var todas = m.Read<FilaRow>().ToList();
        var evento = m.ReadSingleOrDefault<EventoRow>();

        static bool EsNueva(FilaRow f) => f.data_product is null && f.responsable_id is null;

        var filas = todas
            .Where(f => filtro.Pestana switch
            {
                PestanaVistas.Nuevas => EsNueva(f),
                PestanaVistas.Fuera => !f.en_ultima_recertificacion,
                _ => true,
            })
            .Where(f => filtro.MacroArea is null || f.macro_area == filtro.MacroArea)
            .Where(f => filtro.EtapaOrden is null || f.etapa_orden == filtro.EtapaOrden)
            .Where(f => string.IsNullOrWhiteSpace(filtro.Texto)
                        || f.ruta_tecnica.Contains(filtro.Texto, StringComparison.OrdinalIgnoreCase)
                        || f.nombre_negocio.Contains(filtro.Texto, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.accesos_3m ?? -1).ThenBy(f => f.nombre_negocio)
            .Select(f => new FilaVista(f.vista_id, RutaTecnica.Vista(f.ruta_tecnica), RutaTecnica.Paquete(f.ruta_tecnica), f.nombre_negocio,
                                       f.macro_area, f.data_product, Catalogos.EtapaPorOrden(f.etapa_orden).Nombre, f.accesos_3m, f.usuarios,
                                       f.en_ultima_recertificacion))
            .ToList();

        return new InventarioVistas(filas,
            todas.Count(f => f.en_ultima_recertificacion), todas.Count(f => f.data_product is not null), todas.Count(EsNueva),
            todas.Count(f => !f.en_ultima_recertificacion), todas.Count,
            evento is null ? null : D(evento.fecha), evento?.nombre,
            todas.Select(f => f.macro_area).OfType<string>().Distinct().Order().ToList());
    }

    private sealed record DetalleRow(int vista_id, string ruta_tecnica, string nombre_negocio, string? gobierno, string? subdominio,
                                     string? responsable, string? nivel, DateTime fecha_alta, bool activa, int etapa_orden,
                                     int dias_en_etapa, int ciclos, int? dp_id, DateTime? fecha_primera_aparicion,
                                     DateTime? fecha_ultima_aparicion, bool en_ultima_recertificacion, string? rol,
                                     int? accesos_3m, int? usuarios, string? macro_area);
    private sealed record ObjetoRow(int objeto_id, string nombre, string? codigo);
    private sealed record HistRow(DateTime fecha, int orden, bool es_regreso, int? motivo_id, string? comentario, string usuario);

    public DetalleVista? Detalle(int vistaId)
    {
        using var c = db.Abrir();
        using var m = c.QueryMultiple("""
            SELECT v.vista_id, v.ruta_tecnica, v.nombre_negocio, g.nombre AS gobierno, sd.nombre AS subdominio, r.nombre AS responsable,
                   ns.nombre AS nivel, v.fecha_alta, v.activa, CAST(ISNULL(ea.etapa_orden, 0) AS INT) AS etapa_orden,
                   ISNULL(ea.dias_en_etapa, 0) AS dias_en_etapa, ISNULL(mf.ciclos, 1) AS ciclos, dv.dp_id,
                   v.fecha_primera_aparicion, v.fecha_ultima_aparicion, v.en_ultima_recertificacion,
                   rh.nombre AS rol, u.accesos_3m, u.usuarios, ma.macro_area
            FROM gd.vista_gobernada AS v
            LEFT JOIN gd.gobierno AS g ON g.gobierno_id = v.gobierno_id
            LEFT JOIN gd.rol_hana AS rh ON rh.vista_id = v.vista_id
            OUTER APPLY (SELECT TOP (1) evento_id FROM gd.vista_uso WHERE vista_id = v.vista_id ORDER BY evento_id DESC) AS ue
            LEFT JOIN gd.vista_uso AS u ON u.evento_id = ue.evento_id AND u.vista_id = v.vista_id
            OUTER APPLY (SELECT TOP (1) mc.nombre AS macro_area
                         FROM gd.recert_acceso AS a JOIN gd.macro_area AS mc ON mc.macro_area_id = a.macro_area_id
                         WHERE a.rol_id = rh.rol_id AND a.evento_id = ue.evento_id
                         GROUP BY mc.nombre ORDER BY COUNT(*) DESC) AS ma
            LEFT JOIN gd.subdominio AS sd ON sd.subdominio_id = v.subdominio_id
            LEFT JOIN gd.responsable AS r ON r.responsable_id = v.responsable_id
            LEFT JOIN gd.nivel_seguridad AS ns ON ns.nivel_seguridad_id = v.nivel_seguridad_id
            LEFT JOIN gd.vw_vista_etapa_actual AS ea ON ea.vista_id = v.vista_id
            LEFT JOIN gd.vw_metricas_flujo_vista AS mf ON mf.vista_id = v.vista_id
            LEFT JOIN gd.dp_vista AS dv ON dv.vista_id = v.vista_id AND dv.fecha_hasta IS NULL
            WHERE v.vista_id = @vistaId;

            SELECT o.objeto_id, o.nombre, r.codigo
            FROM gd.vista_objeto AS vo
            JOIN gd.objeto_fuente AS o ON o.objeto_id = vo.objeto_id
            LEFT JOIN gd.regla_objeto AS ro ON ro.objeto_id = o.objeto_id
            LEFT JOIN gd.regla AS r ON r.regla_id = ro.regla_id
            WHERE vo.vista_id = @vistaId AND vo.fecha_hasta IS NULL;

            SELECT m.fecha, CAST(e.orden AS INT) AS orden, m.es_regreso, m.motivo_id, m.comentario, m.usuario
            FROM gd.mov_etapa_vista AS m JOIN gd.etapa_vista AS e ON e.etapa_id = m.etapa_id
            WHERE m.vista_id = @vistaId
            ORDER BY m.fecha DESC, m.mov_id DESC;
            """, new { vistaId });

        var v = m.ReadSingleOrDefault<DetalleRow>();
        if (v is null) return null;
        var objetos = m.Read<ObjetoRow>().GroupBy(o => (o.objeto_id, o.nombre))
            .Select(g => new ObjetoDeVista(g.Key.objeto_id, g.Key.nombre, g.Select(x => x.codigo).OfType<string>().ToList())).ToList();
        var historial = m.Read<HistRow>().Select(h => new MovEtapaDetalle(D(h.fecha), Catalogos.EtapaPorOrden(h.orden).Nombre,
            h.es_regreso, Catalogos.NombreMotivo(h.motivo_id), h.comentario, h.usuario)).ToList();

        var etapa = Catalogos.EtapaPorOrden(v.etapa_orden);
        var entidad = new VistaGobernada
        {
            VistaId = v.vista_id, RutaTecnica = v.ruta_tecnica, NombreNegocio = v.nombre_negocio, Gobierno = v.gobierno,
            Subdominio = v.subdominio, Responsable = v.responsable, NivelSeguridad = v.nivel, FechaAlta = D(v.fecha_alta),
            Activa = v.activa, EnUltimaRecertificacion = v.en_ultima_recertificacion,
            FechaPrimeraAparicion = D(v.fecha_primera_aparicion), FechaUltimaAparicion = D(v.fecha_ultima_aparicion),
        };
        return new DetalleVista(entidad, v.rol, v.macro_area, v.accesos_3m, v.usuarios, v.dp_id, etapa, etapa.TareaPendiente, v.dias_en_etapa, v.ciclos, objetos, historial);
    }

    public IReadOnlyList<OpcionDp> DataProductsAbiertos()
    {
        using var c = db.Abrir();
        return c.Query<OpcionDp>("""
            SELECT d.dp_id AS DpId, d.nombre AS Nombre, g.nombre AS Gobierno
            FROM gd.data_product AS d JOIN gd.gobierno AS g ON g.gobierno_id = d.gobierno_id
            WHERE d.estatus IN ('EN_CURSO', 'PLANEADO') ORDER BY d.nombre
            """).ToList();
    }

    public IReadOnlyList<OpcionObjeto> Objetos()
    {
        using var c = db.Abrir();
        return c.Query<OpcionObjeto>("SELECT objeto_id AS ObjetoId, nombre AS Nombre FROM gd.objeto_fuente WHERE activo = 1 ORDER BY nombre").ToList();
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

    private sealed record DpVistaRow(int dp_vista_id, int dp_id, DateTime fecha_desde);
    private sealed record VistaObjetoRow(int vista_objeto_id, DateTime fecha_desde);

    public void Actualizar(int vistaId, string nombreNegocio, string? gobierno, int? dpId, string? responsable, string? nivelSeguridad, string usuario)
    {
        if (string.IsNullOrWhiteSpace(nombreNegocio)) throw new DomainException("Indica el nombre de negocio de la vista.");

        db.EnTransaccion((c, tx) =>
        {
            if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM gd.vista_gobernada WITH (UPDLOCK) WHERE vista_id = @vistaId", new { vistaId }, tx) == 0)
                throw new DomainException("La vista no existe.");
            byte? nivelId = nivelSeguridad is null ? null
                : c.ExecuteScalar<byte?>("SELECT nivel_seguridad_id FROM gd.nivel_seguridad WHERE nombre = @nivelSeguridad", new { nivelSeguridad }, tx)
                  ?? throw new DomainException("Nivel de seguridad no válido.");

            c.Execute("""
                UPDATE gd.vista_gobernada
                SET nombre_negocio = @nombre, gobierno_id = @gobiernoId, responsable_id = @responsableId, nivel_seguridad_id = @nivelId
                WHERE vista_id = @vistaId
                """, new { vistaId, nombre = nombreNegocio.Trim(), gobiernoId = gobierno is null ? (int?)null : GobiernoId(c, tx, gobierno), responsableId = ResponsableId(c, tx, responsable), nivelId }, tx);

            // Un solo DP vigente: el cambio cierra la vigencia anterior (o la corrige si se asignó hoy).
            var hoy = db.Hoy;
            var actual = c.QuerySingleOrDefault<DpVistaRow>(
                "SELECT dp_vista_id, dp_id, fecha_desde FROM gd.dp_vista WHERE vista_id = @vistaId AND fecha_hasta IS NULL", new { vistaId }, tx);
            if (actual?.dp_id == dpId) return;
            if (dpId is not null && c.ExecuteScalar<int>("SELECT COUNT(*) FROM gd.data_product WHERE dp_id = @dpId AND estatus IN ('EN_CURSO','PLANEADO')", new { dpId }, tx) == 0)
                throw new DomainException("El data product no existe o está cerrado.");

            if (actual is { } a)
            {
                if (D(a.fecha_desde) >= hoy) c.Execute("DELETE FROM gd.dp_vista WHERE dp_vista_id = @id", new { id = a.dp_vista_id }, tx);
                else c.Execute("UPDATE gd.dp_vista SET fecha_hasta = @ayer WHERE dp_vista_id = @id", new { id = a.dp_vista_id, ayer = P(hoy.AddDays(-1)) }, tx);
            }
            if (dpId is not null)
                c.Execute("INSERT INTO gd.dp_vista (dp_id, vista_id, fecha_desde) VALUES (@dpId, @vistaId, @hoy)", new { dpId, vistaId, hoy = P(hoy) }, tx);
        });
    }

    // Mismas validaciones que gd.sp_mover_etapa_vista, grabando el usuario del portal.
    public void MoverEtapa(int vistaId, int etapaOrden, int? motivoId, string? comentario, string usuario, Asignacion? asignarA = null)
    {
        var etapa = Catalogos.Etapas.FirstOrDefault(e => e.Orden == etapaOrden) ?? throw new DomainException("La etapa indicada no existe.");
        if (motivoId is not null && Catalogos.Motivos.All(m => m.MotivoId != motivoId || m.AplicaA == "REGLA"))
            throw new DomainException("Motivo no válido para vistas.");
        if (asignarA is not null) ReglasTarea.ValidarEtapaConTarea(etapa);

        db.EnTransaccion((c, tx) =>
        {
            if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM gd.vista_gobernada WITH (UPDLOCK) WHERE vista_id = @vistaId", new { vistaId }, tx) == 0)
                throw new DomainException("La vista no existe.");
            var actual = c.ExecuteScalar<int?>("""
                SELECT TOP (1) CAST(e.orden AS INT) FROM gd.mov_etapa_vista AS m JOIN gd.etapa_vista AS e ON e.etapa_id = m.etapa_id
                WHERE m.vista_id = @vistaId ORDER BY m.fecha DESC, m.mov_id DESC
                """, new { vistaId }, tx);
            if (actual == etapaOrden) throw new DomainException("La vista ya se encuentra en esa etapa.");
            var esRegreso = etapaOrden < (actual ?? 0);
            if (esRegreso && motivoId is null) throw new DomainException("Un regreso de etapa requiere indicar el motivo.");

            c.Execute("""
                INSERT INTO gd.mov_etapa_vista (vista_id, etapa_id, fecha, motivo_id, es_regreso, comentario, usuario)
                VALUES (@vistaId, @etapaId, @hoy, @motivoId, @esRegreso, @comentario, @usuario)
                """, new
            {
                vistaId, etapaId = etapa.EtapaId, hoy = P(db.Hoy), motivoId = motivoId ?? (esRegreso ? null : 1), esRegreso,
                comentario = string.IsNullOrWhiteSpace(comentario) ? null : comentario.Trim(), usuario,
            }, tx);
            SqlTareasService.AbrirTarea(c, tx, vistaId, etapa.EtapaId, asignarA, usuario, "Cambio de etapa", db.Ahora);
        });
    }

    public void RelacionarObjeto(int vistaId, int objetoId) =>
        db.EnTransaccion((c, tx) =>
        {
            if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM gd.objeto_fuente WHERE objeto_id = @objetoId", new { objetoId }, tx) == 0)
                throw new DomainException("El objeto no existe.");
            if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM gd.vista_objeto WHERE vista_id = @vistaId AND objeto_id = @objetoId AND fecha_hasta IS NULL", new { vistaId, objetoId }, tx) > 0)
                throw new DomainException("El objeto ya alimenta esta vista.");
            c.Execute("INSERT INTO gd.vista_objeto (vista_id, objeto_id, fecha_desde) VALUES (@vistaId, @objetoId, @hoy)", new { vistaId, objetoId, hoy = P(db.Hoy) }, tx);
        });

    public void QuitarObjeto(int vistaId, int objetoId) =>
        db.EnTransaccion((c, tx) =>
        {
            var vo = c.QuerySingleOrDefault<VistaObjetoRow>(
                "SELECT vista_objeto_id, fecha_desde FROM gd.vista_objeto WHERE vista_id = @vistaId AND objeto_id = @objetoId AND fecha_hasta IS NULL",
                new { vistaId, objetoId }, tx) ?? throw new DomainException("El objeto no está relacionado con la vista.");
            if (D(vo.fecha_desde) >= db.Hoy) c.Execute("DELETE FROM gd.vista_objeto WHERE vista_objeto_id = @id", new { id = vo.vista_objeto_id }, tx);
            else c.Execute("UPDATE gd.vista_objeto SET fecha_hasta = @ayer WHERE vista_objeto_id = @id", new { id = vo.vista_objeto_id, ayer = P(db.Hoy.AddDays(-1)) }, tx);
        });
}
