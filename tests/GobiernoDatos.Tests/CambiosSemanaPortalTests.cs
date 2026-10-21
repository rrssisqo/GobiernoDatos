using System.Net;
using System.Text.RegularExpressions;

namespace GobiernoDatos.Tests;

// Portal propio: las demás pruebas registran avances y notas en la semana actual.
public class CambiosSemanaPortalTests(PortalTests.Fabrica fabrica) : IClassFixture<PortalTests.Fabrica>
{
    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    private static IEnumerable<string> Filas(string html) =>
        Regex.Matches(html, "<tr data-texto=.*?</tr>", RegexOptions.Singleline).Select(m => m.Value);

    [Fact]
    public async Task Filtro_de_data_products_con_cambios_en_la_semana()
    {
        var client = fabrica.CreateClient();
        var inicio = WebUtility.HtmlDecode(await client.GetStringAsync("/"));
        var semana = Regex.Match(inicio, "<option value=\"(\\d+)\" selected=\"selected\">").Groups[1].Value;   // semana actual
        Assert.DoesNotContain(">Cambió<", inicio);                                                // la muestra es de semanas anteriores
        Assert.Contains($"(0)</option>", inicio);

        // Nota de la semana en un data product.
        var lista = await client.GetStringAsync("/DataProducts");
        var detalle = WebUtility.HtmlDecode(Regex.Match(lista, "href=\"(/DataProducts/Detalle\\?id=\\d+)\"").Groups[1].Value);
        var html = await client.GetStringAsync(detalle);
        var form = Regex.Matches(html, "<form[^>]*>").Select(m => m.Value).First(f => f.Contains("handler=Nota"));
        var accion = WebUtility.HtmlDecode(Regex.Match(form, "action=\"([^\"]*)\"").Groups[1].Value);
        await client.PostAsync(accion, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["NotaLibre"] = "Revisión de la semana", ["__RequestVerificationToken"] = Token(html),
        }));
        var nombre = WebUtility.HtmlDecode(Regex.Match(html, "<h1>([^<]+)</h1>").Groups[1].Value).Trim();

        // Sin filtro: todos, con la marca solo en el que cambió. Con filtro: solo ese.
        var todos = WebUtility.HtmlDecode(await client.GetStringAsync($"/?semana={semana}"));
        var marcadas = Filas(todos).Where(f => f.Contains(">Cambió<")).ToList();
        Assert.Contains(nombre, Assert.Single(marcadas));
        Assert.True(Filas(todos).Count() > 1);
        Assert.Contains("(1)</option>", todos);

        var solo = WebUtility.HtmlDecode(await client.GetStringAsync($"/?semana={semana}&cambios=1"));
        Assert.Contains(nombre, Assert.Single(Filas(solo)));
        Assert.Contains("<option value=\"1\" selected=\"selected\">", solo);
        Assert.Contains("cambios=1", Regex.Match(solo, "<a class=\"chip\"[^>]*>Todos</a>").Value);   // los chips conservan el filtro

        // La exportación respeta el filtro.
        var csv = await client.GetStringAsync($"/?handler=Exportar&semana={semana}&cambios=1");
        Assert.Equal(2, csv.Trim().Split('\n').Length);                                           // encabezado + 1
    }
}
