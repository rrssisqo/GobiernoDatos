using System.Globalization;
using GobiernoDatos.Web.Domain;

namespace GobiernoDatos.Web.Data;

// Carga del extracto de la vista HANA de Recertificación a un evento (espejo de sp_cargar_extracto_recertificacion, v3):
// staging → validación → usuarios/roles/vistas → recert_acceso + vista_uso, y sincronización del inventario de vistas.
// "Área Responsable" = macro área; "Responsable" = certificador. AREA, CONTENT, GOBIERNO y Responsable se guardan
// además en cada acceso tal como llegaron (Auditoría, migración 008).
public static class CargaExtracto
{
    private sealed record Fila(int Num, string Usuario, string Nombre, string Rol, string Vista,
                               int Accesos, string MacroArea, string? Responsable, string? Area, string? Content, string? Gobierno);

    public static void Cargar(GobiernoStore s, RecertEvento evento, IReadOnlyList<Dictionary<string, string>> filas)
    {
        var hoy = evento.FechaExtracto ?? s.Hoy;
        var rechazos = evento.Rechazos;
        var validas = new List<Fila>();

        for (int i = 0; i < filas.Count; i++)
        {
            var f = filas[i];
            string usuario = f.GetValueOrDefault("USUARIO", "").ToUpperInvariant();
            string rol = f.GetValueOrDefault("ROL", "");
            string vista = f.GetValueOrDefault("VISTA", "");
            string macro = f.GetValueOrDefault("Área Responsable", f.GetValueOrDefault("Area Responsable", ""));
            string accesosTxt = f.GetValueOrDefault("ACCESO ULTIMOS 3 MESES", "0");

            string? motivo = null;
            if (usuario == "" || rol == "" || vista == "") motivo = "Usuario, rol o vista vacíos";
            else if (macro == "") motivo = "Sin Área Responsable (macro área)";
            else if (!int.TryParse(accesosTxt.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                motivo = "Accesos de 3 meses no numérico";

            if (motivo is not null) { rechazos.Add(new(i + 2, usuario, rol, vista, motivo)); continue; }

            validas.Add(new(i + 2, usuario, f.GetValueOrDefault("NOMBRE", usuario), rol.ToUpperInvariant(),
                RutaTecnica.Normalizar(vista), int.Parse(accesosTxt.Replace(",", ""), CultureInfo.InvariantCulture),
                macro, NullSiVacio(f.GetValueOrDefault("Responsable")), NullSiVacio(f.GetValueOrDefault("AREA")),
                NullSiVacio(f.GetValueOrDefault("CONTENT")), NullSiVacio(f.GetValueOrDefault("GOBIERNO"))));
        }

        // 1 rol = 1 vista, dentro del extracto y contra lo ya registrado.
        var rolesMultiples = validas.GroupBy(f => f.Rol).Where(g => g.Select(x => x.Vista).Distinct().Count() > 1).Select(g => g.Key).ToHashSet();
        var vistasMultiples = validas.GroupBy(f => f.Vista).Where(g => g.Select(x => x.Rol).Distinct().Count() > 1).Select(g => g.Key).ToHashSet();
        var vistaPorId = s.Vistas.ToDictionary(v => v.VistaId);

        var aceptadas = new List<Fila>();
        var vistos = new HashSet<(string, string)>();
        foreach (var f in validas)
        {
            var rolExistente = s.Roles.FirstOrDefault(r => r.Nombre == f.Rol);
            var vistaExistente = s.Vistas.FirstOrDefault(v => v.RutaTecnica == f.Vista);
            var rolDeVista = vistaExistente is null ? null : s.Roles.FirstOrDefault(r => r.VistaId == vistaExistente.VistaId);

            string? motivo =
                rolesMultiples.Contains(f.Rol) ? "Rol con más de una vista" :
                vistasMultiples.Contains(f.Vista) ? "Vista con más de un rol" :
                rolExistente is not null && vistaPorId[rolExistente.VistaId].RutaTecnica != f.Vista ? "El rol ya está asignado a otra vista" :
                rolDeVista is not null && rolDeVista.Nombre != f.Rol ? "La vista ya tiene otro rol dedicado" :
                !vistos.Add((f.Usuario, f.Rol)) ? "Usuario y rol duplicados en el extracto" :
                null;

            if (motivo is not null) rechazos.Add(new(f.Num, f.Usuario, f.Rol, f.Vista, motivo));
            else aceptadas.Add(f);
        }

        int rolesNuevos = 0, vistasNuevas = 0;
        foreach (var f in aceptadas)
        {
            var vista = s.Vistas.FirstOrDefault(v => v.RutaTecnica == f.Vista);
            if (vista is null)
            {
                // Vista nueva: entra al inventario en "Por Iniciar", sin gobierno ni DP hasta clasificarla.
                vista = new VistaGobernada
                {
                    VistaId = (int)s.NuevoId(), RutaTecnica = f.Vista, NombreNegocio = RutaTecnica.Vista(f.Vista),
                    FechaAlta = hoy, FechaPrimeraAparicion = hoy,
                };
                s.Vistas.Add(vista);
                s.MovEtapas.Add(new MovEtapaVista
                {
                    MovId = s.NuevoId(), VistaId = vista.VistaId, EtapaOrden = 0, Fecha = hoy, MotivoId = 1,
                    Comentario = $"Alta por carga del extracto · {evento.Nombre}", Usuario = "carga",
                });
                vistasNuevas++;
            }
            vista.FechaPrimeraAparicion ??= hoy;

            if (!s.Roles.Any(r => r.Nombre == f.Rol))
            {
                s.Roles.Add(new RolHana { Nombre = f.Rol, VistaId = vista.VistaId, Area = f.Area, Content = f.Content });
                rolesNuevos++;
            }

            var usuario = s.UsuariosHana.FirstOrDefault(u => u.Codigo == f.Usuario);
            if (usuario is null) s.UsuariosHana.Add(new UsuarioHana { Codigo = f.Usuario, Nombre = f.Nombre });
            else usuario.Nombre = f.Nombre;

            var macro = s.MacroAreas.FirstOrDefault(m => m.Nombre.Equals(f.MacroArea, StringComparison.OrdinalIgnoreCase));
            if (macro is null)
            {
                macro = new MacroArea { MacroAreaId = (int)s.NuevoId(), Nombre = f.MacroArea };
                s.MacroAreas.Add(macro);
            }
            if (f.Responsable is not null && macro.Certificadores.Count == 0)
            {
                macro.Certificadores.Add(new CertificadorMacro(null, f.Responsable, "TITULAR"));
                s.AsegurarResponsable(f.Responsable);
            }

            s.Accesos.Add(new RecertAcceso
            {
                AccesoId = s.NuevoId(), EventoId = evento.EventoId, UsuarioCodigo = f.Usuario, Rol = f.Rol,
                VistaId = vista.VistaId, MacroAreaId = macro.MacroAreaId, Accesos3m = f.Accesos,
                Area = f.Area, Content = f.Content, Gobierno = f.Gobierno, Responsable = f.Responsable,
            });
        }

        // Sincronización del inventario: presentes se marcan; ausentes se marcan, nunca se borran.
        var presentes = aceptadas.Select(f => f.Vista).ToHashSet();
        foreach (var v in s.Vistas)
        {
            v.EnUltimaRecertificacion = presentes.Contains(v.RutaTecnica);
            if (v.EnUltimaRecertificacion) v.FechaUltimaAparicion = hoy;
        }

        foreach (var g in s.Accesos.Where(a => a.EventoId == evento.EventoId).GroupBy(a => a.VistaId))
        {
            s.Usos.Add(new VistaUso
            {
                EventoId = evento.EventoId, VistaId = g.Key, Accesos3m = g.Sum(a => a.Accesos3m),
                Usuarios = g.Select(a => a.UsuarioCodigo).Distinct().Count(), Roles = g.Select(a => a.Rol).Distinct().Count(),
            });
        }

        evento.Validacion = new ValidacionCarga(rolesMultiples.Count, vistasMultiples.Count, rolesNuevos, vistasNuevas, rechazos.Count);
    }

    private static string? NullSiVacio(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
