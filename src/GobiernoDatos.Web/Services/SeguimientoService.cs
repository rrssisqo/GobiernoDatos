using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;

namespace GobiernoDatos.Web.Services;

// Estado solo se informa donde existe (rubros de vista); en agregados de DP queda nulo.
public sealed record CeldaRubro(int RubroId, decimal Pct, EstadoAvance? Estado = null);

public sealed record NotaHistorial(Semana Semana, string Texto);

// Nota = la más reciente hasta la semana consultada (NotaSemana: su etiqueta si es de una semana anterior).
// Historial = todas las notas hasta esa semana, una por semana, de la más reciente a la más antigua.
// CambioEnSemana = en la semana consultada hubo avance de un rubro del DP o de una de sus vistas, o una nota de esa semana.
public sealed record FilaSeguimiento(int DpId, string Gobierno, string DataProduct, string Estatus, decimal Avance,
                                     int Vistas, IReadOnlyList<CeldaRubro> Rubros, string? Nota, string? NotaSemana,
                                     IReadOnlyList<NotaHistorial> Historial, bool CambioEnSemana = false)
{
    // Varias notas de la misma semana se unen; formato del Excel al exportar: "42/26 - texto. 41/26 - texto".
    public static IReadOnlyList<NotaHistorial> Agrupar(IEnumerable<(int SemanaId, string Texto)> notas) =>
        notas.GroupBy(n => n.SemanaId).OrderByDescending(g => g.Key)
             .Select(g => new NotaHistorial(Semana.DeId(g.Key), string.Join(" · ", g.Select(x => x.Texto)))).ToList();

    public string HistorialTexto => string.Join(" ", Historial.Select(n => $"{n.Semana.Etiqueta} - {n.Texto.TrimEnd('.')}."));
}

public sealed record TableroSeguimiento(Semana Semana, IReadOnlyList<FilaSeguimiento> Filas, decimal AvanceGlobal,
                                        int VistasEnDp, int VistasInventario, IReadOnlyList<string> DpCompletos,
                                        int RubrosPendientes, int RubrosTotales);

public sealed record RubroDpDetalle(Rubro Rubro, decimal Pct, EstadoAvance? Estado, DateOnly? Fecha);

public sealed record VistaEnDp(int VistaId, string Nombre, string Ruta, string Etapa, int EtapaOrden,
                               IReadOnlyList<CeldaRubro> Rubros, int Reglas, Rubro? RubroActivo);

// Vista = nombre de la vista cuando el movimiento es de una vista (para agrupar la bitácora por vista); nulo si es del data product.
public sealed record EntradaBitacora(Semana Semana, DateOnly Fecha, string Tipo, string Detalle, string Usuario, string? Vista = null);

public sealed record DetalleDp(DataProduct Dp, Semana Semana, decimal Avance, IReadOnlyList<RubroDpDetalle> RubrosDp,
                               IReadOnlyList<VistaEnDp> Vistas, IReadOnlyList<CeldaRubro> PromedioVistas,
                               bool UsaValoresIniciales, IReadOnlyList<EntradaBitacora> Bitacora);

public sealed record ResumenDp(int DpId, string Nombre, string Gobierno, string Estatus, string? Responsable,
                               decimal Avance, int Vistas);

public sealed record ResultadoImportacion(int DataProductsNuevos, int DataProductsActualizados, int Avances, int Notas,
                                          IReadOnlyList<string> GobiernosNuevos);

public sealed record ResultadoEliminacion(string Nombre, int Avances, int Notas, int Vistas);

public interface ISeguimientoService
{
    IReadOnlyList<Semana> Semanas();
    Semana SemanaActual();
    TableroSeguimiento Tablero(Semana semana, string? gobierno);
    IReadOnlyList<ResumenDp> DataProducts();
    DetalleDp? Detalle(int dpId, Semana semana);
    IReadOnlyList<string> Gobiernos();
    IReadOnlyList<string> Responsables();
    int CrearDataProduct(string nombre, string gobierno, string? responsable, string usuario);
    void RegistrarAvanceDp(int dpId, int rubroId, EstadoAvance estado, decimal? pct, string? nota, string usuario);
    void RegistrarAvanceVista(int dpId, int vistaId, int rubroId, EstadoAvance estado, decimal? pct, string? nota, string usuario);
    void AgregarNota(int dpId, string nota, string usuario);
    void CerrarDataProduct(int dpId, string usuario);
    // Edición de los datos del data product (nombre, gobierno, responsable); no toca avances ni notas.
    void EditarDataProduct(int dpId, string nombre, string gobierno, string? responsable, string usuario);
    // Elimina el data product con sus avances, notas semanales y asignaciones de vistas (las vistas siguen en el inventario).
    ResultadoEliminacion EliminarDataProduct(int dpId, string usuario);
    // Importación del Excel de seguimiento: crea o actualiza data products y registra avances (solo los que cambian)
    // y notas por semana. Los movimientos se fechan al cierre de la semana de corte (o hoy si es la semana en curso).
    ResultadoImportacion ImportarSeguimiento(IReadOnlyList<FilaSeguimientoImportada> filas, Semana corte, string usuario);
}

public sealed class SeguimientoService(GobiernoStore s) : ISeguimientoService
{
    public Semana SemanaActual() => Semana.De(s.Hoy);

    // Desde la primera semana con movimientos hasta la actual, más reciente primero.
    public IReadOnlyList<Semana> Semanas()
    {
        lock (s.Sync)
        {
            var actual = SemanaActual();
            var primera = s.MovAvances.Select(a => a.Fecha).Concat(s.Notas.Select(n => Semana.DeId(n.SemanaId).Inicio))
                                      .DefaultIfEmpty(actual.Inicio).Min();
            var lista = new List<Semana>();
            for (var w = actual; w.Fin >= primera && lista.Count < 104; w = w.Anterior()) lista.Add(w);
            return lista;
        }
    }

    public IReadOnlyList<string> Gobiernos() { lock (s.Sync) return [.. s.Gobiernos]; }
    public IReadOnlyList<string> Responsables() { lock (s.Sync) return [.. s.Responsables.Order()]; }

    public TableroSeguimiento Tablero(Semana semana, string? gobierno)
    {
        lock (s.Sync)
        {
            var corte = semana.Fin;
            var dps = s.DataProducts.Where(d => d.Estatus != "CANCELADO"
                                              && (d.FechaAlta is null || d.FechaAlta <= corte)
                                              && (d.FechaCierre is null || Semana.De(d.FechaCierre.Value).Id >= semana.Id))
                                    .ToList();

            var filas = dps.Select(dp =>
            {
                var rubros = Catalogos.Rubros.Select(r => new CeldaRubro(r.RubroId, s.PctRubroDp(dp, r, corte))).ToList();
                var notasDp = s.Notas.Where(n => n.DpId == dp.DpId && n.SemanaId <= semana.Id)
                                     .OrderBy(n => n.SemanaId).ThenBy(n => n.LogId).ToList();
                var nota = notasDp.LastOrDefault();
                var vistasDp = s.DpVistas.Where(x => x.DpId == dp.DpId).ToList();
                var cambio = notasDp.Any(n => n.SemanaId == semana.Id)
                    || s.MovAvances.Any(a => a.Fecha >= semana.Inicio && a.Fecha <= semana.Fin &&
                                             (a.DpId == dp.DpId || a.VistaId is int v && vistasDp.Any(x => x.VistaId == v && x.VigenteEn(a.Fecha))));
                return new FilaSeguimiento(dp.DpId, dp.Gobierno, dp.Nombre, dp.Estatus,
                    Avance.PromedioPonderado(rubros.Select(c => (c.Pct, Catalogos.Rubro(c.RubroId).Peso))),
                    s.NumVistasDp(dp, corte), rubros, nota?.Comentario,
                    nota is null || nota.SemanaId == semana.Id ? null : Semana.DeId(nota.SemanaId).Etiqueta,
                    FilaSeguimiento.Agrupar(notasDp.Select(n => (n.SemanaId, n.Comentario))), cambio);
            }).ToList();

            var visibles = gobierno is null ? filas : filas.Where(f => f.Gobierno == gobierno).ToList();
            var activos = visibles.Where(f => f.Estatus is "EN_CURSO" or "PLANEADO").ToList();

            return new TableroSeguimiento(
                semana,
                visibles.OrderBy(f => f.Gobierno).ThenBy(f => f.DataProduct).ToList(),
                activos.Count == 0 ? 0 : Math.Round(activos.Average(f => f.Avance), 0),
                visibles.Sum(f => f.Vistas),
                s.Vistas.Count(v => v.Activa),
                visibles.Where(f => f.Avance >= 100).Select(f => f.DataProduct).ToList(),
                visibles.Sum(f => f.Rubros.Count(r => r.Pct < 100)),
                visibles.Count * Catalogos.Rubros.Count);
        }
    }

    public IReadOnlyList<ResumenDp> DataProducts()
    {
        lock (s.Sync)
        {
            var hoy = s.Hoy;
            return s.DataProducts.OrderBy(d => d.Gobierno).ThenBy(d => d.Nombre)
                .Select(d => new ResumenDp(d.DpId, d.Nombre, d.Gobierno, d.Estatus, d.Responsable, s.PctDp(d, hoy), s.NumVistasDp(d, hoy)))
                .ToList();
        }
    }

    public DetalleDp? Detalle(int dpId, Semana semana)
    {
        lock (s.Sync)
        {
            var dp = s.DataProducts.FirstOrDefault(d => d.DpId == dpId);
            if (dp is null) return null;
            var corte = semana.Fin;

            var rubrosDp = Catalogos.Rubros.Where(r => r.Nivel == NivelRubro.DP).Select(r =>
            {
                var ultima = s.UltimaCapturaDp(dp.DpId, r.RubroId, corte);
                return new RubroDpDetalle(r, ultima?.Pct ?? 0, ultima?.Estado, ultima?.Fecha);
            }).ToList();

            var rubrosVista = Catalogos.Rubros.Where(r => r.Nivel == NivelRubro.VISTA).ToList();
            var vistas = s.VistasDeDp(dp.DpId, corte).Select(v =>
            {
                var orden = s.EtapaOrdenActual(v.VistaId, corte);
                var objetos = s.VistaObjetos.Where(o => o.VistaId == v.VistaId && o.FechaHasta is null).Select(o => o.ObjetoId).ToHashSet();
                return new VistaEnDp(v.VistaId, v.NombreNegocio, v.RutaTecnica, Catalogos.EtapaPorOrden(orden).Nombre, orden,
                    rubrosVista.Select(r => { var c = s.PctRubroVista(v.VistaId, r, corte); return new CeldaRubro(r.RubroId, c.Pct, c.Estado); }).ToList(),
                    s.Reglas.Count(r => r.Activa && r.Objetos.Any(objetos.Contains)),
                    Catalogos.RubroActivo(s.EtapaOrdenActual(v.VistaId)));
            }).ToList();

            var promedio = rubrosVista.Select(r => new CeldaRubro(r.RubroId, s.PctRubroDp(dp, r, corte))).ToList();
            return new DetalleDp(dp, semana, s.PctDp(dp, corte), rubrosDp, vistas, promedio, vistas.Count == 0, Bitacora(dp.DpId));
        }
    }

    // Línea de tiempo unificada (gd.vw_bitacora_actividades).
    private List<EntradaBitacora> Bitacora(int dpId)
    {
        var vistasDp = s.DpVistas.Where(x => x.DpId == dpId).ToList();
        string NombreVista(int id) => s.Vistas.First(v => v.VistaId == id).NombreNegocio;
        bool EnDp(int vistaId, DateOnly f) => vistasDp.Any(x => x.VistaId == vistaId && x.VigenteEn(f));

        var entradas = new List<EntradaBitacora>();
        entradas.AddRange(s.Notas.Where(n => n.DpId == dpId).Select(n => new EntradaBitacora(
            Semana.DeId(n.SemanaId), DateOnly.FromDateTime(n.FechaRegistro), "Nota semanal",
            n.RubroId is null ? n.Comentario : $"{Catalogos.Rubro(n.RubroId.Value).Nombre} · {n.Comentario}", n.Usuario,
            n.VistaId is int nv ? NombreVista(nv) : null)));
        entradas.AddRange(s.MovAvances.Where(a => a.DpId == dpId && !a.EsInicial).Select(a => new EntradaBitacora(
            Semana.De(a.Fecha), a.Fecha, "Avance de rubro DP",
            $"{Catalogos.Rubro(a.RubroId).Nombre}: {a.Pct:0.#}% {Avance.Etiqueta(a.Estado)}", a.Usuario)));
        entradas.AddRange(s.MovAvances.Where(a => a.VistaId is int v && EnDp(v, a.Fecha)).Select(a => new EntradaBitacora(
            Semana.De(a.Fecha), a.Fecha, "Avance de rubro",
            $"{Catalogos.Rubro(a.RubroId).Nombre}: {a.Pct:0.#}% {Avance.Etiqueta(a.Estado)}", a.Usuario, NombreVista(a.VistaId!.Value))));
        entradas.AddRange(s.MovEtapas.Where(m => EnDp(m.VistaId, m.Fecha)).Select(m => new EntradaBitacora(
            Semana.De(m.Fecha), m.Fecha, "Cambio de etapa",
            $"{Catalogos.EtapaPorOrden(m.EtapaOrden).Nombre}{(m.EsRegreso ? " (regreso)" : "")} · {Catalogos.NombreMotivo(m.MotivoId)}{(m.Comentario is null ? "" : " · " + m.Comentario)}",
            m.Usuario, NombreVista(m.VistaId))));
        entradas.AddRange(vistasDp.Select(x => new EntradaBitacora(
            Semana.De(x.FechaDesde), x.FechaDesde, "Vista agregada al DP", "Vista agregada al data product", "", NombreVista(x.VistaId))));
        entradas.AddRange(vistasDp.Where(x => x.FechaHasta is not null).Select(x => new EntradaBitacora(
            Semana.De(x.FechaHasta!.Value), x.FechaHasta!.Value, "Vista retirada del DP", "Vista retirada del data product", "", NombreVista(x.VistaId))));

        return entradas.OrderByDescending(e => e.Semana.Id).ThenByDescending(e => e.Fecha).ToList();
    }

    public int CrearDataProduct(string nombre, string gobierno, string? responsable, string usuario)
    {
        lock (s.Sync)
        {
            nombre = nombre.Trim();
            if (nombre.Length == 0) throw new DomainException("Indica el nombre del data product.");
            if (s.DataProducts.Any(d => d.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase)))
                throw new DomainException("Ya existe un data product con ese nombre.");
            if (!s.Gobiernos.Contains(gobierno)) throw new DomainException("Selecciona un gobierno válido.");
            s.AsegurarResponsable(responsable);
            var dp = new DataProduct
            {
                DpId = (int)s.NuevoId(), Nombre = nombre, Gobierno = gobierno,
                Responsable = string.IsNullOrWhiteSpace(responsable) ? null : responsable.Trim(), FechaAlta = s.Hoy,
            };
            s.DataProducts.Add(dp);
            return dp.DpId;
        }
    }

    public void RegistrarAvanceDp(int dpId, int rubroId, EstadoAvance estado, decimal? pct, string? nota, string usuario)
    {
        lock (s.Sync)
        {
            var dp = DpAbierto(dpId);
            var rubro = Catalogos.Rubros.FirstOrDefault(r => r.RubroId == rubroId && r.Nivel == NivelRubro.DP)
                        ?? throw new DomainException("El rubro no es de nivel data product.");
            s.MovAvances.Add(new MovAvance
            {
                MovId = s.NuevoId(), DpId = dp.DpId, RubroId = rubro.RubroId, Fecha = s.Hoy,
                Pct = Avance.NormalizarPct(estado, pct), Estado = estado, Comentario = Limpiar(nota), Usuario = usuario,
            });
            AgregarNotaInterna(dp.DpId, nota, rubro.RubroId, null, usuario);
        }
    }

    // gd.sp_registrar_avance_vista (migración 005): cualquier rubro de vista, en cualquier etapa.
    public void RegistrarAvanceVista(int dpId, int vistaId, int rubroId, EstadoAvance estado, decimal? pct, string? nota, string usuario)
    {
        lock (s.Sync)
        {
            var dp = DpAbierto(dpId);
            if (!s.VistasDeDp(dp.DpId, s.Hoy).Any(v => v.VistaId == vistaId))
                throw new DomainException("La vista no pertenece al data product.");
            // Cualquier rubro de vista, en cualquier etapa (ReglaAvanceVista: el último evento manda).
            var rubro = Catalogos.Rubros.FirstOrDefault(r => r.RubroId == rubroId && r.Nivel == NivelRubro.VISTA)
                        ?? throw new DomainException("El rubro no es de nivel vista.");
            s.MovAvances.Add(new MovAvance
            {
                MovId = s.NuevoId(), VistaId = vistaId, RubroId = rubro.RubroId, Fecha = s.Hoy,
                Pct = Avance.NormalizarPct(estado, pct), Estado = estado, Comentario = Limpiar(nota), Usuario = usuario,
            });
            AgregarNotaInterna(dp.DpId, nota, rubro.RubroId, vistaId, usuario);
        }
    }

    public void AgregarNota(int dpId, string nota, string usuario)
    {
        lock (s.Sync)
        {
            if (string.IsNullOrWhiteSpace(nota)) throw new DomainException("La nota está vacía.");
            AgregarNotaInterna(DpAbierto(dpId).DpId, nota, null, null, usuario);
        }
    }

    public void CerrarDataProduct(int dpId, string usuario)
    {
        lock (s.Sync)
        {
            var dp = DpAbierto(dpId);
            dp.Estatus = "CERRADO";
            dp.FechaCierre = s.Hoy;
            AgregarNotaInterna(dp.DpId, "Se cierra el data product.", null, null, usuario);
        }
    }

    public void EditarDataProduct(int dpId, string nombre, string gobierno, string? responsable, string usuario)
    {
        lock (s.Sync)
        {
            var dp = s.DataProducts.FirstOrDefault(d => d.DpId == dpId) ?? throw new DomainException("El data product no existe.");
            nombre = nombre.Trim();
            if (nombre.Length == 0) throw new DomainException("Indica el nombre del data product.");
            if (s.DataProducts.Any(d => d.DpId != dpId && d.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase)))
                throw new DomainException("Ya existe un data product con ese nombre.");
            if (!s.Gobiernos.Contains(gobierno)) throw new DomainException("Selecciona un gobierno válido.");
            s.AsegurarResponsable(responsable);
            dp.Nombre = nombre;
            dp.Gobierno = gobierno;
            dp.Responsable = string.IsNullOrWhiteSpace(responsable) ? null : responsable.Trim();
        }
    }

    public ResultadoEliminacion EliminarDataProduct(int dpId, string usuario)
    {
        lock (s.Sync)
        {
            var dp = s.DataProducts.FirstOrDefault(d => d.DpId == dpId) ?? throw new DomainException("El data product no existe.");
            var avances = s.MovAvances.RemoveAll(a => a.DpId == dpId);
            var notas = s.Notas.RemoveAll(n => n.DpId == dpId);
            var vistas = s.DpVistas.RemoveAll(x => x.DpId == dpId);
            s.DataProducts.Remove(dp);
            return new ResultadoEliminacion(dp.Nombre, avances, notas, vistas);
        }
    }

    public ResultadoImportacion ImportarSeguimiento(IReadOnlyList<FilaSeguimientoImportada> filas, Semana corte, string usuario)
    {
        lock (s.Sync)
        {
            var fecha = corte.Fin < s.Hoy ? corte.Fin : s.Hoy;
            int nuevos = 0, actualizados = 0, avances = 0, notas = 0;
            var gobiernosNuevos = new List<string>();
            foreach (var f in filas)
            {
                if (!s.Gobiernos.Contains(f.Gobierno)) { s.Gobiernos.Add(f.Gobierno); gobiernosNuevos.Add(f.Gobierno); }
                var dp = s.DataProducts.FirstOrDefault(d => d.Nombre.Equals(f.DataProduct, StringComparison.OrdinalIgnoreCase));
                if (dp is null)
                {
                    dp = new DataProduct { DpId = (int)s.NuevoId(), Nombre = f.DataProduct, Gobierno = f.Gobierno, FechaAlta = corte.Inicio };
                    s.DataProducts.Add(dp);
                    nuevos++;
                }
                else actualizados++;
                dp.Gobierno = f.Gobierno;
                dp.VistasIniciales = f.Vistas;

                foreach (var (rubroId, pct) in f.Rubros)
                {
                    if (s.UltimaCapturaDp(dp.DpId, rubroId, fecha)?.Pct == pct) continue;   // sin cambio
                    s.MovAvances.Add(new MovAvance
                    {
                        MovId = s.NuevoId(), DpId = dp.DpId, RubroId = rubroId, Fecha = fecha, Pct = pct, Estado = EstadoPorPct(pct),
                        Comentario = $"Importación seguimiento {corte.Etiqueta}", Usuario = usuario,
                        EsInicial = Catalogos.Rubro(rubroId).Nivel == NivelRubro.VISTA,
                    });
                    avances++;
                }
                foreach (var n in f.Notas)
                {
                    if (s.Notas.Any(x => x.DpId == dp.DpId && x.SemanaId == n.Semana.Id && x.Comentario == n.Texto)) continue;
                    s.Notas.Add(new NotaSemanal
                    {
                        LogId = s.NuevoId(), DpId = dp.DpId, SemanaId = n.Semana.Id, Comentario = n.Texto, Usuario = usuario,
                        FechaRegistro = n.Semana.Fin.ToDateTime(TimeOnly.MinValue),
                    });
                    notas++;
                }
            }
            return new ResultadoImportacion(nuevos, actualizados, avances, notas, gobiernosNuevos);
        }
    }

    internal static EstadoAvance EstadoPorPct(decimal pct) =>
        pct == 0 ? EstadoAvance.NO_INICIADO : pct == 100 ? EstadoAvance.CERRADO : EstadoAvance.EN_CURSO;

    private DataProduct DpAbierto(int dpId)
    {
        var dp = s.DataProducts.FirstOrDefault(d => d.DpId == dpId) ?? throw new DomainException("El data product no existe.");
        if (dp.Estatus is "CERRADO" or "CANCELADO") throw new DomainException("El data product está cerrado.");
        return dp;
    }

    private void AgregarNotaInterna(int dpId, string? nota, int? rubroId, int? vistaId, string usuario)
    {
        if (Limpiar(nota) is not string texto) return;
        s.Notas.Add(new NotaSemanal
        {
            LogId = s.NuevoId(), DpId = dpId, SemanaId = Semana.De(s.Hoy).Id, RubroId = rubroId, VistaId = vistaId,
            Comentario = texto, Usuario = usuario, FechaRegistro = s.Ahora,
        });
    }

    private static string? Limpiar(string? t) => string.IsNullOrWhiteSpace(t) ? null : t.Trim();
}
