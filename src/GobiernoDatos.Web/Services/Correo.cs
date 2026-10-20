using System.Net;
using System.Net.Mail;
using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;
using Microsoft.Extensions.Options;

namespace GobiernoDatos.Web.Services;

public sealed record MensajeCorreo(string Para, string Asunto, string CuerpoHtml);

public interface IEnviadorCorreo
{
    string Descripcion { get; }
    // Lanza DomainException si el correo no está configurado o el servidor lo rechaza.
    void Enviar(MensajeCorreo mensaje);
}

// Relay SMTP interno (Exchange) con la cuenta de servicio (DOMINIO\usuario).
public sealed class EnviadorSmtp(IOptions<GobiernoDatosOptions> opciones, ILogger<EnviadorSmtp> log) : IEnviadorCorreo
{
    private CorreoOptions C => opciones.Value.Correo;

    public string Descripcion => $"SMTP {C.Servidor}:{C.Puerto}";

    public void Enviar(MensajeCorreo mensaje)
    {
        var c = C;
        if (string.IsNullOrWhiteSpace(c.Servidor) || string.IsNullOrWhiteSpace(c.Remitente))
            throw new DomainException("El correo no está configurado (GobiernoDatos:Correo: Servidor y Remitente).");
        try
        {
            using var smtp = new SmtpClient(c.Servidor, c.Puerto) { EnableSsl = c.UsarTls, Timeout = 30_000, DeliveryMethod = SmtpDeliveryMethod.Network };
            if (!string.IsNullOrWhiteSpace(c.Usuario))
                smtp.Credentials = new NetworkCredential(c.Usuario, c.Contrasena, c.Dominio);
            using var correo = new MailMessage(new MailAddress(c.Remitente, "Gobierno de Datos"), new MailAddress(mensaje.Para))
            {
                Subject = mensaje.Asunto, Body = mensaje.CuerpoHtml, IsBodyHtml = true,
                SubjectEncoding = System.Text.Encoding.UTF8, BodyEncoding = System.Text.Encoding.UTF8,
            };
            smtp.Send(correo);
        }
        catch (Exception ex) when (ex is SmtpException or FormatException or InvalidOperationException)
        {
            log.LogError(ex, "No se pudo enviar el correo a {Para}.", mensaje.Para);
            throw new DomainException($"No se pudo enviar el correo a {mensaje.Para}: {ex.Message}");
        }
    }
}

// Desarrollo y pruebas: no envía nada, guarda los mensajes.
public sealed class EnviadorSimulado : IEnviadorCorreo
{
    private readonly List<MensajeCorreo> _enviados = [];
    public string Descripcion => "Correo simulado (no se envía)";
    public IReadOnlyList<MensajeCorreo> Enviados { get { lock (_enviados) return [.. _enviados]; } }
    public void Enviar(MensajeCorreo mensaje) { lock (_enviados) _enviados.Add(mensaje); }
}
