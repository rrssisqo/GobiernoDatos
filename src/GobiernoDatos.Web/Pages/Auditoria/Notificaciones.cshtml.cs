using System.IO.Compression;
using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Pages.Shared;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using static GobiernoDatos.Web.Services.RecordatoriosService;

namespace GobiernoDatos.Web.Pages.Auditoria;

// Evidencia "Solicitud de validación a los responsables de área": por macro área, si se notificó a su responsable
// (recordatorios de Eventos) y el correo enviado en .eml (migración 016); y el VoBo del gerente (Power Automate) con su
// constancia en correo (.eml) generada por el portal: solicitud, envío, respuesta y las bajas que se le pidió autorizar
// (migración 018). El VoBo lo envía Power Automate, así que el portal no tiene el correo original: el .eml lo dice.
public class NotificacionesModel(IRecertificacionService recert, IUsuarioActual usuario, IOptions<GobiernoDatosOptions> opciones,
                                 TimeProvider reloj) : PortalPageModel
{
    public sealed record FilaMacro(string MacroArea, IReadOnlyList<string> Responsables, int Pendientes, bool Confirmada,
                                   IReadOnlyList<RecertRecordatorio> Envios, VoBoGerente? VoBo = null)
    {
        public RecertRecordatorio? Ultimo => Envios.Where(e => e.Enviado).MaxBy(e => e.Fecha);
        public bool Notificado => Ultimo is not null;
    }

    [BindProperty(SupportsGet = true)] public int? Evento { get; set; }

    public IReadOnlyList<FilaHistorialEvento> Eventos { get; private set; } = [];
    public IReadOnlyList<FilaMacro> PorMacro { get; private set; } = [];
    public IReadOnlyList<RecertRecordatorio> Envios { get; private set; } = [];
    public IReadOnlyList<VoBoGerente> VoBos { get; private set; } = [];

    public static string Etiqueta(EstatusVoBo e) => e switch
    {
        EstatusVoBo.PENDIENTE => "Por enviar", EstatusVoBo.ENVIADA => "Enviado, sin respuesta", EstatusVoBo.AUTORIZADA => "Autorizado",
        EstatusVoBo.RECHAZADA => "Rechazado", _ => "Cancelado",
    };

    public IActionResult OnGet()
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        Cargar();
        return Page();
    }

    // Correo tal como salió (.eml, se abre en Outlook).
    public IActionResult OnGetCorreo(long id)
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        var eml = recert.CorreoRecordatorio(id);
        return eml is null ? NotFound() : File(eml, "message/rfc822", $"notificacion_{id}.eml");
    }

    // Constancia del VoBo en correo (.eml): solicitud, envío, respuesta y las bajas que se pidió autorizar.
    public IActionResult OnGetVoBo(long id)
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        Cargar();
        var v = VoBos.FirstOrDefault(x => x.VoBoId == id);
        if (v is null) return NotFound();
        return File(Constancia(v), "message/rfc822", NombreConstancia(v));
    }

    private string NombreConstancia(VoBoGerente v) =>
        $"vobo_{EscritorExcel.NombreArchivo(v.MacroArea)}_{v.FechaSolicitud:yyyyMMdd_HHmm}_{v.VoBoId}.eml";

    private byte[] Constancia(VoBoGerente v)
    {
        var evento = Eventos.FirstOrDefault(e => e.EventoId == v.EventoId)?.Nombre ?? v.EventoId.ToString();
        var bajas = recert.BajasVoBo(v.VoBoId);
        static string F(DateTime? f) => f?.ToString("yyyy-MM-dd HH:mm") ?? "—";
        string Fila(string dato, string? valor) =>
            $"<tr><td style=\"padding:4px 12px 4px 0;color:#5B6672;vertical-align:top\">{H(dato)}</td><td style=\"padding:4px 0\">{H(valor ?? "—")}</td></tr>";
        var estatus = v.Estatus switch
        {
            EstatusVoBo.AUTORIZADA => "<strong style=\"color:#0B4F4B\">AUTORIZADO</strong>",
            EstatusVoBo.RECHAZADA => "<strong style=\"color:#8A1C12\">RECHAZADO</strong>",
            _ => $"<strong>{H(Etiqueta(v.Estatus).ToUpperInvariant())}</strong>",
        };
        var filasBajas = string.Concat(bajas.Select(b =>
            $"<tr><td style=\"padding:4px 8px;border-bottom:1px solid #DDE1E6\">{H(b.Nombre)}<br><span style=\"color:#5B6672\">{H(b.Usuario)}</span></td>" +
            $"<td style=\"padding:4px 8px;border-bottom:1px solid #DDE1E6;font-family:Consolas,monospace;font-size:12px\">{H(b.Rol)}<br>{H(b.Vista)}</td>" +
            $"<td style=\"padding:4px 8px;border-bottom:1px solid #DDE1E6\">{H(b.Comentario ?? "")}</td>" +
            $"<td style=\"padding:4px 8px;border-bottom:1px solid #DDE1E6\">{H(b.DecididoPor ?? "")}<br><span style=\"color:#5B6672\">{F(b.FechaDecision)}</span></td></tr>"));
        var cuerpo =
            "<p style=\"background:#FBE9C8;padding:8px 12px;border-radius:6px\">Constancia generada por el portal de Gobierno de Datos el " +
            $"{reloj.GetLocalNow():yyyy-MM-dd HH:mm} a partir del registro de la solicitud. La aprobación al gerente la envía Power Automate.</p>" +
            $"<p>Resultado del VoBo: {estatus}</p>" +
            "<table style=\"border-collapse:collapse;margin:8px 0 16px\">" +
            Fila("Evento", evento) + Fila("Macro área", v.MacroArea) + Fila("Id de la solicitud", v.VoBoId.ToString()) +
            Fila("Gerente", $"{v.GerenteNombre} <{v.GerenteCorreo}>") + Fila("Bajas a autorizar", v.Bajas.ToString()) +
            Fila("Solicitado por", $"{v.SolicitadoPor} · {F(v.FechaSolicitud)}") + Fila("Enviado al gerente (Power Automate)", F(v.FechaEnvio)) +
            Fila("Respondió", v.RespondidoPor) + Fila("Fecha de respuesta", F(v.FechaRespuesta)) + Fila("Comentario del gerente", v.Comentario) +
            (v.MotivoCancelacion is null ? "" : Fila("Motivo de cancelación", v.MotivoCancelacion)) +
            "</table>" +
            $"<p><strong>Bajas que se pidió autorizar ({bajas.Count:N0})</strong></p>" +
            "<table style=\"border-collapse:collapse\"><thead><tr>" +
            "<th style=\"padding:4px 8px;text-align:left;border-bottom:2px solid #17212B\">Usuario</th>" +
            "<th style=\"padding:4px 8px;text-align:left;border-bottom:2px solid #17212B\">Rol a retirar · vista</th>" +
            "<th style=\"padding:4px 8px;text-align:left;border-bottom:2px solid #17212B\">Comentario</th>" +
            "<th style=\"padding:4px 8px;text-align:left;border-bottom:2px solid #17212B\">Decidió</th></tr></thead>" +
            $"<tbody>{filasBajas}</tbody></table>";
        var remitente = opciones.Value.Correo.Remitente ?? "gobierno.datos@mabe.com.mx";
        using var correo = CorreoEml.Construir(new MensajeCorreo(v.GerenteCorreo,
            $"Constancia de VoBo · {v.MacroArea} · evento {evento} · {Etiqueta(v.Estatus)}", Plantilla("VoBo de bajas del gerente", cuerpo)), remitente);
        return CorreoEml.Generar(correo);
    }

    // Todos los correos enviados del evento en un .zip, más el resumen en Excel.
    public IActionResult OnGetEvidencia()
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        Cargar();
        var evento = EscritorExcel.NombreArchivo(Eventos.FirstOrDefault(e => e.EventoId == Evento)?.Nombre ?? $"evento_{Evento}");
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Agregar(zip, $"notificaciones_{evento}.xlsx", Resumen());
            foreach (var e in Envios.Where(e => e.TieneCorreo))
                if (recert.CorreoRecordatorio(e.RecordatorioId) is { } eml)
                    Agregar(zip, $"correos/{e.Fecha:yyyyMMdd_HHmm}_{EscritorExcel.NombreArchivo(e.Certificador)}_{e.RecordatorioId}.eml", eml);
            foreach (var v in VoBos)
                Agregar(zip, $"vobo_gerente/{NombreConstancia(v)}", Constancia(v));
        }
        return File(ms.ToArray(), "application/zip", $"evidencia_notificaciones_{evento}.zip");
    }

    private static void Agregar(ZipArchive zip, string nombre, byte[] contenido)
    {
        using var destino = zip.CreateEntry(nombre, CompressionLevel.Fastest).Open();
        destino.Write(contenido);
    }

    private byte[] Resumen() => EscritorExcel.Libro("Notificaciones",
        ["Fecha", "Macro áreas", "Responsable", "Usuario de red", "Correo", "Asunto", "Pendientes", "Resultado", "Error", "Envió", "Id correo"],
        Envios.OrderBy(e => e.Fecha).Select(e => new object?[]
        {
            e.Fecha, e.MacroAreas, e.Certificador, e.UsuarioRed, e.Correo, e.Asunto, e.Pendientes, e.Enviado ? "Enviado" : "No enviado",
            e.Error, e.EnviadoPor, e.TieneCorreo ? e.RecordatorioId : null,
        }));

    // Sin evento en la URL: el evento activo.
    private void Cargar()
    {
        Eventos = recert.Historial().Where(e => e.Estatus != Domain.EstatusEvento.BORRADOR).ToList();
        Evento ??= recert.EventoActivo()?.EventoId;
        if (Evento is not int id || recert.Tablero(id) is not { } t) return;
        Envios = recert.Recordatorios(id);
        VoBos = recert.VoBos(id);
        PorMacro = t.MacroAreas.Select(m => new FilaMacro(m.MacroArea, m.Certificadores, m.Conteo.Pendientes, m.Confirmada,
                Envios.Where(e => e.MacroAreas.Split(", ").Contains(m.MacroArea)).ToList(),
                VoBos.Where(v => v.MacroAreaId == m.MacroAreaId).MaxBy(v => v.VoBoId)))
            .OrderBy(m => m.MacroArea).ToList();
    }
}
