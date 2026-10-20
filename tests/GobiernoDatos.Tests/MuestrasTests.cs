using Microsoft.Extensions.Options;
using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;
using GobiernoDatos.Web.Services;

namespace GobiernoDatos.Tests;

// Flujos completos sobre data/muestras con el reloj fijo en la semana 40/26.
public class MuestrasTests
{
    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private const string Demo = "Usuario demo";
    private const string DemoLogin = "demo";          // se normaliza a MABENET\demo
    private readonly GobiernoStore _s;
    private readonly SeguimientoService _seg;
    private readonly VistasService _vistas;
    private readonly CalidadService _calidad;
    private readonly RecertificacionService _recert;

    public MuestrasTests()
    {
        _s = new GobiernoStore(new RelojFijo(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero)));
        var opciones = new GobiernoDatosOptions { UsuarioDemo = new UsuarioDemoOptions { Nombre = Demo, MacroAreas = ["TI"] } };
        MuestrasLoader.Cargar(_s, RutaMuestras(), opciones.UsuarioDemo, opciones.DominioPredeterminado);
        _seg = new(_s); _vistas = new(_s); _calidad = new(_s); _recert = new(_s, new DirectorioSimulado(Options.Create(opciones)), Options.Create(opciones));
    }

    private static string RutaMuestras()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var ruta = Path.Combine(dir.FullName, "data", "muestras");
            if (Directory.Exists(ruta)) return ruta;
        }
        throw new DirectoryNotFoundException("No se encontró data/muestras.");
    }

    private FilaSeguimiento Fila(string dp, Semana? semana = null) =>
        _seg.Tablero(semana ?? new Semana(2026, 39), null).Filas.Single(f => f.DataProduct == dp);

    [Theory]
    [InlineData("MOM - Tránsitos - Entrega Entrante", 78)]
    [InlineData("MOM - Demand Variation", 31)]
    [InlineData("Auxiliar de Inventarios", 100)]
    [InlineData("Importaciones", 57)]
    [InlineData("Costo Tipo", 77)]
    [InlineData("Rotación y Ausentismo", 57)]
    public void Avance_reproduce_el_excel(string dp, int avanceExcel) =>
        Assert.Equal(avanceExcel, Math.Round(Fila(dp).Avance, 0));

    [Fact]
    public void Notas_del_excel_se_separan_por_semana()
    {
        var detalle = _seg.Detalle(Fila("Auxiliar de Inventarios").DpId, new Semana(2026, 39))!;
        Assert.Contains(detalle.Bitacora, b => b.Semana.Etiqueta == "38/26" && b.Detalle.StartsWith("Se confirman linajes"));
        Assert.Contains(detalle.Bitacora, b => b.Semana.Etiqueta == "39/26" && b.Detalle.StartsWith("Se concluye Catalogado"));
    }

    [Fact]
    public void Avance_dp_registrado_hoy_cuenta_en_la_semana_actual_y_no_en_la_anterior()
    {
        var dpId = Fila("Costo Tipo").DpId;
        _seg.RegistrarAvanceDp(dpId, rubroId: 4, EstadoAvance.CERRADO, null, "Se cierra arquitectura", "demo");

        Assert.Equal(50, Fila("Costo Tipo").Rubros.Single(r => r.RubroId == 4).Pct);
        Assert.Equal(100, Fila("Costo Tipo", new Semana(2026, 40)).Rubros.Single(r => r.RubroId == 4).Pct);
    }

    [Fact]
    public void Asignar_vista_reemplaza_el_valor_inicial_del_excel_por_el_calculo_de_vistas()
    {
        var dpId = Fila("Costo Tipo").DpId;
        var vista = _s.Vistas.Single(v => v.NombreNegocio == "Ordenes Trabajo Mantenimiento");   // etapa Completada
        _vistas.Actualizar(vista.VistaId, vista.NombreNegocio, vista.Gobierno, dpId, vista.Responsable, vista.NivelSeguridad, "demo");

        var semana40 = new Semana(2026, 40);
        Assert.All(Fila("Costo Tipo", semana40).Rubros.Where(r => Catalogos.Rubro(r.RubroId).Nivel == NivelRubro.VISTA),
                   r => Assert.Equal(100, r.Pct));
        Assert.Equal(40, Fila("Costo Tipo").Rubros.Single(r => r.RubroId == 1).Pct);   // semana 39 sin cambios
    }

    [Fact]
    public void Se_captura_cualquier_rubro_de_vista_en_cualquier_etapa()
    {
        var dpId = Fila("Importaciones").DpId;
        var vista = _s.Vistas.Single(v => v.NombreNegocio == "Costo de Nomina");     // Certificación Glosarios (6)
        _vistas.Actualizar(vista.VistaId, vista.NombreNegocio, null, dpId, null, null, "demo");

        Assert.Throws<DomainException>(() => _seg.RegistrarAvanceVista(dpId, vista.VistaId, 2, EstadoAvance.EN_CURSO, 50, null, "demo"));  // rubro de DP
        _seg.RegistrarAvanceVista(dpId, vista.VistaId, 1, EstadoAvance.EN_CURSO, 60, null, "demo");   // Glosario (rubro de la etapa)
        _seg.RegistrarAvanceVista(dpId, vista.VistaId, 5, EstadoAvance.EN_CURSO, 50, null, "demo");   // Linaje, antes de su etapa
        Assert.Equal(60, _s.PctRubroVista(vista.VistaId, Catalogos.Rubro(1), _s.Hoy).Pct);
        Assert.Equal(50, _s.PctRubroVista(vista.VistaId, Catalogos.Rubro(5), _s.Hoy).Pct);
    }

    [Fact]
    public void Regreso_de_etapa_exige_motivo()
    {
        var vista = _s.Vistas.Single(v => v.NombreNegocio == "Head Count PBI");       // Reescaneo (5)
        Assert.Throws<DomainException>(() => _vistas.MoverEtapa(vista.VistaId, 3, null, null, "demo"));
        _vistas.MoverEtapa(vista.VistaId, 3, motivoId: 2, "Cambio de estructura", "demo");
        Assert.True(_s.UltimoMovEtapa(vista.VistaId)!.EsRegreso);
    }

    [Fact]
    public void Extracto_sincroniza_inventario_y_uso()
    {
        var inv = _vistas.Inventario(new FiltroVistas(PestanaVistas.Todas, null, null, null));
        var cal = inv.Filas.Single(f => f.Vista == "CONTINENTAL_GENERICO_CALENDARIOFISCAL");
        Assert.Equal(1673 + 1012 + 859, cal.Accesos3m);
        Assert.Equal(3, cal.Usuarios);
        Assert.Equal(7, inv.EnUltimaRecert);
        Assert.Equal(7, inv.Fuera);     // las de SharePoint no vienen en el extracto: se marcan, no se borran
        Assert.Equal(14, inv.Total);
    }

    [Fact]
    public void Certificador_solo_ve_su_macro_area()
    {
        var mi = _recert.MisAccesos(DemoLogin);
        Assert.Equal(4, mi.Accesos.Count);
        Assert.All(mi.Accesos, a => Assert.Equal("TI", a.MacroArea));
        Assert.Empty(_recert.MisAccesos("Otra persona").Accesos);

        var ajeno = _s.Accesos.First(a => _s.MacroAreas.Single(m => m.MacroAreaId == a.MacroAreaId).Nombre != "TI");
        Assert.Throws<DomainException>(() => _recert.Decidir(ajeno.AccesoId, Decision.CONTINUA, null, DemoLogin));
    }

    [Fact]
    public void Flujo_completo_de_recertificacion_con_baja_ejecutada()
    {
        var accesos = _recert.MisAccesos(DemoLogin).Accesos;
        var baja = accesos[0];

        Assert.Throws<DomainException>(() => _recert.Decidir(baja.AccesoId, Decision.BAJA, "", DemoLogin));
        _recert.Decidir(baja.AccesoId, Decision.CONTINUA, null, DemoLogin);
        _recert.Decidir(baja.AccesoId, Decision.BAJA, "Ya no pertenece al área", DemoLogin);   // vale la última
        Assert.Equal(3, _recert.ContinuaMasivo(accesos.Select(a => a.AccesoId), DemoLogin));

        var macro = _recert.MisAccesos(DemoLogin).MacroAreas.Single();
        Assert.Equal(0, macro.Conteo.Pendientes);
        _recert.ConfirmarMacroArea(macro.MacroAreaId, DemoLogin);

        var evento = _recert.EventoActivo()!;
        var pendientes = _recert.CerrarEvento(evento.EventoId);
        Assert.Equal(5, pendientes);   // las otras macro áreas quedan reportadas, no se asume decisión
        Assert.Throws<DomainException>(() => _recert.Decidir(baja.AccesoId, Decision.CONTINUA, null, DemoLogin));

        Assert.Throws<DomainException>(() => _recert.RegistrarEjecucion(baja.AccesoId, " ", null, "TI"));
        _recert.RegistrarEjecucion(baja.AccesoId, "INC-001", null, "TI");
        var bajas = _recert.Bajas(evento.EventoId)!;
        Assert.Equal(1, bajas.Ejecutadas);
        Assert.Contains(_recert.Traza(baja.AccesoId), p => p.Titulo == "Decisión cambiada a Baja");
        Assert.Contains("INC-001", _recert.EvidenciaCsv(evento.EventoId));
    }

    [Fact]
    public void Extracto_con_rol_en_dos_vistas_queda_en_borrador_con_rechazos()
    {
        _recert.CerrarEvento(_recert.EventoActivo()!.EventoId);
        Dictionary<string, string> F(string usuario, string rol, string vista) => new()
        {
            ["USUARIO"] = usuario, ["NOMBRE"] = usuario, ["ROL"] = rol, ["VISTA"] = vista,
            ["ACCESO ULTIMOS 3 MESES"] = "10", ["Área Responsable"] = "TI",
        };
        var id = _recert.CrearEvento("Prueba", null, null,
            [F("U1", "ROL_X", "MABE.A/V1"), F("U2", "ROL_X", "MABE.A/V2"), F("U3", "ROL_Y", "MABE.A/V3")]);

        var t = _recert.Tablero(id)!;
        Assert.Equal(EstatusEvento.BORRADOR, t.Evento.Estatus);
        Assert.Equal(1, t.Evento.Validacion.RolesConVariasVistas);
        Assert.Equal(2, t.Evento.Rechazos.Count);
        Assert.Throws<DomainException>(() => _recert.AbrirEvento(id));
        Assert.Single(_s.Vistas, v => v.RutaTecnica == "MABE.A/V3");   // vista nueva entra al inventario en Por Iniciar
    }

    [Fact]
    public void Carga_de_calidad_valida_y_agrega_como_suma_de_validos_entre_evaluados()
    {
        var r = _calidad.CargarResumen(
        [
            new("RL_1_1", null, 202609, 1000, 10),
            new(null, "02ZMM_O09_RL_01.csv", 202609, 3000, 300),
            new("RL_99", null, 202609, 5, 0),            // no registrada
            new("RL_2_1", null, 202613, 5, 0),           // periodo inválido
            new("RL_2_2", null, 202609, 5, 6),           // errores > evaluados
        ]);
        Assert.Equal(new ResultadoCarga(2, 3), r);

        var t = _calidad.Tablero(202609, null);
        Assert.Equal(1 - 310m / 4000m, t.Total.Calidad);   // no es el promedio de 99% y 90%
        Assert.Equal(3, t.Rechazos.Count);
    }

    [Fact]
    public void Importar_seguimiento_crea_dps_registra_avances_y_es_repetible()
    {
        var corte = new Semana(2026, 40);
        var lectura = ImportacionSeguimiento.Leer(
            Csv.LeerFilas(Csv.LeerTexto(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Datos", "seguimiento_prueba.csv")))), corte);

        var r = _seg.ImportarSeguimiento(lectura.Filas, corte, "demo");
        Assert.Equal(4, r.DataProductsNuevos);
        Assert.Contains("Producto", r.GobiernosNuevos);
        Assert.Equal(7 + 4 + 0 + 1, r.Avances);

        var dos = Fila("DP Dos", corte);
        Assert.Equal("Finanzas", dos.Gobierno);
        Assert.Equal(44.86m, dos.Avance);                       // (70 + 0 + 100 + 45 + 0 + 99 + 0) / 7: rubros de vista reportados cuentan
        Assert.Equal(4, Fila("DP Uno", corte).Vistas);           // vistas reportadas mientras no haya asignadas

        var otra = _seg.ImportarSeguimiento(lectura.Filas, corte, "demo");   // mismo archivo: no duplica
        Assert.Equal(0, otra.DataProductsNuevos);
        Assert.Equal(4, otra.DataProductsActualizados);
        Assert.Equal(0, otra.Avances);
        Assert.Equal(0, otra.Notas);
    }
}
