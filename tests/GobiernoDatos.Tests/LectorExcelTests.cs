using ClosedXML.Excel;
using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;

namespace GobiernoDatos.Tests;

public class LectorExcelTests
{
    private static readonly Semana Corte = new(2026, 42);

    // Libro con la estructura del Excel de seguimiento: renglones de grupo, porcentajes numéricos con formato 0%,
    // "Avance" como fórmula, comentario con salto de línea y una hoja previa sin el encabezado.
    internal static MemoryStream LibroDePrueba()
    {
        using var libro = new XLWorkbook();
        libro.AddWorksheet("Resumen").Cell(1, 1).Value = "Hoja sin datos de seguimiento";
        var h = libro.AddWorksheet("Seguimiento");
        h.Cell(1, 5).Value = "Data Product";
        h.Cell(2, 5).Value = "Owner"; h.Cell(2, 8).Value = "Funcional"; h.Cell(2, 10).Value = "Owner-Funcional";
        string[] enc = ["Gobierno", "Data Product", "Avance", "Vistas", "Glosarios", "Catalogado", "DF", "Arq.", "Linaje", "Perfil",
                        "Reglas de Calidad", "IA", "Comunicado", "ID Regla Cal", "Comentarios", "HALLAZGO", "Glosarios", "Catalogado"];
        for (int c = 0; c < enc.Length; c++) h.Cell(3, c + 1).Value = enc[c];

        void Fila(int r, string gob, string dp, int? vistas, double?[] pct, string comentario)
        {
            h.Cell(r, 1).Value = gob; h.Cell(r, 2).Value = dp;
            h.Cell(r, 3).FormulaA1 = $"AVERAGE(E{r}:K{r})"; h.Cell(r, 3).Style.NumberFormat.Format = "0%";
            if (vistas is not null) h.Cell(r, 4).Value = vistas.Value;
            for (int i = 0; i < pct.Length; i++)
                if (pct[i] is double v) { h.Cell(r, 5 + i).Value = v; h.Cell(r, 5 + i).Style.NumberFormat.Format = "0%"; }
            h.Cell(r, 15).Value = comentario;
            h.Cell(r, 17).Value = new DateTime(2026, 4, 21); h.Cell(r, 17).Style.NumberFormat.Format = "d-mmm";
        }
        Fila(4, "CDV", "DP Uno", 4, [1, 1, 1, 1, 1, 1, 1], "42/26 - Se cierra catalogado. 41/26 Se corrige perfilado");
        Fila(5, "Finanzas", "DP Dos", null, [0.7, null, 1, 0.45, null, 0.99, null], "35/26* - Sesión con usuario.\n40/26 - Línea en otro renglón.");
        var ms = new MemoryStream();
        libro.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Lee_celdas_como_se_ven_en_Excel_y_elige_la_hoja_con_el_encabezado()
    {
        var filas = LectorExcel.LeerFilas(LibroDePrueba(), "Data Product");
        Assert.Contains(filas, f => f.Contains("Gobierno"));
        var uno = filas.First(f => f.Count > 1 && f[1] == "DP Uno");
        Assert.Equal("100%", uno[4]);                         // 1 con formato 0% → "100%"
        Assert.Equal("4", uno[3]);

        var l = ImportacionSeguimiento.Leer(filas, Corte);
        Assert.Empty(l.Advertencias);
        Assert.Equal(["DP Uno", "DP Dos"], l.Filas.Select(f => f.DataProduct));
        Assert.All(l.Filas[0].Rubros.Values, v => Assert.Equal(100, v));
        Assert.Equal(new Dictionary<int, decimal> { [1] = 70, [3] = 100, [4] = 45, [6] = 99 }, l.Filas[1].Rubros);
        Assert.Equal([42, 41], l.Filas[0].Notas.Select(n => n.Semana.Numero));
        Assert.Equal([35, 40], l.Filas[1].Notas.Select(n => n.Semana.Numero));
    }

    [Fact]
    public void Un_archivo_que_no_es_Excel_da_un_mensaje_claro()
    {
        var ex = Assert.Throws<DomainException>(() => LectorExcel.LeerFilas(new MemoryStream("Gobierno,Data Product"u8.ToArray()), "Data Product"));
        Assert.Contains(".xlsx", ex.Message);
    }

    // El archivo real del usuario: el .xlsx debe dar lo mismo que su versión CSV.
    [SkippableFact]
    public void El_xlsx_real_coincide_con_su_version_csv()
    {
        var descargas = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        string xlsx = Path.Combine(descargas, "Gobierno.xlsx"), csv = Path.Combine(descargas, "Gobierno.csv");
        Skip.IfNot(File.Exists(xlsx) && File.Exists(csv), "Faltan Downloads/Gobierno.xlsx o Gobierno.csv.");

        using var archivo = new FileStream(xlsx, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);   // puede estar abierto en Excel
        var deExcel = ImportacionSeguimiento.Leer(LectorExcel.LeerFilas(archivo, "Data Product"), Corte);
        var deCsv = ImportacionSeguimiento.Leer(Csv.LeerFilas(Csv.LeerTexto(ArchivoCompartido.Leer(csv))), Corte);

        Assert.Equal(deCsv.Filas.Select(f => (f.Gobierno, f.DataProduct, f.Vistas)), deExcel.Filas.Select(f => (f.Gobierno, f.DataProduct, f.Vistas)));
        foreach (var (e, c) in deExcel.Filas.Zip(deCsv.Filas))
        {
            Assert.Equal(c.Rubros.OrderBy(x => x.Key), e.Rubros.OrderBy(x => x.Key));
            Assert.Equal(c.Notas.Select(n => n.Semana), e.Notas.Select(n => n.Semana));
        }
    }
}

// Lee un archivo aunque esté abierto en Excel (que lo bloquea para escritura).
internal static class ArchivoCompartido
{
    public static byte[] Leer(string ruta)
    {
        using var f = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new MemoryStream();
        f.CopyTo(ms);
        return ms.ToArray();
    }
}
