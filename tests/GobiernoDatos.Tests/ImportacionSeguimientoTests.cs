using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;

namespace GobiernoDatos.Tests;

public class ImportacionSeguimientoTests
{
    private static readonly Semana Corte = new(2026, 42);

    private static LecturaSeguimiento Leer(string ruta) =>
        ImportacionSeguimiento.Leer(Csv.LeerFilas(Csv.LeerTexto(ArchivoCompartido.Leer(ruta))), Corte);

    private static string Fixture() => Path.Combine(AppContext.BaseDirectory, "Datos", "seguimiento_prueba.csv");

    [Fact]
    public void Lee_encabezado_con_grupos_y_toma_la_primera_aparicion_de_cada_columna()
    {
        var l = Leer(Fixture());
        Assert.Equal(["DP Uno", "DP Dos", "DP Tres", "DP Cuatro"], l.Filas.Select(f => f.DataProduct));

        var uno = l.Filas[0];
        Assert.Equal("CDV", uno.Gobierno);
        Assert.Equal(4, uno.Vistas);
        Assert.Equal(7, uno.Rubros.Count);
        Assert.All(uno.Rubros.Values, v => Assert.Equal(100, v));

        var dos = l.Filas[1];   // la sección de fechas (21-Apr…) no se confunde con los rubros
        Assert.Null(dos.Vistas);
        Assert.Equal(new Dictionary<int, decimal> { [1] = 70, [3] = 100, [4] = 45, [6] = 99 }, dos.Rubros);
        Assert.Empty(l.Filas[2].Rubros);   // sin datos: no registra movimientos
    }

    [Fact]
    public void Advierte_filas_y_valores_invalidos_sin_detener_la_importacion()
    {
        var l = Leer(Fixture());
        Assert.Contains(l.Advertencias, a => a.Contains("falta gobierno"));
        Assert.Contains(l.Advertencias, a => a.Contains("«abc»"));
        Assert.Equal(new Dictionary<int, decimal> { [2] = 50 }, l.Filas[3].Rubros);
    }

    [Fact]
    public void Separa_comentarios_en_notas_por_semana()
    {
        var l = Leer(Fixture());
        var uno = l.Filas[0].Notas;
        Assert.Equal([42, 41, 29], uno.Select(n => n.Semana.Numero));
        Assert.Equal("Se cierra catalogado", uno[0].Texto);
        Assert.Contains("38/52 - texto con marca mal escrita", uno[1].Texto);   // marca inválida: queda como texto
        Assert.Equal("Se crea DP", uno[2].Texto);

        var dos = l.Filas[1].Notas;   // 35/26* y salto de línea dentro del campo; 27/27 es de otro año
        Assert.Equal([35, 40], dos.Select(n => n.Semana.Numero));
        Assert.Contains("27/27 - marca de otro año", dos[1].Texto);

        Assert.Equal(Corte, l.Filas[3].Notas.Single().Semana);   // sin marca: semana de corte
    }

    [Theory]
    [InlineData("100%", 100)]
    [InlineData("99 %", 99)]
    [InlineData("100", 100)]
    [InlineData("0.86", 86)]
    [InlineData("0%", 0)]
    public void Porcentajes(string texto, int esperado) => Assert.Equal(esperado, ImportacionSeguimiento.Porcentaje(texto));

    [Theory]
    [InlineData("120%")]
    [InlineData("-5")]
    [InlineData("21-Apr")]
    public void Porcentajes_invalidos(string texto) => Assert.Null(ImportacionSeguimiento.Porcentaje(texto));

    // El archivo real que compartió el usuario (si está en Descargas).
    [SkippableFact]
    public void Archivo_real_de_seguimiento()
    {
        var ruta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Gobierno.csv");
        Skip.IfNot(File.Exists(ruta), "No está Downloads/Gobierno.csv.");
        var l = Leer(ruta);
        Assert.True(l.Filas.Count >= 50);
        var transitos = l.Filas.Single(f => f.DataProduct == "MOM - Tránsitos - Entrega Entrante");
        Assert.Equal("CDV", transitos.Gobierno);
        Assert.Equal(4, transitos.Vistas);
        Assert.Equal(100, transitos.Rubros[1]);
        Assert.False(transitos.Rubros.ContainsKey(4));   // Arq. vacía
        Assert.Contains(transitos.Notas, n => n.Semana.Numero == 42);
        Assert.Contains(transitos.Notas, n => n.Semana.Numero == 24);
    }
}
