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

    public CorreoOptions Correo { get; set; } = new();
}

// Recordatorios por correo (relay SMTP interno). La contraseña no va en appsettings: user-secrets en local y
// variable de entorno GobiernoDatos__Correo__Contrasena en el web.config del servidor.
public sealed class CorreoOptions
{
    public string? Servidor { get; set; }
    public int Puerto { get; set; } = 25;
    public bool UsarTls { get; set; }
    public string? Usuario { get; set; }
    public string? Dominio { get; set; }
    public string? Contrasena { get; set; }
    public string? Remitente { get; set; }
    // Dirección del portal para la liga de los correos (ej. http://srvmabe602:8010).
    public string? UrlPortal { get; set; }
}

// Identidad simulada para desarrollo local (Kestrel no tiene autenticación de Windows).
public sealed class UsuarioDemoOptions
{
    public string Login { get; set; } = "demo";
    public string Nombre { get; set; } = "Usuario demo";
    public string[] Roles { get; set; } = ["Equipo", "Administrador", "Certificador"];
    public string[] MacroAreas { get; set; } = [];
}
