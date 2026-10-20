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

public sealed record TableroBajas(RecertEvento Evento, IReadOnlyList<FilaBaja> Filas, int Decididas, int Ejecutadas, decimal? DiasPromedio);

// Auditoría: un renglón por acceso con las columnas de la consulta HANA (CONT_TI_RECERTIFICACION), en su orden.
// AreaResponsable = macro área; OKBAJA y COMENTARIOS = decisión vigente y comentario capturados en el portal.
public sealed record FilaAuditoria(long AccesoId, int EventoId, string Evento,
                                   string? Area, string? Content, string Rol, string Vista, string Usuario, string Name,
                                   string? Gobierno, string AreaResponsable, string? Responsable,
                                   Decision? OkBaja, string? Comentarios, int Accesos);

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
    string EvidenciaCsv(int eventoId);
    // Todos los accesos de un evento (o de todos los eventos si es nulo), ordenados por evento, macro área, vista y usuario.
    IReadOnlyList<FilaAuditoria> Auditoria(int? eventoId);
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
            return new TableroBajas(e, filas, filas.Count, ejecutadas.Count, dias);
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
            var f = fecha ?? s.Hoy;
            if (f > s.Hoy) throw new DomainException("La fecha de ejecución no puede ser futura.");
            s.BajasEjecutadas.Add(new RecertBajaEjecucion { AccesoId = accesoId, Ticket = ticket.Trim(), FechaEjecucion = f, EjecutadoPor = usuario });
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
                    x.Decision?.Decision, x.Decision?.Comentario, a.Accesos3m);
            }))
            .OrderByDescending(f => f.EventoId).ThenBy(f => f.AreaResponsable).ThenBy(f => f.Vista).ThenBy(f => f.Usuario).ToList();
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
