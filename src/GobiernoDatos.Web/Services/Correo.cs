using System.Net;
using System.Net.Mail;
using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;
using Microsoft.Extensions.Options;

namespace GobiernoDatos.Web.Services;

public sealed record AdjuntoCorreo(string Nombre, byte[] Contenido, string TipoContenido);

public sealed record MensajeCorreo(string Para, string Asunto, string CuerpoHtml, IReadOnlyList<AdjuntoCorreo>? Adjuntos = null);

public interface IEnviadorCorreo
{
    string Descripcion { get; }
    // Envía y devuelve el correo tal como salió en formato .eml (evidencia).
    // Lanza DomainException si el correo no está configurado o el servidor lo rechaza.
    byte[] Enviar(MensajeCorreo mensaje);
}

// Arma el MailMessage y lo serializa a .eml (MIME) con el mismo SmtpClient, escribiendo a un directorio temporal.
public static class CorreoEml
{
    public static MailMessage Construir(MensajeCorreo m, string remitente)
    {
        var correo = new MailMessage(new MailAddress(remitente, "Gobierno de Datos"), new MailAddress(m.Para))
        {
            Subject = m.Asunto, Body = m.CuerpoHtml, IsBodyHtml = true,
            SubjectEncoding = System.Text.Encoding.UTF8, BodyEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in m.Adjuntos ?? [])
            correo.Attachments.Add(new Attachment(new MemoryStream(a.Contenido), a.Nombre, a.TipoContenido));
        return correo;
    }

    public static byte[] Generar(MailMessage correo)
    {
        var dir = Directory.CreateTempSubdirectory("gd-eml-");
        try
        {
            using (var smtp = new SmtpClient { DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory, PickupDirectoryLocation = dir.FullName })
                smtp.Send(correo);
            var texto = File.ReadAllText(dir.EnumerateFiles("*.eml").Single().FullName);
            // El directorio de recogida agrega X-Sender y X-Receiver: no son parte del correo enviado.
            var lineas = texto.Split("\r\n").SkipWhile(l => l.StartsWith("X-Sender:") || l.StartsWith("X-Receiver:"));
            return System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", lineas));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

// Relay SMTP interno (Exchange) con la cuenta de servicio (DOMINIO\usuario).
public sealed class EnviadorSmtp(IOptions<GobiernoDatosOptions> opciones, ILogger<EnviadorSmtp> log) : IEnviadorCorreo
{
    private CorreoOptions C => opciones.Value.Correo;

    public string Descripcion => $"SMTP {C.Servidor}:{C.Puerto}";

    public byte[] Enviar(MensajeCorreo mensaje)
    {
        var c = C;
        if (string.IsNullOrWhiteSpace(c.Servidor) || string.IsNullOrWhiteSpace(c.Remitente))
            throw new DomainException("El correo no está configurado (GobiernoDatos:Correo: Servidor y Remitente).");
        try
        {
            using var correo = CorreoEml.Construir(mensaje, c.Remitente);
            var eml = CorreoEml.Generar(correo);
            using var smtp = new SmtpClient(c.Servidor, c.Puerto) { EnableSsl = c.UsarTls, Timeout = 30_000, DeliveryMethod = SmtpDeliveryMethod.Network };
            if (!string.IsNullOrWhiteSpace(c.Usuario))
                smtp.Credentials = new NetworkCredential(c.Usuario, c.Contrasena, c.Dominio);
            foreach (var a in correo.Attachments) a.ContentStream.Position = 0;   // Generar ya leyó los adjuntos
            smtp.Send(correo);
            return eml;
        }
        catch (Exception ex) when (ex is SmtpException or FormatException or InvalidOperationException or IOException)
        {
            log.LogError(ex, "No se pudo enviar el correo a {Para}.", mensaje.Para);
            throw new DomainException($"No se pudo enviar el correo a {mensaje.Para}: {ex.Message}");
        }
    }
}

// Desarrollo y pruebas: no envía nada, guarda los mensajes y devuelve su .eml.
public sealed class EnviadorSimulado : IEnviadorCorreo
{
    private readonly List<MensajeCorreo> _enviados = [];
    public string Descripcion => "Correo simulado (no se envía)";
    public IReadOnlyList<MensajeCorreo> Enviados { get { lock (_enviados) return [.. _enviados]; } }

    public byte[] Enviar(MensajeCorreo mensaje)
    {
        lock (_enviados) _enviados.Add(mensaje);
        using var correo = CorreoEml.Construir(mensaje, "gobierno.datos@ejemplo.local");
        return CorreoEml.Generar(correo);
    }
}
