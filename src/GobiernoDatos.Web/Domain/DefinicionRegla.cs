using System.Net.Mail;
using System.Text.RegularExpressions;

namespace GobiernoDatos.Web.Domain;

// Definición de una regla de calidad para alta y cambio (columnas de la lista "Reglas" de SharePoint → gd.regla y sus tablas hijas).
// Contactos = correos de contacto de la regla (gd.regla_contacto CONTACTO); Frecuencia = nombre del catálogo gd.frecuencia_regla.
public sealed record DatosRegla(
    string Codigo, string? TipoRegla, string Descripcion, string? ReglaFuncional, string? PropositoNegocio,
    string? DimensionDq, string? Gobierno, IReadOnlyList<string> Contactos, string? CamposEvaluados, IReadOnlyList<string> Objetos,
    string? TablaStg, string? Mapping, int? NoCsv, string? NombreCsv, string? Frecuencia, bool EscribeLog,
    string? CorreoAsunto, IReadOnlyList<string> Destinatarios, int EtapaOrden, string? QueryValidacion = null,
    string? TaskFlow = null, TimeOnly? Hora = null, DateOnly? FechaProduccion = null, string? Comentarios = null);

public sealed record EtapaRegla(int Orden, string Nombre);

public static partial class DefinicionRegla
{
    // Valores permitidos por el CHECK de gd.regla y el catálogo gd.etapa_regla (las frecuencias son un catálogo editable).
    public static readonly string[] Dimensiones = ["COMPLETITUD", "VALIDEZ", "UNICIDAD", "CONSISTENCIA", "EXACTITUD", "OPORTUNIDAD"];
    public static readonly EtapaRegla[] Etapas =
    [
        new(1, "Definición"), new(2, "Desarrollo"), new(3, "Pruebas DEV"), new(4, "Validación"),
        new(5, "Pase a PRD"), new(6, "Operación PRD"), new(7, "Suspendida"), new(8, "Baja"),
    ];

    public static string Etiqueta(string valor) => valor[..1] + valor[1..].ToLowerInvariant();

    // "PMATERIAL; ZMM_O09" → [PMATERIAL, ZMM_O09]; igual para correos separados por ; o ,
    public static IReadOnlyList<string> Lista(string? texto, bool mayusculas) =>
        (texto ?? "").Split([';', ',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => mayusculas ? x.ToUpperInvariant() : x).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // Limpia y valida; lanza DomainException con el primer problema.
    public static DatosRegla Normalizar(DatosRegla r)
    {
        var codigo = (r.Codigo ?? "").Trim().ToUpperInvariant();
        if (codigo.Length == 0) throw new DomainException("Indica el código de la regla (por ejemplo RL_4_1).");
        if (!Codigo().IsMatch(codigo)) throw new DomainException("El código solo admite letras, números y guion bajo (máximo 20), por ejemplo RL_4_1.");
        var descripcion = (r.Descripcion ?? "").Trim();
        if (descripcion.Length == 0) throw new DomainException("Indica la descripción de la regla.");
        if (r.DimensionDq is { } d && !Dimensiones.Contains(d)) throw new DomainException("La dimensión de calidad no es válida.");
        if (r.NoCsv is <= 0) throw new DomainException("El número de CSV debe ser mayor que cero.");
        if (Etapas.All(e => e.Orden != r.EtapaOrden)) throw new DomainException("La etapa no es válida.");
        foreach (var o in r.Objetos)
            if (o.Length > 100) throw new DomainException($"El objeto BW «{o[..20]}…» es demasiado largo (máximo 100).");
        foreach (var correo in r.Destinatarios.Concat(r.Contactos))
            if (!MailAddress.TryCreate(correo, out var m) || m.Address != correo) throw new DomainException($"«{correo}» no es un correo válido.");

        Largo(descripcion, 300, "La descripción");
        Largo(r.ReglaFuncional, 2000, "La regla funcional");
        Largo(r.PropositoNegocio, 2000, "El propósito de negocio");
        Largo(r.CamposEvaluados, 400, "Los campos evaluados");
        Largo(r.TablaStg, 128, "La tabla STG");
        Largo(r.Mapping, 128, "El mapping");
        Largo(r.NombreCsv, 260, "El nombre del CSV");
        Largo(r.CorreoAsunto, 200, "El asunto del correo");
        Largo(r.TipoRegla, 80, "El tipo de regla");
        Largo(r.TaskFlow, 128, "El task flow");
        Largo(r.Frecuencia, 40, "La frecuencia");
        Largo(r.Comentarios, 2000, "Los comentarios");

        return r with
        {
            Codigo = codigo, Descripcion = descripcion, TipoRegla = Nulo(r.TipoRegla), ReglaFuncional = Nulo(r.ReglaFuncional),
            PropositoNegocio = Nulo(r.PropositoNegocio), Gobierno = Nulo(r.Gobierno), Frecuencia = Nulo(r.Frecuencia),
            TaskFlow = Nulo(r.TaskFlow), Comentarios = Nulo(r.Comentarios),
            CamposEvaluados = Nulo(r.CamposEvaluados), TablaStg = Nulo(r.TablaStg), Mapping = Nulo(r.Mapping),
            NombreCsv = Nulo(r.NombreCsv), CorreoAsunto = Nulo(r.CorreoAsunto), QueryValidacion = Nulo(r.QueryValidacion),
        };
    }

    private static void Largo(string? valor, int maximo, string campo)
    {
        if (valor?.Trim().Length > maximo) throw new DomainException($"{campo} admite máximo {maximo} caracteres.");
    }

    private static string? Nulo(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    [GeneratedRegex("^[A-Z0-9_]{1,20}$")]
    private static partial Regex Codigo();
}
