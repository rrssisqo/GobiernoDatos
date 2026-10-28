using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;
using Microsoft.Extensions.Options;

namespace GobiernoDatos.Web.Services;

public sealed record Conteo(int Accesos, int Continua, int Baja)
{
    public int Decididos => Continua + Baja;
    public int Pendientes => Accesos - Decididos;
    public decimal Avance => Accesos == 0 ? 0 : (decimal)Decididos / Accesos;
}

public sealed record FilaMacroArea(int MacroAreaId, string MacroArea, IReadOnlyList<string> Certificadores, Conteo Conteo, bool Confirmada);

public sealed record FilaHistorialEvento(int EventoId, string Nombre, DateOnly? Inicio, DateOnly? Fin, EstatusEvento Estatus,
                                         int Accesos, int Bajas, int BajasEjecutadas);

public sealed record TableroEvento(RecertEvento Evento, Conteo Conteo, IReadOnlyList<FilaMacroArea> MacroAreas);

public sealed record AccesoCertificar(long AccesoId, string UsuarioCodigo, string UsuarioNombre, string Vista, string Rol,
                                      string MacroArea, int Accesos3m, Decision? Anterior, Decision? Decision,
                                      string? Comentario, bool BajaEjecutada);

public sealed record MiRecertificacion(RecertEvento? Evento, IReadOnlyList<FilaMacroArea> MacroAreas, IReadOnlyList<AccesoCertificar> Accesos);

public sealed record ResultadoDecision(long AccesoId, Decision Decision, string? Comentario, Conteo Conteo);

public sealed record FilaBaja(long AccesoId, string UsuarioCodigo, string UsuarioNombre, string Vista, string Rol, string MacroArea,
                              string DecididoPor, DateTime FechaDecision, string? Comentario, RecertBajaEjecucion? Ejecucion);

// VoBo del gerente (gd.recert_vobo, migración 011). Power Automate lo envía y registra la respuesta.
public enum EstatusVoBo { PENDIENTE, ENVIADA, AUTORIZADA, RECHAZADA, CANCELADA }

// Gerente único: autoriza las bajas de todas las macro áreas (gd.recert_gerente, migración 012).
public sealed record GerenteVoBo(string UsuarioRed, string Nombre, string Correo);

public sealed record VoBoGerente(long VoBoId, int EventoId, int MacroAreaId, string GerenteNombre, string GerenteCorreo, int Bajas,
                                 EstatusVoBo Estatus, string SolicitadoPor, DateTime FechaSolicitud, DateTime? FechaEnvio,
                                 DateTime? FechaRespuesta, string? RespondidoPor, string? Comentario, string? MotivoCancelacion,
                                 string MacroArea = "");

// Baja incluida en una solicitud de VoBo, copiada al solicitarla (gd.recert_vobo_baja, migración 018).
public sealed record BajaVoBo(long AccesoId, string Usuario, string Nombre, string Rol, string Vista, string? Comentario,
                              string? DecididoPor, DateTime? FechaDecision);

// Asignación de las bajas de una macro área a un responsable de infraestructura con número de cambio (migración 014).
public sealed record AsignacionInfra(long AsignacionId, int EventoId, int MacroAreaId, string ResponsableUsuarioRed, string ResponsableNombre,
                                     string ResponsableCorreo, string Cambio, int Bajas, string AsignadoPor, DateTime Fecha,
                                     bool Notificado, string? Error);

// Correo con el que el responsable de infraestructura avisa que terminó de retirar los roles de una macro área (migración 019).
public sealed record CierreInfra(long CierreId, int EventoId, int MacroAreaId, string MacroArea, string NombreArchivo, int Tamano,
                                 string? Comentario, string SubidoPor, DateTime Fecha);

// Bajas de una macro área en el evento, con su gerente, su última solicitud de VoBo y su asignación a infraestructura.
public sealed record BajasMacroArea(int MacroAreaId, string MacroArea, int Bajas, int Ejecutadas, int PorDecidir,
                                    GerenteVoBo? Gerente, VoBoGerente? VoBo, AsignacionInfra? Infra = null)
{
    public bool Autorizada => VoBo?.Estatus == EstatusVoBo.AUTORIZADA;
    public bool SolicitudActiva => VoBo?.Estatus is EstatusVoBo.PENDIENTE or EstatusVoBo.ENVIADA or EstatusVoBo.AUTORIZADA;
}

public sealed record TableroBajas(RecertEvento Evento, IReadOnlyList<FilaBaja> Filas, int Decididas, int Ejecutadas, decimal? DiasPromedio,
                                  IReadOnlyList<BajasMacroArea> MacroAreas);

// Auditoría: un renglón por acceso con las columnas de la consulta HANA (CONT_TI_RECERTIFICACION), en su orden.
// AreaResponsable = macro área; OKBAJA y COMENTARIOS = decisión vigente y comentario capturados en el portal.
public sealed record FilaAuditoria(long AccesoId, int EventoId, string Evento,
                                   string? Area, string? Content, string Rol, string Vista, string Usuario, string Name,
                                   string? Gobierno, string AreaResponsable, string? Responsable,
                                   Decision? OkBaja, string? Comentarios, int Accesos,
                                   string? DecididoPor = null, DateTime? FechaDecision = null);

// Pendientes de un evento abierto por certificador vigente y macro área (gd.vw_recert_pendientes_certificador).
// Correo = el registrado en gd.responsable; si es nulo se busca en Active Directory con el usuario de red.
public sealed record CertificadorPendiente(string Certificador, string? UsuarioRed, string? Correo, string MacroArea, string Tipo, int Pendientes);

public sealed record PasoTraza(DateTime Fecha, string Titulo, string Detalle);

// UsuarioRed nulo: responsable registrado solo por nombre (extracto); no puede actuar hasta asignarle su usuario de red.
public sealed record CertificadorVigente(string Nombre, string? UsuarioRed, string Tipo);

public sealed record CertificadoresMacroArea(int MacroAreaId, string MacroArea, IReadOnlyList<CertificadorVigente> Certificadores);

public interface IRecertificacionService
{
    RecertEvento? EventoActivo();
    IReadOnlyList<FilaHistorialEvento> Historial();
    TableroEvento? Tablero(int eventoId);
    int CrearEvento(string nombre, DateOnly? fechaExtracto, DateOnly? fechaLimite, IReadOnlyList<Dictionary<string, string>> extracto);
    void AbrirEvento(int eventoId);
    int CerrarEvento(int eventoId);
    void DescartarBorrador(int eventoId);
    MiRecertificacion MisAccesos(string certificador);
    ResultadoDecision Decidir(long accesoId, Decision decision, string? comentario, string certificador);
    int ContinuaMasivo(IEnumerable<long> accesoIds, string certificador);
    void ConfirmarMacroArea(int macroAreaId, string certificador);
    TableroBajas? Bajas(int eventoId);
    IReadOnlyList<PasoTraza> Traza(long accesoId);
    void RegistrarEjecucion(long accesoId, string ticket, DateOnly? fecha, string usuario);
    // Quita un ticket mal capturado: se conserva como anulado (motivo, quién, cuándo) y la baja vuelve a pendiente.
    void AnularEjecucion(long accesoId, string motivo, string usuario);
    string EvidenciaCsv(int eventoId);
    // Todos los accesos de un evento (o de todos los eventos si es nulo), ordenados por evento, macro área, vista y usuario.
    IReadOnlyList<FilaAuditoria> Auditoria(int? eventoId);
    IReadOnlyList<CertificadorPendiente> PendientesPorCertificador(int eventoId);
    // eml = el correo tal como salió (evidencia); nulo si no se envió.
    void RegistrarRecordatorio(RecertRecordatorio recordatorio, byte[]? eml = null);
    byte[]? CorreoRecordatorio(long recordatorioId);
    IReadOnlyList<RecertRecordatorio> Recordatorios(int eventoId);
    // Catálogo GOBIERNO (extracto HANA) → macro área. La macro área se crea si no existe. Aplica a los extractos que se carguen después.
    IReadOnlyList<MapeoGobierno> MapeosGobierno();
    void CrearMapeoGobierno(string gobierno, string macroArea, string usuario);
    void CambiarMapeoGobierno(string gobierno, string macroArea, string usuario);
    void EliminarMapeoGobierno(string gobierno);
    // Gerente que autoriza las bajas de todas las macro áreas antes del ticket. Nombre y correo salen del directorio.
    GerenteVoBo? Gerente();
    void AsignarGerente(string usuarioRed, string usuario);
    void QuitarGerente();
    // Crea la solicitud de VoBo que Power Automate envía al gerente. Devuelve su id.
    long SolicitarVoBo(int eventoId, int macroAreaId, string usuario);
    // Respuesta del gerente (en producción la registra Power Automate con gd.sp_vobo_responder). Devuelve si se registró.
    bool ResponderVoBo(long voboId, bool autorizada, string respondidoPor, string? comentario);
    // Evidencia: todas las solicitudes de VoBo del evento (también canceladas y rechazadas) y las bajas de cada una.
    IReadOnlyList<VoBoGerente> VoBos(int eventoId);
    IReadOnlyList<BajaVoBo> BajasVoBo(long voboId);
    // Responsable de infraestructura único: retira los roles de todas las macro áreas (migración 015). Mismo modelo que el gerente.
    GerenteVoBo? ResponsableInfra();
    void AsignarResponsableInfra(string usuarioRed, string usuario);
    void QuitarResponsableInfra();
    // Asigna las bajas pendientes de ejecutar de la macro área (con VoBo autorizado) al responsable de infraestructura
    // con el número de cambio. La notificación la envía NotificacionesInfra.
    AsignacionInfra AsignarInfraestructura(int eventoId, int macroAreaId, string cambio, string usuario);
    void RegistrarNotificacionInfra(long asignacionId, bool enviado, string? error);
    // Evidencia de cierre: el correo (.eml o .msg) con el que infraestructura avisa que terminó. Se conservan todos los que se suban.
    CierreInfra SubirCierreInfra(int eventoId, int macroAreaId, string nombreArchivo, byte[] contenido, string? comentario, string usuario);
    IReadOnlyList<CierreInfra> CierresInfra(int eventoId);
    (string NombreArchivo, byte[] Contenido)? ArchivoCierreInfra(long cierreId);
    IReadOnlyList<CertificadoresMacroArea> Certificadores();
    // El certificador se identifica por su usuario de red (DOMINIO\usuario); el nombre sale del directorio.
    void AsignarCertificador(int macroAreaId, string usuarioRed, string tipo);
    // Identificador = usuario de red, o el nombre si el certificador aún no tiene usuario de red.
    void RetirarCertificador(int macroAreaId, string identificador);
}

public sealed class RecertificacionService(GobiernoStore s, IDirectorioUsuarios directorio, IOptions<GobiernoDatosOptions> opciones) : IRecertificacionService
{
    public RecertEvento? EventoActivo()
    {
        lock (s.Sync) return s.Eventos.Where(e => e.Estatus != EstatusEvento.CERRADO).MaxBy(e => e.EventoId) ?? s.Eventos.MaxBy(e => e.EventoId);
    }

    public IReadOnlyList<FilaHistorialEvento> Historial()
    {
        lock (s.Sync)
            return s.Eventos.OrderByDescending(e => e.EventoId).Select(e =>
            {
                var bajas = Vigentes(e.EventoId).Where(x => x.Decision?.Decision == Decision.BAJA).Select(x => x.Acceso.AccesoId).ToList();
                return new FilaHistorialEvento(e.EventoId, e.Nombre, e.FechaInicio, e.FechaCierre ?? e.FechaLimite, e.Estatus,
                    s.Accesos.Count(a => a.EventoId == e.EventoId), bajas.Count, s.BajasEjecutadas.Count(b => bajas.Contains(b.AccesoId)));
            }).ToList();
    }

    public TableroEvento? Tablero(int eventoId)
    {
        lock (s.Sync)
        {
            var e = s.Eventos.FirstOrDefault(x => x.EventoId == eventoId);
            if (e is null) return null;
            var vig = Vigentes(eventoId);
            var macros = vig.GroupBy(x => x.Acceso.MacroAreaId).Select(g =>
            {
                var m = s.MacroAreas.First(x => x.MacroAreaId == g.Key);
                return new FilaMacroArea(m.MacroAreaId, m.Nombre, m.Certificadores.Select(c => c.Nombre).ToList(), Contar(g.Select(x => x.Decision)),
                                         e.Confirmaciones.Any(c => c.MacroAreaId == m.MacroAreaId));
            }).OrderBy(f => f.MacroArea).ToList();
            return new TableroEvento(e, Contar(vig.Select(x => x.Decision)), macros);
        }
    }

    public int CrearEvento(string nombre, DateOnly? fechaExtracto, DateOnly? fechaLimite, IReadOnlyList<Dictionary<string, string>> extracto)
    {
        lock (s.Sync)
        {
            nombre = nombre.Trim();
            if (nombre.Length == 0) throw new DomainException("Indica el nombre del evento.");
            if (s.Eventos.Any(e => e.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase))) throw new DomainException("Ya existe un evento con ese nombre.");
            if (s.Eventos.FirstOrDefault(e => e.Estatus != EstatusEvento.CERRADO) is { } activo)
                throw new DomainException($"Cierra o descarta el evento «{activo.Nombre}» antes de crear otro.");
            if (extracto.Count == 0) throw new DomainException("El extracto no tiene filas.");
            if (!extracto[0].ContainsKey("USUARIO") || !extracto[0].ContainsKey("ROL") || !extracto[0].ContainsKey("VISTA"))
                throw new DomainException("El archivo no tiene las columnas del extracto de recertificación (USUARIO, ROL, VISTA…).");

            var evento = new RecertEvento { EventoId = (int)s.NuevoId(), Nombre = nombre, FechaExtracto = fechaExtracto ?? s.Hoy, FechaLimite = fechaLimite };
            s.Eventos.Add(evento);
            CargaExtracto.Cargar(s, evento, extracto);
            return evento.EventoId;
        }
    }

    public void AbrirEvento(int eventoId)
    {
        lock (s.Sync)
        {
            var e = Evento(eventoId);
            ReglasRecertificacion.ValidarApertura(e.Estatus, e.Rechazos.Count, s.Accesos.Count(a => a.EventoId == eventoId));
            e.Estatus = EstatusEvento.ABIERTO;
            e.FechaInicio = s.Hoy;
        }
    }

    // Los pendientes al cierre se reportan; nunca se asume una decisión.
    public int CerrarEvento(int eventoId)
    {
        lock (s.Sync)
        {
            var e = Evento(eventoId);
            ReglasRecertificacion.ValidarCierre(e.Estatus);
            e.Estatus = EstatusEvento.CERRADO;
            e.FechaCierre = s.Hoy;
            return Vigentes(eventoId).Count(x => x.Decision is null);
        }
    }

    public void DescartarBorrador(int eventoId)
    {
        lock (s.Sync)
        {
            var e = Evento(eventoId);
            if (e.Estatus != EstatusEvento.BORRADOR) throw new DomainException("Solo se puede descartar un evento en borrador.");
            s.Accesos.RemoveAll(a => a.EventoId == eventoId);
            s.Usos.RemoveAll(u => u.EventoId == eventoId);
            s.Eventos.Remove(e);
        }
    }

    // Seguridad por fila: el certificador solo ve las macro áreas donde es titular o delegado.
    public MiRecertificacion MisAccesos(string certificador)
    {
        lock (s.Sync)
        {
            var evento = s.Eventos.Where(e => e.Estatus != EstatusEvento.BORRADOR).MaxBy(e => e.EventoId);
            if (evento is null) return new(null, [], []);
            var misMacros = MacroAreasDe(certificador);
            var anterior = s.Eventos.Where(e => e.EventoId < evento.EventoId && e.Estatus != EstatusEvento.BORRADOR).MaxBy(e => e.EventoId);

            var vig = Vigentes(evento.EventoId).Where(x => misMacros.Contains(x.Acceso.MacroAreaId)).ToList();
            var accesos = vig.Select(x =>
            {
                var a = x.Acceso;
                // Decisión del evento anterior como referencia; nunca se precarga.
                var previo = anterior is null ? null
                    : s.Accesos.FirstOrDefault(p => p.EventoId == anterior.EventoId && p.UsuarioCodigo == a.UsuarioCodigo && p.Rol == a.Rol);
                return new AccesoCertificar(a.AccesoId, a.UsuarioCodigo, s.UsuariosHana.First(u => u.Codigo == a.UsuarioCodigo).Nombre,
                    s.Vistas.First(v => v.VistaId == a.VistaId).RutaTecnica, a.Rol, s.MacroAreas.First(m => m.MacroAreaId == a.MacroAreaId).Nombre,
                    a.Accesos3m, previo is null ? null : s.DecisionVigente(previo.AccesoId)?.Decision, x.Decision?.Decision, x.Decision?.Comentario,
                    s.BajasEjecutadas.Any(b => b.AccesoId == a.AccesoId));
            }).OrderBy(a => a.Vista).ThenByDescending(a => a.Accesos3m).ToList();

            var macros = vig.GroupBy(x => x.Acceso.MacroAreaId).Select(g =>
            {
                var m = s.MacroAreas.First(x => x.MacroAreaId == g.Key);
                return new FilaMacroArea(m.MacroAreaId, m.Nombre, m.Certificadores.Select(c => c.Nombre).ToList(), Contar(g.Select(x => x.Decision)),
                                         evento.Confirmaciones.Any(c => c.MacroAreaId == m.MacroAreaId));
            }).OrderBy(m => m.MacroArea).ToList();

            return new(evento, macros, accesos);
        }
    }

    public ResultadoDecision Decidir(long accesoId, Decision decision, string? comentario, string certificador)
    {
        lock (s.Sync)
        {
            var a = AccesoAutorizado(accesoId, certificador);
            var e = Evento(a.EventoId);
            ReglasRecertificacion.ValidarDecision(e.Estatus, decision, comentario);
            if (s.BajasEjecutadas.Any(b => b.AccesoId == accesoId)) throw new DomainException("La baja de este acceso ya se ejecutó; la decisión no puede cambiar.");

            // Insert-only: la decisión vigente es la última registrada.
            s.Decisiones.Add(new RecertDecision
            {
                DecisionId = s.NuevoId(), AccesoId = accesoId, Decision = decision,
                Comentario = string.IsNullOrWhiteSpace(comentario) ? null : comentario.Trim(), DecididoPor = NombreDe(LoginParaBuscar(certificador)), FechaRegistro = s.Ahora,
            });
            e.Confirmaciones.RemoveAll(c => c.MacroAreaId == a.MacroAreaId);   // un cambio invalida la confirmación previa
            CancelarVoBo(e.EventoId, a.MacroAreaId);                           // y el VoBo del gerente (tr_recert_decision_vobo)
            return new(accesoId, decision, comentario?.Trim(), ConteoMacro(e.EventoId, a.MacroAreaId));
        }
    }

    // Acción masiva: registra una decisión individual por cada acceso pendiente.
    public int ContinuaMasivo(IEnumerable<long> accesoIds, string certificador)
    {
        lock (s.Sync)
        {
            int n = 0;
            foreach (var id in accesoIds.Distinct())
            {
                var a = AccesoAutorizado(id, certificador);
                if (s.DecisionVigente(id) is not null) continue;
                var e = Evento(a.EventoId);
                ReglasRecertificacion.ValidarDecision(e.Estatus, Decision.CONTINUA, null);
                s.Decisiones.Add(new RecertDecision
                {
                    DecisionId = s.NuevoId(), AccesoId = id, Decision = Decision.CONTINUA, DecididoPor = NombreDe(LoginParaBuscar(certificador)),
                    Comentario = "Acción masiva", FechaRegistro = s.Ahora,
                });
                CancelarVoBo(e.EventoId, a.MacroAreaId);
                n++;
            }
            return n;
        }
    }

    public void ConfirmarMacroArea(int macroAreaId, string certificador)
    {
        lock (s.Sync)
        {
            if (!MacroAreasDe(certificador).Contains(macroAreaId)) throw new DomainException("No eres certificador de esa macro área.");
            var e = s.Eventos.Where(x => x.Estatus == EstatusEvento.ABIERTO).MaxBy(x => x.EventoId) ?? throw new DomainException("No hay un evento abierto.");
            var conteo = ConteoMacro(e.EventoId, macroAreaId);
            if (conteo.Pendientes > 0) throw new DomainException($"Faltan {conteo.Pendientes} accesos por decidir.");
            e.Confirmaciones.RemoveAll(c => c.MacroAreaId == macroAreaId);
            e.Confirmaciones.Add(new ConfirmacionMacroArea(macroAreaId, LoginParaBuscar(certificador), s.Ahora));
            // El certificador terminó: si hay bajas y gerente, se pide el VoBo.
            if (conteo.Baja > 0 && s.GerenteVoBo is not null && !VoBoActivo(e.EventoId, macroAreaId))
                SolicitarVoBo(e.EventoId, macroAreaId, LoginParaBuscar(certificador));
        }
    }

    public TableroBajas? Bajas(int eventoId)
    {
        lock (s.Sync)
        {
            var e = s.Eventos.FirstOrDefault(x => x.EventoId == eventoId);
            if (e is null) return null;
            var filas = Vigentes(eventoId).Where(x => x.Decision?.Decision == Decision.BAJA).Select(x =>
            {
                var a = x.Acceso;
                return new FilaBaja(a.AccesoId, a.UsuarioCodigo, s.UsuariosHana.First(u => u.Codigo == a.UsuarioCodigo).Nombre,
                    s.Vistas.First(v => v.VistaId == a.VistaId).RutaTecnica, a.Rol, s.MacroAreas.First(m => m.MacroAreaId == a.MacroAreaId).Nombre,
                    x.Decision!.DecididoPor, x.Decision.FechaRegistro, x.Decision.Comentario, s.BajasEjecutadas.FirstOrDefault(b => b.AccesoId == a.AccesoId));
            }).OrderBy(f => f.Ejecucion is not null).ThenBy(f => f.FechaDecision).ToList();

            var ejecutadas = filas.Where(f => f.Ejecucion is not null).ToList();
            decimal? dias = ejecutadas.Count == 0 ? null
                : (decimal)ejecutadas.Average(f => f.Ejecucion!.FechaEjecucion.DayNumber - DateOnly.FromDateTime(f.FechaDecision).DayNumber);
            var macros = Vigentes(eventoId).GroupBy(x => x.Acceso.MacroAreaId)
                .Where(g => g.Any(x => x.Decision?.Decision == Decision.BAJA))
                .Select(g => new BajasMacroArea(g.Key, s.MacroAreas.First(m => m.MacroAreaId == g.Key).Nombre,
                    g.Count(x => x.Decision?.Decision == Decision.BAJA), g.Count(x => s.BajasEjecutadas.Any(b => b.AccesoId == x.Acceso.AccesoId)),
                    g.Count(x => x.Decision is null), s.GerenteVoBo,
                    s.VoBos.Where(v => v.EventoId == eventoId && v.MacroAreaId == g.Key).MaxBy(v => v.VoBoId),
                    s.AsignacionesInfra.Where(a => a.EventoId == eventoId && a.MacroAreaId == g.Key).MaxBy(a => a.AsignacionId)))
                .OrderBy(m => m.MacroArea).ToList();
            return new TableroBajas(e, filas, filas.Count, ejecutadas.Count, dias, macros);
        }
    }

    public IReadOnlyList<PasoTraza> Traza(long accesoId)
    {
        lock (s.Sync)
        {
            var a = s.Accesos.FirstOrDefault(x => x.AccesoId == accesoId);
            if (a is null) return [];
            var e = Evento(a.EventoId);
            var pasos = new List<PasoTraza>
            {
                new((e.FechaInicio ?? e.FechaExtracto ?? DateOnly.MinValue).ToDateTime(TimeOnly.MinValue),
                    "Acceso incluido en el evento", $"{e.Nombre} · {a.Accesos3m:N0} accesos en 3 meses"),
            };
            Decision? previa = null;
            foreach (var d in s.Decisiones.Where(d => d.AccesoId == accesoId).OrderBy(d => d.DecisionId))
            {
                var titulo = previa is null ? $"Decisión: {ReglasRecertificacion.Etiqueta(d.Decision)}"
                           : previa == d.Decision ? $"Decisión ratificada: {ReglasRecertificacion.Etiqueta(d.Decision)}"
                           : $"Decisión cambiada a {ReglasRecertificacion.Etiqueta(d.Decision)}";
                pasos.Add(new(d.FechaRegistro, titulo, d.DecididoPor + (d.Comentario is null ? "" : " · " + d.Comentario)));
                previa = d.Decision;
            }
            foreach (var an in s.BajasAnuladas.Where(b => b.AccesoId == accesoId))
            {
                pasos.Add(new(an.FechaEjecucion.ToDateTime(TimeOnly.MinValue), "Rol retirado", $"{an.EjecutadoPor} · ticket {an.Ticket}"));
                pasos.Add(new(an.FechaAnulacion, "Ticket anulado", $"{an.AnuladoPor} · ticket {an.Ticket} · {an.Motivo}"));
            }
            if (s.BajasEjecutadas.FirstOrDefault(b => b.AccesoId == accesoId) is { } ej)
                pasos.Add(new(ej.FechaEjecucion.ToDateTime(TimeOnly.MinValue), "Rol retirado", $"{ej.EjecutadoPor} · ticket {ej.Ticket}"));
            return pasos;
        }
    }

    public void RegistrarEjecucion(long accesoId, string ticket, DateOnly? fecha, string usuario)
    {
        lock (s.Sync)
        {
            if (!s.Accesos.Any(a => a.AccesoId == accesoId)) throw new DomainException("El acceso no existe.");
            ReglasRecertificacion.ValidarEjecucionBaja(s.DecisionVigente(accesoId)?.Decision, s.BajasEjecutadas.Any(b => b.AccesoId == accesoId), ticket);
            var acceso = s.Accesos.First(a => a.AccesoId == accesoId);
            if (!s.VoBos.Any(v => v.EventoId == acceso.EventoId && v.MacroAreaId == acceso.MacroAreaId && v.Estatus == EstatusVoBo.AUTORIZADA))
                throw new DomainException("La baja requiere el VoBo autorizado del gerente de la macro área.");
            var f = fecha ?? s.Hoy;
            if (f > s.Hoy) throw new DomainException("La fecha de ejecución no puede ser futura.");
            s.BajasEjecutadas.Add(new RecertBajaEjecucion { AccesoId = accesoId, Ticket = ticket.Trim(), FechaEjecucion = f, EjecutadoPor = usuario });
        }
    }

    public void AnularEjecucion(long accesoId, string motivo, string usuario)
    {
        if (string.IsNullOrWhiteSpace(motivo)) throw new DomainException("Indica el motivo para quitar el ticket.");
        lock (s.Sync)
        {
            var ej = s.BajasEjecutadas.FirstOrDefault(b => b.AccesoId == accesoId) ?? throw new DomainException("La baja no tiene ticket registrado.");
            s.BajasEjecutadas.Remove(ej);
            s.BajasAnuladas.Add(new RecertBajaAnulada(accesoId, ej.Ticket, ej.FechaEjecucion, ej.EjecutadoPor, motivo.Trim(), usuario, s.Ahora));
        }
    }

    public string EvidenciaCsv(int eventoId)
    {
        lock (s.Sync)
        {
            var e = Evento(eventoId);
            var filas = Vigentes(eventoId).Select(x =>
            {
                var a = x.Acceso;
                var ej = s.BajasEjecutadas.FirstOrDefault(b => b.AccesoId == a.AccesoId);
                return new object?[]
                {
                    e.Nombre, ReglasRecertificacion.Etiqueta(e.Estatus), s.MacroAreas.First(m => m.MacroAreaId == a.MacroAreaId).Nombre,
                    a.UsuarioCodigo, s.UsuariosHana.First(u => u.Codigo == a.UsuarioCodigo).Nombre, a.Rol,
                    s.Vistas.First(v => v.VistaId == a.VistaId).RutaTecnica, a.Accesos3m,
                    x.Decision is null ? "PENDIENTE" : ReglasRecertificacion.Etiqueta(x.Decision.Decision),
                    x.Decision?.Comentario, x.Decision?.DecididoPor, x.Decision?.FechaRegistro.ToString("yyyy-MM-dd HH:mm"),
                    ej?.Ticket, ej?.FechaEjecucion.ToString("yyyy-MM-dd"), ej?.EjecutadoPor,
                };
            });
            return Csv.Escribir(["Evento", "Estatus evento", "Macro área", "Usuario", "Nombre", "Rol", "Vista", "Accesos 3 meses",
                                 "Decisión", "Comentario", "Decidió", "Fecha decisión", "Ticket", "Fecha ejecución", "Ejecutó"], filas);
        }
    }

    public IReadOnlyList<FilaAuditoria> Auditoria(int? eventoId)
    {
        lock (s.Sync)
            return s.Eventos.Where(e => eventoId is null || e.EventoId == eventoId).SelectMany(e => Vigentes(e.EventoId).Select(x =>
            {
                var a = x.Acceso;
                var rol = s.Roles.FirstOrDefault(r => r.Nombre == a.Rol);
                return new FilaAuditoria(a.AccesoId, e.EventoId, e.Nombre, a.Area ?? rol?.Area, a.Content ?? rol?.Content, a.Rol,
                    s.Vistas.First(v => v.VistaId == a.VistaId).RutaTecnica, a.UsuarioCodigo,
                    s.UsuariosHana.First(u => u.Codigo == a.UsuarioCodigo).Nombre, a.Gobierno,
                    s.MacroAreas.First(m => m.MacroAreaId == a.MacroAreaId).Nombre, a.Responsable,
                    x.Decision?.Decision, x.Decision?.Comentario, a.Accesos3m, x.Decision?.DecididoPor, x.Decision?.FechaRegistro);
            }))
            .OrderByDescending(f => f.EventoId).ThenBy(f => f.AreaResponsable).ThenBy(f => f.Vista).ThenBy(f => f.Usuario).ToList();
    }

    public IReadOnlyList<CertificadorPendiente> PendientesPorCertificador(int eventoId)
    {
        lock (s.Sync)
        {
            if (Evento(eventoId).Estatus != EstatusEvento.ABIERTO) return [];
            return Vigentes(eventoId).Where(x => x.Decision is null).GroupBy(x => x.Acceso.MacroAreaId)
                .SelectMany(g =>
                {
                    var m = s.MacroAreas.First(x => x.MacroAreaId == g.Key);
                    return m.Certificadores.Select(c => new CertificadorPendiente(c.Nombre, c.UsuarioRed, null, m.Nombre, c.Tipo, g.Count()));
                })
                .OrderBy(p => p.MacroArea).ThenByDescending(p => p.Tipo).ToList();
        }
    }

    public void RegistrarRecordatorio(RecertRecordatorio recordatorio, byte[]? eml = null)
    {
        lock (s.Sync)
        {
            var id = s.NuevoId();
            s.Recordatorios.Add(recordatorio with { RecordatorioId = id, TieneCorreo = eml is not null });
            if (eml is not null) s.CorreosRecordatorio[id] = eml;
        }
    }

    public byte[]? CorreoRecordatorio(long recordatorioId)
    {
        lock (s.Sync) return s.CorreosRecordatorio.GetValueOrDefault(recordatorioId);
    }

    public IReadOnlyList<RecertRecordatorio> Recordatorios(int eventoId)
    {
        lock (s.Sync) return s.Recordatorios.Where(r => r.EventoId == eventoId).OrderByDescending(r => r.Fecha).ToList();
    }

    public IReadOnlyList<MapeoGobierno> MapeosGobierno()
    {
        lock (s.Sync) return s.MapeoGobiernos.Values.OrderBy(m => m.Gobierno, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void CrearMapeoGobierno(string gobierno, string macroArea, string usuario)
    {
        var (g, m) = ReglasRecertificacion.MapeoGobierno(gobierno, macroArea);
        lock (s.Sync)
        {
            if (s.MapeoGobiernos.ContainsKey(g)) throw new DomainException($"El gobierno {g} ya tiene macro área; cámbiala en su renglón.");
            s.MapeoGobiernos[g] = new MapeoGobierno(g, AsegurarMacroArea(m), usuario, s.Ahora);
        }
    }

    public void CambiarMapeoGobierno(string gobierno, string macroArea, string usuario)
    {
        var (g, m) = ReglasRecertificacion.MapeoGobierno(gobierno, macroArea);
        lock (s.Sync)
        {
            if (!s.MapeoGobiernos.TryGetValue(g, out var actual)) throw new DomainException($"El gobierno {g} no está en el catálogo.");
            s.MapeoGobiernos[g] = new MapeoGobierno(actual.Gobierno, AsegurarMacroArea(m), usuario, s.Ahora);
        }
    }

    public void EliminarMapeoGobierno(string gobierno)
    {
        lock (s.Sync)
            if (!s.MapeoGobiernos.Remove(gobierno.Trim())) throw new DomainException($"El gobierno {gobierno} no está en el catálogo.");
    }

    public GerenteVoBo? Gerente()
    {
        lock (s.Sync) return s.GerenteVoBo;
    }

    public void AsignarGerente(string usuarioRed, string usuario)
    {
        var login = Login(usuarioRed);
        var nombre = directorio.BuscarNombre(login) ?? throw new DomainException($"El usuario {login} no existe en Active Directory.");
        var correo = directorio.BuscarCorreo(login) ?? throw new DomainException($"{nombre} no tiene correo en Active Directory: Power Automate no podría pedirle el VoBo.");
        lock (s.Sync) s.GerenteVoBo = new GerenteVoBo(login, nombre, correo);
    }

    public void QuitarGerente()
    {
        lock (s.Sync)
        {
            if (s.GerenteVoBo is null) throw new DomainException("No hay gerente asignado.");
            s.GerenteVoBo = null;
        }
    }

    public long SolicitarVoBo(int eventoId, int macroAreaId, string usuario)
    {
        lock (s.Sync)
        {
            var e = Evento(eventoId);
            if (e.Estatus == EstatusEvento.BORRADOR) throw new DomainException("El evento todavía no está abierto.");
            var conteo = ConteoMacro(eventoId, macroAreaId);
            if (e.Estatus == EstatusEvento.ABIERTO && conteo.Pendientes > 0)
                throw new DomainException("La macro área todavía tiene accesos por decidir: el VoBo se pide cuando el certificador termina.");
            if (conteo.Baja == 0) throw new DomainException("La macro área no tiene bajas que autorizar.");
            var g = s.GerenteVoBo ?? throw new DomainException("No hay gerente asignado para el VoBo de las bajas (Recertificación → Macro áreas).");
            if (s.VoBos.Any(v => v.EventoId == eventoId && v.MacroAreaId == macroAreaId && v.Estatus == EstatusVoBo.AUTORIZADA))
                throw new DomainException("El gerente ya autorizó las bajas de esta macro área.");
            for (int i = 0; i < s.VoBos.Count; i++)
                if (s.VoBos[i] is { Estatus: EstatusVoBo.PENDIENTE or EstatusVoBo.ENVIADA } v && v.EventoId == eventoId && v.MacroAreaId == macroAreaId)
                    s.VoBos[i] = v with { Estatus = EstatusVoBo.CANCELADA, MotivoCancelacion = "Reemplazada por una nueva solicitud" };
            var vobo = new VoBoGerente(s.NuevoId(), eventoId, macroAreaId, g.Nombre, g.Correo, conteo.Baja, EstatusVoBo.PENDIENTE,
                                       usuario, s.Ahora, null, null, null, null, null);
            s.VoBos.Add(vobo);
            s.VoBoBajas[vobo.VoBoId] = Vigentes(eventoId).Where(x => x.Acceso.MacroAreaId == macroAreaId && x.Decision?.Decision == Decision.BAJA)
                .Select(x => new BajaVoBo(x.Acceso.AccesoId, x.Acceso.UsuarioCodigo, s.UsuariosHana.First(u => u.Codigo == x.Acceso.UsuarioCodigo).Nombre,
                    x.Acceso.Rol, s.Vistas.First(v => v.VistaId == x.Acceso.VistaId).RutaTecnica, x.Decision!.Comentario, x.Decision.DecididoPor,
                    x.Decision.FechaRegistro)).ToList();
            return vobo.VoBoId;
        }
    }

    public bool ResponderVoBo(long voboId, bool autorizada, string respondidoPor, string? comentario)
    {
        lock (s.Sync)
        {
            var i = s.VoBos.FindIndex(v => v.VoBoId == voboId);
            if (i < 0 || s.VoBos[i].Estatus is not (EstatusVoBo.PENDIENTE or EstatusVoBo.ENVIADA)) return false;
            s.VoBos[i] = s.VoBos[i] with
            {
                Estatus = autorizada ? EstatusVoBo.AUTORIZADA : EstatusVoBo.RECHAZADA, FechaRespuesta = s.Ahora, RespondidoPor = respondidoPor,
                Comentario = string.IsNullOrWhiteSpace(comentario) ? null : comentario.Trim(), FechaEnvio = s.VoBos[i].FechaEnvio ?? s.Ahora,
            };
            return true;
        }
    }

    public GerenteVoBo? ResponsableInfra()
    {
        lock (s.Sync) return s.ResponsableInfra;
    }

    public void AsignarResponsableInfra(string usuarioRed, string usuario)
    {
        var login = Login(usuarioRed);
        var nombre = directorio.BuscarNombre(login) ?? throw new DomainException($"El usuario {login} no existe en Active Directory.");
        var correo = directorio.BuscarCorreo(login) ?? throw new DomainException($"{nombre} no tiene correo en Active Directory: no se le podría avisar.");
        lock (s.Sync) s.ResponsableInfra = new GerenteVoBo(login, nombre, correo);
    }

    public void QuitarResponsableInfra()
    {
        lock (s.Sync)
        {
            if (s.ResponsableInfra is null) throw new DomainException("No hay responsable de infraestructura asignado.");
            s.ResponsableInfra = null;
        }
    }

    public AsignacionInfra AsignarInfraestructura(int eventoId, int macroAreaId, string cambio, string usuario)
    {
        cambio = ReglasRecertificacion.Cambio(cambio);
        lock (s.Sync)
        {
            var (login, nombre, correo) = s.ResponsableInfra
                ?? throw new DomainException("No hay responsable de infraestructura asignado (Recertificación → Macro áreas).");
            if (!s.VoBos.Any(v => v.EventoId == eventoId && v.MacroAreaId == macroAreaId && v.Estatus == EstatusVoBo.AUTORIZADA))
                throw new DomainException("Las bajas de la macro área necesitan el VoBo autorizado del gerente antes de asignarlas.");
            var pendientes = Vigentes(eventoId).Count(x => x.Acceso.MacroAreaId == macroAreaId && x.Decision?.Decision == Decision.BAJA
                                                         && !s.BajasEjecutadas.Any(b => b.AccesoId == x.Acceso.AccesoId));
            if (pendientes == 0) throw new DomainException("La macro área no tiene bajas pendientes de ejecutar.");
            var a = new AsignacionInfra(s.NuevoId(), eventoId, macroAreaId, login, nombre, correo, cambio, pendientes, usuario, s.Ahora, false, null);
            s.AsignacionesInfra.Add(a);
            return a;
        }
    }

    public void RegistrarNotificacionInfra(long asignacionId, bool enviado, string? error)
    {
        lock (s.Sync)
        {
            var i = s.AsignacionesInfra.FindIndex(a => a.AsignacionId == asignacionId);
            if (i >= 0) s.AsignacionesInfra[i] = s.AsignacionesInfra[i] with { Notificado = enviado, Error = error };
        }
    }

    public CierreInfra SubirCierreInfra(int eventoId, int macroAreaId, string nombreArchivo, byte[] contenido, string? comentario, string usuario)
    {
        nombreArchivo = ReglasRecertificacion.ArchivoCierreInfra(nombreArchivo, contenido.Length);
        comentario = ReglasRecertificacion.ComentarioCierreInfra(comentario);
        lock (s.Sync)
        {
            if (Evento(eventoId).Estatus == EstatusEvento.BORRADOR) throw new DomainException("El evento todavía no está abierto.");
            var macro = s.MacroAreas.FirstOrDefault(m => m.MacroAreaId == macroAreaId) ?? throw new DomainException("La macro área no existe.");
            var bajas = Vigentes(eventoId).Where(x => x.Acceso.MacroAreaId == macroAreaId && x.Decision?.Decision == Decision.BAJA).ToList();
            ReglasRecertificacion.ValidarCierreInfra(bajas.Count, bajas.Count(x => !s.BajasEjecutadas.Any(b => b.AccesoId == x.Acceso.AccesoId)));
            var c = new CierreInfra(s.NuevoId(), eventoId, macroAreaId, macro.Nombre, nombreArchivo, contenido.Length, comentario, usuario, s.Ahora);
            s.CierresInfra.Add(c);
            s.ArchivosCierreInfra[c.CierreId] = contenido;
            return c;
        }
    }

    public IReadOnlyList<CierreInfra> CierresInfra(int eventoId)
    {
        lock (s.Sync)
            return s.CierresInfra.Where(c => c.EventoId == eventoId).OrderBy(c => c.MacroArea).ThenByDescending(c => c.CierreId).ToList();
    }

    public (string NombreArchivo, byte[] Contenido)? ArchivoCierreInfra(long cierreId)
    {
        lock (s.Sync)
            return s.CierresInfra.FirstOrDefault(c => c.CierreId == cierreId) is { } c && s.ArchivosCierreInfra.TryGetValue(cierreId, out var a)
                ? (c.NombreArchivo, a) : null;
    }

    public IReadOnlyList<VoBoGerente> VoBos(int eventoId)
    {
        lock (s.Sync)
            return s.VoBos.Where(v => v.EventoId == eventoId)
                .Select(v => v with { MacroArea = s.MacroAreas.First(m => m.MacroAreaId == v.MacroAreaId).Nombre })
                .OrderBy(v => v.MacroArea).ThenByDescending(v => v.VoBoId).ToList();
    }

    public IReadOnlyList<BajaVoBo> BajasVoBo(long voboId)
    {
        lock (s.Sync) return s.VoBoBajas.GetValueOrDefault(voboId) ?? [];
    }

    private bool VoBoActivo(int eventoId, int macroAreaId) =>
        s.VoBos.Any(v => v.EventoId == eventoId && v.MacroAreaId == macroAreaId && v.Estatus is EstatusVoBo.PENDIENTE or EstatusVoBo.ENVIADA or EstatusVoBo.AUTORIZADA);

    private void CancelarVoBo(int eventoId, int macroAreaId)
    {
        for (int i = 0; i < s.VoBos.Count; i++)
            if (s.VoBos[i] is { Estatus: EstatusVoBo.PENDIENTE or EstatusVoBo.ENVIADA or EstatusVoBo.AUTORIZADA } v && v.EventoId == eventoId && v.MacroAreaId == macroAreaId)
                s.VoBos[i] = v with { Estatus = EstatusVoBo.CANCELADA, MotivoCancelacion = "Cambió una decisión de la macro área después de la solicitud" };
    }

    private string AsegurarMacroArea(string nombre)
    {
        var m = s.MacroAreas.FirstOrDefault(x => x.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase));
        if (m is null) s.MacroAreas.Add(m = new MacroArea { MacroAreaId = (int)s.NuevoId(), Nombre = nombre });
        return m.Nombre;
    }

    public IReadOnlyList<CertificadoresMacroArea> Certificadores()
    {
        lock (s.Sync)
            return s.MacroAreas.OrderBy(m => m.Nombre).Select(m => new CertificadoresMacroArea(m.MacroAreaId, m.Nombre,
                m.Certificadores.Select(c => new CertificadorVigente(c.Nombre, c.UsuarioRed, c.Tipo)).ToList())).ToList();
    }

    public void AsignarCertificador(int macroAreaId, string usuarioRed, string tipo)
    {
        if (tipo is not ("TITULAR" or "DELEGADO")) throw new DomainException("El tipo debe ser titular o delegado.");
        var login = Login(usuarioRed);
        var nombre = directorio.BuscarNombre(login) ?? throw new DomainException($"El usuario {login} no existe en Active Directory.");
        lock (s.Sync)
        {
            var m = s.MacroAreas.FirstOrDefault(x => x.MacroAreaId == macroAreaId) ?? throw new DomainException("La macro área no existe.");
            // Mismo usuario: se actualiza. Mismo nombre sin usuario de red (llegó por el extracto): se le asigna.
            m.Certificadores.RemoveAll(c => string.Equals(c.UsuarioRed, login, StringComparison.OrdinalIgnoreCase)
                                            || (c.UsuarioRed is null && c.Nombre == nombre));
            m.Certificadores.Add(new CertificadorMacro(login, nombre, tipo));
            s.AsegurarResponsable(nombre);
        }
    }

    public void RetirarCertificador(int macroAreaId, string identificador)
    {
        lock (s.Sync)
        {
            var m = s.MacroAreas.FirstOrDefault(x => x.MacroAreaId == macroAreaId) ?? throw new DomainException("La macro área no existe.");
            var n = m.Certificadores.RemoveAll(c => string.Equals(c.UsuarioRed, identificador, StringComparison.OrdinalIgnoreCase)
                                                    || (c.UsuarioRed is null && c.Nombre == identificador));
            if (n == 0) throw new DomainException("No es certificador de esa macro área.");
        }
    }

    private string Login(string usuarioRed) => UsuarioRed.Normalizar(usuarioRed, opciones.Value.DominioPredeterminado);

    // Para buscar: una identidad que no es usuario de red no coincide con nadie (no ve nada), en vez de fallar.
    private string LoginParaBuscar(string identidad)
    {
        try { return Login(identidad); }
        catch (DomainException) { return identidad.Trim(); }
    }

    // Nombre del certificador para la bitácora de decisiones.
    private string NombreDe(string login) =>
        s.MacroAreas.SelectMany(m => m.Certificadores)
                    .FirstOrDefault(c => string.Equals(c.UsuarioRed, login, StringComparison.OrdinalIgnoreCase))?.Nombre ?? login;

    // ---- auxiliares ----

    private RecertEvento Evento(int eventoId) =>
        s.Eventos.FirstOrDefault(e => e.EventoId == eventoId) ?? throw new DomainException("El evento no existe.");

    // Seguridad por fila: macro áreas donde el usuario de red es titular o delegado.
    private HashSet<int> MacroAreasDe(string certificador)
    {
        var login = LoginParaBuscar(certificador);
        return s.MacroAreas.Where(m => m.Certificadores.Any(c => string.Equals(c.UsuarioRed, login, StringComparison.OrdinalIgnoreCase)))
                           .Select(m => m.MacroAreaId).ToHashSet();
    }

    private RecertAcceso AccesoAutorizado(long accesoId, string certificador)
    {
        var a = s.Accesos.FirstOrDefault(x => x.AccesoId == accesoId) ?? throw new DomainException("El acceso no existe.");
        if (!MacroAreasDe(certificador).Contains(a.MacroAreaId)) throw new DomainException("No eres certificador de la macro área de este acceso.");
        return a;
    }

    private List<(RecertAcceso Acceso, RecertDecision? Decision)> Vigentes(int eventoId) =>
        s.Accesos.Where(a => a.EventoId == eventoId).Select(a => (a, s.DecisionVigente(a.AccesoId))).ToList();

    private Conteo ConteoMacro(int eventoId, int macroAreaId) =>
        Contar(Vigentes(eventoId).Where(x => x.Acceso.MacroAreaId == macroAreaId).Select(x => x.Decision));

    private static Conteo Contar(IEnumerable<RecertDecision?> decisiones)
    {
        var l = decisiones.ToList();
        return new(l.Count, l.Count(d => d?.Decision == Decision.CONTINUA), l.Count(d => d?.Decision == Decision.BAJA));
    }
}
