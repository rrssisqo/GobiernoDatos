using System.Net;
using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;
using GobiernoDatos.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GobiernoDatos.Tests;

public class BuscadorUsuariosTests
{
    // Responde como Entra ID: token y una página de usuarios; guarda las URL pedidas.
    private sealed class GraphFalso(string usuarios) : HttpMessageHandler
    {
        public List<string> Pedidos { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(Send(r, ct));

        protected override HttpResponseMessage Send(HttpRequestMessage r, CancellationToken ct)
        {
            Pedidos.Add(Uri.UnescapeDataString(r.RequestUri!.ToString()));
            var cuerpo = r.RequestUri!.Host == "login.microsoftonline.com"
                ? """{"access_token":"tok","expires_in":3600}"""
                : usuarios;
            if (r.RequestUri.Host == "graph.microsoft.com") Assert.Equal("Bearer tok", r.Headers.Authorization!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(cuerpo) };
        }
    }

    private sealed class Fabrica(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private static BuscadorEntra Buscador(GraphFalso graph) => new(new Fabrica(graph), Options.Create(new GobiernoDatosOptions
    {
        Entra = new EntraOptions { TenantId = "t", ClientId = "c", Usuario = "svc@mabe.com.mx", Contrasena = "x" },
    }), TimeProvider.System, NullLogger<BuscadorEntra>.Instance);

    [Fact]
    public void Devuelve_solo_cuentas_con_usuario_de_red_y_dominio_NetBIOS()
    {
        var graph = new GraphFalso("""
            {"value":[
              {"displayName":"Medrano, Tania (MEX, QUA, CV)","mail":"tania.medrano@mabe.com.mx","onPremisesSamAccountName":"TMMMEXCV",
               "onPremisesDomainName":"mabenet.corpmabe.com","jobTitle":"Especialista de Trafico","department":"CADENA DE VALOR MW"},
              {"displayName":"Sofia Medrano","mail":"Sofia.Medrano@sourceability.com","onPremisesSamAccountName":null,"onPremisesDomainName":null}
            ]}
            """);
        var r = Buscador(graph).Buscar("medrano");

        var u = Assert.Single(r);                                      // la cuenta externa (sin usuario de red) no aparece
        Assert.Equal(@"MABENET\TMMMEXCV", u.UsuarioRed);
        Assert.Equal(("Medrano, Tania (MEX, QUA, CV)", "tania.medrano@mabe.com.mx", "CADENA DE VALOR MW"), (u.Nombre, u.Correo, u.Area));
        Assert.Contains(graph.Pedidos, p => p.Contains("\"displayName:medrano\""));
        Assert.Contains(graph.Pedidos, p => p.Contains("startswith(onPremisesSamAccountName,'medrano')"));
        Assert.Single(graph.Pedidos, p => p.Contains("oauth2"));         // el token se pide una vez
    }

    [Fact]
    public void Texto_corto_no_consulta_y_quita_el_dominio()
    {
        var graph = new GraphFalso("""{"value":[]}""");
        Assert.Empty(Buscador(graph).Buscar("m"));
        Assert.Empty(graph.Pedidos);
        Buscador(graph).Buscar(@"MABENET\tmmmexcv");
        Assert.Contains(graph.Pedidos, p => p.Contains("startswith(onPremisesSamAccountName,'tmmmexcv')"));
    }

    [Fact]
    public void Sin_configuracion_avisa()
    {
        var b = new BuscadorEntra(new Fabrica(new GraphFalso("{}")), Options.Create(new GobiernoDatosOptions()), TimeProvider.System,
                                  NullLogger<BuscadorEntra>.Instance);
        Assert.Contains("no está configurada", Assert.Throws<DomainException>(() => b.Buscar("medrano")).Message);
    }
}
