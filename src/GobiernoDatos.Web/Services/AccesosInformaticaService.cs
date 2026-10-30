using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;
using Microsoft.Extensions.Options;

namespace GobiernoDatos.Web.Services;

// Solicitud de un rol de Informatica para una persona (gd.acceso_informatica, migración 020). El administrador la registra,
// exporta el Excel para pedir el cambio a infraestructura y después captura el número de change.
public sealed record AccesoInformatica(long AccesoId, string UsuarioRed, string Nombre, string? Correo, string Rol, DateOnly FechaSolicitud,
                                       string? Change, string? Comentario, string RegistradoPor, DateTime FechaRegistro);

public interface IAccesosInformaticaService
{
    // Más recientes primero.
    IReadOnlyList<AccesoInformatica> Listar();
    // Nombre y correo salen del directorio. Una persona no puede tener dos veces el mismo rol.
    AccesoInformatica Registrar(string usuarioRed, string rol, DateOnly? fechaSolicitud, string? change, string? comentario, string usuario);
    void AsignarChange(long accesoId, string? change);
    void Eliminar(long accesoId);
}

public static class ReglasAccesoInformatica
{
    public static readonly IReadOnlyList<string> Roles = ["Data Owner", "Data Administrator", "Data Steward"];

    public static string Rol(string? rol) =>
        Roles.FirstOrDefault(r => string.Equals(r, rol?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new DomainException("Elige el rol: Data Owner, Data Administrator o Data Steward.");

    public static DateOnly FechaSolicitud(DateOnly? fecha, DateOnly hoy)
    {
        var f = fecha ?? hoy;
        if (f > hoy) throw new DomainException("La fecha de solicitud no puede ser futura.");
        return f;
    }

    // Vacío = todavía sin change.
    public static string? Change(string? change)
    {
        var c = change?.Trim();
        if (string.IsNullOrEmpty(c)) return null;
        if (c.Length > 60) throw new DomainException("El número de change admite máximo 60 caracteres.");
        return c;
    }

    public static string? Comentario(string? comentario)
    {
        var c = comentario?.Trim();
        if (string.IsNullOrEmpty(c)) return null;
        if (c.Length > 500) throw new DomainException("El comentario admite máximo 500 caracteres.");
        return c;
    }

    public static string Duplicado(string nombre, string rol) => $"{nombre} ya tiene una solicitud de {rol}.";

    // Usuario de red normalizado, con su nombre y correo del directorio.
    public static (string Login, string Nombre, string? Correo) Persona(IDirectorioUsuarios directorio, string usuarioRed, string dominio)
    {
        var login = UsuarioRed.Normalizar(usuarioRed, dominio);
        var nombre = directorio.BuscarNombre(login) ?? throw new DomainException($"El usuario {login} no existe en Active Directory.");
        return (login, nombre, directorio.BuscarCorreo(login));
    }
}

public sealed class AccesosInformaticaService(GobiernoStore s, IDirectorioUsuarios directorio, IOptions<GobiernoDatosOptions> opciones)
    : IAccesosInformaticaService
{
    public IReadOnlyList<AccesoInformatica> Listar()
    {
        lock (s.Sync) return s.AccesosInformatica.OrderByDescending(a => a.FechaSolicitud).ThenByDescending(a => a.AccesoId).ToList();
    }

    public AccesoInformatica Registrar(string usuarioRed, string rol, DateOnly? fechaSolicitud, string? change, string? comentario, string usuario)
    {
        rol = ReglasAccesoInformatica.Rol(rol);
        var fecha = ReglasAccesoInformatica.FechaSolicitud(fechaSolicitud, s.Hoy);
        change = ReglasAccesoInformatica.Change(change);
        comentario = ReglasAccesoInformatica.Comentario(comentario);
        var (login, nombre, correo) = ReglasAccesoInformatica.Persona(directorio, usuarioRed, opciones.Value.DominioPredeterminado);
        lock (s.Sync)
        {
            if (s.AccesosInformatica.Any(a => a.UsuarioRed == login && a.Rol == rol))
                throw new DomainException(ReglasAccesoInformatica.Duplicado(nombre, rol));
            var a = new AccesoInformatica(s.NuevoId(), login, nombre, correo, rol, fecha, change, comentario, usuario, s.Ahora);
            s.AccesosInformatica.Add(a);
            return a;
        }
    }

    public void AsignarChange(long accesoId, string? change)
    {
        var c = ReglasAccesoInformatica.Change(change) ?? throw new DomainException("Indica el número de change.");
        lock (s.Sync)
        {
            var i = s.AccesosInformatica.FindIndex(a => a.AccesoId == accesoId);
            if (i < 0) throw new DomainException("La solicitud no existe.");
            s.AccesosInformatica[i] = s.AccesosInformatica[i] with { Change = c };
        }
    }

    public void Eliminar(long accesoId)
    {
        lock (s.Sync)
            if (s.AccesosInformatica.RemoveAll(a => a.AccesoId == accesoId) == 0) throw new DomainException("La solicitud no existe.");
    }
}
