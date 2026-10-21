using System.Globalization;
using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Data.Sql;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Server.IIS;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GobiernoDatosOptions>(builder.Configuration.GetSection(GobiernoDatosOptions.Seccion));
builder.Services.AddSingleton(TimeProvider.System);

// Almacén en memoria con data/muestras (solo lo usan los servicios de la fuente "Muestras").
builder.Services.AddSingleton(sp =>
{
    var opciones = sp.GetRequiredService<IOptions<GobiernoDatosOptions>>().Value;
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    var store = new GobiernoStore(sp.GetRequiredService<TimeProvider>());
    MuestrasLoader.Cargar(store, Path.GetFullPath(Path.Combine(env.ContentRootPath, opciones.RutaMuestras)), opciones.UsuarioDemo, opciones.DominioPredeterminado);
    return store;
});
var fuente = builder.Configuration[$"{GobiernoDatosOptions.Seccion}:FuenteDatos"] ?? "Muestras";
if (fuente.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
{
    // Todos los módulos sobre el esquema gd (v3: db/migrations).
    var cadena = builder.Configuration.GetConnectionString("GobiernoDatos")
        ?? throw new InvalidOperationException("Falta ConnectionStrings:GobiernoDatos (configúrala con dotnet user-secrets; ver docs/05_portal_razor.md).");
    builder.Services.AddSingleton(sp => new SqlConexion(cadena, sp.GetRequiredService<TimeProvider>()));
    builder.Services.AddSingleton<ISeguimientoService, SqlSeguimientoService>();
    builder.Services.AddSingleton<IVistasService, SqlVistasService>();
    builder.Services.AddSingleton<ICalidadService, SqlCalidadService>();
    builder.Services.AddSingleton<IReglasService, SqlReglasService>();
    builder.Services.AddSingleton<IRecertificacionService, SqlRecertificacionService>();
    builder.Services.AddSingleton<ITareasService, SqlTareasService>();
    builder.Services.AddSingleton<IFuenteExtracto, ExtractoHana>();
    builder.Services.AddSingleton(sp =>
    {
        var db = sp.GetRequiredService<SqlConexion>();
        return new FuenteDatos($"SQL Server · {db.Servidor}/{db.BaseDatos}");
    });
}
else if (fuente.Equals("Muestras", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ISeguimientoService, SeguimientoService>();
    builder.Services.AddSingleton<IVistasService, VistasService>();
    builder.Services.AddSingleton<ICalidadService, CalidadService>();
    builder.Services.AddSingleton<IReglasService, ReglasService>();
    builder.Services.AddSingleton<IRecertificacionService, RecertificacionService>();
    builder.Services.AddSingleton<ITareasService, TareasService>();
    builder.Services.AddSingleton<IFuenteExtracto, ExtractoMuestras>();
    builder.Services.AddSingleton(new FuenteDatos("Datos de muestra en memoria"));
}
else
{
    throw new InvalidOperationException($"GobiernoDatos:FuenteDatos = '{fuente}' no es válido (Muestras o SqlServer).");
}
// Identidad: Windows (IIS) en producción; usuario demo solo para desarrollo local.
var autenticacion = builder.Configuration[$"{GobiernoDatosOptions.Seccion}:Autenticacion"] ?? "Demo";
if (autenticacion.Equals("Windows", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddAuthentication(IISServerDefaults.AuthenticationScheme);
    builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<IDirectorioUsuarios, DirectorioActivo>();
    builder.Services.AddScoped<IUsuarioActual, UsuarioWindows>();
}
else if (builder.Environment.IsProduction())
{
    throw new InvalidOperationException("En producción GobiernoDatos:Autenticacion debe ser \"Windows\": la sesión demo no se permite.");
}
else
{
    builder.Services.AddSingleton<IDirectorioUsuarios, DirectorioSimulado>();
    builder.Services.AddScoped<IUsuarioActual, UsuarioDemo>();
}

// Correo: relay SMTP si está configurado o en producción (sin configuración avisa al enviar); en desarrollo, simulado.
var servidorCorreo = builder.Configuration[$"{GobiernoDatosOptions.Seccion}:Correo:Servidor"];
if (!string.IsNullOrWhiteSpace(servidorCorreo) || builder.Environment.IsProduction())
    builder.Services.AddSingleton<IEnviadorCorreo, EnviadorSmtp>();
else
    builder.Services.AddSingleton<IEnviadorCorreo, EnviadorSimulado>();
builder.Services.AddSingleton<RecordatoriosService>();
builder.Services.AddSingleton<NotificacionesInfra>();

// Búsqueda de usuarios para asignar certificadores: Entra ID si está configurado o en producción; en desarrollo, simulada.
builder.Services.AddHttpClient("graph", c => c.Timeout = TimeSpan.FromSeconds(20));
var tenantEntra = builder.Configuration[$"{GobiernoDatosOptions.Seccion}:Entra:TenantId"];
if (!string.IsNullOrWhiteSpace(tenantEntra) || builder.Environment.IsProduction())
    builder.Services.AddSingleton<IBuscadorUsuarios, BuscadorEntra>();
else
    builder.Services.AddSingleton<IBuscadorUsuarios, BuscadorSimulado>();

builder.Services.AddSingleton<ResolutorUsuarios>();
builder.Services.AddRazorPages();

var app = builder.Build();

var cultura = new CultureInfo("es-MX");
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(cultura),
    SupportedCultures = [cultura],
    SupportedUICultures = [cultura],
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
if (autenticacion.Equals("Windows", StringComparison.OrdinalIgnoreCase))
{
    app.UseAuthentication();
    app.UseAuthorization();
}
app.MapRazorPages();

// Con muestras, carga los datos al arrancar para que un error en los archivos se vea de inmediato.
if (fuente.Equals("Muestras", StringComparison.OrdinalIgnoreCase)) app.Services.GetRequiredService<GobiernoStore>();

app.Run();

// Expuesto para las pruebas de punta a punta (WebApplicationFactory).
public partial class Program;
