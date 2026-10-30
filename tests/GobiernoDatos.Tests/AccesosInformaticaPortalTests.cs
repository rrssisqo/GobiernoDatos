using System.Net;
using System.Text.RegularExpressions;

namespace GobiernoDatos.Tests;

// Informatica → Accesos Informatica: alta de la solicitud, change, exportación a Excel y eliminación.
public class AccesosInformaticaPortalTests(PortalTests.Fabrica fabrica) : IClassFixture<PortalTests.Fabrica>
{
    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    private static string Accion(string html, string handler) =>
        WebUtility.HtmlDecode(Regex.Match(Regex.Matches(html, "<form[^>]*>").Select(m => m.Value).First(f => f.Contains("handler=" + handler)),
                                          "action=\"([^\"]*)\"").Groups[1].Value);

    private static string Fila(string html, string usuario) =>
        Regex.Matches(html, "<tr data-acceso=.*?</tr>", RegexOptions.Singleline).Select(m => m.Value).Single(f => f.Contains(usuario));

    [Fact]
    public async Task Registra_asigna_change_exporta_y_elimina()
    {
        var client = fabrica.CreateClient();
        var html = await client.GetStringAsync("/Informatica/Accesos");
        Assert.Contains(">Accesos Informatica</a>", html);                      // en el menú
        Assert.Contains("No hay solicitudes en esta vista.", html);

        async Task<string> Post(string handler, Dictionary<string, string> datos)
        {
            var pagina = await client.GetStringAsync("/Informatica/Accesos");
            datos["__RequestVerificationToken"] = Token(pagina);
            var r = await client.PostAsync(Accion(pagina, handler), new FormUrlEncodedContent(datos));
            r.EnsureSuccessStatusCode();
            return WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
        }

        var alta = new Dictionary<string, string>
        {
            ["usuarioRed"] = @"MABENET\jsteward", ["rol"] = "Data Steward", ["fechaSolicitud"] = "2026-10-01", ["comentario"] = "Glosarios de Finanzas",
        };
        var pagina = await Post("Registrar", new(alta));
        Assert.Contains("Solicitud registrada", pagina);
        var fila = Fila(pagina, @"MABENET\jsteward");
        Assert.Contains("Data Steward", fila);
        Assert.Contains("2026-10-01", fila);
        Assert.Contains("Glosarios de Finanzas", fila);

        // El mismo rol dos veces y un rol fuera del catálogo se rechazan.
        Assert.Contains("ya tiene una solicitud de Data Steward", await Post("Registrar", new(alta)));
        Assert.Contains("Elige el rol", await Post("Registrar", new() { ["usuarioRed"] = @"MABENET\jsteward", ["rol"] = "Admin", ["fechaSolicitud"] = "2026-10-01" }));

        // Sin change aparece en el filtro «Sin change»; al capturarlo pasa a «Con change».
        var id = Regex.Match(fila, "data-acceso=\"(\\d+)\"").Groups[1].Value;
        Assert.Contains(@"MABENET\jsteward", await client.GetStringAsync("/Informatica/Accesos?estado=SIN"));
        pagina = await Post("Change", new() { ["accesoId"] = id, ["change"] = "CHG-0420" });
        Assert.Contains("Change registrado", pagina);
        Assert.Contains("value=\"CHG-0420\"", Fila(pagina, @"MABENET\jsteward"));
        Assert.DoesNotContain(@"MABENET\jsteward", await client.GetStringAsync("/Informatica/Accesos?estado=SIN"));

        // Excel para infraestructura, con los filtros aplicados.
        var xlsx = await client.GetAsync("/Informatica/Accesos?handler=Exportar&estado=CON&rolFiltro=Data%20Steward");
        using (var libro = new ClosedXML.Excel.XLWorkbook(new MemoryStream(await xlsx.Content.ReadAsByteArrayAsync())))
        {
            var hoja = libro.Worksheet("Accesos Informatica");
            Assert.Equal(new[] { "Usuario de red", "Nombre", "Correo", "Rol", "Fecha de solicitud", "Change", "Comentario", "Registró" },
                         hoja.Row(1).CellsUsed().Select(c => c.GetString()));
            var r = hoja.RowsUsed().Single(x => x.Cell(1).GetString() == @"MABENET\jsteward");
            Assert.Equal(("Data Steward", "CHG-0420", new DateTime(2026, 10, 1)), (r.Cell(4).GetString(), r.Cell(6).GetString(), r.Cell(5).GetDateTime()));
        }

        pagina = await Post("Eliminar", new() { ["accesoId"] = id });
        Assert.Contains("Solicitud eliminada", pagina);
        Assert.DoesNotContain(@"MABENET\jsteward", pagina);
    }
}
