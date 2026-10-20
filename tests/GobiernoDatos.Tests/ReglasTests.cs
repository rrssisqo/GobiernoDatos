using GobiernoDatos.Web.Domain;

namespace GobiernoDatos.Tests;

public class ReglasTests
{
    [Theory]
    [InlineData("\"_SYS_BIC\".\"MABE.INDUSTRIAL.OEE/CONT_IND_OEE_PARADAS\"", "MABE.INDUSTRIAL.OEE/CONT_IND_OEE_PARADAS")]
    [InlineData("MABE.AGENERICO.GENERICO/dm_material", "MABE.AGENERICO.GENERICO/DM_MATERIAL")]
    [InlineData("  _SYS_BIC.MABE.CSC/X  ", "MABE.CSC/X")]
    public void Ruta_tecnica_se_normaliza(string entrada, string esperada) =>
        Assert.Equal(esperada, RutaTecnica.Normalizar(entrada));

    [Theory]
    [InlineData(EstadoAvance.NO_INICIADO, 50, 0)]
    [InlineData(EstadoAvance.CERRADO, null, 100)]
    [InlineData(EstadoAvance.NO_APLICA, 10, 100)]
    [InlineData(EstadoAvance.EN_CURSO, 75, 75)]
    public void Estado_y_pct_siempre_coherentes(EstadoAvance estado, int? pct, int esperado) =>
        Assert.Equal(esperado, Avance.NormalizarPct(estado, pct));

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(100)]
    public void En_curso_exige_pct_entre_1_y_99(int? pct) =>
        Assert.Throws<DomainException>(() => Avance.NormalizarPct(EstadoAvance.EN_CURSO, pct));

    [Fact]
    public void Baja_sin_comentario_se_rechaza()
    {
        var ex = Assert.Throws<DomainException>(() => ReglasRecertificacion.ValidarDecision(EstatusEvento.ABIERTO, Decision.BAJA, "  "));
        Assert.Contains("comentario", ex.Message);
    }

    [Fact]
    public void Continua_no_requiere_comentario() =>
        ReglasRecertificacion.ValidarDecision(EstatusEvento.ABIERTO, Decision.CONTINUA, null);

    [Theory]
    [InlineData(EstatusEvento.CERRADO)]
    [InlineData(EstatusEvento.BORRADOR)]
    public void Solo_se_decide_con_el_evento_abierto(EstatusEvento estatus) =>
        Assert.Throws<DomainException>(() => ReglasRecertificacion.ValidarDecision(estatus, Decision.CONTINUA, null));

    [Fact]
    public void Evento_con_rechazos_no_abre() =>
        Assert.Throws<DomainException>(() => ReglasRecertificacion.ValidarApertura(EstatusEvento.BORRADOR, 2, 100));

    [Fact]
    public void Semana_iso_con_etiqueta_del_excel()
    {
        var s = Semana.De(new DateOnly(2026, 9, 24));
        Assert.Equal("39/26", s.Etiqueta);
        Assert.Equal(new DateOnly(2026, 9, 21), s.Inicio);
        Assert.Equal(new DateOnly(2026, 9, 27), s.Fin);
    }

    [Theory]
    [InlineData(@"MABENET\rrsmexti", @"MABENET\rrsmexti")]
    [InlineData(@"mabenet\RRSMEXTI", @"MABENET\rrsmexti")]
    [InlineData("rrsmexti", @"MABENET\rrsmexti")]
    [InlineData(@"  OTRO\j.perez ", @"OTRO\j.perez")]
    public void Usuario_de_red_se_normaliza(string entrada, string esperado) =>
        Assert.Equal(esperado, UsuarioRed.Normalizar(entrada, "MABENET"));

    [Theory]
    [InlineData("rrsmexti@mabe.com.mx")]
    [InlineData(@"A\B\C")]
    [InlineData("Rojas, Ruben (MEX, EX, TI)")]
    [InlineData("")]
    public void Usuario_de_red_invalido_se_rechaza(string entrada) =>
        Assert.Throws<DomainException>(() => UsuarioRed.Normalizar(entrada, "MABENET"));
}
