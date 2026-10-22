using System.Net;
using System.Text.RegularExpressions;
using GobiernoDatos.Web.Domain;
using GobiernoDatos.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GobiernoDatos.Tests;

// Portal propio: decide accesos del evento de muestra (las demás pruebas usan sus pendientes).
public class BajasVoBoPortalTests(PortalTests.Fabrica fabrica) : IClassFixture<PortalTests.Fabrica>
{
    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    private static string Accion(string html, string handler) =>
        WebUtility.HtmlDecode(Regex.Match(Regex.Matches(html, "<form[^>]*>").Select(m => m.Value).First(f => f.Contains("handler=" + handler)),
                                          "action=\"([^\"]*)\"").Groups[1].Value);

    // Cuerpo HTML de un .eml (base64 o quoted-printable).
    private static string CuerpoEml(string eml)
    {
        var m = Regex.Match(eml, @"Content-Transfer-Encoding: (base64|quoted-printable)\r\n(?:[^\r\n]+\r\n)*\r\n(?<cuerpo>.*?)(\r\n\r\n|\r\n--|$)", RegexOptions.Singleline);
        var texto = m.Groups["cuerpo"].Value;
        if (m.Groups[1].Value == "base64") return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Regex.Replace(texto, @"\s", "")));
        var qp = Regex.Replace(texto.Replace("=\r\n", ""), "(=[0-9A-F]{2})+", g => System.Text.Encoding.UTF8.GetString(
            g.Value.Split('=', StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToByte(h, 16)).ToArray()));
        return qp;
    }

    [Fact]
    public async Task Gerente_vobo_y_ticket_con_filtro_por_macro_area()
    {
        var client = fabrica.CreateClient();
        var recert = fabrica.Services.GetRequiredService<IRecertificacionService>();

        // El certificador demo (TI) marca una baja y el resto Continúa.
        var mi = recert.MisAccesos("demo");
        var ti = mi.MacroAreas.Single(m => m.MacroArea == "TI");
        var baja = mi.Accesos.First(a => a.MacroArea == "TI");
        recert.Decidir(baja.AccesoId, Decision.BAJA, "Ya no lo usa", "demo");
        recert.ContinuaMasivo(mi.Accesos.Where(a => a.MacroArea == "TI").Select(a => a.AccesoId), "demo");

        // Auditoría → Evidencia de la respuesta: OKBAJA y comentarios de TI, y exportación a Excel con el resumen.
        var resp = WebUtility.HtmlDecode(await client.GetStringAsync("/Auditoria/Respuestas?macro=TI"));
        Assert.Matches(@"<a class=""chip""[^>]*aria-current=""page""[^>]*>Evidencia de la respuesta</a>", resp);
        var filasResp = Regex.Matches(Regex.Match(resp, "<table class=\"tabla tabla-auditoria\" id=\"tabla-respuestas\">.*?</table>", RegexOptions.Singleline).Value,
                                      "<tr>.*?</tr>", RegexOptions.Singleline).Select(m => m.Value).Skip(1).ToList();
        Assert.Contains(filasResp, f => f.Contains(baja.UsuarioCodigo) && f.Contains(">BAJA<") && f.Contains("Ya no lo usa"));
        Assert.Contains(filasResp, f => f.Contains(">OK<"));
        Assert.All(filasResp, f => Assert.Contains("<td>TI</td>", f));
        var soloBajas = WebUtility.HtmlDecode(await client.GetStringAsync("/Auditoria/Respuestas?macro=TI&respuesta=BAJA"));
        Assert.DoesNotContain(">OK<", Regex.Match(soloBajas, "id=\"tabla-respuestas\">.*?</table>", RegexOptions.Singleline).Value);
        var xlsx = await client.GetAsync("/Auditoria/Respuestas?handler=Exportar&macro=TI");
        using (var libro = new ClosedXML.Excel.XLWorkbook(new MemoryStream(await xlsx.Content.ReadAsByteArrayAsync())))
        {
            var hoja = libro.Worksheet("Respuestas");
            Assert.Equal("OKBAJA", hoja.Cell(1, 7).GetString());
            Assert.Contains(hoja.RowsUsed(), r => r.Cell(3).GetString() == baja.UsuarioCodigo && r.Cell(7).GetString() == "BAJA" && r.Cell(8).GetString() == "Ya no lo usa");
            Assert.Equal("TI", libro.Worksheet("Resumen por macro área").Cell(2, 1).GetString());
        }

        // Un solo gerente para todas las macro áreas, desde Macro áreas.
        var macros = await client.GetStringAsync("/Recertificacion/MacroAreas");
        var panel = Regex.Match(macros, "<section[^>]*id=\"gerente\".*?</section>", RegexOptions.Singleline).Value;
        Assert.Contains("Sin gerente", panel);
        Assert.Single(Regex.Matches(macros, "handler=AsignarGerente"));
        var r = await client.PostAsync(Accion(panel, "AsignarGerente"), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["usuarioRed"] = @"MABENET\gerenteti", ["__RequestVerificationToken"] = Token(macros),
        }));
        var final = WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
        Assert.Contains("Gerente asignado: autoriza las bajas de todas las macro áreas.", final);
        Assert.Contains("gerenteti@ejemplo.local", Regex.Match(final, "<section[^>]*id=\"gerente\".*?</section>", RegexOptions.Singleline).Value);

        // Al confirmar la macro área se pide el VoBo; sin autorizar no hay formulario de ticket.
        recert.ConfirmarMacroArea(ti.MacroAreaId, "demo");
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/Recertificacion/Bajas?macro=TI"));
        var vobo = Regex.Match(html, "<tr data-macro=\"TI\">.*?</tr>", RegexOptions.Singleline).Value;
        Assert.Contains("Por enviar", vobo);
        Assert.Contains("Requiere VoBo del gerente", html);
        Assert.DoesNotContain("handler=Ejecutar", html);
        Assert.All(Regex.Matches(html, @"<td>([^<]+)</td>\s*<td>[^<]*<span class=""ayuda""").Select(m => m.Groups[1].Value), m => Assert.Equal("TI", m));

        // Power Automate registra la autorización: aparece el VoBo y el formulario del ticket.
        var id = recert.Bajas(mi.Evento!.EventoId)!.MacroAreas.Single(m => m.MacroArea == "TI").VoBo!.VoBoId;
        Assert.True(recert.ResponderVoBo(id, true, "gerenteti@ejemplo.local", "Autorizado"));
        html = await client.GetStringAsync("/Recertificacion/Bajas?macro=TI");
        Assert.Contains("Autorizado", WebUtility.HtmlDecode(Regex.Match(html, "<tr data-macro=\"TI\">.*?</tr>", RegexOptions.Singleline).Value));

        // Evidencia en Auditoría → Notificaciones: el VoBo con su constancia (solicitud, respuesta y bajas).
        var notif = WebUtility.HtmlDecode(await client.GetStringAsync("/Auditoria/Notificaciones"));
        var filaVobo = Regex.Match(notif, $"<tr data-vobo=\"{id}\">.*?</tr>", RegexOptions.Singleline).Value;
        Assert.Contains("Autorizado", filaVobo);
        Assert.Contains("gerenteti@ejemplo.local", filaVobo);
        var constancia = await client.GetAsync(Regex.Match(filaVobo, "href=\"([^\"]*handler=VoBo[^\"]*)\"").Groups[1].Value);
        Assert.Equal("message/rfc822", constancia.Content.Headers.ContentType!.MediaType);
        var disposicion = constancia.Content.Headers.ContentDisposition!;
        Assert.EndsWith(".eml", disposicion.FileNameStar ?? disposicion.FileName!.Trim('"'));
        var eml = await constancia.Content.ReadAsStringAsync();
        Assert.Contains("To: gerenteti@ejemplo.local", eml);
        var cuerpo = WebUtility.HtmlDecode(CuerpoEml(eml));
        Assert.Contains("Constancia generada por el portal", cuerpo);
        Assert.Contains("AUTORIZADO", cuerpo);
        Assert.Contains("gerenteti@ejemplo.local", cuerpo);
        Assert.Contains(baja.UsuarioCodigo, cuerpo);
        Assert.Contains(baja.Rol, cuerpo);
        Assert.Contains("Por asignar a infraestructura", WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("handler=Ejecutar", html);

        // Sin responsable de infraestructura el botón está deshabilitado; se configura una sola vez en Macro áreas.
        Assert.Contains("Sin responsable de infraestructura", WebUtility.HtmlDecode(html));
        macros = await client.GetStringAsync("/Recertificacion/MacroAreas");
        r = await client.PostAsync(Accion(macros, "AsignarResponsableInfra"), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["usuarioRed"] = @"MABENET\infra1", ["__RequestVerificationToken"] = Token(macros),
        }));
        Assert.Contains("Responsable de infraestructura asignado", WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync()));

        // Asignación de la macro área: solo el número de cambio; se envía el correo con el Excel al responsable.
        var correos = (EnviadorSimulado)fabrica.Services.GetRequiredService<IEnviadorCorreo>();
        html = await client.GetStringAsync("/Recertificacion/Bajas?macro=TI");
        Assert.DoesNotContain("name=\"usuarioRed\"", Regex.Match(html, "<div class=\"asignacion-infra\".*?</form>", RegexOptions.Singleline).Value);
        r = await client.PostAsync(Accion(html, "AsignarInfra"), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["macroAreaId"] = ti.MacroAreaId.ToString(), ["macroArea"] = "TI", ["cambio"] = "CHG-100",
            ["__RequestVerificationToken"] = Token(html),
        }));
        final = WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
        Assert.Contains("TI: 1 bajas asignadas a MABENET\\infra1 con el cambio CHG-100. Se le envió el correo con el Excel a infra1@ejemplo.local.", final);
        Assert.Contains("Correo enviado", Regex.Match(final, "<div class=\"asignacion-fila\" data-macro-infra=\"TI\">.*?</form>", RegexOptions.Singleline).Value);
        Assert.DoesNotContain("handler=AsignarInfra", Regex.Match(final, "<section[^>]*id=\"vobo\".*?</section>", RegexOptions.Singleline).Value);   // ya no está en el panel de VoBo
        var aviso = correos.Enviados[^1];
        Assert.Equal("infra1@ejemplo.local", aviso.Para);
        Assert.Contains("CHG-100", aviso.Asunto);
        var adjunto = Assert.Single(aviso.Adjuntos!);
        Assert.EndsWith(".xlsx", adjunto.Nombre);
        using (var libro = new ClosedXML.Excel.XLWorkbook(new MemoryStream(adjunto.Contenido)))
        {
            var hoja = libro.Worksheet(1);
            Assert.Equal("Cambio", hoja.Cell(1, 1).GetString());
            Assert.Equal(("CHG-100", baja.UsuarioCodigo, baja.Rol), (hoja.Cell(2, 1).GetString(), hoja.Cell(2, 4).GetString(), hoja.Cell(2, 6).GetString()));
        }

        // La tabla de bajas muestra el responsable y el cambio; el ticket viene prellenado con el número de cambio.
        html = await client.GetStringAsync("/Recertificacion/Bajas?macro=TI");
        Assert.Contains("value=\"CHG-100\"", Regex.Match(html, "<form[^>]*handler=Ejecutar.*?</form>", RegexOptions.Singleline).Value);
        r = await client.PostAsync(Accion(html, "Ejecutar"), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["accesoId"] = baja.AccesoId.ToString(), ["ticket"] = "INC-777", ["__RequestVerificationToken"] = Token(html),
        }));
        final = WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
        Assert.Contains("Ejecución de la baja registrada.", final);
        Assert.Contains("Ticket INC-777", final);
        Assert.Contains("macro=ti", r.RequestMessage!.RequestUri!.Query.ToLowerInvariant());   // conserva el filtro

        // Quitar el ticket con motivo: desaparece, la baja vuelve a pendiente y la traza lo registra.
        html = await r.Content.ReadAsStringAsync();
        Assert.Contains("data-anular=\"" + baja.AccesoId + "\"", html);
        r = await client.PostAsync(Accion(html, "Anular"), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["accesoId"] = baja.AccesoId.ToString(), ["ticket"] = "INC-777", ["motivo"] = "Se capturó en otra baja",
            ["__RequestVerificationToken"] = Token(html),
        }));
        final = WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
        Assert.Contains("Ticket INC-777 quitado.", final);
        Assert.DoesNotContain("Ticket INC-777 ", final.Replace("Ticket INC-777 quitado", ""));
        Assert.Contains("Ticket anulado", final);
        Assert.Contains("Se capturó en otra baja", final);
    }
}
