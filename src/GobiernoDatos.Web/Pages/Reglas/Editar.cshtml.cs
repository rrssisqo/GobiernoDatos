using GobiernoDatos.Web.Domain;
using GobiernoDatos.Web.Pages.Shared;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GobiernoDatos.Web.Pages.Reglas;

// Alta (sin id) y cambio (con id) de una regla. El código solo se captura en el alta.
public class EditarModel(IReglasService reglas, IUsuarioActual usuario) : PortalPageModel
{
    // Campos del formulario (columnas de la lista Reglas de SharePoint); listas separadas por punto y coma.
    public sealed class FormRegla
    {
        public string? Codigo { get; set; }
        public string? TipoRegla { get; set; }
        public string? Descripcion { get; set; }
        public string? ReglaFuncional { get; set; }
        public string? PropositoNegocio { get; set; }
        public string? DimensionDq { get; set; } = "VALIDEZ";
        public string? Gobierno { get; set; }
        public string? Contactos { get; set; }
        public string? CamposEvaluados { get; set; }
        public string? Objetos { get; set; }
        public string? TablaStg { get; set; }
        public string? Mapping { get; set; }
        public int? NoCsv { get; set; }
        public string? NombreCsv { get; set; }
        public string? Frecuencia { get; set; }
        public bool EscribeLog { get; set; } = true;
        public string? CorreoAsunto { get; set; }
        public string? Destinatarios { get; set; }
        public int EtapaOrden { get; set; } = 1;
        public string? QueryValidacion { get; set; }
        public string? TaskFlow { get; set; }
        public TimeOnly? Hora { get; set; }
        public DateOnly? FechaProduccion { get; set; }
        public string? Comentarios { get; set; }
    }

    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    [BindProperty] public FormRegla Regla { get; set; } = new();

    public bool Activa { get; private set; } = true;
    public CatalogosRegla Catalogos { get; private set; } = null!;
    public string? Error { get; private set; }
    public bool EsAlta => Id is null;

    public IActionResult OnGet()
    {
        if (Id is int id)
        {
            var r = reglas.Obtener(id);
            if (r is null) return NotFound();
            var d = r.Datos;
            Activa = r.Activa;
            Regla = new FormRegla
            {
                Codigo = d.Codigo, TipoRegla = d.TipoRegla, Descripcion = d.Descripcion, ReglaFuncional = d.ReglaFuncional,
                PropositoNegocio = d.PropositoNegocio, DimensionDq = d.DimensionDq, Gobierno = d.Gobierno, Contactos = string.Join("; ", d.Contactos),
                CamposEvaluados = d.CamposEvaluados, Objetos = string.Join("; ", d.Objetos), TablaStg = d.TablaStg, Mapping = d.Mapping,
                NoCsv = d.NoCsv, NombreCsv = d.NombreCsv, Frecuencia = d.Frecuencia, EscribeLog = d.EscribeLog, CorreoAsunto = d.CorreoAsunto,
                Destinatarios = string.Join("; ", d.Destinatarios), EtapaOrden = d.EtapaOrden, QueryValidacion = d.QueryValidacion,
                TaskFlow = d.TaskFlow, Hora = d.Hora, FechaProduccion = d.FechaProduccion, Comentarios = d.Comentarios,
            };
        }
        Catalogos = reglas.Catalogos();
        return Page();
    }

    public IActionResult OnPost()
    {
        var f = Regla;
        var datos = new DatosRegla(
            f.Codigo ?? "", f.TipoRegla, f.Descripcion ?? "", f.ReglaFuncional, f.PropositoNegocio,
            string.IsNullOrEmpty(f.DimensionDq) ? null : f.DimensionDq, f.Gobierno, DefinicionRegla.Lista(f.Contactos, mayusculas: false), f.CamposEvaluados,
            DefinicionRegla.Lista(f.Objetos, mayusculas: true), f.TablaStg, f.Mapping, f.NoCsv, f.NombreCsv,
            string.IsNullOrEmpty(f.Frecuencia) ? null : f.Frecuencia, f.EscribeLog, f.CorreoAsunto,
            DefinicionRegla.Lista(f.Destinatarios, mayusculas: false), f.EtapaOrden, f.QueryValidacion,
            f.TaskFlow, f.Hora, f.FechaProduccion, f.Comentarios);
        try
        {
            if (Id is int id)
            {
                reglas.Actualizar(id, datos, usuario.Login);
                MensajeOk = $"Regla {reglas.Obtener(id)!.Datos.Codigo} actualizada.";
            }
            else
            {
                reglas.Crear(datos, usuario.Login);
                MensajeOk = $"Regla {f.Codigo!.Trim().ToUpperInvariant()} dada de alta. Aparecerá en Calidad con resultados cuando se cargue su resumen de Informatica.";
            }
        }
        catch (DomainException ex)
        {
            // El formulario se queda con lo capturado y el mensaje del problema.
            Error = ex.Message;
            Catalogos = reglas.Catalogos();
            if (Id is int id && reglas.Obtener(id) is { } actual) { Activa = actual.Activa; f.Codigo = actual.Datos.Codigo; }
            return Page();
        }
        return RedirectToPage("Index");
    }
}
