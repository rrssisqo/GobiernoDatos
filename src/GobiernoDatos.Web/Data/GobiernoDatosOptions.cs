namespace GobiernoDatos.Web.Data;

public sealed class GobiernoDatosOptions
{
    public const string Seccion = "GobiernoDatos";

    // "Muestras" = almacén en memoria con data/muestras. "SqlServer" = esquema gd (v3).
    public string FuenteDatos { get; set; } = "Muestras";
    public string RutaMuestras { get; set; } = "../../data/muestras";

    // "Demo" = identidad simulada (solo desarrollo). "Windows" = usuario autenticado por IIS (producción).
    public string Autenticacion { get; set; } = "Demo";

    // Cuentas o grupos de AD con rol Administrador (Eventos, Bajas y evidencia). Ej.: MABENET\usuario, MABENET\Grupo.
    public string[] Administradores { get; set; } = [];

    // Dominio que se antepone cuando el usuario de red se escribe sin él (rrsmexti → MABENET\rrsmexti).
    public string DominioPredeterminado { get; set; } = "MABENET";

    public UsuarioDemoOptions UsuarioDemo { get; set; } = new();
}

// Identidad simulada para desarrollo local (Kestrel no tiene autenticación de Windows).
public sealed class UsuarioDemoOptions
{
    public string Login { get; set; } = "demo";
    public string Nombre { get; set; } = "Usuario demo";
    public string[] Roles { get; set; } = ["Equipo", "Administrador", "Certificador"];
    public string[] MacroAreas { get; set; } = [];
}
