using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;

namespace GobiernoDatos.Web.Services;

public enum PestanaVistas { Todas, Nuevas, Fuera }

public sealed record FiltroVistas(PestanaVistas Pestana, string? MacroArea, int? EtapaOrden, string? Texto);

public sealed record FilaVista(int VistaId, string Vista, string Paquete, string NombreNegocio, string? MacroArea,
                               string? DataProduct, string Etapa, int? Accesos3m, int? Usuarios, bool EnUltimaRecert);

public sealed record InventarioVistas(IReadOnlyList<FilaVista> Filas, int EnUltimaRecert, int AsignadasDp,
                                      int Nuevas, int Fuera, int Total, DateOnly? UltimaSincronizacion,
                                      string? EventoSincronizacion, IReadOnlyList<string> MacroAreas);

public sealed record ObjetoDeVista(int ObjetoId, string Nombre, IReadOnlyList<string> Reglas);

public sealed record MovEtapaDetalle(DateOnly Fecha, string Etapa, bool EsRegreso, string Motivo, string? Comentario, string Usuario);

public sealed record DetalleVista(VistaGobernada Vista, string? Rol, string? MacroArea, int? Accesos3m, int? Usuarios,
                                  int? DpId, EtapaVista Etapa, string? TareaPendiente, int DiasEnEtapa, int Ciclo,
                                  IReadOnlyList<ObjetoDeVista> Objetos, IReadOnlyList<MovEtapaDetalle> Historial);

public sealed record OpcionDp(int DpId, string Nombre, string Gobierno);

public sealed record OpcionObjeto(int ObjetoId, string Nombre);

public interface IVistasService
{
    InventarioVistas Inventario(FiltroVistas filtro);
    DetalleVista? Detalle(int vistaId);
    IReadOnlyList<OpcionDp> DataProductsAbiertos();
    IReadOnlyList<OpcionObjeto> Objetos();
    IReadOnlyList<string> Gobiernos();
    IReadOnlyList<string> Responsables();
    void Actualizar(int vistaId, string nombreNegocio, string? gobierno, int? dpId, string? responsable, string? nivelSeguridad, string usuario);
    // Al cambiar de etapa se cancela la tarea abierta; con asignarA se crea la tarea de la nueva etapa.
    void MoverEtapa(int vistaId, int etapaOrden, int? motivoId, string? comentario, string usuario, Asignacion? asignarA = null);
    void RelacionarObjeto(int vistaId, int objetoId);
    void QuitarObjeto(int vistaId, int objetoId);
}

public sealed class VistasService(GobiernoStore s) : IVistasService
{
    public InventarioVistas Inventario(FiltroVistas filtro)
    {
        lock (s.Sync)
        {
            var ultimoEvento = s.Eventos.Where(e => s.Usos.Any(u => u.EventoId == e.EventoId)).MaxBy(e => e.EventoId);
            var usos = s.Usos.Where(u => u.EventoId == ultimoEvento?.EventoId).ToDictionary(u => u.VistaId);
            var macroPorVista = s.Accesos.Where(a => a.EventoId == ultimoEvento?.EventoId)
                                         .GroupBy(a => a.VistaId)
                                         .ToDictionary(g => g.Key, g => s.MacroAreas.First(m => m.MacroAreaId == g.First().MacroAreaId).Nombre);

            var todas = s.Vistas.Where(v => v.Activa).Select(v =>
            {
                var orden = s.EtapaOrdenActual(v.VistaId);
                usos.TryGetValue(v.VistaId, out var uso);
                return (Vista: v, Orden: orden, Fila: new FilaVista(v.VistaId, RutaTecnica.Vista(v.RutaTecnica), RutaTecnica.Paquete(v.RutaTecnica),
                    v.NombreNegocio, macroPorVista.GetValueOrDefault(v.VistaId), s.DpVigente(v.VistaId)?.Nombre,
                    Catalogos.EtapaPorOrden(orden).Nombre, uso?.Accesos3m, uso?.Usuarios, v.EnUltimaRecertificacion));
            }).ToList();

            bool EsNueva(FilaVista f, VistaGobernada v) => f.DataProduct is null && v.Responsable is null;

            var filas = todas.Where(t => filtro.Pestana switch
                {
                    PestanaVistas.Nuevas => EsNueva(t.Fila, t.Vista),
                    PestanaVistas.Fuera => !t.Vista.EnUltimaRecertificacion,
                    _ => true,
                })
                .Where(t => filtro.MacroArea is null || t.Fila.MacroArea == filtro.MacroArea)
                .Where(t => filtro.EtapaOrden is null || t.Orden == filtro.EtapaOrden)
                .Where(t => string.IsNullOrWhiteSpace(filtro.Texto)
                            || t.Vista.RutaTecnica.Contains(filtro.Texto, StringComparison.OrdinalIgnoreCase)
                            || t.Vista.NombreNegocio.Contains(filtro.Texto, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Fila)
                .OrderByDescending(f => f.Accesos3m ?? -1).ThenBy(f => f.Vista)
                .ToList();

            return new InventarioVistas(filas,
                todas.Count(t => t.Vista.EnUltimaRecertificacion),
                todas.Count(t => t.Fila.DataProduct is not null),
                todas.Count(t => EsNueva(t.Fila, t.Vista)),
                todas.Count(t => !t.Vista.EnUltimaRecertificacion),
                todas.Count,
                ultimoEvento?.FechaExtracto, ultimoEvento?.Nombre,
                macroPorVista.Values.Distinct().Order().ToList());
        }
    }

    public DetalleVista? Detalle(int vistaId)
    {
        lock (s.Sync)
        {
            var v = s.Vistas.FirstOrDefault(x => x.VistaId == vistaId);
            if (v is null) return null;

            var ultimoEvento = s.Eventos.Where(e => s.Usos.Any(u => u.EventoId == e.EventoId && u.VistaId == vistaId)).MaxBy(e => e.EventoId);
            var uso = s.Usos.FirstOrDefault(u => u.EventoId == ultimoEvento?.EventoId && u.VistaId == vistaId);
            var acceso = s.Accesos.FirstOrDefault(a => a.EventoId == ultimoEvento?.EventoId && a.VistaId == vistaId);

            var ultimo = s.UltimoMovEtapa(vistaId);
            var etapa = Catalogos.EtapaPorOrden(ultimo?.EtapaOrden ?? 0);
            var movs = s.MovEtapas.Where(m => m.VistaId == vistaId).OrderBy(m => m.Fecha).ThenBy(m => m.MovId).ToList();
            // Ciclos (gd.vw_metricas_flujo_vista): 1 + veces que la vista salió de una etapa final.
            int ciclo = 1 + movs.Zip(movs.Skip(1)).Count(p => Catalogos.EtapaPorOrden(p.First.EtapaOrden).EsFinal);

            var objetos = s.VistaObjetos.Where(o => o.VistaId == vistaId && o.FechaHasta is null)
                .Select(o => new ObjetoDeVista(o.ObjetoId, s.Objetos.First(x => x.ObjetoId == o.ObjetoId).Nombre,
                    s.Reglas.Where(r => r.Objetos.Contains(o.ObjetoId)).Select(r => r.Codigo).ToList()))
                .ToList();

            return new DetalleVista(v, s.Roles.FirstOrDefault(r => r.VistaId == vistaId)?.Nombre,
                acceso is null ? null : s.MacroAreas.First(m => m.MacroAreaId == acceso.MacroAreaId).Nombre,
                uso?.Accesos3m, uso?.Usuarios, s.DpVigente(vistaId)?.DpId, etapa, etapa.TareaPendiente,
                s.Hoy.DayNumber - (ultimo?.Fecha ?? v.FechaAlta).DayNumber, ciclo, objetos,
                movs.AsEnumerable().Reverse().Select(m => new MovEtapaDetalle(m.Fecha, Catalogos.EtapaPorOrden(m.EtapaOrden).Nombre,
                    m.EsRegreso, Catalogos.NombreMotivo(m.MotivoId), m.Comentario, m.Usuario)).ToList());
        }
    }

    public IReadOnlyList<OpcionDp> DataProductsAbiertos()
    {
        lock (s.Sync)
            return s.DataProducts.Where(d => d.Estatus is "EN_CURSO" or "PLANEADO")
                                 .OrderBy(d => d.Nombre).Select(d => new OpcionDp(d.DpId, d.Nombre, d.Gobierno)).ToList();
    }

    public IReadOnlyList<OpcionObjeto> Objetos()
    {
        lock (s.Sync) return s.Objetos.OrderBy(o => o.Nombre).Select(o => new OpcionObjeto(o.ObjetoId, o.Nombre)).ToList();
    }

    public IReadOnlyList<string> Gobiernos() { lock (s.Sync) return [.. s.Gobiernos]; }
    public IReadOnlyList<string> Responsables() { lock (s.Sync) return [.. s.Responsables.Order()]; }

    public void Actualizar(int vistaId, string nombreNegocio, string? gobierno, int? dpId, string? responsable,
                           string? nivelSeguridad, string usuario)
    {
        lock (s.Sync)
        {
            var v = s.Vistas.FirstOrDefault(x => x.VistaId == vistaId) ?? throw new DomainException("La vista no existe.");
            if (string.IsNullOrWhiteSpace(nombreNegocio)) throw new DomainException("Indica el nombre de negocio de la vista.");
            if (nivelSeguridad is not null && !Catalogos.NivelesSeguridad.Contains(nivelSeguridad))
                throw new DomainException("Nivel de seguridad no válido.");
            if (gobierno is not null && !s.Gobiernos.Contains(gobierno)) throw new DomainException("Gobierno no válido.");

            v.NombreNegocio = nombreNegocio.Trim();
            v.Gobierno = gobierno;
            v.NivelSeguridad = nivelSeguridad;
            v.Responsable = string.IsNullOrWhiteSpace(responsable) ? null : responsable.Trim();
            s.AsegurarResponsable(v.Responsable);

            // Una vista pertenece a un solo DP vigente; el cambio cierra la vigencia anterior (fecha_hasta = ayer).
            var hoy = s.Hoy;
            var actual = s.DpVistas.FirstOrDefault(x => x.VistaId == vistaId && x.FechaHasta is null);
            if (actual?.DpId == dpId) return;
            if (dpId is not null && !s.DataProducts.Any(d => d.DpId == dpId && d.Estatus is "EN_CURSO" or "PLANEADO"))
                throw new DomainException("El data product no existe o está cerrado.");

            if (actual is not null)
            {
                if (actual.FechaDesde == hoy) s.DpVistas.Remove(actual);   // asignación del mismo día: se corrige
                else actual.FechaHasta = hoy.AddDays(-1);
            }
            if (dpId is not null) s.DpVistas.Add(new DpVista { DpId = dpId.Value, VistaId = vistaId, FechaDesde = hoy });
        }
    }

    // gd.sp_mover_etapa_vista: no repetir la etapa actual; un regreso exige motivo.
    public void MoverEtapa(int vistaId, int etapaOrden, int? motivoId, string? comentario, string usuario, Asignacion? asignarA = null)
    {
        lock (s.Sync)
        {
            if (!s.Vistas.Any(v => v.VistaId == vistaId)) throw new DomainException("La vista no existe.");
            if (Catalogos.Etapas.All(e => e.Orden != etapaOrden)) throw new DomainException("La etapa indicada no existe.");
            var tieneMov = s.MovEtapas.Any(m => m.VistaId == vistaId);
            var actual = s.EtapaOrdenActual(vistaId);
            if (tieneMov && etapaOrden == actual) throw new DomainException("La vista ya se encuentra en esa etapa.");
            var esRegreso = etapaOrden < actual;
            if (esRegreso && motivoId is null) throw new DomainException("Un regreso de etapa requiere indicar el motivo.");
            if (motivoId is not null && Catalogos.Motivos.All(m => m.MotivoId != motivoId || m.AplicaA == "REGLA"))
                throw new DomainException("Motivo no válido para vistas.");
            if (asignarA is not null) ReglasTarea.ValidarEtapaConTarea(Catalogos.EtapaPorOrden(etapaOrden));

            s.MovEtapas.Add(new MovEtapaVista
            {
                MovId = s.NuevoId(), VistaId = vistaId, EtapaOrden = etapaOrden, Fecha = s.Hoy,
                MotivoId = motivoId ?? (esRegreso ? null : 1), EsRegreso = esRegreso,
                Comentario = string.IsNullOrWhiteSpace(comentario) ? null : comentario.Trim(), Usuario = usuario,
            });
            s.AbrirTarea(vistaId, etapaOrden, asignarA, usuario, "Cambio de etapa");
        }
    }

    public void RelacionarObjeto(int vistaId, int objetoId)
    {
        lock (s.Sync)
        {
            if (!s.Objetos.Any(o => o.ObjetoId == objetoId)) throw new DomainException("El objeto no existe.");
            if (s.VistaObjetos.Any(o => o.VistaId == vistaId && o.ObjetoId == objetoId && o.FechaHasta is null))
                throw new DomainException("El objeto ya alimenta esta vista.");
            s.VistaObjetos.Add(new VistaObjeto { VistaId = vistaId, ObjetoId = objetoId, FechaDesde = s.Hoy });
        }
    }

    public void QuitarObjeto(int vistaId, int objetoId)
    {
        lock (s.Sync)
        {
            var vo = s.VistaObjetos.FirstOrDefault(o => o.VistaId == vistaId && o.ObjetoId == objetoId && o.FechaHasta is null)
                     ?? throw new DomainException("El objeto no está relacionado con la vista.");
            if (vo.FechaDesde == s.Hoy) s.VistaObjetos.Remove(vo);
            else vo.FechaHasta = s.Hoy.AddDays(-1);
        }
    }
}
