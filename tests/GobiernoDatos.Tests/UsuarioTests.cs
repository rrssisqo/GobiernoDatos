using System.Security.Claims;
using System.Security.Principal;
using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace GobiernoDatos.Tests;

public class UsuarioTests
{
    private sealed class DirectorioFijo : IDirectorioUsuarios
    {
        public string NombreVisible(string login) => "Nombre de " + login;
        public string? BuscarNombre(string usuarioRed) => "Nombre de " + usuarioRed;
    }

    private static UsuarioWindows Usuario(ClaimsPrincipal principal, params string[] administradores) =>
        new(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } },
            Options.Create(new GobiernoDatosOptions { Administradores = administradores }), new DirectorioFijo());

    private static ClaimsPrincipal Windows(string login, params string[] grupos) =>
        new GenericPrincipal(new GenericIdentity(login, "Negotiate"), grupos);

    [Fact]
    public void Administrador_por_cuenta_sin_distinguir_mayusculas()
    {
        var u = Usuario(Windows(@"MABENET\RRSMEXTI"), @"mabenet\rrsmexti");
        Assert.True(u.TieneRol(RolesPortal.Administrador));
        Assert.Equal(@"MABENET\RRSMEXTI", u.Login);
        Assert.Equal(@"Nombre de MABENET\RRSMEXTI", u.Nombre);
        Assert.False(u.Simulado);
    }

    [Fact]
    public void Administrador_por_grupo_de_ad()
    {
        var u = Usuario(Windows(@"MABENET\otro", @"MABENET\GobiernoDatos_Admin"), @"MABENET\GobiernoDatos_Admin");
        Assert.True(u.TieneRol(RolesPortal.Administrador));
    }

    [Fact]
    public void Usuario_autenticado_sin_configurar_es_equipo_y_certificador_pero_no_administrador()
    {
        var u = Usuario(Windows(@"MABENET\otro"), @"MABENET\rrsmexti");
        Assert.False(u.TieneRol(RolesPortal.Administrador));
        Assert.True(u.TieneRol(RolesPortal.Equipo));
        Assert.True(u.TieneRol(RolesPortal.Certificador));
    }

    [Fact]
    public void Sin_identidad_de_windows_no_hay_roles_ni_usuario()
    {
        var u = Usuario(new ClaimsPrincipal(new ClaimsIdentity()), @"MABENET\rrsmexti");
        Assert.False(u.TieneRol(RolesPortal.Equipo));
        Assert.False(u.TieneRol(RolesPortal.Administrador));
        Assert.Throws<InvalidOperationException>(() => u.Login);
    }

    [Fact]
    public void Produccion_no_arranca_con_la_sesion_demo()
    {
        using var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // UseSetting llega a la configuración que Program.cs lee al arrancar.
            b.UseEnvironment("Production");
            b.UseSetting("GobiernoDatos:Autenticacion", "Demo");
            b.UseSetting("GobiernoDatos:FuenteDatos", "Muestras");
        });
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("sesión demo no se permite", ex.ToString());
    }
}
