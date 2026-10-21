using System.Net;
using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;
using Microsoft.Extensions.Options;

namespace GobiernoDatos.Web.Services;

public sealed record ResultadoRecordatorios(int Enviados, IReadOnlyList<string> SinCorreo, IReadOnlyList<string> Errores);

// Recordatorios de la recertificación: un correo por certificador vigente (titular o delegado) con accesos pendientes,
// con sus macro áreas, la fecha límite y la liga a "Mi recertificación". Cada intento queda en la bitácora (evidencia).
public sealed class RecordatoriosService(IRecertificacionService recert, IDirectorioUsuarios directorio, IEnviadorCorreo enviador,
                                         IOptions<GobiernoDatosOptions> opciones, TimeProvider reloj, ILogger<RecordatoriosService> log)
{
    public ResultadoRecordatorios Enviar(int eventoId, string? macroArea, string enviadoPor)
    {
        var evento = recert.Tablero(eventoId)?.Evento ?? throw new DomainException("El evento no existe.");
        if (evento.Estatus != EstatusEvento.ABIERTO) throw new DomainException("Solo se envían recordatorios de un evento abierto.");

        var pendientes = recert.PendientesPorCertificador(eventoId).Where(p => macroArea is null || p.MacroArea == macroArea).ToList();
        if (pendientes.Count == 0)
            throw new DomainException(macroArea is null ? "No hay accesos pendientes con certificador asignado." : $"{macroArea} no tiene accesos pendientes con certificador asignado.");

        int enviados = 0;
        var sinCorreo = new List<string>();
        var errores = new List<string>();
        // Un correo por persona aunque certifique varias macro áreas.
        foreach (var g in pendientes.GroupBy(p => p.UsuarioRed?.ToUpperInvariant() ?? "nombre:" + p.Certificador))
        {
            var p = g.First();
            var macros = g.OrderBy(x => x.MacroArea).ToList();
            var total = macros.Sum(x => x.Pendientes);
            var macrosTexto = string.Join(", ", macros.Select(x => x.MacroArea));

            string? correo = p.Correo, error = null;
            try { correo ??= p.UsuarioRed is null ? null : directorio.BuscarCorreo(p.UsuarioRed); }
            catch (DomainException ex) { error = ex.Message; }

            var enviado = false;
            if (correo is null)
            {
                error ??= p.UsuarioRed is null ? "Sin usuario de red ni correo registrado" : "Sin correo en Active Directory";
                sinCorreo.Add($"{p.Certificador} ({error})");
            }
            else
            {
                try
                {
                    enviador.Enviar(Mensaje(correo, p.Certificador, evento, macros, total));
                    enviado = true;
                    enviados++;
                }
                catch (DomainException ex)
                {
                    error = ex.Message;
                    errores.Add($"{p.Certificador}: {ex.Message}");
                }
            }
            recert.RegistrarRecordatorio(new RecertRecordatorio(eventoId, p.Certificador, p.UsuarioRed, correo, macrosTexto, total,
                                                                enviado, error, enviadoPor, reloj.GetLocalNow().DateTime));
        }
        log.LogInformation("Recordatorios del evento {Evento}: {Enviados} enviados, {SinCorreo} sin correo, {Errores} con error.",
                           evento.Nombre, enviados, sinCorreo.Count, errores.Count);
        return new ResultadoRecordatorios(enviados, sinCorreo, errores);
    }

    // Correo de prueba al administrador que lo pide, para validar la configuración del relay.
    public string EnviarPrueba(string usuarioRed)
    {
        var correo = directorio.BuscarCorreo(usuarioRed) ?? throw new DomainException($"{usuarioRed} no tiene correo en Active Directory.");
        enviador.Enviar(new MensajeCorreo(correo, "Gobierno de Datos · correo de prueba",
            Plantilla("Correo de prueba", $"<p>Este es un correo de prueba del portal de Gobierno de Datos, enviado por {H(usuarioRed)}.</p>" +
                                          "<p>Si lo recibiste, los recordatorios de recertificación ya pueden enviarse.</p>")));
        return correo;
    }

    internal MensajeCorreo Mensaje(string para, string certificador, RecertEvento evento, IReadOnlyList<CertificadorPendiente> macros, int total)
    {
        var limite = evento.FechaLimite is { } f ? $"<p>La fecha límite es el <strong>{f.ToString("dddd d 'de' MMMM 'de' yyyy", Cultura)}</strong>.</p>" : "";
        var filas = string.Concat(macros.Select(m =>
            $"<tr><td style=\"padding:6px 12px;border-bottom:1px solid #DDE1E6\">{H(m.MacroArea)}</td>" +
            $"<td style=\"padding:6px 12px;border-bottom:1px solid #DDE1E6;text-align:right\">{m.Pendientes:N0}</td></tr>"));
        var liga = Liga();
        var cuerpo =
            $"<p>Hola {H(certificador)}:</p>" +
            $"<p>Tienes <strong>{total:N0} {(total == 1 ? "acceso pendiente" : "accesos pendientes")}</strong> de certificar en el evento de recertificación <strong>{H(evento.Nombre)}</strong>.</p>" +
            limite +
            "<table style=\"border-collapse:collapse;margin:8px 0 16px\"><thead><tr>" +
            "<th style=\"padding:6px 12px;text-align:left;border-bottom:2px solid #17212B\">Macro área</th>" +
            "<th style=\"padding:6px 12px;text-align:right;border-bottom:2px solid #17212B\">Pendientes</th></tr></thead>" +
            $"<tbody>{filas}</tbody></table>" +
            (liga is null ? "" :
                $"<p><a href=\"{H(liga)}\" style=\"display:inline-block;background:#0E6B66;color:#fff;padding:10px 18px;border-radius:6px;text-decoration:none\">Ir a Mi recertificación</a></p>") +
            "<p>Para cada acceso indica si <strong>Continúa</strong> o se da de <strong>Baja</strong>; en las bajas el comentario es obligatorio.</p>";
        return new MensajeCorreo(para, $"Recertificación {evento.Nombre}: tienes {total:N0} {(total == 1 ? "acceso pendiente" : "accesos pendientes")}",
                                 Plantilla("Recertificación de accesos", cuerpo));
    }

    private string? Liga() =>
        string.IsNullOrWhiteSpace(opciones.Value.Correo.UrlPortal) ? null : opciones.Value.Correo.UrlPortal.TrimEnd('/') + "/Recertificacion/Mi";

    internal static string Plantilla(string titulo, string cuerpo) =>
        "<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#17212B;max-width:640px\">" +
        $"<h2 style=\"font-size:18px;margin:0 0 12px\">{H(titulo)}</h2>{cuerpo}" +
        "<p style=\"color:#5B6672;font-size:12px;margin-top:24px\">Mensaje automático del portal de Gobierno de Datos. No respondas a este correo.</p></div>";

    private static readonly System.Globalization.CultureInfo Cultura = new("es-MX");
    internal static string H(string texto) => WebUtility.HtmlEncode(texto);
}
