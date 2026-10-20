using GobiernoDatos.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GobiernoDatos.Tests;

// Solo lectura contra HANA. Corre si GD_HANA_TEST tiene la cadena ODBC, p. ej. "DSN=Hana;UID=...;PWD=...".
public class ExtractoHanaTests
{
    [SkippableFact]
    public void Lee_la_vista_de_recertificacion_con_las_columnas_de_la_carga()
    {
        var cadena = Environment.GetEnvironmentVariable("GD_HANA_TEST");
        Skip.If(string.IsNullOrWhiteSpace(cadena), "Define GD_HANA_TEST para probar contra HANA.");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Hana"] = cadena }).Build();

        var filas = new ExtractoHana(config, NullLogger<ExtractoHana>.Instance).Leer();

        Assert.True(filas.Count > 0);
        Assert.All(filas.Take(50), f =>
        {
            foreach (var col in new[] { "USUARIO", "NOMBRE", "ROL", "VISTA", "ACCESO ULTIMOS 3 MESES", "Área Responsable", "Responsable", "AREA", "CONTENT" })
                Assert.True(f.ContainsKey(col), col);
            Assert.True(int.TryParse(f["ACCESO ULTIMOS 3 MESES"], out _));   // nulos → "0"
        });
    }

    [Fact]
    public void Sin_conexion_configurada_avisa_en_vez_de_fallar()
    {
        var config = new ConfigurationBuilder().Build();
        var fuente = new ExtractoHana(config, NullLogger<ExtractoHana>.Instance);
        Assert.False(fuente.Configurada);
        Assert.Throws<GobiernoDatos.Web.Domain.DomainException>(() => fuente.Leer());
    }
}
