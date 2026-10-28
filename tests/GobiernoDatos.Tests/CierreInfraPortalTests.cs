using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using GobiernoDatos.Web.Domain;
using GobiernoDatos.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GobiernoDatos.Tests;

// Auditoría → Notificaciones: el administrador sube por macro área el correo con el que infraestructura avisa que terminó.
public class CierreInfraPortalTests(PortalTests.Fabrica fabrica) : IClassFixture<PortalTests.Fabrica>
{
    private static string FilaTi(string html) => Regex.Match(html, "<tr data-macro=\"TI\">.*?</tr>", RegexOptions.Singleline).Value;

    [Fact]
    public async Task Sube_descarga_y_empaqueta_el_correo_de_cierre_de_infraestructura()
    {
        var client = fabrica.CreateClient();
        var recert = fabrica.Services.GetRequiredService<IRecertificacionService>();

        // Sin tickets la columna no aparece.
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/Auditoria/Notificaciones"));
        Assert.Contains("No se ha subido ningún correo de cierre", html);
        Assert.DoesNotContain("<th>Cierre infraestructura</th>", html);

        // TI: una baja con VoBo del gerente y ticket.
        var mi = recert.MisAccesos("demo");
        var ti = mi.MacroAreas.Single(m => m.MacroArea == "TI");
        var baja = mi.Accesos.First(a => a.MacroArea == "TI");
        recert.Decidir(baja.AccesoId, Decision.BAJA, "Ya no lo usa", "demo");
        recert.ContinuaMasivo(mi.Accesos.Where(a => a.MacroArea == "TI").Select(a => a.AccesoId), "demo");
        recert.AsignarGerente(@"MABENET\gerenteti", "admin");
        recert.ConfirmarMacroArea(ti.MacroAreaId, "demo");
        var vobo = recert.Bajas(mi.Evento!.EventoId)!.MacroAreas.Single(m => m.MacroArea == "TI").VoBo!.VoBoId;
        Assert.True(recert.ResponderVoBo(vobo, true, "gerenteti@ejemplo.local", "Autorizado"));
        recert.RegistrarEjecucion(baja.AccesoId, "INC-100", null, "admin");

        html = WebUtility.HtmlDecode(await client.GetStringAsync("/Auditoria/Notificaciones"));
        Assert.Contains("<th>Cierre infraestructura</th>", html);
        Assert.Contains("Sin bajas", html);                                      // otras macro áreas sin bajas: sin formulario
        Assert.Single(Regex.Matches(html, "class=\"subir-cierre\""));             // solo TI puede subir
        var form = Regex.Match(FilaTi(html), "<form[^>]*handler=CierreInfra.*?</form>", RegexOptions.Singleline).Value;
        var accion = Regex.Match(form, "action=\"([^\"]*)\"").Groups[1].Value;
        var macroAreaId = Regex.Match(form, "name=\"macroAreaId\" value=\"(\\d+)\"").Groups[1].Value;
        var token = Regex.Match(form, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

        async Task<string> Subir(string nombre, byte[] contenido)
        {
            using var datos = new MultipartFormDataContent();
            datos.Add(new StringContent(macroAreaId), "macroAreaId");
            datos.Add(new StringContent("Retiro concluido"), "comentario");
            datos.Add(new StringContent(token), "__RequestVerificationToken");
            var archivo = new ByteArrayContent(contenido);
            archivo.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            datos.Add(archivo, "archivo", nombre);
            var r = await client.PostAsync(accion, datos);
            r.EnsureSuccessStatusCode();
            return WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
        }

        // Solo correos de Outlook (.eml o .msg).
        Assert.Contains("El correo debe ser .eml o .msg", await Subir("cierre.txt", "hola"u8.ToArray()));

        var eml = "From: infra@mabe.com.mx\r\nSubject: Bajas retiradas TI\r\n\r\nListo."u8.ToArray();
        var pagina = await Subir("cierre_TI.eml", eml);
        Assert.Contains("Correo de cierre de infraestructura guardado", pagina);
        Assert.Contains("Recibido", FilaTi(pagina));
        var tabla = Regex.Match(pagina, "<section[^>]*id=\"cierre-infra\".*?</section>", RegexOptions.Singleline).Value;
        Assert.Contains("cierre_TI.eml", tabla);
        Assert.Contains("Retiro concluido", tabla);

        // Descarga con su nombre original.
        var link = Regex.Match(FilaTi(pagina), "href=\"([^\"]*handler=CierreInfra[^\"]*)\"").Groups[1].Value;
        var descarga = await client.GetAsync(link);
        Assert.Equal(eml, await descarga.Content.ReadAsByteArrayAsync());
        Assert.Equal("message/rfc822", descarga.Content.Headers.ContentType!.MediaType);
        Assert.Equal("cierre_TI.eml", descarga.Content.Headers.ContentDisposition!.FileNameStar ?? descarga.Content.Headers.ContentDisposition.FileName);

        // Va en el .zip de evidencia.
        var evidencia = Regex.Match(pagina, "href=\"([^\"]*handler=Evidencia[^\"]*)\"").Groups[1].Value;
        using var zip = new ZipArchive(new MemoryStream(await client.GetByteArrayAsync(evidencia)));
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("cierre_infraestructura/TI_") && e.FullName.EndsWith(".eml"));
    }
}
