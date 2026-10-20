using GobiernoDatos.Web.Domain;

namespace GobiernoDatos.Tests;

// Regla "el último evento manda" (migración 005; sustituye la opción C). Los casos de la opción C que siguen
// vigentes se conservan; los nuevos cubren capturas fuera del rango de etapas.
public class ReglaAvanceVistaTests
{
    private static readonly Rubro Glosario = Catalogos.Rubro(1);   // etapas 3–6
    private static readonly Rubro Linaje = Catalogos.Rubro(5);     // etapas 7–8
    private static readonly DateOnly D0 = new(2026, 9, 1);

    private static MovEtapaRef E(int id, int dia, int orden) => new(id, D0.AddDays(dia), orden);
    private static CapturaRef C(int id, int dia, decimal pct, EstadoAvance estado = EstadoAvance.EN_CURSO) => new(id, D0.AddDays(dia), pct, estado);
    private static ResultadoRubro Calc(Rubro r, MovEtapaRef[] etapas, CapturaRef[]? caps = null, int corte = 30) =>
        ReglaAvanceVista.Calcular(r, etapas, caps ?? [], D0.AddDays(corte));

    [Fact]
    public void Nunca_entro_y_no_lo_rebasa_es_0_no_iniciado()
    {
        var r = Calc(Glosario, [E(1, 0, 0), E(2, 1, 2)]);
        Assert.Equal(new ResultadoRubro(0, EstadoAvance.NO_INICIADO, "ETAPA"), r);
    }

    [Fact]
    public void Nunca_entro_pero_salto_el_rango_es_100_cerrado()
    {
        var r = Calc(Glosario, [E(1, 0, 2), E(2, 1, 8)]);
        Assert.Equal(new ResultadoRubro(100, EstadoAvance.CERRADO, "ETAPA"), r);
    }

    [Fact]
    public void Entro_y_avanzo_mas_alla_es_100_aunque_la_captura_fuera_menor()
    {
        var r = Calc(Glosario, [E(1, 0, 3), E(2, 5, 7)], [C(10, 2, 40)]);
        Assert.Equal(100, r.Pct);
        Assert.Equal(EstadoAvance.CERRADO, r.Estado);
    }

    [Fact]
    public void Dentro_del_rango_toma_el_ultimo_pct_capturado_desde_la_entrada()
    {
        var r = Calc(Glosario, [E(1, 0, 3), E(2, 4, 4)], [C(10, 1, 20), C(11, 5, 60)]);
        Assert.Equal(new ResultadoRubro(60, EstadoAvance.EN_CURSO, "CAPTURA"), r);
    }

    [Fact]
    public void Dentro_del_rango_sin_captura_es_0_en_curso()
    {
        var r = Calc(Linaje, [E(1, 0, 7)]);
        Assert.Equal(new ResultadoRubro(0, EstadoAvance.EN_CURSO, "ETAPA"), r);
    }

    [Fact]
    public void Capturas_previas_a_la_ultima_entrada_no_cuentan()
    {
        // Entró a Glosario, capturó 80, avanzó a Linaje y regresó a Reescaneo (reapertura): arranca sin captura.
        var r = Calc(Glosario, [E(1, 0, 3), E(2, 3, 7), E(3, 6, 5)], [C(10, 2, 80)]);
        Assert.Equal(0, r.Pct);
        Assert.Equal(EstadoAvance.EN_CURSO, r.Estado);
    }

    [Fact]
    public void Regresar_antes_del_rango_tras_rebasarlo_no_reabre_el_rubro()
    {
        // Solo se reabre si vuelve a entrar a una etapa de su rango.
        var r = Calc(Glosario, [E(1, 0, 4), E(2, 3, 8), E(3, 6, 1)]);
        Assert.Equal(100, r.Pct);
        Assert.Equal(EstadoAvance.CERRADO, r.Estado);
    }

    [Fact]
    public void Regresar_antes_del_rango_sin_haberlo_rebasado_conserva_la_captura()
    {
        var r = Calc(Glosario, [E(1, 0, 4), E(2, 3, 1)], [C(10, 1, 30)]);
        Assert.Equal(new ResultadoRubro(30, EstadoAvance.EN_CURSO, "CAPTURA"), r);
    }

    [Fact]
    public void El_corte_ignora_movimientos_posteriores()
    {
        var r = Calc(Glosario, [E(1, 0, 3), E(2, 20, 7)], [C(10, 2, 45)], corte: 10);
        Assert.Equal(45, r.Pct);
    }

    [Fact]
    public void Mismo_dia_desempata_por_id_de_movimiento()
    {
        var r = Calc(Glosario, [E(2, 0, 7), E(1, 0, 3)]);  // la entrada (id 1) es anterior al avance (id 2)
        Assert.Equal(EstadoAvance.CERRADO, r.Estado);
    }

    [Fact]
    public void Rubro_de_nivel_dp_no_aplica()
    {
        Assert.Throws<ArgumentException>(() => ReglaAvanceVista.Calcular(Catalogos.Rubro(2), [], [], D0));
    }

    // ---- Regla nueva: se captura en cualquier etapa ----

    [Fact]
    public void Captura_antes_de_entrar_al_rango_cuenta()
    {
        var r = Calc(Linaje, [E(1, 0, 3)], [C(10, 1, 50)]);
        Assert.Equal(new ResultadoRubro(50, EstadoAvance.EN_CURSO, "CAPTURA"), r);
    }

    [Fact]
    public void Entrar_al_rango_por_primera_vez_no_borra_la_captura_previa()
    {
        var r = Calc(Linaje, [E(1, 0, 3), E(3, 2, 7)], [C(2, 1, 50)]);
        Assert.Equal(50, r.Pct);
    }

    [Fact]
    public void Captura_despues_de_rebasar_el_rango_cuenta()
    {
        var r = Calc(Glosario, [E(1, 0, 3), E(2, 2, 8)], [C(10, 5, 70)]);
        Assert.Equal(new ResultadoRubro(70, EstadoAvance.EN_CURSO, "CAPTURA"), r);
    }

    [Fact]
    public void Avanzar_entre_etapas_posteriores_no_borra_la_captura()
    {
        var r = Calc(Linaje, [E(1, 0, 10), E(3, 5, 11)], [C(2, 2, 70)]);   // la captura es posterior al cierre (10) y 10→11 no cruza el rango
        Assert.Equal(70, r.Pct);
    }

    [Fact]
    public void Cruzar_el_rango_despues_de_la_captura_cierra_al_100()
    {
        var r = Calc(Linaje, [E(1, 0, 3), E(3, 4, 9)], [C(2, 1, 50)]);
        Assert.Equal(new ResultadoRubro(100, EstadoAvance.CERRADO, "ETAPA"), r);
    }

    [Fact]
    public void En_empate_de_fecha_y_secuencia_gana_el_orden_de_registro()
    {
        // Cierre (mov 2) y captura (mov 3) el mismo día: la captura se registró después y manda.
        var r = Calc(Glosario, [E(1, 0, 3), E(2, 4, 7)], [C(3, 4, 80)]);
        Assert.Equal(80, r.Pct);
    }
}
