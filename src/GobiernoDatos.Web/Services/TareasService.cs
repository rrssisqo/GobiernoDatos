using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;
using Microsoft.Extensions.Options;

namespace GobiernoDatos.Web.Services;

public static class EstatusTarea
{
    public const string Abierta = "ABIERTA";
    public const string Terminada = "TERMINADA";
    public const string Cancelada = "CANCELADA";

    public static string Etiqueta(string estatus) => estatus switch
    {
        Abierta => "Abierta",
        Terminada => "Terminada",
        _ => "Cancelada",
    };
}

// Usuario de red ya validado contra el directorio.
public sealed record Asignacion(string UsuarioRed, string Nombre);

public sealed record FiltroTareas(string? Estatus, string? AsignadoA, int? DpId);

public sealed record FilaTarea(int TareaId, int VistaId, string Vista, string? DataProduct, string Etapa, string AsignadoA,
                               string AsignadoNombre, DateTime FechaAsignacion, string Estatus, DateTime? FechaCierre,
                               string? CerradoPor, string? MotivoCierre, int DiasTranscurridos);

public interface ITareasService
{
    IReadOnlyList<FilaTarea> Listar(FiltroTareas filtro);
    FilaTarea? TareaAbierta(int vistaId);
    // Asigna (o reasigna) la tarea de la etapa actual de la vista.
    void Asignar(int vistaId, Asignacion asignacion, string usuario);
    void Terminar(int tareaId, string usuario);
}

public static class ReglasTarea
{
    // Días transcurridos: hasta hoy mientras está abierta; congelados en la fecha de cierre.
    public static int Dias(DateTime asignacion, DateTime? cierre, DateOnly hoy) =>
        (cierre is null ? hoy : DateOnly.FromDateTime(cierre.Value)).DayNumber - DateOnly.FromDateTime(asignacion).DayNumber;

    public static void ValidarEtapaConTarea(EtapaVista etapa)
    {
        if (etapa.EsFinal) throw new DomainException($"La etapa «{etapa.Nombre}» no tiene tarea pendiente que asignar.");
    }
}

// Valida el usuario de red contra el directorio (Active Directory en producción).
public sealed class ResolutorUsuarios(IDirectorioUsuarios directorio, IOptions<GobiernoDatosOptions> opciones)
{
    public Asignacion Resolver(string usuarioRed)
    {
        var login = UsuarioRed.Normalizar(usuarioRed, opciones.Value.DominioPredeterminado);
        var nombre = directorio.BuscarNombre(login) ?? throw new DomainException($"El usuario {login} no existe en Active Directory.");
        return new Asignacion(login, nombre);
    }
}

public sealed class TareasService(GobiernoStore s) : ITareasService
{
    public IReadOnlyList<FilaTarea> Listar(FiltroTareas filtro)
    {
        lock (s.Sync)
            return s.Tareas
                .Where(t => filtro.Estatus is null || t.Estatus == filtro.Estatus)
                .Where(t => filtro.AsignadoA is null || string.Equals(t.AsignadoA, filtro.AsignadoA, StringComparison.OrdinalIgnoreCase))
                .Where(t => filtro.DpId is null || s.DpVigente(t.VistaId)?.DpId == filtro.DpId)
                .Select(Fila)
                .OrderBy(f => f.Estatus == EstatusTarea.Abierta ? 0 : 1).ThenBy(f => f.FechaAsignacion)
                .ToList();
    }

    public FilaTarea? TareaAbierta(int vistaId)
    {
        lock (s.Sync)
            return s.Tareas.Where(t => t.VistaId == vistaId && t.Estatus == EstatusTarea.Abierta).Select(Fila).FirstOrDefault();
    }

    public void Asignar(int vistaId, Asignacion asignacion, string usuario)
    {
        lock (s.Sync)
        {
            if (!s.Vistas.Any(v => v.VistaId == vistaId)) throw new DomainException("La vista no existe.");
            var etapa = Catalogos.EtapaPorOrden(s.EtapaOrdenActual(vistaId));
            ReglasTarea.ValidarEtapaConTarea(etapa);
            var abierta = s.Tareas.FirstOrDefault(t => t.VistaId == vistaId && t.Estatus == EstatusTarea.Abierta);
            if (abierta is not null && abierta.EtapaOrden == etapa.Orden
                && string.Equals(abierta.AsignadoA, asignacion.UsuarioRed, StringComparison.OrdinalIgnoreCase))
                throw new DomainException($"La tarea ya está asignada a {asignacion.Nombre}.");
            s.AbrirTarea(vistaId, etapa.Orden, asignacion, usuario, "Reasignada");
        }
    }

    public void Terminar(int tareaId, string usuario)
    {
        lock (s.Sync)
        {
            var t = s.Tareas.FirstOrDefault(x => x.TareaId == tareaId) ?? throw new DomainException("La tarea no existe.");
            if (t.Estatus != EstatusTarea.Abierta) throw new DomainException("La tarea ya está cerrada.");
            t.Estatus = EstatusTarea.Terminada;
            t.FechaCierre = s.Ahora;
            t.CerradoPor = usuario;
        }
    }

    private FilaTarea Fila(TareaVista t) =>
        new(t.TareaId, t.VistaId, s.Vistas.First(v => v.VistaId == t.VistaId).NombreNegocio, s.DpVigente(t.VistaId)?.Nombre,
            Catalogos.EtapaPorOrden(t.EtapaOrden).Nombre, t.AsignadoA, t.AsignadoNombre, t.FechaAsignacion, t.Estatus,
            t.FechaCierre, t.CerradoPor, t.MotivoCierre, ReglasTarea.Dias(t.FechaAsignacion, t.FechaCierre, s.Hoy));
}
