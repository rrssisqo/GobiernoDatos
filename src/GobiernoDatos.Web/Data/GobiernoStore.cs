using GobiernoDatos.Web.Domain;

namespace GobiernoDatos.Web.Data;

// Almacén en memoria con la misma forma que el esquema gd. Sustituto temporal de SQL Server
// mientras se resuelven la migración v3 y el entorno (ver docs/04_pendientes_y_backlog.md).
// Todas las lecturas y escrituras de los servicios se hacen dentro de Sync.
public sealed class GobiernoStore(TimeProvider reloj)
{
    public object Sync { get; } = new();

    public List<string> Gobiernos { get; } = [];
    public List<string> Responsables { get; } = [];
    public List<DataProduct> DataProducts { get; } = [];
    public List<VistaGobernada> Vistas { get; } = [];
    public List<DpVista> DpVistas { get; } = [];
    public List<MovEtapaVista> MovEtapas { get; } = [];
    public List<MovAvance> MovAvances { get; } = [];
    public List<NotaSemanal> Notas { get; } = [];
    public List<TareaVista> Tareas { get; } = [];
    public List<ObjetoFuente> Objetos { get; } = [];
    public List<VistaObjeto> VistaObjetos { get; } = [];
    public List<Regla> Reglas { get; } = [];
    public List<string> FrecuenciasRegla { get; } = ["Diaria", "Semanal", "Mensual", "Trimestral"];   // gd.frecuencia_regla
    public List<ResumenCalidad> Resumenes { get; } = [];
    public List<CargaRechazo> RechazosCalidad { get; } = [];

    public List<MacroArea> MacroAreas { get; } = [];
    public List<UsuarioHana> UsuariosHana { get; } = [];
    public List<RolHana> Roles { get; } = [];
    public List<RecertEvento> Eventos { get; } = [];
    public List<RecertAcceso> Accesos { get; } = [];
    public List<RecertDecision> Decisiones { get; } = [];
    public List<RecertBajaEjecucion> BajasEjecutadas { get; } = [];
    public List<RecertBajaAnulada> BajasAnuladas { get; } = [];
    public List<RecertRecordatorio> Recordatorios { get; } = [];
    public Dictionary<long, byte[]> CorreosRecordatorio { get; } = [];
    // GOBIERNO del extracto → nombre de la macro área (gd.recert_gobierno_macro_area).
    public Dictionary<string, MapeoGobierno> MapeoGobiernos { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Services.GerenteVoBo? GerenteVoBo { get; set; }
    public Services.GerenteVoBo? ResponsableInfra { get; set; }
    public List<Services.VoBoGerente> VoBos { get; } = [];
    public Dictionary<long, List<Services.BajaVoBo>> VoBoBajas { get; } = [];
    public List<Services.AsignacionInfra> AsignacionesInfra { get; } = [];
    public List<Services.AccesoInformatica> AccesosInformatica { get; } = [];
    public List<Services.CierreInfra> CierresInfra { get; } = [];
    public Dictionary<long, byte[]> ArchivosCierreInfra { get; } = [];
    public List<VistaUso> Usos { get; } = [];

    private long _seq;
    public long NuevoId() => ++_seq;

    public DateTime Ahora => reloj.GetLocalNow().DateTime;
    public DateOnly Hoy => DateOnly.FromDateTime(Ahora);

    // ---- Consultas compartidas (equivalentes a las vistas gd.vw_*) ----

    public int EtapaOrdenActual(int vistaId, DateOnly? corte = null) =>
        MovEtapas.Where(m => m.VistaId == vistaId && (corte is null || m.Fecha <= corte))
                 .OrderBy(m => m.Fecha).ThenBy(m => m.MovId)
                 .LastOrDefault()?.EtapaOrden ?? 0;

    public MovEtapaVista? UltimoMovEtapa(int vistaId) =>
        MovEtapas.Where(m => m.VistaId == vistaId).OrderBy(m => m.Fecha).ThenBy(m => m.MovId).LastOrDefault();

    public DataProduct? DpVigente(int vistaId, DateOnly? fecha = null)
    {
        var f = fecha ?? Hoy;
        var dv = DpVistas.Where(x => x.VistaId == vistaId && x.VigenteEn(f)).MaxBy(x => x.FechaDesde);
        return dv is null ? null : DataProducts.First(d => d.DpId == dv.DpId);
    }

    public List<VistaGobernada> VistasDeDp(int dpId, DateOnly fecha) =>
        DpVistas.Where(x => x.DpId == dpId && x.VigenteEn(fecha))
                .Select(x => Vistas.First(v => v.VistaId == x.VistaId))
                .Where(v => v.Activa && v.FechaAlta <= fecha)
                .ToList();

    public ResultadoRubro PctRubroVista(int vistaId, Rubro rubro, DateOnly corte) =>
        ReglaAvanceVista.Calcular(rubro,
            MovEtapas.Where(m => m.VistaId == vistaId).Select(m => new MovEtapaRef(m.MovId, m.Fecha, m.EtapaOrden)),
            MovAvances.Where(a => a.VistaId == vistaId && a.RubroId == rubro.RubroId)
                      .Select(a => new CapturaRef(a.MovId, a.Fecha, a.Pct, a.Estado)),
            corte);

    // gd.vw_avance_rubro_dp_semana: rubro DP = último valor capturado; rubro de vista = promedio de vistas vigentes.
    // Mientras un DP no tenga vistas asignadas se usa el valor inicial migrado del Excel (EsInicial).
    public decimal PctRubroDp(DataProduct dp, Rubro rubro, DateOnly corte)
    {
        if (rubro.Nivel == NivelRubro.VISTA)
        {
            var vistas = VistasDeDp(dp.DpId, corte);
            if (vistas.Count > 0) return Math.Round(vistas.Average(v => PctRubroVista(v.VistaId, rubro, corte).Pct), 2);
        }
        return UltimaCapturaDp(dp.DpId, rubro.RubroId, corte)?.Pct ?? 0;
    }

    public MovAvance? UltimaCapturaDp(int dpId, int rubroId, DateOnly corte) =>
        MovAvances.Where(a => a.DpId == dpId && a.RubroId == rubroId && a.Fecha <= corte)
                  .OrderBy(a => a.Fecha).ThenBy(a => a.MovId)
                  .LastOrDefault();

    public decimal PctDp(DataProduct dp, DateOnly corte) =>
        Avance.PromedioPonderado(Catalogos.Rubros.Select(r => (PctRubroDp(dp, r, corte), r.Peso)));

    public int NumVistasDp(DataProduct dp, DateOnly corte)
    {
        var n = VistasDeDp(dp.DpId, corte).Count;
        return n > 0 ? n : dp.VistasIniciales ?? 0;
    }

    // Cierra la tarea abierta de la vista (cancelada, con motivo) y, si se indica, abre una nueva para la etapa.
    public void AbrirTarea(int vistaId, int etapaOrden, GobiernoDatos.Web.Services.Asignacion? asignacion, string usuario, string motivoCierre)
    {
        foreach (var t in Tareas.Where(t => t.VistaId == vistaId && t.Estatus == "ABIERTA"))
        {
            t.Estatus = "CANCELADA";
            t.FechaCierre = Ahora;
            t.CerradoPor = usuario;
            t.MotivoCierre = motivoCierre;
        }
        if (asignacion is null) return;
        Tareas.Add(new TareaVista
        {
            TareaId = (int)NuevoId(), VistaId = vistaId, EtapaOrden = etapaOrden, AsignadoA = asignacion.UsuarioRed,
            AsignadoNombre = asignacion.Nombre, AsignadoPor = usuario, FechaAsignacion = Ahora,
        });
    }

    public RecertDecision? DecisionVigente(long accesoId) =>
        Decisiones.Where(d => d.AccesoId == accesoId).MaxBy(d => d.DecisionId);

    public void AsegurarResponsable(string? nombre)
    {
        if (!string.IsNullOrWhiteSpace(nombre) && !Responsables.Contains(nombre, StringComparer.OrdinalIgnoreCase))
            Responsables.Add(nombre.Trim());
    }
}
