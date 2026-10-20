using GobiernoDatos.Web.Pages.Shared;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GobiernoDatos.Web.Pages.Vistas;

public class IndexModel(IVistasService vistas, ITareasService tareas, ResolutorUsuarios resolutor, IUsuarioActual usuario) : PortalPageModel
{
    [BindProperty(SupportsGet = true)] public PestanaVistas Tab { get; set; }
    [BindProperty(SupportsGet = true)] public string? Macro { get; set; }
    [BindProperty(SupportsGet = true)] public int? Etapa { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }

    public InventarioVistas Inventario { get; private set; } = null!;
    public DetalleVista? Seleccionada { get; private set; }
    public IReadOnlyList<OpcionDp> DataProducts { get; private set; } = [];
    public IReadOnlyList<OpcionObjeto> Objetos { get; private set; } = [];
    public IReadOnlyList<string> Gobiernos { get; private set; } = [];
    public IReadOnlyList<string> Responsables { get; private set; } = [];

    [BindProperty] public FormGobierno Form { get; set; } = new();
    [BindProperty] public FormEtapa Mov { get; set; } = new();
    [BindProperty] public int ObjetoId { get; set; }

    public sealed class FormGobierno
    {
        public string NombreNegocio { get; set; } = "";
        public string? Gobierno { get; set; }
        public int? DpId { get; set; }
        public string? Responsable { get; set; }
        public string? NivelSeguridad { get; set; }
    }

    public sealed class FormEtapa
    {
        public int EtapaOrden { get; set; }
        public int? MotivoId { get; set; }
        public string? Comentario { get; set; }
        public string? AsignarA { get; set; }   // usuario de red para la tarea de la nueva etapa (opcional)
    }

    [BindProperty] public string? UsuarioTarea { get; set; }
    public FilaTarea? Tarea { get; private set; }

    public void OnGet()
    {
        Inventario = vistas.Inventario(new FiltroVistas(Tab, Macro, Etapa, Q));
        var id = Id ?? Inventario.Filas.FirstOrDefault()?.VistaId;
        Seleccionada = id is null ? null : vistas.Detalle(id.Value);
        Tarea = id is null ? null : tareas.TareaAbierta(id.Value);
        DataProducts = vistas.DataProductsAbiertos();
        Objetos = vistas.Objetos();
        Gobiernos = vistas.Gobiernos();
        Responsables = vistas.Responsables();
    }

    // Valores de ruta actuales (filtros + selección), con cambios opcionales.
    public Dictionary<string, object?> Ruta(object? cambios = null)
    {
        var r = new Dictionary<string, object?> { ["tab"] = Tab == PestanaVistas.Todas ? null : Tab, ["macro"] = Macro, ["etapa"] = Etapa, ["q"] = Q, ["id"] = Id };
        if (cambios is not null)
            foreach (var p in cambios.GetType().GetProperties()) r[p.Name] = p.GetValue(cambios);
        return r.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private IActionResult Volver() => RedirectToPage(Ruta());

    public IActionResult OnPostGuardar() =>
        Ejecutar(() => vistas.Actualizar(Id!.Value, Form.NombreNegocio, Vacio(Form.Gobierno), Form.DpId, Form.Responsable, Vacio(Form.NivelSeguridad), usuario.Login),
                 "Cambios guardados.", Volver);

    public IActionResult OnPostMover() =>
        Ejecutar(() =>
            {
                // El usuario se valida contra el directorio antes de mover, para no dejar la etapa cambiada sin tarea.
                var asignacion = string.IsNullOrWhiteSpace(Mov.AsignarA) ? null : resolutor.Resolver(Mov.AsignarA);
                vistas.MoverEtapa(Id!.Value, Mov.EtapaOrden, Mov.MotivoId, Mov.Comentario, usuario.Login, asignacion);
            },
            string.IsNullOrWhiteSpace(Mov.AsignarA) ? "Etapa actualizada." : "Etapa actualizada y tarea asignada.", Volver);

    public IActionResult OnPostAsignarTarea() =>
        Ejecutar(() => tareas.Asignar(Id!.Value, resolutor.Resolver(UsuarioTarea ?? ""), usuario.Login), "Tarea asignada.", Volver);

    public IActionResult OnPostRelacionar() =>
        Ejecutar(() => vistas.RelacionarObjeto(Id!.Value, ObjetoId), "Objeto relacionado con la vista.", Volver);

    public IActionResult OnPostQuitar() =>
        Ejecutar(() => vistas.QuitarObjeto(Id!.Value, ObjetoId), "Objeto retirado de la vista.", Volver);

    private static string? Vacio(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;
}
