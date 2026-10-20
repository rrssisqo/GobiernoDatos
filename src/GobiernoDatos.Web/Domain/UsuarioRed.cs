using System.Text.RegularExpressions;

namespace GobiernoDatos.Web.Domain;

// Usuario de red en forma canónica DOMINIO\usuario (dominio en mayúsculas, usuario en minúsculas),
// que es la llave con la que se reconoce a un certificador.
public static partial class UsuarioRed
{
    public static string Normalizar(string entrada, string dominioPredeterminado)
    {
        var t = (entrada ?? "").Trim();
        if (t.Contains('@')) throw new DomainException("Escribe el usuario de red (DOMINIO\\usuario), no el correo.");
        var partes = t.Split('\\');
        var (dominio, usuario) = partes.Length switch
        {
            1 => (dominioPredeterminado, partes[0]),
            2 => (partes[0], partes[1]),
            _ => throw new DomainException("Usuario de red no válido: usa DOMINIO\\usuario."),
        };
        if (!Valido().IsMatch(dominio) || !Valido().IsMatch(usuario))
            throw new DomainException("Usuario de red no válido: usa DOMINIO\\usuario.");
        return $"{dominio.ToUpperInvariant()}\\{usuario.ToLowerInvariant()}";
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex Valido();
}
