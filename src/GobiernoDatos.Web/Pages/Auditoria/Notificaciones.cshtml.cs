using System.IO.Compression;
using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Pages.Shared;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GobiernoDatos.Web.Pages.Auditoria;

// Evidencia "Solicitud de validación a los responsables de área": por macro área, si se notificó a su responsable
// (recordatorios de Eventos) y el correo enviado en .eml (migración 016).
public class NotificacionesModel(IRecertificacionService recert, IUsuarioActual usuario) : PortalPageModel
{
    public sealed record FilaMacro(string MacroArea, IReadOnlyList<string> Responsables, int Pendientes, bool Confirmada,
                                   IReadOnlyList<RecertRecordatorio> Envios)
    {
        public RecertRecordatorio? Ultimo => Envios.Where(e => e.Enviado).MaxBy(e => e.Fecha);
        public bool Notificado => Ultimo is not null;
    }

    [BindProperty(SupportsGet = true)] public int? Evento { get; set; }

    public IReadOnlyList<FilaHistorialEvento> Eventos { get; private set; } = [];
    public IReadOnlyList<FilaMacro> PorMacro { get; private set; } = [];
    public IReadOnlyList<RecertRecordatorio> Envios { get; private set; } = [];

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
        PorMacro = t.MacroAreas.Select(m => new FilaMacro(m.MacroArea, m.Certificadores, m.Conteo.Pendientes, m.Confirmada,
                Envios.Where(e => e.MacroAreas.Split(", ").Contains(m.MacroArea)).ToList()))
            .OrderBy(m => m.MacroArea).ToList();
    }
}
