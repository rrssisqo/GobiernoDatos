using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;
using GobiernoDatos.Web.Pages.Shared;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GobiernoDatos.Web.Pages.Recertificacion;

public class EventosModel(IRecertificacionService recert, IFuenteExtracto fuente, TimeProvider reloj, IUsuarioActual usuario) : PortalPageModel
{
    [BindProperty(SupportsGet = true)] public int? Evento { get; set; }

    public TableroEvento? Tablero { get; private set; }
    public IReadOnlyList<FilaHistorialEvento> Historial { get; private set; } = [];
    public bool PuedeCrear { get; private set; }
    public IFuenteExtracto Fuente => fuente;
    public IReadOnlyList<CertificadoresMacroArea> Certificadores { get; private set; } = [];

    [BindProperty] public string Nombre { get; set; } = "";
    [BindProperty] public DateOnly? FechaExtracto { get; set; }
    [BindProperty] public DateOnly? FechaLimite { get; set; }

    public IActionResult OnGet()
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        var id = Evento ?? recert.EventoActivo()?.EventoId;
        Tablero = id is null ? null : recert.Tablero(id.Value);
        Historial = recert.Historial();
        PuedeCrear = Historial.All(h => h.Estatus == EstatusEvento.CERRADO);
        Certificadores = recert.Certificadores();
        return Page();
    }

    // Nuevo evento: el extracto se lee de la vista HANA de recertificación (IFuenteExtracto).
    public IActionResult OnPostCrear()
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        int id = 0;
        return Ejecutar(() =>
            {
                var filas = fuente.Leer();
                var hoy = DateOnly.FromDateTime(reloj.GetLocalNow().DateTime);
                id = recert.CrearEvento(Nombre, FechaExtracto ?? hoy, FechaLimite, filas);
                var t = recert.Tablero(id)!;
                MensajeOk = $"Extracto cargado desde {fuente.Descripcion}: {filas.Count:N0} filas leídas, {t.Conteo.Accesos:N0} accesos" +
                            (t.Evento.Rechazos.Count > 0 ? $" y {t.Evento.Rechazos.Count:N0} filas rechazadas. Revisa la validación de la carga."
                                                         : ". Revisa la validación antes de abrir el evento.");
            },
            "", () => RedirectToPage(new { evento = id == 0 ? (int?)null : id }));
    }

    public IActionResult OnPostAbrir() => Admin(() => recert.AbrirEvento(Evento!.Value), "El evento está abierto a los certificadores.");

    public IActionResult OnPostCerrar()
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        int pendientes = 0;
        return Ejecutar(() => pendientes = recert.CerrarEvento(Evento!.Value),
            "Evento cerrado.", () =>
            {
                if (pendientes > 0) MensajeOk = $"Evento cerrado con {pendientes} accesos sin decisión; quedan reportados como pendientes.";
                return RedirectToPage(new { Evento });
            });
    }

    public IActionResult OnPostDescartar()
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        return Ejecutar(() => recert.DescartarBorrador(Evento!.Value), "Borrador descartado.", () => RedirectToPage());
    }

    // El envío de correo depende de la decisión de stack y hosting (pendiente con Memo).
    public IActionResult OnPostRecordar(string? macroArea)
    {
        MensajeOk = $"Recordatorio{(macroArea is null ? "s" : " a " + macroArea)}: el envío de correo se habilitará cuando se defina el servicio de notificaciones.";
        return RedirectToPage(new { Evento });
    }

    public IActionResult OnPostAsignar(int macroAreaId, string? usuarioRed, string tipo) =>
        Admin(() => recert.AsignarCertificador(macroAreaId, usuarioRed ?? "", tipo), $"Certificador asignado: {usuarioRed}.", "certificadores");

    public IActionResult OnPostRetirar(int macroAreaId, string identificador) =>
        Admin(() => recert.RetirarCertificador(macroAreaId, identificador), $"Se retiró a {identificador} como certificador.", "certificadores");

    private IActionResult Admin(Action accion, string ok, string fragmento)
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        return Ejecutar(accion, ok, () => RedirectToPage(null, null, new { Evento }, fragmento));
    }

    private IActionResult Admin(Action accion, string ok)
    {
        if (!usuario.TieneRol(RolesPortal.Administrador)) return SinPermiso();
        return Ejecutar(accion, ok, () => RedirectToPage(new { Evento }));
    }
}
