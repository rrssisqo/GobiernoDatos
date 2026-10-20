using System.DirectoryServices.AccountManagement;
using System.Security.Claims;
using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace GobiernoDatos.Web.Services;

public static class RolesPortal
{
    public const string Equipo = "Equipo";                 // equipo de gobierno de datos
    public const string Administrador = "Administrador";   // administra eventos y bajas
    public const string Certificador = "Certificador";     // responsable de macro área
}

public interface IUsuarioActual
{
    string Login { get; }
    // Nombre para mostrar. En recertificación identifica al certificador (gd.responsable.nombre).
    string Nombre { get; }
    bool Simulado { get; }
    bool TieneRol(string rol);
}

// Desarrollo local: identidad de configuración.
public sealed class UsuarioDemo(IOptions<GobiernoDatosOptions> opciones) : IUsuarioActual
{
    private readonly UsuarioDemoOptions _u = opciones.Value.UsuarioDemo;
    public string Login => _u.Login;
    public string Nombre => _u.Nombre;
    public bool Simulado => true;
    public bool TieneRol(string rol) => _u.Roles.Contains(rol, StringComparer.OrdinalIgnoreCase);
}

// Producción: el usuario que autenticó IIS (autenticación de Windows).
//   Administrador = cuentas o grupos de AD en GobiernoDatos:Administradores.
//   Equipo y Certificador = cualquier usuario autenticado; un certificador solo ve las macro áreas
//   donde es titular o delegado vigente (lo filtra la base).
public sealed class UsuarioWindows(IHttpContextAccessor http, IOptions<GobiernoDatosOptions> opciones, IDirectorioUsuarios directorio) : IUsuarioActual
{
    private ClaimsPrincipal Principal =>
        http.HttpContext?.User is { Identity.IsAuthenticated: true } u ? u
        : throw new InvalidOperationException("No hay un usuario autenticado por Windows: revisa la autenticación del sitio en IIS.");

    public string Login => Principal.Identity!.Name!;
    public string Nombre => directorio.NombreVisible(Login);
    public bool Simulado => false;

    public bool TieneRol(string rol) => rol switch
    {
        RolesPortal.Administrador => http.HttpContext?.User is { } u && EsAdministrador(u, opciones.Value.Administradores),
        RolesPortal.Equipo or RolesPortal.Certificador => http.HttpContext?.User.Identity?.IsAuthenticated == true,
        _ => false,
    };

    // Coincide por cuenta (DOMINIO\usuario) o por pertenencia a un grupo de AD.
    internal static bool EsAdministrador(ClaimsPrincipal usuario, IEnumerable<string> administradores) =>
        usuario.Identity?.IsAuthenticated == true &&
        administradores.Any(a => string.Equals(a, usuario.Identity.Name, StringComparison.OrdinalIgnoreCase) || usuario.IsInRole(a));
}

public interface IDirectorioUsuarios
{
    // Nombre para mostrar; si el directorio no responde, la propia cuenta.
    string NombreVisible(string login);

    // Nombre del usuario en el directorio, o null si no existe. Lanza DomainException si el directorio no responde.
    string? BuscarNombre(string usuarioRed);

    // Correo registrado en el directorio (recordatorios), o null si no tiene. Lanza DomainException si el directorio no responde.
    string? BuscarCorreo(string usuarioRed) => null;
}

// Active Directory, con caché.
public sealed class DirectorioActivo(IMemoryCache cache, ILogger<DirectorioActivo> log) : IDirectorioUsuarios
{
    public string NombreVisible(string login)
    {
        try
        {
            return BuscarNombre(login) ?? login;
        }
        catch (DomainException)
        {
            return login;
        }
    }

    // Solo se guardan en caché las respuestas del directorio (encontrado o no); un error no se guarda.
    public string? BuscarNombre(string usuarioRed) => Datos(usuarioRed)?.Nombre;

    public string? BuscarCorreo(string usuarioRed) => Datos(usuarioRed)?.Correo;

    private sealed record DatosAd(string Nombre, string? Correo);

    private DatosAd? Datos(string usuarioRed)
    {
        var clave = "ad:" + usuarioRed.ToUpperInvariant();
        if (cache.TryGetValue(clave, out DatosAd? datos)) return datos;
        datos = Consultar(usuarioRed);
        cache.Set(clave, datos, TimeSpan.FromHours(8));
        return datos;
    }

    private DatosAd? Consultar(string usuarioRed)
    {
        if (!OperatingSystem.IsWindows()) throw new DomainException("Active Directory no está disponible en este servidor.");
        try
        {
            var partes = usuarioRed.Split('\\', 2);
            using var contexto = partes.Length == 2 ? new PrincipalContext(ContextType.Domain, partes[0]) : new PrincipalContext(ContextType.Domain);
            using var usuario = UserPrincipal.FindByIdentity(contexto, IdentityType.SamAccountName, partes[^1]);
            if (usuario is null) return null;
            return new DatosAd(string.IsNullOrWhiteSpace(usuario.DisplayName) ? usuario.SamAccountName : usuario.DisplayName,
                               string.IsNullOrWhiteSpace(usuario.EmailAddress) ? null : usuario.EmailAddress.Trim());
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "No se pudo consultar {Usuario} en Active Directory.", usuarioRed);
            throw new DomainException("No se pudo consultar Active Directory; intenta de nuevo.");
        }
    }
}

// Desarrollo local: sin directorio, cualquier usuario de red se acepta; el usuario demo conserva su nombre.
public sealed class DirectorioSimulado(IOptions<GobiernoDatosOptions> opciones) : IDirectorioUsuarios
{
    public string NombreVisible(string login) => login;

    public string? BuscarNombre(string usuarioRed)
    {
        var o = opciones.Value;
        var demo = UsuarioRed.Normalizar(o.UsuarioDemo.Login, o.DominioPredeterminado);
        return string.Equals(usuarioRed, demo, StringComparison.OrdinalIgnoreCase) ? o.UsuarioDemo.Nombre : usuarioRed;
    }

    // Correo ficticio para desarrollo (el envío también es simulado).
    public string? BuscarCorreo(string usuarioRed) => $"{usuarioRed.Split('\\')[^1].ToLowerInvariant()}@ejemplo.local";
}
