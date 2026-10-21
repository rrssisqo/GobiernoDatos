using System.Globalization;
using System.Text.RegularExpressions;
using GobiernoDatos.Web.Domain;

namespace GobiernoDatos.Web.Data;

// Llena el almacén con data/muestras siguiendo los mapeos de docs/01_contexto_y_fuentes.md.
// Lo que las muestras no traen (fechas de alta, certificadores, nombre del evento) se deja vacío
// y la interfaz lo muestra entre corchetes.
public static partial class MuestrasLoader
{
    // El Excel es el corte de la semana 39/26: sus valores se registran al cierre de esa semana.
    private static readonly Semana SemanaExcel = new(2026, 39);
    private const string UsuarioMigracion = "migración";

    public static void Cargar(GobiernoStore s, string ruta, UsuarioDemoOptions demo, string dominio = "MABENET")
    {
        var fecha = SemanaExcel.Fin;
        CargarExcel(s, Leer(ruta, "excel_seguimiento_sem39_26.csv"), fecha);
        CargarVistasSharePoint(s, Leer(ruta, "sharepoint_vistas_hana_gobernadas.csv"), fecha);
        CargarReglas(s, Leer(ruta, "sharepoint_reglas.csv"));

        foreach (var f in Leer(ruta, "recert_gobierno_macro_area.csv"))
        {
            if (!s.MacroAreas.Any(m => m.Nombre.Equals(f["MACRO_AREA"], StringComparison.OrdinalIgnoreCase)))
                s.MacroAreas.Add(new MacroArea { MacroAreaId = (int)s.NuevoId(), Nombre = f["MACRO_AREA"] });
            s.MapeoGobiernos[f["GOBIERNO"]] = new MapeoGobierno(f["GOBIERNO"], f["MACRO_AREA"], "migración", s.Ahora);
        }

        var evento = new RecertEvento { EventoId = (int)s.NuevoId(), Nombre = "[Evento muestra]" };
        s.Eventos.Add(evento);
        CargaExtracto.Cargar(s, evento, Leer(ruta, "recertificacion_extracto_muestra.csv"));
        if (evento.Rechazos.Count == 0) evento.Estatus = EstatusEvento.ABIERTO;

        // Los certificadores no vienen del extracto: el usuario demo certifica las macro áreas configuradas.
        foreach (var nombre in demo.MacroAreas)
            s.MacroAreas.FirstOrDefault(m => m.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase))?.Certificadores
             .Add(new CertificadorMacro(UsuarioRed.Normalizar(demo.Login, dominio), demo.Nombre, "TITULAR"));
    }

    private static List<Dictionary<string, string>> Leer(string ruta, string archivo) =>
        Csv.Leer(Csv.LeerTexto(File.ReadAllBytes(Path.Combine(ruta, archivo))));

    private static void CargarExcel(GobiernoStore s, List<Dictionary<string, string>> filas, DateOnly fecha)
    {
        // Columnas del Excel → rubro_id
        var columnas = new (string Col, int RubroId)[]
            { ("Glosario", 1), ("Catalogado", 2), ("DF", 3), ("Arq", 4), ("Linaje", 5), ("Perfil", 6), ("Reglas", 7) };

        foreach (var f in filas)
        {
            var gobierno = f["Gobierno"];
            if (!s.Gobiernos.Contains(gobierno)) s.Gobiernos.Add(gobierno);

            var dp = new DataProduct
            {
                DpId = (int)s.NuevoId(), Nombre = f["DataProduct"], Gobierno = gobierno,
                VistasIniciales = int.TryParse(f["Vistas"], out var n) ? n : null,
            };
            s.DataProducts.Add(dp);

            foreach (var (col, rubroId) in columnas)
            {
                var pct = ParsePct(f[col]);
                if (pct is null) continue;  // sin dato = 0, no se registra movimiento
                var estado = pct == 0 ? EstadoAvance.NO_INICIADO : pct == 100 ? EstadoAvance.CERRADO : EstadoAvance.EN_CURSO;
                s.MovAvances.Add(new MovAvance
                {
                    MovId = s.NuevoId(), DpId = dp.DpId, RubroId = rubroId, Fecha = fecha, Pct = pct.Value,
                    Estado = estado, Usuario = UsuarioMigracion,
                    EsInicial = Catalogos.Rubro(rubroId).Nivel == NivelRubro.VISTA,
                });
            }

            // "39/26 - texto. 38/26 - texto" → una nota por semana.
            foreach (Match m in NotaSemana().Matches(f["Comentarios"]))
            {
                if (!Semana.TryParseEtiqueta(m.Groups[1].Value, out var sem)) continue;
                s.Notas.Add(new NotaSemanal
                {
                    LogId = s.NuevoId(), DpId = dp.DpId, SemanaId = sem.Id, Comentario = m.Groups[2].Value.Trim(),
                    Usuario = UsuarioMigracion, FechaRegistro = sem.Fin.ToDateTime(TimeOnly.MinValue),
                });
            }
        }
    }

    private static void CargarVistasSharePoint(GobiernoStore s, List<Dictionary<string, string>> filas, DateOnly fecha)
    {
        foreach (var f in filas)
        {
            var gobierno = NullSiVacio(f["Área"]);
            if (gobierno is not null && !s.Gobiernos.Contains(gobierno)) s.Gobiernos.Add(gobierno);
            var responsable = NullSiVacio(f["Responsable"]);
            s.AsegurarResponsable(responsable);

            var vista = new VistaGobernada
            {
                VistaId = (int)s.NuevoId(), RutaTecnica = RutaTecnica.Normalizar(f["Vista Técnica"]),
                NombreNegocio = f["Vista"], Gobierno = gobierno, Responsable = responsable,
                NivelSeguridad = NullSiVacio(f["Nivel de Seguridad"]),
                Subdominio = NullSiVacio(f["SubDominioPadre"]) ?? NullSiVacio(f["Proceso"]),
                FechaAlta = fecha,
            };
            s.Vistas.Add(vista);

            // "Estatus" de SharePoint = etapa actual (movimiento inicial).
            var etapa = Catalogos.Etapas.FirstOrDefault(e => e.Nombre.Equals(f["Estatus"], StringComparison.OrdinalIgnoreCase));
            s.MovEtapas.Add(new MovEtapaVista
            {
                MovId = s.NuevoId(), VistaId = vista.VistaId, EtapaOrden = etapa?.Orden ?? 0, Fecha = fecha,
                MotivoId = 1, Comentario = "Etapa migrada de SharePoint", Usuario = UsuarioMigracion,
            });
        }
    }

    private static void CargarReglas(GobiernoStore s, List<Dictionary<string, string>> filas)
    {
        foreach (var f in filas)
        {
            var gobierno = NullSiVacio(f["GOBIERNO"]);
            s.AsegurarResponsable(NullSiVacio(f["RESPONSABLE"]));
            var regla = new Regla
            {
                ReglaId = (int)s.NuevoId(), Codigo = f["ID"], TipoRegla = TipoRegla(f["NOMBRE_REGLA"]),
                Descripcion = f["DESCRIPCION"], ReglaFuncional = NullSiVacio(f["DESCRIPCION_FUNCIONAL"]),
                Gobierno = gobierno, Responsable = NullSiVacio(f["RESPONSABLE"]),
                CamposEvaluados = NullSiVacio(f["NOMBRE_CAMPO"]), DimensionDq = NullSiVacio(f["DIMENSION DE CALIDAD"]),
                NoCsv = int.TryParse(f["NO_CSV"], out var no) ? no : null, NombreCsv = NullSiVacio(f["NOMBRE_CSV"]),
                EscribeLog = f["STAGE"].Equals("SI", StringComparison.OrdinalIgnoreCase),
                Etapa = NullSiVacio(f["FECHA_OPERACION_PRD"]) is not null ? "Operación PRD"
                      : NullSiVacio(f["FECHA_PASE_A_PRD"]) is not null ? "Pase a PRD" : null,
            };

            // NOMBRE_CUBOS puede traer varios objetos separados por ';'.
            foreach (var nombre in f["NOMBRE_CUBOS"].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var obj = s.Objetos.FirstOrDefault(o => o.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase));
                if (obj is null) s.Objetos.Add(obj = new ObjetoFuente { ObjetoId = (int)s.NuevoId(), Nombre = nombre.ToUpperInvariant() });
                regla.Objetos.Add(obj.ObjetoId);
            }
            s.Reglas.Add(regla);
        }
    }

    // Rl_Null y Rl_Nulos son el mismo tipo.
    private static string TipoRegla(string codigo) => codigo.ToUpperInvariant() switch
    {
        "RL_NULL" or "RL_NULOS" => "Nulos",
        "RL_CAT_PLANTAS" => "Catálogo de plantas",
        _ => codigo,
    };

    private static decimal? ParsePct(string v)
    {
        v = v.Trim().TrimEnd('%');
        return decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static string? NullSiVacio(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    [GeneratedRegex(@"(\d{2}/\d{2})\s*-\s*(.*?)(?=\s*\d{2}/\d{2}\s*-|$)", RegexOptions.Singleline)]
    private static partial Regex NotaSemana();
}
