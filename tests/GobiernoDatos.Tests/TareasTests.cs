using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;
using GobiernoDatos.Web.Services;
using Microsoft.Extensions.Options;

namespace GobiernoDatos.Tests;

public class TareasTests
{
    // Reloj que se puede adelantar para verificar los días transcurridos.
    private sealed class Reloj(DateTimeOffset inicio) : TimeProvider
    {
        public DateTimeOffset Ahora { get; set; } = inicio;
        public override DateTimeOffset GetUtcNow() => Ahora;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private readonly Reloj _reloj = new(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
    private readonly GobiernoStore _s;
    private readonly VistasService _vistas;
    private readonly TareasService _tareas;
    private readonly int _vista;
    private static readonly Asignacion Ana = new(@"MABENET\aperez", "Perez, Ana (MEX, TI)");
    private static readonly Asignacion Luis = new(@"MABENET\lgomez", "Gomez, Luis (MEX, TI)");

    public TareasTests()
    {
        _s = new GobiernoStore(_reloj);
        MuestrasLoader.Cargar(_s, MuestrasTestsRuta.Ruta(), new UsuarioDemoOptions());
        _vistas = new VistasService(_s);
        _tareas = new TareasService(_s);
        _vista = _s.Vistas.Single(v => v.NombreNegocio == "Head Count PBI").VistaId;   // etapa Reescaneo (5)
    }

    [Fact]
    public void Mover_de_etapa_con_usuario_crea_la_tarea_de_la_nueva_etapa()
    {
        _vistas.MoverEtapa(_vista, 6, null, null, "admin", Ana);
        var t = _tareas.TareaAbierta(_vista)!;
        Assert.Equal("Certificación Glosarios", t.Etapa);
        Assert.Equal(Ana.UsuarioRed, t.AsignadoA);
        Assert.Equal(EstatusTarea.Abierta, t.Estatus);
        Assert.Equal(0, t.DiasTranscurridos);
    }

    [Fact]
    public void Los_dias_avanzan_mientras_esta_abierta_y_se_congelan_al_terminar()
    {
        _tareas.Asignar(_vista, Ana, "admin");
        var id = _tareas.TareaAbierta(_vista)!.TareaId;

        _reloj.Ahora = _reloj.Ahora.AddDays(5);
        Assert.Equal(5, _tareas.TareaAbierta(_vista)!.DiasTranscurridos);

        _tareas.Terminar(id, Ana.UsuarioRed);
        _reloj.Ahora = _reloj.Ahora.AddDays(10);
        var cerrada = _tareas.Listar(new FiltroTareas(EstatusTarea.Terminada, null, null)).Single(t => t.TareaId == id);
        Assert.Equal(5, cerrada.DiasTranscurridos);                       // congelados
        Assert.Equal(new DateTime(2026, 10, 20, 9, 0, 0), cerrada.FechaCierre);
        Assert.Equal(Ana.UsuarioRed, cerrada.CerradoPor);
        Assert.Null(_tareas.TareaAbierta(_vista));
        Assert.Throws<DomainException>(() => _tareas.Terminar(id, "otro"));   // ya cerrada
    }

    [Fact]
    public void Cambiar_de_etapa_cancela_la_tarea_abierta()
    {
        _tareas.Asignar(_vista, Ana, "admin");
        _vistas.MoverEtapa(_vista, 6, null, null, "admin");
        Assert.Null(_tareas.TareaAbierta(_vista));
        var t = Assert.Single(_tareas.Listar(new FiltroTareas(EstatusTarea.Cancelada, null, null)));
        Assert.Equal("Cambio de etapa", t.MotivoCierre);
    }

    [Fact]
    public void Reasignar_cancela_la_anterior_y_solo_queda_una_abierta()
    {
        _tareas.Asignar(_vista, Ana, "admin");
        Assert.Throws<DomainException>(() => _tareas.Asignar(_vista, Ana, "admin"));   // ya es suya
        _tareas.Asignar(_vista, Luis, "admin");
        Assert.Equal(Luis.UsuarioRed, _tareas.TareaAbierta(_vista)!.AsignadoA);
        Assert.Single(_tareas.Listar(new FiltroTareas(EstatusTarea.Abierta, null, null)));
        Assert.Equal("Reasignada", _tareas.Listar(new FiltroTareas(EstatusTarea.Cancelada, null, null)).Single().MotivoCierre);
        Assert.Single(_tareas.Listar(new FiltroTareas(null, Luis.UsuarioRed, null)));
    }

    [Fact]
    public void La_etapa_final_no_tiene_tarea()
    {
        var completada = _s.Vistas.Single(v => v.NombreNegocio == "Ordenes Trabajo Mantenimiento").VistaId;
        Assert.Throws<DomainException>(() => _tareas.Asignar(completada, Ana, "admin"));
        Assert.Throws<DomainException>(() => _vistas.MoverEtapa(_vista, 12, null, null, "admin", Ana));
        Assert.Equal(5, _s.EtapaOrdenActual(_vista));                    // no se movió
    }

    [Fact]
    public void El_resolutor_rechaza_usuarios_que_no_estan_en_el_directorio()
    {
        var resolutor = new ResolutorUsuarios(new DirectorioDeUno(), Options.Create(new GobiernoDatosOptions()));
        Assert.Equal(new Asignacion(@"MABENET\aperez", "Perez, Ana"), resolutor.Resolver("APerez"));
        Assert.Throws<DomainException>(() => resolutor.Resolver(@"MABENET\nadie"));
    }

    private sealed class DirectorioDeUno : IDirectorioUsuarios
    {
        public string NombreVisible(string login) => login;
        public string? BuscarNombre(string usuarioRed) => usuarioRed == @"MABENET\aperez" ? "Perez, Ana" : null;
    }
}

internal static class MuestrasTestsRuta
{
    public static string Ruta()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var ruta = Path.Combine(dir.FullName, "data", "muestras");
            if (Directory.Exists(ruta)) return ruta;
        }
        throw new DirectoryNotFoundException("No se encontró data/muestras.");
    }
}
