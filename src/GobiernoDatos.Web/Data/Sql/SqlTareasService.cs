using System.Data;
using Dapper;
using GobiernoDatos.Web.Domain;
using GobiernoDatos.Web.Services;

namespace GobiernoDatos.Web.Data.Sql;

// Tareas por etapa de vista sobre gd.tarea_vista (migración 004).
public sealed class SqlTareasService(SqlConexion db) : ITareasService
{
    private sealed record TareaRow(int tarea_id, int vista_id, string vista, string? data_product, string etapa, string asignado_a,
                                   string asignado_nombre, DateTime fecha_asignacion, string estatus, DateTime? fecha_cierre,
                                   string? cerrado_por, string? motivo_cierre);

    private const string Consulta = """
        SELECT t.tarea_id, t.vista_id, v.nombre_negocio AS vista, d.nombre AS data_product, e.nombre AS etapa,
               t.asignado_a, t.asignado_nombre, t.fecha_asignacion, t.estatus, t.fecha_cierre, t.cerrado_por, t.motivo_cierre
        FROM gd.tarea_vista AS t
        JOIN gd.vista_gobernada AS v ON v.vista_id = t.vista_id
        JOIN gd.etapa_vista AS e ON e.etapa_id = t.etapa_id
        LEFT JOIN gd.dp_vista AS dv ON dv.vista_id = t.vista_id AND dv.fecha_hasta IS NULL
        LEFT JOIN gd.data_product AS d ON d.dp_id = dv.dp_id
        """;

    public IReadOnlyList<FilaTarea> Listar(FiltroTareas filtro)
    {
        using var c = db.Abrir();
        return c.Query<TareaRow>(Consulta + """

            WHERE (@Estatus IS NULL OR t.estatus = @Estatus)
              AND (@AsignadoA IS NULL OR t.asignado_a = @AsignadoA)
              AND (@DpId IS NULL OR dv.dp_id = @DpId)
            ORDER BY CASE t.estatus WHEN 'ABIERTA' THEN 0 ELSE 1 END, t.fecha_asignacion
            """, filtro).Select(Fila).ToList();
    }

    public FilaTarea? TareaAbierta(int vistaId)
    {
        using var c = db.Abrir();
        return c.Query<TareaRow>(Consulta + "\nWHERE t.vista_id = @vistaId AND t.estatus = 'ABIERTA'", new { vistaId }).Select(Fila).FirstOrDefault();
    }

    public void Asignar(int vistaId, Asignacion asignacion, string usuario) =>
        db.EnTransaccion((c, tx) =>
        {
            var orden = c.ExecuteScalar<int?>("""
                SELECT ISNULL((SELECT TOP (1) CAST(e.orden AS INT) FROM gd.mov_etapa_vista AS m JOIN gd.etapa_vista AS e ON e.etapa_id = m.etapa_id
                               WHERE m.vista_id = v.vista_id ORDER BY m.fecha DESC, m.mov_id DESC), 0)
                FROM gd.vista_gobernada AS v WITH (UPDLOCK) WHERE v.vista_id = @vistaId
                """, new { vistaId }, tx) ?? throw new DomainException("La vista no existe.");
            var etapa = Catalogos.EtapaPorOrden(orden);
            ReglasTarea.ValidarEtapaConTarea(etapa);
            var mismo = c.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM gd.tarea_vista
                WHERE vista_id = @vistaId AND estatus = 'ABIERTA' AND etapa_id = @etapaId AND asignado_a = @usuarioRed
                """, new { vistaId, etapaId = etapa.EtapaId, usuarioRed = asignacion.UsuarioRed }, tx);
            if (mismo > 0) throw new DomainException($"La tarea ya está asignada a {asignacion.Nombre}.");
            AbrirTarea(c, tx, vistaId, etapa.EtapaId, asignacion, usuario, "Reasignada", db.Ahora);
        });

    public void Terminar(int tareaId, string usuario) =>
        db.EnTransaccion((c, tx) =>
        {
            var estatus = c.ExecuteScalar<string?>("SELECT estatus FROM gd.tarea_vista WITH (UPDLOCK) WHERE tarea_id = @tareaId", new { tareaId }, tx)
                          ?? throw new DomainException("La tarea no existe.");
            if (estatus != EstatusTarea.Abierta) throw new DomainException("La tarea ya está cerrada.");
            c.Execute("""
                UPDATE gd.tarea_vista SET estatus = 'TERMINADA', fecha_cierre = @ahora, cerrado_por = @usuario
                WHERE tarea_id = @tareaId
                """, new { tareaId, usuario, ahora = db.Ahora }, tx);
        });

    // Cierra (cancela) la tarea abierta de la vista y, si se indica, abre la de la etapa. Se usa también al mover de etapa.
    internal static void AbrirTarea(IDbConnection c, IDbTransaction tx, int vistaId, int etapaId, Asignacion? asignacion,
                                    string usuario, string motivoCierre, DateTime ahora)
    {
        c.Execute("""
            UPDATE gd.tarea_vista
            SET estatus = 'CANCELADA', fecha_cierre = CASE WHEN fecha_asignacion > @ahora THEN fecha_asignacion ELSE @ahora END,
                cerrado_por = @usuario, motivo_cierre = @motivoCierre
            WHERE vista_id = @vistaId AND estatus = 'ABIERTA'
            """, new { vistaId, usuario, motivoCierre, ahora }, tx);
        if (asignacion is null) return;
        c.Execute("""
            INSERT INTO gd.tarea_vista (vista_id, etapa_id, asignado_a, asignado_nombre, asignado_por, fecha_asignacion)
            VALUES (@vistaId, @etapaId, @UsuarioRed, @Nombre, @usuario, @ahora)
            """, new { vistaId, etapaId, asignacion.UsuarioRed, asignacion.Nombre, usuario, ahora }, tx);
    }

    private FilaTarea Fila(TareaRow t) =>
        new(t.tarea_id, t.vista_id, t.vista, t.data_product, t.etapa, t.asignado_a, t.asignado_nombre, t.fecha_asignacion, t.estatus,
            t.fecha_cierre, t.cerrado_por, t.motivo_cierre, ReglasTarea.Dias(t.fecha_asignacion, t.fecha_cierre, db.Hoy));
}
