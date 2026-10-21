using GobiernoDatos.Web.Domain;

namespace GobiernoDatos.Tests;

public class DefinicionReglaTests
{
    private static DatosRegla Regla(string codigo = "rl_4_1", string descripcion = "Gestión de Materiales", string[]? destinatarios = null,
                                    string? dimension = "VALIDEZ", int etapa = 1) =>
        new(codigo, " Nulos ", descripcion, "  ", null, dimension, "Datos Maestros", null, "MATL_TYPE", ["PMATERIAL"], null, null, 7,
            null, null, true, null, destinatarios ?? [], etapa);

    [Fact]
    public void Normaliza_codigo_y_textos()
    {
        var r = DefinicionRegla.Normalizar(Regla());
        Assert.Equal("RL_4_1", r.Codigo);
        Assert.Equal("Nulos", r.TipoRegla);
        Assert.Null(r.ReglaFuncional);                                   // solo espacios → nulo
    }

    [Theory]
    [InlineData("RL 4", "Gestión", "VALIDEZ", 1, "solo admite letras")]
    [InlineData("", "Gestión", "VALIDEZ", 1, "código")]
    [InlineData("RL_4_1", " ", "VALIDEZ", 1, "descripción")]
    [InlineData("RL_4_1", "Gestión", "OTRA", 1, "dimensión")]
    [InlineData("RL_4_1", "Gestión", "VALIDEZ", 9, "etapa")]
    public void Rechaza_datos_invalidos(string codigo, string descripcion, string dimension, int etapa, string mensaje)
    {
        var ex = Assert.Throws<DomainException>(() => DefinicionRegla.Normalizar(Regla(codigo, descripcion, dimension: dimension, etapa: etapa)));
        Assert.Contains(mensaje, ex.Message);
    }

    [Fact]
    public void Valida_destinatarios_y_separa_listas()
    {
        Assert.Throws<DomainException>(() => DefinicionRegla.Normalizar(Regla(destinatarios: ["no es correo"])));
        Assert.Equal(["PMATERIAL", "ZMM_O09"], DefinicionRegla.Lista(" pmaterial; zmm_o09 ;PMATERIAL", mayusculas: true));
        Assert.Equal(["a@mabe.com.mx", "b@mabe.com.mx"], DefinicionRegla.Lista("a@mabe.com.mx, b@mabe.com.mx", mayusculas: false));
    }
}
