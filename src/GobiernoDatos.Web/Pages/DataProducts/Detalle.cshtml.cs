using GobiernoDatos.Web.Domain;
using GobiernoDatos.Web.Pages.Shared;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GobiernoDatos.Web.Pages.DataProducts;

public class DetalleModel(ISeguimientoService seguimiento, IUsuarioActual usuario) : PortalPageModel
{
    [BindProperty(SupportsGet = true)] public int Id { get; set; }
    [BindProperty(SupportsGet = true)] public int? Semana { get; set; }
    [BindProperty(SupportsGet = true)] public int? Rubro { get; set; }
    [BindProperty(SupportsGet = true)] public int? Vista { get; set; }

    public DetalleDp Detalle { get; private set; } = null!;
    // Valores vigentes hoy: el formulario registra con fecha de hoy aunque se consulte otra semana.
    public DetalleDp Actual { get; private set; } = null!;
    public IReadOnlyList<Semana> Semanas { get; private set; } = [];
    public Semana SemanaActual { get; private set; }

    [BindProperty] public FormAvance Form { get; set; } = new();
    [BindProperty] public string? NotaLibre { get; set; }

    public sealed class FormAvance
    {
        public NivelRubro Nivel { get; set; } = NivelRubro.DP;
        public int? VistaId { get; set; }
        public int RubroId { get; set; }
        public EstadoAvance Estado { get; set; } = EstadoAvance.EN_CURSO;
        public decimal? Pct { get; set; }
        public string? Nota { get; set; }
    }

    public IActionResult OnGet()
    {
        Semanas = seguimiento.Semanas();
        SemanaActual = seguimiento.SemanaActual();
        var semana = Semanas.FirstOrDefault(s => s.Id == Semana, Semanas[0]);
        var detalle = seguimiento.Detalle(Id, semana);
        if (detalle is null) return NotFound();
        Detalle = detalle;
        Actual = semana.Id == SemanaActual.Id ? detalle : seguimiento.Detalle(Id, SemanaActual)!;

        // Rubro o vista preseleccionados ("Actualizar" en una tarjeta o fila, o el último movimiento guardado).
        // Estado y porcentaje los precarga el formulario con el valor actual del rubro elegido.
        if (Vista is int v && Actual.Vistas.FirstOrDefault(x => x.VistaId == v) is { } vista)
        {
            Form.Nivel = NivelRubro.VISTA;
            Form.VistaId = v;
            Form.RubroId = vista.RubroActivo?.RubroId ?? 0;
        }
        else if (Rubro is int r && Actual.RubrosDp.Any(x => x.Rubro.RubroId == r))
        {
            Form.RubroId = r;
        }
        return Page();
    }

    public IActionResult OnPostAvance() =>
        Ejecutar(() =>
            {
                if (Form.Nivel == NivelRubro.VISTA)
                    seguimiento.RegistrarAvanceVista(Id, Form.VistaId ?? throw new DomainException("Selecciona la vista."),
                        Form.RubroId, Form.Estado, Form.Pct, Form.Nota, usuario.Login);
                else
                    seguimiento.RegistrarAvanceDp(Id, Form.RubroId, Form.Estado, Form.Pct, Form.Nota, usuario.Login);
            },
            "Movimiento registrado.",
            () => RedirectToPage(Form.Nivel == NivelRubro.VISTA ? new { Id, vista = Form.VistaId, rubro = (int?)null }
                                                                : new { Id, vista = (int?)null, rubro = (int?)Form.RubroId }));

    public IActionResult OnPostNota() =>
        Ejecutar(() => seguimiento.AgregarNota(Id, NotaLibre ?? "", usuario.Login), "Nota agregada a la bitácora.", () => RedirectToPage(new { Id }));

    public IActionResult OnPostCerrar() =>
        Ejecutar(() => seguimiento.CerrarDataProduct(Id, usuario.Login), "Data product cerrado.", () => RedirectToPage(new { Id }));
}
