using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GobiernoDatos.Tests;

// Pruebas de punta a punta: el portal completo en memoria (fuente "Muestras"), usando los formularios tal como
// los genera Razor. Entorno "Testing": no carga user-secrets, así que nunca toca la base real.
public class PortalTests(PortalTests.Fabrica fabrica) : IClassFixture<PortalTests.Fabrica>
{
    public sealed class Fabrica : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");
    }

    private static string Atributo(string html, string formId, string atributo)
    {
        var form = Regex.Match(html, $"<form[^>]*id=\"{formId}\"[^>]*>").Value;
        return WebUtility.HtmlDecode(Regex.Match(form, $"{atributo}=\"([^\"]*)\"").Groups[1].Value);
    }

    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    [Fact]
    public async Task Los_formularios_del_detalle_conservan_el_id_del_data_product()
    {
        var client = fabrica.CreateClient();
        var lista = await client.GetStringAsync("/DataProducts");
        var detalle = WebUtility.HtmlDecode(Regex.Match(lista, "href=\"(/DataProducts/Detalle\\?id=\\d+)\"").Groups[1].Value);
        var id = Regex.Match(detalle, @"id=(\d+)").Groups[1].Value;

        var html = await client.GetStringAsync(detalle);
        var acciones = Regex.Matches(html, "<form[^>]*>").Select(m => m.Value)
            .Where(f => f.Contains("method=\"post\""))
            .Select(f => WebUtility.HtmlDecode(Regex.Match(f, "action=\"([^\"]*)\"").Groups[1].Value)).ToList();
        Assert.Equal(3, acciones.Count);                                      // cerrar, nota, registrar avance
        Assert.All(acciones, a => Assert.Matches($@"[?&]id={id}(&|$)", a));
    }

    [Fact]
    public async Task Registrar_avance_desde_el_formulario_guarda_y_vuelve_al_mismo_data_product()
    {
        var client = fabrica.CreateClient();
        var lista = await client.GetStringAsync("/DataProducts");
        var detalle = WebUtility.HtmlDecode(Regex.Match(lista, "href=\"(/DataProducts/Detalle\\?id=\\d+)\"").Groups[1].Value);
        var html = await client.GetStringAsync(detalle);

        var respuesta = await client.PostAsync(Atributo(html, "form-avance", "action"), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Form.Nivel"] = "DP", ["Form.RubroId"] = "4", ["Form.Estado"] = "EN_CURSO", ["Form.Pct"] = "40",
            ["Form.Nota"] = "Prueba de punta a punta", ["__RequestVerificationToken"] = Token(html),
        }));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);                // sigue la redirección al detalle
        var final = await respuesta.Content.ReadAsStringAsync();
        Assert.Contains("Movimiento registrado.", final);
        Assert.Contains("Prueba de punta a punta", final);
        Assert.Matches(@"[?&][Ii]d=" + Regex.Match(detalle, @"id=(\d+)").Groups[1].Value, respuesta.RequestMessage!.RequestUri!.Query);

        // Vuelve con el mismo rubro seleccionado y su valor vigente disponible para precargar el formulario.
        var opcion = Regex.Match(final, "<option[^>]*value=\"4\"[^>]*>").Value;
        Assert.Contains("selected=\"selected\"", opcion);
        Assert.Contains("data-pct=\"40\"", opcion);
        Assert.Contains("data-estado=\"EN_CURSO\"", opcion);
    }

    [Fact]
    public async Task Marcar_pendientes_como_continua_acepta_miles_de_accesos_en_un_solo_campo()
    {
        var client = fabrica.CreateClient();
        var html = await client.GetStringAsync("/Recertificacion/Mi");
        var ids = Regex.Matches(html, @"data-acceso=""(\d+)""").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(ids);

        // Más de 1,024 valores (límite de campos de formulario): repetidos, el servicio los registra una sola vez.
        var muchos = string.Join(",", Enumerable.Repeat(ids, 400).SelectMany(x => x));
        var form = Regex.Match(html, "<form[^>]*id=\"form-masivo\"[^>]*>").Value;
        var accion = WebUtility.HtmlDecode(Regex.Match(form, "action=\"([^\"]*)\"").Groups[1].Value);
        var respuesta = await client.PostAsync(accion, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ids"] = muchos, ["__RequestVerificationToken"] = Token(html),
        }));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var final = await respuesta.Content.ReadAsStringAsync();
        Assert.Contains($"Se registró «Continúa» en {ids.Count} accesos pendientes.", WebUtility.HtmlDecode(final));
    }

    [Fact]
    public async Task Cargar_archivo_desde_seguimiento_semanal_separa_comentarios_por_semana()
    {
        var client = fabrica.CreateClient();
        var html = await client.GetStringAsync("/");
        var form = Regex.Match(html, "<form[^>]*id=\"form-cargar\"[^>]*>").Value;
        var accion = WebUtility.HtmlDecode(Regex.Match(form, "action=\"([^\"]*)\"").Groups[1].Value);

        using var contenido = new MultipartFormDataContent
        {
            { new StringContent("202642"), "semanaCorte" },
            { new StringContent(Token(html)), "__RequestVerificationToken" },
            { new ByteArrayContent(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Datos", "seguimiento_prueba.csv"))), "archivo", "seguimiento.csv" },
        };
        var respuesta = await client.PostAsync(accion, contenido);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Contains("semana=202642", respuesta.RequestMessage!.RequestUri!.Query);
        var final = WebUtility.HtmlDecode(await respuesta.Content.ReadAsStringAsync());
        Assert.Matches(@"Archivo cargado \(42/26\): \d+ data products nuevos", final);   // otras pruebas pueden haber creado algunos

        // Fila de "DP Uno": nota de la semana y desplegable con las 3 semanas (42/26, 41/26, 29/26).
        var fila = Regex.Match(final, "<tr data-texto=\"DP Uno[^\"]*\">.*?</tr>", RegexOptions.Singleline).Value;
        Assert.Contains("Se cierra catalogado", fila);
        Assert.Contains("Comentarios por semana (3)", fila);
        Assert.Matches(@"42/26</span>\s*Se cierra catalogado", fila);
        Assert.Matches(@"29/26</span>\s*Se crea DP", fila);
    }

    [Fact]
    public async Task Cargar_archivo_acepta_xlsx_y_rechaza_otros_formatos()
    {
        var client = fabrica.CreateClient();
        var html = await client.GetStringAsync("/");
        var form = Regex.Match(html, "<form[^>]*id=\"form-cargar\"[^>]*>").Value;
        var accion = WebUtility.HtmlDecode(Regex.Match(form, "action=\"([^\"]*)\"").Groups[1].Value);

        async Task<string> Subir(byte[] contenido, string nombre)
        {
            using var datos = new MultipartFormDataContent
            {
                { new StringContent("202642"), "semanaCorte" },
                { new StringContent(Token(html)), "__RequestVerificationToken" },
                { new ByteArrayContent(contenido), "archivo", nombre },
            };
            var r = await client.PostAsync(accion, datos);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
        }

        var final = await Subir(LectorExcelTests.LibroDePrueba().ToArray(), "Gobierno.xlsx");
        Assert.Contains("Archivo cargado (42/26):", final);
        Assert.Matches(@"<tr data-texto=""DP Uno[^""]*"">.*?Comentarios por semana \(2\)", Regex.Replace(final, @"\s+", " "));

        var error = await Subir("no es excel"u8.ToArray(), "Gobierno.xls");
        Assert.Contains("El archivo debe ser de Excel (.xlsx)", error);
    }

    [Fact]
    public async Task Auditoria_muestra_los_accesos_y_exporta_a_excel_completo_y_por_macro_area()
    {
        var client = fabrica.CreateClient();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/Auditoria?evento=0"));
        Assert.Contains("<h1>Auditoría</h1>", html);
        var filasGrid = Regex.Matches(html, "<tbody>(.*?)</tbody>", RegexOptions.Singleline)[0].Groups[1].Value;
        Assert.True(Regex.Matches(filasGrid, "<tr>").Count > 0);
        var macros = Regex.Matches(Regex.Match(html, "<select name=\"macro\".*?</select>", RegexOptions.Singleline).Value, "<option value=\"([^\"]+)\"")
                          .Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(macros);

        // Excel completo: un renglón por acceso con los mismos encabezados que la evidencia.
        var r = await client.GetAsync("/Auditoria?handler=Exportar&evento=0");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", r.Content.Headers.ContentType!.MediaType);
        using var libro = new ClosedXML.Excel.XLWorkbook(new MemoryStream(await r.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheet(1);
        Assert.Equal(GobiernoDatos.Web.Pages.Auditoria.IndexModel.ColumnasHana, Enumerable.Range(1, 9).Select(i => hoja.Cell(1, i).GetString()));
        Assert.All(Enumerable.Range(2, hoja.LastRowUsed()!.RowNumber() - 1), i => Assert.Equal("", hoja.Cell(i, 8).GetString()));   // OKBAJA vacía
        var total = hoja.LastRowUsed()!.RowNumber() - 1;
        Assert.Contains($"de {total:N0} accesos", html);

        // Por macro área: un .xlsx por cada una dentro de un .zip; juntos suman lo mismo que el completo.
        r = await client.GetAsync("/Auditoria?handler=ExportarPorMacro&evento=0");
        Assert.Equal("application/zip", r.Content.Headers.ContentType!.MediaType);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await r.Content.ReadAsByteArrayAsync()));
        Assert.Equal(macros.Count, zip.Entries.Count);
        var suma = 0;
        foreach (var e in zip.Entries)
        {
            using var s = new MemoryStream();
            using (var z = e.Open()) z.CopyTo(s);
            using var l = new ClosedXML.Excel.XLWorkbook(s);
            var h = l.Worksheet(1);
            var filas = h.LastRowUsed()!.RowNumber() - 1;
            Assert.All(Enumerable.Range(2, filas), i => Assert.Equal(h.Cell(2, 7).GetString(), h.Cell(i, 7).GetString()));   // un solo AREARESPONSABLE
            suma += filas;
        }
        Assert.Equal(total, suma);

        Assert.Contains("<th>OKBAJA</th>", html);
        Assert.DoesNotContain("<th>COMENTARIOS</th>", html);
        Assert.DoesNotContain("name=\"decision\"", html);
        Assert.DoesNotContain("class=\"kpis\"", html);
        Assert.DoesNotContain("<th>GOBIERNO</th>", html);
        Assert.DoesNotContain("<th>RESPONSABLE</th>", html);
    }

    [Fact]
    public async Task Editar_y_eliminar_data_product_desde_seguimiento_semanal()
    {
        var client = fabrica.CreateClient();
        var html = await client.GetStringAsync("/?semana=202642");
        var cargar = WebUtility.HtmlDecode(Regex.Match(Regex.Match(html, "<form[^>]*id=\"form-cargar\"[^>]*>").Value, "action=\"([^\"]*)\"").Groups[1].Value);
        using (var datos = new MultipartFormDataContent
        {
            { new StringContent("202642"), "semanaCorte" },
            { new StringContent(Token(html)), "__RequestVerificationToken" },
            { new ByteArrayContent(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Datos", "seguimiento_prueba.csv"))), "archivo", "seguimiento.csv" },
        })
            await client.PostAsync(cargar, datos);

        html = await client.GetStringAsync("/?semana=202642");
        var boton = Regex.Match(html, "<button[^>]*data-editar-dp=\"(\\d+)\"[^>]*data-nombre=\"DP Cuatro\"");
        Assert.True(boton.Success);
        var id = boton.Groups[1].Value;

        // Editar: nombre, gobierno y responsable; los avances se conservan.
        var editar = Atributo(html, "form-editar", "action");
        Assert.Contains("handler=Editar", editar);
        var r = await client.PostAsync(editar, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["dpId"] = id, ["nombre"] = "DP Cuatro Renombrado", ["gobierno"] = "CDV", ["responsable"] = "MABENET\\prueba",
            ["__RequestVerificationToken"] = Token(html),
        }));
        var final = WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
        Assert.Contains("Data product «DP Cuatro Renombrado» actualizado.", final);
        Assert.Contains("semana=202642", r.RequestMessage!.RequestUri!.Query);
        Assert.Matches(@"<tr data-texto=""DP Cuatro Renombrado CDV"">", final);

        // El diálogo pide confirmación antes de guardar y cada eliminación antes de enviar.
        Assert.Contains("data-confirmar=\"¿Guardar los cambios del data product?\"", final);
        var fila = Regex.Match(final, "<tr data-texto=\"DP Cuatro Renombrado[^\"]*\">.*?</tr>", RegexOptions.Singleline).Value;
        Assert.Contains("data-confirmar=\"¿Eliminar el data product «DP Cuatro Renombrado»?", fila);

        // Nombre duplicado: se rechaza.
        r = await client.PostAsync(editar, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["dpId"] = id, ["nombre"] = "DP Uno", ["gobierno"] = "CDV", ["__RequestVerificationToken"] = Token(html),
        }));
        Assert.Contains("Ya existe un data product con ese nombre.", WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync()));

        // Eliminar: el DP y sus avances y notas.
        var eliminar = WebUtility.HtmlDecode(Regex.Match(Regex.Match(fila, "<form[^>]*handler=Eliminar[^>]*>").Value, "action=\"([^\"]*)\"").Groups[1].Value);
        r = await client.PostAsync(eliminar, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["dpId"] = id, ["__RequestVerificationToken"] = Token(html),
        }));
        final = WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
        Assert.Matches(@"Data product «DP Cuatro Renombrado» eliminado junto con \d+ avances, \d+ comentarios", final);
        Assert.DoesNotContain("<tr data-texto=\"DP Cuatro Renombrado", final);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/DataProducts/Detalle?id={id}")).StatusCode);
    }
}

// Portal propio: las demás pruebas de PortalTests deciden pendientes del evento de muestra.
public class RecordatoriosPortalTests(PortalTests.Fabrica fabrica) : IClassFixture<PortalTests.Fabrica>
{
    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    [Fact]
    public async Task Recordar_envia_un_correo_por_certificador_y_queda_en_la_bitacora()
    {
        var client = fabrica.CreateClient();
        var correos = (EnviadorSimulado)fabrica.Services.GetService(typeof(IEnviadorCorreo))!;
        var antes = correos.Enviados.Count;
        var html = await client.GetStringAsync("/Recertificacion/Eventos");
        var form = WebUtility.HtmlDecode(Regex.Matches(html, "<form[^>]*>").Select(m => m.Value)
            .First(f => f.Contains("handler=Recordar") && !f.Contains("class=\"inline\"")));
        Assert.Contains("data-confirmar=\"Se enviará un correo a cada certificador", form);   // pide confirmación
        var accion = Regex.Match(form, "action=\"([^\"]*)\"").Groups[1].Value;

        var r = await client.PostAsync(accion, new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = Token(html) }));
        var final = WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
        var enviados = correos.Enviados.Skip(antes).ToList();
        Assert.NotEmpty(enviados);
        Assert.Contains($"Recordatorios: {enviados.Count} {(enviados.Count == 1 ? "correo enviado" : "correos enviados")}.", final);
        Assert.All(enviados, m => Assert.Contains("accesos pendientes", m.CuerpoHtml.Replace("acceso pendiente<", "accesos pendientes<")));
        Assert.Contains("Recordatorios enviados", final);
        Assert.Contains(">Enviado<", final);

        // Correo de prueba al propio administrador.
        html = await client.GetStringAsync("/Recertificacion/Eventos");
        var prueba = WebUtility.HtmlDecode(Regex.Match(Regex.Match(html, "<form[^>]*handler=ProbarCorreo[^>]*>").Value, "action=\"([^\"]*)\"").Groups[1].Value);
        r = await client.PostAsync(prueba, new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = Token(html) }));
        Assert.Contains("Correo de prueba enviado a demo@ejemplo.local.", WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync()));
        Assert.Equal("demo@ejemplo.local", correos.Enviados[^1].Para);
    }
}
