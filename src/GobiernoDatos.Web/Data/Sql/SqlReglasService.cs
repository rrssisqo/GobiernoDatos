using System.Data;
using Dapper;
using GobiernoDatos.Web.Domain;
using GobiernoDatos.Web.Services;
using static GobiernoDatos.Web.Data.Sql.SqlAux;

namespace GobiernoDatos.Web.Data.Sql;

// ABC de reglas sobre gd.regla, gd.regla_objeto, gd.regla_contacto (DESTINATARIO) y gd.mov_etapa_regla (etapa = último movimiento).
// gd.regla tiene trigger (historial del query): los INSERT usan SCOPE_IDENTITY, no OUTPUT.
public sealed class SqlReglasService(SqlConexion db) : IReglasService
{
    private const int OrdenBaja = 8;

    // Etapa vigente de cada regla: el último movimiento por fecha y registro.
    private const string EtapaActual = """
        OUTER APPLY (SELECT TOP (1) e.nombre, e.orden FROM gd.mov_etapa_regla AS m JOIN gd.etapa_regla AS e ON e.etapa_regla_id = m.etapa_regla_id
                     WHERE m.regla_id = r.regla_id ORDER BY m.fecha DESC, m.mov_id DESC) AS et
        """;

    private sealed record FilaRow(int regla_id, string codigo, string descripcion, string? tipo, string? gobierno, string? dimension_dq,
                                  string? etapa, bool activa, string? frecuencia, TimeSpan? hora_ejecucion, DateTime? fecha_produccion);
    private sealed record ObjetoRow(int regla_id, string nombre);

    public IReadOnlyList<FilaRegla> Listar()
    {
        using var c = db.Abrir();
        using var m = c.QueryMultiple($"""
            SELECT r.regla_id, r.codigo, r.descripcion, t.nombre AS tipo, g.nombre AS gobierno, r.dimension_dq,
                   et.nombre AS etapa, r.activa, r.frecuencia, r.hora_ejecucion, r.fecha_produccion
            FROM gd.regla AS r
            LEFT JOIN gd.tipo_regla AS t ON t.tipo_regla_id = r.tipo_regla_id
            LEFT JOIN gd.gobierno AS g ON g.gobierno_id = r.gobierno_id
            {EtapaActual}
            ORDER BY r.codigo;

            SELECT ro.regla_id, o.nombre FROM gd.regla_objeto AS ro JOIN gd.objeto_fuente AS o ON o.objeto_id = ro.objeto_id ORDER BY o.nombre;
            SELECT regla_id, correo AS nombre FROM gd.regla_contacto WHERE tipo = 'CONTACTO' ORDER BY correo;
            """);
        var reglas = m.Read<FilaRow>().ToList();
        var objetos = m.Read<ObjetoRow>().ToLookup(o => o.regla_id, o => o.nombre);
        var contactos = m.Read<ObjetoRow>().ToLookup(o => o.regla_id, o => o.nombre);
        return reglas.Select(r => new FilaRegla(r.regla_id, r.codigo, r.descripcion, r.tipo, r.gobierno, r.dimension_dq,
                                                contactos[r.regla_id].ToList(), objetos[r.regla_id].ToList(), r.etapa, r.activa,
                                                r.frecuencia, Hora(r.hora_ejecucion), D(r.fecha_produccion))).ToList();
    }

    private sealed record DetalleRow(int regla_id, string codigo, string? tipo, string descripcion, string? regla_funcional, string? proposito_negocio,
                                     string? dimension_dq, string? gobierno, string? campos_evaluados, string? tabla_stg,
                                     string? mapping, int? no_csv, string? nombre_csv, string? frecuencia, bool escribe_log, string? correo_asunto,
                                     string? query_validacion, bool activa, int? etapa_orden, string? task_flow, TimeSpan? hora_ejecucion,
                                     DateTime? fecha_produccion, string? comentarios);

    private static TimeOnly? Hora(TimeSpan? t) => t is { } x ? TimeOnly.FromTimeSpan(x) : null;

    public ReglaEdicion? Obtener(int reglaId)
    {
        using var c = db.Abrir();
        using var m = c.QueryMultiple($"""
            SELECT r.regla_id, r.codigo, t.nombre AS tipo, r.descripcion, r.regla_funcional, r.proposito_negocio, r.dimension_dq,
                   g.nombre AS gobierno, r.campos_evaluados, r.tabla_stg, r.mapping, r.no_csv, r.nombre_csv,
                   r.frecuencia, r.escribe_log, r.correo_asunto, r.query_validacion, r.activa, CAST(et.orden AS INT) AS etapa_orden,
                   r.task_flow, r.hora_ejecucion, r.fecha_produccion, r.comentarios
            FROM gd.regla AS r
            LEFT JOIN gd.tipo_regla AS t ON t.tipo_regla_id = r.tipo_regla_id
            LEFT JOIN gd.gobierno AS g ON g.gobierno_id = r.gobierno_id
            {EtapaActual}
            WHERE r.regla_id = @reglaId;

            SELECT o.nombre FROM gd.regla_objeto AS ro JOIN gd.objeto_fuente AS o ON o.objeto_id = ro.objeto_id WHERE ro.regla_id = @reglaId ORDER BY o.nombre;
            SELECT correo FROM gd.regla_contacto WHERE regla_id = @reglaId AND tipo = 'DESTINATARIO' ORDER BY correo;
            SELECT correo FROM gd.regla_contacto WHERE regla_id = @reglaId AND tipo = 'CONTACTO' ORDER BY correo;
            """, new { reglaId });
        var r = m.ReadSingleOrDefault<DetalleRow>();
        if (r is null) return null;
        var objetos = m.Read<string>().ToList();
        var destinatarios = m.Read<string>().ToList();
        var contactos = m.Read<string>().ToList();
        return new ReglaEdicion(r.regla_id, r.activa, new DatosRegla(r.codigo, r.tipo, r.descripcion, r.regla_funcional, r.proposito_negocio,
            r.dimension_dq, r.gobierno, contactos, r.campos_evaluados, objetos, r.tabla_stg, r.mapping, r.no_csv, r.nombre_csv,
            r.frecuencia, r.escribe_log, r.correo_asunto, destinatarios, r.etapa_orden ?? 1, r.query_validacion,
            r.task_flow, Hora(r.hora_ejecucion), D(r.fecha_produccion), r.comentarios));
    }

    public CatalogosRegla Catalogos()
    {
        using var c = db.Abrir();
        using var m = c.QueryMultiple("""
            SELECT nombre FROM gd.tipo_regla ORDER BY nombre;
            SELECT nombre FROM gd.gobierno WHERE activo = 1 ORDER BY nombre;
            SELECT nombre FROM gd.frecuencia_regla ORDER BY orden, nombre;
            SELECT nombre FROM gd.objeto_fuente WHERE activo = 1 ORDER BY nombre;
            """);
        return new(m.Read<string>().ToList(), m.Read<string>().ToList(), m.Read<string>().ToList(), m.Read<string>().ToList());
    }

    public int Crear(DatosRegla datos, string usuario)
    {
        var d = DefinicionRegla.Normalizar(datos);
        return db.EnTransaccion((c, tx) =>
        {
            if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM gd.regla WITH (UPDLOCK, HOLDLOCK) WHERE codigo = @Codigo", d, tx) > 0)
                throw new DomainException($"Ya existe una regla con el código {d.Codigo}.");
            var reglaId = c.ExecuteScalar<int>("""
                INSERT INTO gd.regla (codigo, descripcion, activa, fecha_alta) VALUES (@Codigo, @Descripcion, 1, @hoy);
                SELECT CAST(SCOPE_IDENTITY() AS INT);
                """, new { d.Codigo, d.Descripcion, hoy = P(db.Hoy) }, tx);
            Guardar(c, tx, reglaId, d);
            MoverEtapa(c, tx, reglaId, d.EtapaOrden, "Alta desde el portal", usuario);
            return reglaId;
        });
    }

    public void Actualizar(int reglaId, DatosRegla datos, string usuario) =>
        db.EnTransaccion((c, tx) =>
        {
            var (codigo, activa, orden) = Estado(c, tx, reglaId);
            var d = DefinicionRegla.Normalizar(datos with { Codigo = codigo });
            if (!activa && d.EtapaOrden != OrdenBaja) throw new DomainException("La regla está dada de baja: reactívala antes de cambiar su etapa.");
            if (activa && d.EtapaOrden == OrdenBaja) throw new DomainException("Para dar de baja la regla usa el botón «Dar de baja».");
            Guardar(c, tx, reglaId, d);
            if (d.EtapaOrden != orden) MoverEtapa(c, tx, reglaId, d.EtapaOrden, "Cambio desde el portal", usuario);
        });

    public void DarDeBaja(int reglaId, string motivo, string usuario)
    {
        if (string.IsNullOrWhiteSpace(motivo)) throw new DomainException("Indica el motivo de la baja.");
        db.EnTransaccion((c, tx) =>
        {
            if (!Estado(c, tx, reglaId).Activa) throw new DomainException("La regla ya está dada de baja.");
            c.Execute("UPDATE gd.regla SET activa = 0 WHERE regla_id = @reglaId", new { reglaId }, tx);
            MoverEtapa(c, tx, reglaId, OrdenBaja, motivo.Trim(), usuario);
        });
    }

    public void Reactivar(int reglaId, string usuario) =>
        db.EnTransaccion((c, tx) =>
        {
            if (Estado(c, tx, reglaId).Activa) throw new DomainException("La regla ya está activa.");
            var anterior = c.ExecuteScalar<int?>("""
                SELECT TOP (1) CAST(e.orden AS INT) FROM gd.mov_etapa_regla AS m JOIN gd.etapa_regla AS e ON e.etapa_regla_id = m.etapa_regla_id
                WHERE m.regla_id = @reglaId AND e.orden <> @baja ORDER BY m.fecha DESC, m.mov_id DESC
                """, new { reglaId, baja = OrdenBaja }, tx) ?? 1;
            c.Execute("UPDATE gd.regla SET activa = 1 WHERE regla_id = @reglaId", new { reglaId }, tx);
            MoverEtapa(c, tx, reglaId, anterior, "Reactivada desde el portal", usuario);
        });

    public void CrearFrecuencia(string nombre)
    {
        nombre = ReglasService.Frecuencia(nombre);
        using var c = db.Abrir();
        if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM gd.frecuencia_regla WHERE nombre = @nombre", new { nombre }) > 0)
            throw new DomainException($"La frecuencia «{nombre}» ya existe.");
        c.Execute("INSERT INTO gd.frecuencia_regla (nombre) VALUES (@nombre)", new { nombre });
    }

    public void EliminarFrecuencia(string nombre)
    {
        using var c = db.Abrir();
        if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM gd.regla WHERE frecuencia = @nombre", new { nombre }) > 0)
            throw new DomainException($"La frecuencia «{nombre}» la usan reglas; cámbialas antes de eliminarla.");
        if (c.Execute("DELETE FROM gd.frecuencia_regla WHERE nombre = @nombre", new { nombre }) == 0)
            throw new DomainException($"La frecuencia «{nombre}» no existe.");
    }

    private static (string Codigo, bool Activa, int? Orden) Estado(IDbConnection c, IDbTransaction tx, int reglaId) =>
        c.QuerySingleOrDefault<(string, bool, int?)>($"""
            SELECT r.codigo, r.activa, CAST(et.orden AS INT) FROM gd.regla AS r WITH (UPDLOCK) {EtapaActual} WHERE r.regla_id = @reglaId
            """, new { reglaId }, tx) is var e && e.Item1 is not null ? e : throw new DomainException("La regla no existe.");

    // Datos de la regla, objetos BW y destinatarios (se reemplazan completos).
    private void Guardar(IDbConnection c, IDbTransaction tx, int reglaId, DatosRegla d)
    {
        int? tipoId = d.TipoRegla is null ? null
            : c.ExecuteScalar<int?>("SELECT tipo_regla_id FROM gd.tipo_regla WHERE nombre = @TipoRegla", d, tx)
              ?? c.ExecuteScalar<int>("INSERT INTO gd.tipo_regla (nombre) VALUES (@TipoRegla); SELECT CAST(SCOPE_IDENTITY() AS INT);", d, tx);
        // Con el nombre exacto del catálogo ("MENSUAL" → "Mensual").
        var frecuencia = d.Frecuencia is null ? null
            : c.ExecuteScalar<string?>("SELECT nombre FROM gd.frecuencia_regla WHERE nombre = @Frecuencia", d, tx)
              ?? throw new DomainException($"La frecuencia «{d.Frecuencia}» no está en el catálogo.");
        c.Execute("""
            UPDATE gd.regla
            SET tipo_regla_id = @tipoId, descripcion = @Descripcion, regla_funcional = @ReglaFuncional, proposito_negocio = @PropositoNegocio,
                dimension_dq = @DimensionDq, gobierno_id = @gobiernoId, campos_evaluados = @CamposEvaluados,
                tabla_stg = @TablaStg, mapping = @Mapping, task_flow = @TaskFlow, no_csv = @NoCsv, nombre_csv = @NombreCsv, frecuencia = @frecuencia,
                hora_ejecucion = @hora, escribe_log = @EscribeLog, correo_asunto = @CorreoAsunto, query_validacion = @QueryValidacion,
                fecha_produccion = @fechaProduccion, comentarios = @Comentarios
            WHERE regla_id = @reglaId
            """, new
        {
            reglaId, tipoId, d.Descripcion, d.ReglaFuncional, d.PropositoNegocio, d.DimensionDq,
            gobiernoId = d.Gobierno is null ? (int?)null : GobiernoId(c, tx, d.Gobierno),
            d.CamposEvaluados, d.TablaStg, d.Mapping, d.TaskFlow, d.NoCsv, d.NombreCsv, frecuencia, d.EscribeLog, d.CorreoAsunto, d.QueryValidacion,
            hora = d.Hora?.ToTimeSpan(), fechaProduccion = d.FechaProduccion is { } fp ? P(fp) : (DateTime?)null, d.Comentarios,
        }, tx);

        c.Execute("DELETE FROM gd.regla_objeto WHERE regla_id = @reglaId", new { reglaId }, tx);
        foreach (var nombre in d.Objetos)
        {
            var objetoId = c.ExecuteScalar<int?>("SELECT objeto_id FROM gd.objeto_fuente WHERE nombre = @nombre", new { nombre }, tx)
                           ?? c.ExecuteScalar<int>("INSERT INTO gd.objeto_fuente (nombre) VALUES (@nombre); SELECT CAST(SCOPE_IDENTITY() AS INT);", new { nombre }, tx);
            c.Execute("INSERT INTO gd.regla_objeto (regla_id, objeto_id) VALUES (@reglaId, @objetoId)", new { reglaId, objetoId }, tx);
        }
        c.Execute("DELETE FROM gd.regla_contacto WHERE regla_id = @reglaId AND tipo = 'DESTINATARIO'", new { reglaId }, tx);
        foreach (var correo in d.Destinatarios)
            c.Execute("INSERT INTO gd.regla_contacto (regla_id, correo, tipo) VALUES (@reglaId, @correo, 'DESTINATARIO')", new { reglaId, correo }, tx);
        c.Execute("DELETE FROM gd.regla_contacto WHERE regla_id = @reglaId AND tipo = 'CONTACTO'", new { reglaId }, tx);
        foreach (var correo in d.Contactos)
            c.Execute("INSERT INTO gd.regla_contacto (regla_id, correo, tipo) VALUES (@reglaId, @correo, 'CONTACTO')", new { reglaId, correo }, tx);
    }

    private void MoverEtapa(IDbConnection c, IDbTransaction tx, int reglaId, int orden, string comentario, string usuario) =>
        c.Execute("""
            INSERT INTO gd.mov_etapa_regla (regla_id, etapa_regla_id, fecha, comentario, usuario)
            SELECT @reglaId, etapa_regla_id, @hoy, @comentario, @usuario FROM gd.etapa_regla WHERE orden = @orden
            """, new { reglaId, hoy = P(db.Hoy), comentario, usuario, orden }, tx);
}
