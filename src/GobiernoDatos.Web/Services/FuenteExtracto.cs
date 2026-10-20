using System.Data.Odbc;
using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;

namespace GobiernoDatos.Web.Services;

// Origen del extracto de recertificación para el botón "Actualizar" de Eventos.
// Devuelve filas con las columnas del extracto que entiende la carga (las mismas del CSV):
//   AREA, CONTENT, USUARIO, NOMBRE, ROL, VISTA, ACCESO ULTIMOS 3 MESES, GOBIERNO, Área Responsable, Responsable.
public interface IFuenteExtracto
{
    string Descripcion { get; }
    bool Configurada { get; }
    IReadOnlyList<Dictionary<string, string>> Leer();
}

// Vista HANA de recertificación por ODBC (ConnectionStrings:Hana, p. ej. "DSN=Hana;UID=...;PWD=...").
// El servidor que ejecuta el portal necesita el DSN de sistema de 64 bits con el driver HDBODBC.
public sealed class ExtractoHana(IConfiguration configuracion, ILogger<ExtractoHana> log) : IFuenteExtracto
{
    // OKBAJA y COMENTARIOS llegan vacíos: la decisión y el comentario se capturan en el portal.
    internal const string Consulta = """
        SELECT
            "AREA", "CONTENT", "ROL", "VISTA", "USUARIO", "NAME", "GOBIERNO", "AREARESPONSABLE", "RESPONSABLE",
            "OKBAJA", "COMENTARIOS", sum("ACCESOS") AS "ACCESOS"
        FROM "_SYS_BIC"."MABE.TI.ANALYTICS/CONT_TI_RECERTIFICACION"
        GROUP BY "AREA", "CONTENT", "ROL", "VISTA", "USUARIO", "NAME", "GOBIERNO", "AREARESPONSABLE", "RESPONSABLE",
                 "OKBAJA", "COMENTARIOS"
        """;

    private string? Cadena => configuracion.GetConnectionString("Hana");

    public string Descripcion => "Vista HANA MABE.TI.ANALYTICS/CONT_TI_RECERTIFICACION";
    public bool Configurada => !string.IsNullOrWhiteSpace(Cadena);

    public IReadOnlyList<Dictionary<string, string>> Leer()
    {
        if (!Configurada) throw new DomainException("Falta la conexión a HANA (ConnectionStrings:Hana) en la configuración del servidor.");
        try
        {
            using var cn = new OdbcConnection(Cadena);
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = Consulta;
            cmd.CommandTimeout = 300;
            using var rd = cmd.ExecuteReader();
            string Texto(string col) { var i = rd.GetOrdinal(col); return rd.IsDBNull(i) ? "" : Convert.ToString(rd.GetValue(i))!.Trim(); }

            var filas = new List<Dictionary<string, string>>();
            while (rd.Read())
            {
                var accesos = Texto("ACCESOS");
                filas.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AREA"] = Texto("AREA"), ["CONTENT"] = Texto("CONTENT"), ["USUARIO"] = Texto("USUARIO"), ["NOMBRE"] = Texto("NAME"),
                    ["ROL"] = Texto("ROL"), ["VISTA"] = Texto("VISTA"), ["GOBIERNO"] = Texto("GOBIERNO"),
                    ["ACCESO ULTIMOS 3 MESES"] = accesos == "" ? "0" : accesos,   // sin accesos en el periodo = 0
                    ["Área Responsable"] = Texto("AREARESPONSABLE"), ["Responsable"] = Texto("RESPONSABLE"),
                });
            }
            return filas;
        }
        catch (OdbcException ex)
        {
            log.LogError(ex, "Error al leer el extracto de recertificación desde HANA.");
            throw new DomainException($"No se pudo leer HANA por ODBC: {ex.Errors.Cast<OdbcError>().FirstOrDefault()?.Message ?? ex.Message}. " +
                                      "Revisa que el servidor tenga el DSN de sistema de 64 bits configurado.");
        }
    }
}

// Desarrollo: el extracto de muestra (data/muestras) hace las veces de HANA.
public sealed class ExtractoMuestras(IWebHostEnvironment env, Microsoft.Extensions.Options.IOptions<GobiernoDatosOptions> opciones) : IFuenteExtracto
{
    public string Descripcion => "Extracto de muestra (data/muestras)";
    public bool Configurada => true;

    public IReadOnlyList<Dictionary<string, string>> Leer() =>
        Csv.Leer(Csv.LeerTexto(File.ReadAllBytes(Path.GetFullPath(Path.Combine(env.ContentRootPath, opciones.Value.RutaMuestras,
                                                                                "recertificacion_extracto_muestra.csv")))));
}
