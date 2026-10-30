using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Pages.Shared;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GobiernoDatos.Web.Pages.Informatica;

// Accesos de Informatica: solicitudes de rol (Data Owner, Data Administrator, Data Steward) con fecha y número de change.
// El Excel (con los filtros aplicados) es lo que se envía a infraestructura para pedir el cambio.
public class AccesosModel(IAccesosInformaticaService accesos, IUsuarioActual usuario, TimeProvider reloj) : PortalPageModel
{
    [BindProperty(SupportsGet = true)] public string? Estado { get; set; }      // null = todas, SIN = sin change, CON = con change
    [BindProperty(SupportsGet = true)] public string? RolFiltro { get; set; }

    public IReadOnlyList<AccesoInformatica> Todas { get; private set; } = [];
    public IReadOnlyList<AccesoInformatica> Filas { get; private set; } = [];
    public DateOnly Hoy => DateOnly.FromDateTime(reloj.GetLocalNow().DateTime);

    private bool Admin => usuario.TieneRol(RolesPortal.Administrador);

    public IActionResult OnGet()
    {
        if (!Admin) return SinPermiso();
        Cargar();
        return Page();
    }

    public IActionResult OnGetExportar()
    {
        if (!Admin) return SinPermiso();
        Cargar();
        var libro = EscritorExcel.Libro("Accesos Informatica",
            ["Usuario de red", "Nombre", "Correo", "Rol", "Fecha de solicitud", "Change", "Comentario", "Registró"],
            Filas.Select(a => new object?[] { a.UsuarioRed, a.Nombre, a.Correo, a.Rol, a.FechaSolicitud, a.Change, a.Comentario, a.RegistradoPor }));
        return File(libro, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"accesos_informatica_{Hoy:yyyyMMdd}.xlsx");
    }

    public IActionResult OnPostRegistrar(string? usuarioRed, string? rol, DateOnly? fechaSolicitud, string? change, string? comentario)
    {
        if (!Admin) return SinPermiso();
        return Ejecutar(() =>
        {
            var a = accesos.Registrar(usuarioRed ?? "", rol ?? "", fechaSolicitud, change, comentario, usuario.Login);
            MensajeOk = $"Solicitud registrada: {a.Nombre} · {a.Rol}.";
        }, "", Volver);
    }

    public IActionResult OnPostChange(long accesoId, string? change)
    {
        if (!Admin) return SinPermiso();
        return Ejecutar(() => accesos.AsignarChange(accesoId, change), "Change registrado.", Volver);
    }

    public IActionResult OnPostEliminar(long accesoId)
    {
        if (!Admin) return SinPermiso();
        return Ejecutar(() => accesos.Eliminar(accesoId), "Solicitud eliminada.", Volver);
    }

    private IActionResult Volver() => RedirectToPage(new { Estado, RolFiltro });

    private void Cargar()
    {
        Todas = accesos.Listar();
        Filas = Todas.Where(a => Estado switch { "SIN" => a.Change is null, "CON" => a.Change is not null, _ => true })
                     .Where(a => string.IsNullOrEmpty(RolFiltro) || a.Rol == RolFiltro).ToList();
    }
}
