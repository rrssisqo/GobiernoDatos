using System.Net.Http.Headers;
using System.Text.Json;
using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;
using Microsoft.Extensions.Options;

namespace GobiernoDatos.Web.Services;

// UsuarioRed = DOMINIO\cuenta de Windows (onPremisesSamAccountName); es lo que identifica al certificador en el portal.
public sealed record UsuarioEncontrado(string Nombre, string UsuarioRed, string? Correo, string? Puesto, string? Area);

public interface IBuscadorUsuarios
{
    // Hasta 15 cuentas activas con usuario de red cuyo nombre, correo o UPN empiece con el texto. Lanza DomainException si falla.
    IReadOnlyList<UsuarioEncontrado> Buscar(string texto);
}

// Microsoft Graph: GET /users con $search (ConsistencyLevel: eventual). Solo cuentas sincronizadas desde el AD local.
public sealed class BuscadorEntra(IHttpClientFactory http, IOptions<GobiernoDatosOptions> opciones, TimeProvider reloj,
                                  ILogger<BuscadorEntra> log) : IBuscadorUsuarios
{
    private readonly SemaphoreSlim _candado = new(1, 1);
    private (string Token, DateTimeOffset Vence)? _token;

    public IReadOnlyList<UsuarioEncontrado> Buscar(string texto)
    {
        texto = texto.Trim().Replace("\"", "");
        if (texto.Contains('\\')) texto = texto[(texto.IndexOf('\\') + 1)..];   // MABENET\usuario → usuario
        if (texto.Length < 2) return [];
        try
        {
            using var cliente = http.CreateClient("graph");
            const string campos = "&$select=displayName,mail,userPrincipalName,onPremisesSamAccountName,onPremisesDomainName,jobTitle,department";
            var encontrados = Consultar(cliente, "https://graph.microsoft.com/v1.0/users" +
                $"?$search={Uri.EscapeDataString($"\"displayName:{texto}\" OR \"mail:{texto}\" OR \"userPrincipalName:{texto}\"")}" +
                "&$filter=accountEnabled eq true&$count=true&$top=25" + campos);
            // $search no cubre la cuenta de Windows: si el texto parece un usuario de red, se busca también por ella.
            if (!texto.Contains(' ') && texto.Length >= 3)
                encontrados.AddRange(Consultar(cliente, "https://graph.microsoft.com/v1.0/users" +
                    $"?$filter={Uri.EscapeDataString($"accountEnabled eq true and startswith(onPremisesSamAccountName,'{texto.Replace("'", "''")}')")}" +
                    "&$count=true&$top=10" + campos));
            return encontrados.DistinctBy(u => u.UsuarioRed.ToUpperInvariant()).OrderBy(u => u.Nombre).Take(15).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            log.LogError(ex, "No se pudo buscar usuarios en Entra ID.");
            throw new DomainException("No se pudo consultar Entra ID; revisa la salida a internet del servidor.");
        }
    }

    private List<UsuarioEncontrado> Consultar(HttpClient cliente, string url)
    {
        using var pedido = new HttpRequestMessage(HttpMethod.Get, url);
        pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(cliente));
        pedido.Headers.Add("ConsistencyLevel", "eventual");
        using var respuesta = cliente.Send(pedido);
        var cuerpo = respuesta.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!respuesta.IsSuccessStatusCode)
        {
            log.LogError("Graph respondió {Estado} al buscar usuarios: {Cuerpo}", (int)respuesta.StatusCode, cuerpo);
            throw new DomainException($"Entra ID respondió {(int)respuesta.StatusCode} al buscar usuarios.");
        }
        using var json = JsonDocument.Parse(cuerpo);
        return json.RootElement.GetProperty("value").EnumerateArray()
            .Select(u => (Sam: Texto(u, "onPremisesSamAccountName"), Dominio: Texto(u, "onPremisesDomainName"), u))
            .Where(x => x.Sam is not null)
            .Select(x => new UsuarioEncontrado(Texto(x.u, "displayName") ?? x.Sam!, $"{Netbios(x.Dominio)}\\{x.Sam}",
                Texto(x.u, "mail"), Texto(x.u, "jobTitle"), Texto(x.u, "department")))
            .ToList();
    }

    // mabenet.corpmabe.com → MABENET (dominio NetBIOS con el que IIS identifica al usuario).
    private string Netbios(string? dominio) =>
        string.IsNullOrWhiteSpace(dominio) ? opciones.Value.DominioPredeterminado : dominio.Split('.')[0].ToUpperInvariant();

    private static string? Texto(JsonElement e, string propiedad) =>
        e.TryGetProperty(propiedad, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    // Token de la cuenta de servicio (ROPC); se reutiliza hasta 5 minutos antes de vencer.
    private string Token(HttpClient cliente)
    {
        _candado.Wait();
        try
        {
            if (_token is { } t && t.Vence > reloj.GetUtcNow()) return t.Token;
            var e = opciones.Value.Entra;
            if (string.IsNullOrWhiteSpace(e.TenantId) || string.IsNullOrWhiteSpace(e.ClientId) || string.IsNullOrWhiteSpace(e.Usuario) || string.IsNullOrWhiteSpace(e.Contrasena))
                throw new DomainException("La búsqueda en Entra ID no está configurada (GobiernoDatos:Entra).");
            using var respuesta = cliente.Send(new HttpRequestMessage(HttpMethod.Post, $"https://login.microsoftonline.com/{e.TenantId}/oauth2/v2.0/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = e.ClientId, ["grant_type"] = "password", ["username"] = e.Usuario, ["password"] = e.Contrasena,
                    ["scope"] = "https://graph.microsoft.com/.default",
                }),
            });
            var cuerpo = respuesta.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var json = JsonDocument.Parse(cuerpo);
            if (!respuesta.IsSuccessStatusCode)
            {
                var detalle = json.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() : cuerpo;
                log.LogError("Entra ID rechazó el token: {Detalle}", detalle);
                throw new DomainException("Entra ID rechazó la cuenta de servicio; revisa usuario y contraseña.");
            }
            var token = json.RootElement.GetProperty("access_token").GetString()!;
            var segundos = json.RootElement.GetProperty("expires_in").GetInt32();
            _token = (token, reloj.GetUtcNow().AddSeconds(segundos - 300));
            return token;
        }
        finally { _candado.Release(); }
    }
}

// Desarrollo y pruebas: busca entre usuarios ficticios.
public sealed class BuscadorSimulado : IBuscadorUsuarios
{
    private static readonly UsuarioEncontrado[] Usuarios =
    [
        new("Medrano, Juan (MEX, PAL, CL)", @"MABENET\jmmmexcl", "juan.medrano@ejemplo.local", "Gerente de Ciclo Largo", "CICLO LARGO"),
        new("Landa, Saray (MEX, PAL, CO)", @"MABENET\slamexco", "saray.landa@ejemplo.local", "Gerente Comercial", "COMERCIAL"),
        new("Prueba, Certificador (MEX, TI)", @"MABENET\cprueba", "cprueba@ejemplo.local", "Analista", "TI"),
    ];

    public IReadOnlyList<UsuarioEncontrado> Buscar(string texto)
    {
        texto = texto.Trim();
        if (texto.Contains('\\')) texto = texto[(texto.IndexOf('\\') + 1)..];
        if (texto.Length < 2) return [];
        return Usuarios.Where(u => u.Nombre.Contains(texto, StringComparison.OrdinalIgnoreCase) ||
                                   u.UsuarioRed.Contains(texto, StringComparison.OrdinalIgnoreCase) ||
                                   u.Correo?.Contains(texto, StringComparison.OrdinalIgnoreCase) == true).ToList();
    }
}
