using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;
using Microsoft.Extensions.Options;
using static GobiernoDatos.Web.Services.RecordatoriosService;

namespace GobiernoDatos.Web.Services;

public sealed record ResultadoAsignacionInfra(AsignacionInfra Asignacion, bool Notificado, string? Error);

// Asignación de las bajas de una macro área a infraestructura: registra la asignación y avisa al responsable por correo
// con el número de cambio y las bajas en Excel. Si el correo falla, la asignación queda registrada con el error.
public sealed class NotificacionesInfra(IRecertificacionService recert, IEnviadorCorreo enviador, IOptions<GobiernoDatosOptions> opciones,
                                        ILogger<NotificacionesInfra> log)
{
    private const string TipoXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public ResultadoAsignacionInfra Asignar(int eventoId, int macroAreaId, string cambio, string usuario)
    {
        var a = recert.AsignarInfraestructura(eventoId, macroAreaId, cambio, usuario);
        var tablero = recert.Bajas(eventoId)!;
        var macro = tablero.MacroAreas.First(m => m.MacroAreaId == macroAreaId);
        var bajas = tablero.Filas.Where(f => f.MacroArea == macro.MacroArea && f.Ejecucion is null).OrderBy(f => f.UsuarioNombre).ToList();
        try
        {
            enviador.Enviar(Mensaje(a, tablero.Evento, macro, bajas));
            recert.RegistrarNotificacionInfra(a.AsignacionId, true, null);
            return new(a, true, null);
        }
        catch (DomainException ex)
        {
            log.LogWarning("No se pudo avisar a {Responsable} de la asignación {Asignacion}: {Error}", a.ResponsableCorreo, a.AsignacionId, ex.Message);
            recert.RegistrarNotificacionInfra(a.AsignacionId, false, ex.Message);
            return new(a, false, ex.Message);
        }
    }

    internal MensajeCorreo Mensaje(AsignacionInfra a, RecertEvento evento, BajasMacroArea macro, IReadOnlyList<FilaBaja> bajas)
    {
        var vobo = macro.VoBo;
        var archivo = $"bajas_{EscritorExcel.NombreArchivo(macro.MacroArea)}_{EscritorExcel.NombreArchivo(a.Cambio)}.xlsx";
        var excel = EscritorExcel.Libro("Bajas",
            ["Cambio", "Evento", "Macro área", "Usuario", "Nombre", "Rol a retirar", "Vista", "Comentario", "Decidió", "Fecha decisión", "VoBo", "Fecha VoBo"],
            bajas.Select(f => new object?[]
            {
                a.Cambio, evento.Nombre, macro.MacroArea, f.UsuarioCodigo, f.UsuarioNombre, f.Rol, f.Vista, f.Comentario, f.DecididoPor,
                f.FechaDecision, vobo?.RespondidoPor, vobo?.FechaRespuesta,
            }));

        var liga = string.IsNullOrWhiteSpace(opciones.Value.Correo.UrlPortal) ? null
            : opciones.Value.Correo.UrlPortal.TrimEnd('/') + $"/Recertificacion/Bajas?evento={evento.EventoId}&macro={Uri.EscapeDataString(macro.MacroArea)}";
        var filas = string.Concat(bajas.Take(20).Select(f =>
            $"<tr><td style=\"padding:4px 10px;border-bottom:1px solid #DDE1E6\">{H(f.UsuarioNombre)}<br><span style=\"color:#5B6672\">{H(f.UsuarioCodigo)}</span></td>" +
            $"<td style=\"padding:4px 10px;border-bottom:1px solid #DDE1E6;font-family:Consolas,monospace;font-size:12px\">{H(f.Rol)}</td></tr>"));
        var cuerpo =
            $"<p>Hola {H(a.ResponsableNombre)}:</p>" +
            $"<p>Se te asignó el cambio <strong>{H(a.Cambio)}</strong> para retirar <strong>{bajas.Count:N0} {(bajas.Count == 1 ? "rol" : "roles")}</strong> " +
            $"de la recertificación <strong>{H(evento.Nombre)}</strong>, macro área <strong>{H(macro.MacroArea)}</strong>.</p>" +
            (vobo?.Estatus == EstatusVoBo.AUTORIZADA
                ? $"<p>Las bajas tienen el VoBo de {H(vobo.GerenteNombre)} ({vobo.FechaRespuesta:dd/MM/yyyy}).</p>" : "") +
            "<p>El detalle completo va en el Excel adjunto.</p>" +
            "<table style=\"border-collapse:collapse;margin:8px 0 16px\"><thead><tr>" +
            "<th style=\"padding:4px 10px;text-align:left;border-bottom:2px solid #17212B\">Usuario</th>" +
            "<th style=\"padding:4px 10px;text-align:left;border-bottom:2px solid #17212B\">Rol a retirar</th></tr></thead>" +
            $"<tbody>{filas}</tbody></table>" +
            (bajas.Count > 20 ? $"<p style=\"color:#5B6672\">… y {bajas.Count - 20:N0} más en el Excel.</p>" : "") +
            (liga is null ? "" : $"<p><a href=\"{H(liga)}\" style=\"display:inline-block;background:#0E6B66;color:#fff;padding:10px 18px;border-radius:6px;text-decoration:none\">Ver las bajas en el portal</a></p>") +
            "<p>Al terminar, registra la ejecución con el número de cambio en el portal (Bajas y evidencia).</p>";
        return new MensajeCorreo(a.ResponsableCorreo, $"Cambio {a.Cambio}: retirar {bajas.Count:N0} {(bajas.Count == 1 ? "rol" : "roles")} · {macro.MacroArea} · {evento.Nombre}",
            Plantilla("Bajas asignadas a infraestructura", cuerpo), [new AdjuntoCorreo(archivo, excel, TipoXlsx)]);
    }
}
