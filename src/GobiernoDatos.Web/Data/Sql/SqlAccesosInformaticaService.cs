using Dapper;
using GobiernoDatos.Web.Domain;
using GobiernoDatos.Web.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using static GobiernoDatos.Web.Data.Sql.SqlAux;

namespace GobiernoDatos.Web.Data.Sql;

// Accesos de Informatica sobre gd.acceso_informatica (migración 020). La base impide el mismo rol dos veces por persona.
public sealed class SqlAccesosInformaticaService(SqlConexion db, IDirectorioUsuarios directorio, IOptions<GobiernoDatosOptions> opciones)
    : IAccesosInformaticaService
{
    private sealed record AccesoRow(long acceso_id, string usuario_red, string nombre, string? correo, string rol, DateTime fecha_solicitud,
                                    string? numero_change, string? comentario, string registrado_por, DateTime fecha_registro);

    private static AccesoInformatica Acceso(AccesoRow r) => new(r.acceso_id, r.usuario_red, r.nombre, r.correo, r.rol, D(r.fecha_solicitud),
                                                                 r.numero_change, r.comentario, r.registrado_por, r.fecha_registro);

    public IReadOnlyList<AccesoInformatica> Listar()
    {
        using var c = db.Abrir();
        return c.Query<AccesoRow>("""
            SELECT acceso_id, usuario_red, nombre, correo, rol, fecha_solicitud, numero_change, comentario, registrado_por, fecha_registro
            FROM gd.acceso_informatica
            ORDER BY fecha_solicitud DESC, acceso_id DESC
            """).Select(Acceso).ToList();
    }

    public AccesoInformatica Registrar(string usuarioRed, string rol, DateOnly? fechaSolicitud, string? change, string? comentario, string usuario)
    {
        rol = ReglasAccesoInformatica.Rol(rol);
        var fecha = ReglasAccesoInformatica.FechaSolicitud(fechaSolicitud, db.Hoy);
        change = ReglasAccesoInformatica.Change(change);
        comentario = ReglasAccesoInformatica.Comentario(comentario);
        var (login, nombre, correo) = ReglasAccesoInformatica.Persona(directorio, usuarioRed, opciones.Value.DominioPredeterminado);
        using var c = db.Abrir();
        try
        {
            return Acceso(c.QuerySingle<AccesoRow>("""
                INSERT INTO gd.acceso_informatica (usuario_red, nombre, correo, rol, fecha_solicitud, numero_change, comentario, registrado_por)
                OUTPUT inserted.acceso_id, inserted.usuario_red, inserted.nombre, inserted.correo, inserted.rol, inserted.fecha_solicitud,
                       inserted.numero_change, inserted.comentario, inserted.registrado_por, inserted.fecha_registro
                VALUES (@login, @nombre, @correo, @rol, @fecha, @change, @comentario, @usuario)
                """, new { login, nombre, correo, rol, fecha = P(fecha), change, comentario, usuario }));
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            throw new DomainException(ReglasAccesoInformatica.Duplicado(nombre, rol));
        }
    }

    public void AsignarChange(long accesoId, string? change)
    {
        var numero = ReglasAccesoInformatica.Change(change) ?? throw new DomainException("Indica el número de change.");
        using var c = db.Abrir();
        if (c.Execute("UPDATE gd.acceso_informatica SET numero_change = @numero WHERE acceso_id = @accesoId", new { accesoId, numero }) == 0)
            throw new DomainException("La solicitud no existe.");
    }

    public void Eliminar(long accesoId)
    {
        using var c = db.Abrir();
        if (c.Execute("DELETE FROM gd.acceso_informatica WHERE acceso_id = @accesoId", new { accesoId }) == 0)
            throw new DomainException("La solicitud no existe.");
    }
}
