namespace GobiernoDatos.Web.Domain;

// Llave de la vista: PAQUETE/VISTA en mayúsculas, sin "_SYS_BIC". ni comillas.
// SharePoint trae "_SYS_BIC"."MABE.X/Y"; el extracto de recertificación trae MABE.X/Y.
public static class RutaTecnica
{
    public static string Normalizar(string ruta)
    {
        var r = ruta.Trim().Replace("\"", "");
        if (r.StartsWith("_SYS_BIC.", StringComparison.OrdinalIgnoreCase)) r = r["_SYS_BIC.".Length..];
        return r.Trim().ToUpperInvariant();
    }

    public static string Paquete(string rutaNormalizada)
    {
        var i = rutaNormalizada.LastIndexOf('/');
        return i < 0 ? "" : rutaNormalizada[..i];
    }

    public static string Vista(string rutaNormalizada)
    {
        var i = rutaNormalizada.LastIndexOf('/');
        return i < 0 ? rutaNormalizada : rutaNormalizada[(i + 1)..];
    }
}
