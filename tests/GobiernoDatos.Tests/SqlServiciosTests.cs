using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;
using Dapper;
using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Data.Sql;
using GobiernoDatos.Web.Domain;
using GobiernoDatos.Web.Services;
using Microsoft.Data.SqlClient;

namespace GobiernoDatos.Tests;

// Base desechable: DDL v2 + migraciones (db/migrations) + cargas de muestra (db/seeds).
// Corre solo si GD_SQL_TEST apunta a un servidor de pruebas (p. ej. LocalDB); nunca usar la base compartida.
//   $env:GD_SQL_TEST = "Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true"
public sealed class BaseSqlPrueba : IDisposable
{
    public const string Nombre = "GobiernoDatos_Pruebas";
    public string? Cadena { get; }

    public BaseSqlPrueba()
    {
        var servidor = Environment.GetEnvironmentVariable("GD_SQL_TEST");
        if (string.IsNullOrWhiteSpace(servidor)) return;

        using (var master = new SqlConnection(new SqlConnectionStringBuilder(servidor) { InitialCatalog = "master" }.ConnectionString))
        {
            master.Open();
            master.Execute($"IF DB_ID('{Nombre}') IS NOT NULL BEGIN ALTER DATABASE [{Nombre}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Nombre}]; END; CREATE DATABASE [{Nombre}];");
        }
        Cadena = new SqlConnectionStringBuilder(servidor) { InitialCatalog = Nombre }.ConnectionString;
        using var c = new SqlConnection(Cadena);
        c.Open();
        foreach (var script in new[] { "db/gobierno_datos_ddl_v2.sql", "db/seeds/001_muestras_v2.sql", "db/migrations/001_v3_recertificacion.sql",
                                        "db/migrations/002_portal.sql", "db/migrations/003_responsable_usuario_red.sql",
                                        "db/migrations/004_tarea_vista.sql", "db/migrations/005_avance_vista_captura_libre.sql", "db/migrations/006_calidad_marca_de_carga.sql", "db/migrations/007_avance_reportado_dp.sql",
                                        "db/migrations/008_auditoria_columnas_hana.sql", "db/migrations/009_recert_recordatorio.sql",
                                        "db/migrations/010_recert_gobierno_macro_area.sql",
                                        "db/seeds/002_recertificacion_muestra.sql" })
            foreach (var lote in Regex.Split(File.ReadAllText(Path.Combine(Raiz(), script)), @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                if (!string.IsNullOrWhiteSpace(lote)) c.Execute(lote, commandTimeout: 120);
    }

    private static string Raiz()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "db", "gobierno_datos_ddl_v2.sql"))) return d.FullName;
        throw new DirectoryNotFoundException("No se encontró db/.");
    }

    public void Dispose() => SqlConnection.ClearAllPools();
}

[CollectionDefinition("sql")]
public sealed class ColeccionSql : ICollectionFixture<BaseSqlPrueba>;

[Collection("sql")]
public class SqlServiciosTests(BaseSqlPrueba bd)
{
    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private SqlConexion Db()
    {
        Skip.If(bd.Cadena is null, "Define GD_SQL_TEST para correr las pruebas contra SQL Server.");
        return new SqlConexion(bd.Cadena!, new RelojFijo(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero)));
    }

    private static int DpId(SqlConexion db, string nombre)
    {
        using var c = db.Abrir();
        return c.ExecuteScalar<int>("SELECT dp_id FROM gd.data_product WHERE nombre = @nombre", new { nombre });
    }

    private static int VistaId(SqlConexion db, string nombre)
    {
        using var c = db.Abrir();
        return c.ExecuteScalar<int>("SELECT vista_id FROM gd.vista_gobernada WHERE nombre_negocio = @nombre", new { nombre });
    }

    [SkippableFact]
    public void Tablero_lee_las_vistas_de_avance()
    {
        var seg = new SqlSeguimientoService(Db());
        var t = seg.Tablero(new Semana(2026, 39), null);
        Assert.Equal(6, t.Filas.Count);
        var aux = t.Filas.Single(f => f.DataProduct == "Auxiliar de Inventarios");
        Assert.Equal(42.86m, aux.Avance);   // 3 rubros DP al 100%; los de vista sin vistas asignadas = 0
        Assert.Equal("Se concluye Catalogado de Data Product.", aux.Nota);
        Assert.Contains(new Semana(2026, 39), seg.Semanas());
    }

    [SkippableFact]
    public void Asignar_vista_y_capturar_rubros_cambia_el_avance_del_dp()
    {
        var db = Db();
        var seg = new SqlSeguimientoService(db);
        var vistas = new SqlVistasService(db);
        var dp = DpId(db, "Costo Tipo");
        var vista = VistaId(db, "Costo de Nomina");   // Certificación Glosarios → rubro activo Glosario

        vistas.Actualizar(vista, "Costo de Nomina", "Nominas", dp, "Fernando Sosa", "Restringido/Privado", "prueba");
        Assert.Throws<DomainException>(() => seg.RegistrarAvanceVista(dp, vista, 3, EstadoAvance.EN_CURSO, 50, null, "prueba"));   // rubro de DP
        seg.RegistrarAvanceVista(dp, vista, 1, EstadoAvance.EN_CURSO, 60, "Avance de glosario", "prueba");
        seg.RegistrarAvanceVista(dp, vista, 5, EstadoAvance.EN_CURSO, 50, null, "prueba");   // Linaje, antes de su etapa: cuenta

        var detalle = seg.Detalle(dp, new Semana(2026, 40))!;
        var v = Assert.Single(detalle.Vistas);
        Assert.Equal(60, v.Rubros.Single(r => r.RubroId == 1).Pct);
        Assert.Equal(50, v.Rubros.Single(r => r.RubroId == 5).Pct);
        Assert.Equal(1, v.RubroActivo!.RubroId);
        Assert.Contains(detalle.Bitacora, b => b.Detalle.Contains("Avance de glosario") && b.Usuario == "prueba");

        using var c = db.Abrir();
        Assert.Equal("prueba", c.ExecuteScalar<string>("SELECT TOP (1) usuario FROM gd.mov_avance_vista ORDER BY mov_id DESC"));
    }

    [SkippableFact]
    public void Avance_dp_respeta_la_coherencia_estado_pct_y_el_cierre()
    {
        var db = Db();
        var seg = new SqlSeguimientoService(db);
        var dp = seg.CrearDataProduct("DP de prueba", "Finanzas", "Responsable prueba", "prueba");

        Assert.Throws<DomainException>(() => seg.RegistrarAvanceDp(dp, 1, EstadoAvance.CERRADO, null, null, "prueba"));   // rubro de vista
        seg.RegistrarAvanceDp(dp, 4, EstadoAvance.NO_APLICA, null, null, "prueba");
        Assert.Equal(100, seg.Detalle(dp, new Semana(2026, 40))!.RubrosDp.Single(r => r.Rubro.RubroId == 4).Pct);

        seg.CerrarDataProduct(dp, "prueba");
        Assert.Throws<DomainException>(() => seg.AgregarNota(dp, "otra", "prueba"));
    }

    [SkippableFact]
    public void Mover_etapa_exige_motivo_en_regreso_y_queda_en_historial()
    {
        var db = Db();
        var vistas = new SqlVistasService(db);
        var vista = VistaId(db, "Head Count PBI");   // Reescaneo (5)

        Assert.Throws<DomainException>(() => vistas.MoverEtapa(vista, 5, null, null, "prueba"));
        Assert.Throws<DomainException>(() => vistas.MoverEtapa(vista, 2, null, null, "prueba"));
        vistas.MoverEtapa(vista, 2, 2, "Cambio de estructura", "prueba");

        var d = vistas.Detalle(vista)!;
        Assert.Equal("Asignación Stake Holders", d.Etapa.Nombre);
        Assert.True(d.Historial[0].EsRegreso);
        Assert.Equal("Cambio de estructura", d.Historial[0].Motivo);
    }

    [SkippableFact]
    public void Carga_de_calidad_usa_el_procedimiento_y_agrega_sumando()
    {
        var db = Db();
        var calidad = new SqlCalidadService(db);
        var vistas = new SqlVistasService(db);
        var vista = VistaId(db, "OEE Paradas");
        var pmaterial = vistas.Objetos().Single(o => o.Nombre == "PMATERIAL").ObjetoId;
        vistas.RelacionarObjeto(vista, pmaterial);

        var r = calidad.CargarResumen(
        [
            new("RL_1_1", null, 202609, 1000, 10),
            new(null, "02ZMM_O09_RL_01.csv", 202609, 3000, 300),
            new("RL_99", null, 202609, 5, 0),
            new("RL_1_1", null, 202610, 500, 5),
        ]);
        Assert.Equal(new ResultadoCarga(3, 1), r);

        var t = calidad.Tablero(202609, null);
        Assert.Equal(1 - 310m / 4000m, t.Total.Calidad);
        Assert.Equal("Regla no registrada", Assert.Single(t.Rechazos).Motivo);
        // La relación objeto → vista empieza hoy (octubre): no cuenta para septiembre, sí para octubre.
        Assert.Empty(t.PorRegla.Single(x => x.Codigo == "RL_1_1").Vistas);
        Assert.Contains(calidad.Tablero(202610, null).PorRegla.Single(x => x.Codigo == "RL_1_1").Vistas, v => v.VistaId == vista);
    }

    [SkippableFact]
    public void Inventario_refleja_el_extracto_de_recertificacion()
    {
        var vistas = new SqlVistasService(Db());
        var inv = vistas.Inventario(new FiltroVistas(PestanaVistas.Todas, null, null, null));
        Assert.Equal(14, inv.Total);
        Assert.Equal(7, inv.EnUltimaRecert);
        Assert.Equal("[Evento muestra]", inv.EventoSincronizacion);

        var cal = inv.Filas.Single(f => f.Vista == "CONTINENTAL_GENERICO_CALENDARIOFISCAL");
        Assert.Equal(3544, cal.Accesos3m);
        Assert.Equal(3, cal.Usuarios);
        Assert.Equal("TI", cal.MacroArea);
        Assert.Equal("Por Iniciar", cal.Etapa);
        Assert.All(vistas.Inventario(new FiltroVistas(PestanaVistas.Todas, "TI", null, null)).Filas, f => Assert.Equal("TI", f.MacroArea));

        var d = vistas.Detalle(cal.VistaId)!;
        Assert.Null(d.Vista.Gobierno);                                  // v3: sin gobierno hasta clasificar
        Assert.Equal("CON_GEN_CALENDARIO_FISCAL_GOVERNANCE", d.Rol);
        Assert.True(d.Vista.EnUltimaRecertificacion);
    }

    [SkippableFact]
    public void Clasificar_una_vista_nueva_sin_gobierno()
    {
        var vistas = new SqlVistasService(Db());
        var id = vistas.Inventario(new FiltroVistas(PestanaVistas.Nuevas, null, null, null)).Filas.First(f => f.Vista == "DM_MATERIAL").VistaId;
        vistas.Actualizar(id, "Material (dato maestro)", null, null, "Responsable prueba", "Interno/Confidencial", "prueba");
        var d = vistas.Detalle(id)!;
        Assert.Equal("Material (dato maestro)", d.Vista.NombreNegocio);
        Assert.Null(d.Vista.Gobierno);
        Assert.Equal("Responsable prueba", d.Vista.Responsable);
    }

    [SkippableFact]
    public void Vista_de_avance_cuenta_los_accesos_sin_decision_como_pendientes()
    {
        // Independiente del orden de las pruebas: los pendientes de la vista = accesos sin ninguna decisión registrada.
        using var c = Db().Abrir();
        var sinDecision = c.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM gd.recert_acceso AS a
            WHERE NOT EXISTS (SELECT 1 FROM gd.recert_decision AS d WHERE d.acceso_id = a.acceso_id)
            """);
        Assert.True(sinDecision > 0);
        Assert.Equal(sinDecision, c.ExecuteScalar<int>("SELECT SUM(pendientes) FROM gd.vw_recert_avance_macro_area"));
        Assert.Equal(sinDecision, c.ExecuteScalar<int>("SELECT COUNT(*) FROM gd.vw_recert_evidencia WHERE decision = 'PENDIENTE'"));
    }

    // Un solo método para controlar el orden: el flujo cambia el estatus del evento de muestra.
    [SkippableFact]
    public void Flujo_completo_de_recertificacion_sobre_la_base()
    {
        var db = Db();
        var opciones = Options.Create(new GobiernoDatosOptions());
        var recert = new SqlRecertificacionService(db, new DirectorioDePrueba(), opciones);
        const string cert = @"MABENET\cprueba";
        var evento = recert.EventoActivo()!;
        Assert.Equal(EstatusEvento.BORRADOR, evento.Estatus);

        // Sin certificador asignado no ve nada; el administrador lo asigna a TI.
        Assert.Empty(recert.MisAccesos(cert).Accesos);
        var ti = recert.Certificadores().Single(m => m.MacroArea == "TI");
        Assert.Throws<DomainException>(() => recert.AsignarCertificador(ti.MacroAreaId, @"MABENET\noexiste", "TITULAR"));   // no está en AD
        recert.AsignarCertificador(ti.MacroAreaId, "CPrueba", "TITULAR");                                                  // sin dominio: MABENET
        Assert.Contains(recert.Certificadores().Single(m => m.MacroArea == "TI").Certificadores,
                        c => c.UsuarioRed == cert && c.Nombre == "Prueba, Certificador (MEX, TI)" && c.Tipo == "TITULAR");

        recert.AbrirEvento(evento.EventoId);
        var mi = recert.MisAccesos(@"mabenet\CPRUEBA");                                                        // sin distinguir mayúsculas
        Assert.Equal(4, mi.Accesos.Count);
        Assert.All(mi.Accesos, a => Assert.Equal("TI", a.MacroArea));

        // Recordatorio a TI: un correo al certificador (correo de AD) y su renglón en la bitácora.
        var correos = new EnviadorSimulado();
        var recordatorios = new RecordatoriosService(recert, new DirectorioDePrueba(), correos, opciones, TimeProvider.System,
                                                     Microsoft.Extensions.Logging.Abstractions.NullLogger<RecordatoriosService>.Instance);
        Assert.Equal(1, recordatorios.Enviar(evento.EventoId, "TI", "admin").Enviados);
        var enviado = Assert.Single(correos.Enviados);
        Assert.Equal("cprueba@mabe.com.mx", enviado.Para);
        Assert.Contains("4 accesos pendientes", enviado.Asunto);
        var bitacora = Assert.Single(recert.Recordatorios(evento.EventoId));
        Assert.True(bitacora.Enviado);
        Assert.Equal(("TI", 4, cert, "admin"), (bitacora.MacroAreas, bitacora.Pendientes, bitacora.UsuarioRed, bitacora.EnviadoPor));

        var baja = mi.Accesos[0];
        Assert.Throws<DomainException>(() => recert.Decidir(baja.AccesoId, Decision.BAJA, " ", cert));
        Assert.Throws<DomainException>(() => recert.Decidir(baja.AccesoId, Decision.CONTINUA, null, @"MABENET\otra"));
        recert.Decidir(baja.AccesoId, Decision.CONTINUA, null, cert);
        var r = recert.Decidir(baja.AccesoId, Decision.BAJA, "Ya no pertenece al área", cert);
        Assert.Equal(1, r.Conteo.Baja);
        Assert.Throws<DomainException>(() => recert.ConfirmarMacroArea(ti.MacroAreaId, cert));      // faltan 3
        Assert.Equal(3, recert.ContinuaMasivo(mi.Accesos.Select(a => a.AccesoId), cert));
        recert.ConfirmarMacroArea(ti.MacroAreaId, cert);
        Assert.True(recert.Tablero(evento.EventoId)!.MacroAreas.Single(m => m.MacroArea == "TI").Confirmada);

        Assert.Equal(5, recert.CerrarEvento(evento.EventoId));                                   // otras macro áreas: pendientes reportados
        Assert.Throws<DomainException>(() => recert.Decidir(baja.AccesoId, Decision.CONTINUA, null, cert));

        Assert.Throws<DomainException>(() => recert.RegistrarEjecucion(baja.AccesoId, " ", null, "TI"));
        recert.RegistrarEjecucion(baja.AccesoId, "INC-001", null, "TI");
        var bajas = recert.Bajas(evento.EventoId)!;
        Assert.Equal(1, bajas.Ejecutadas);
        Assert.Contains(recert.Traza(baja.AccesoId), p => p.Titulo == "Decisión cambiada a Baja");
        Assert.Contains("INC-001", recert.EvidenciaCsv(evento.EventoId));
        Assert.Equal(EstatusEvento.CERRADO, recert.Historial().Single(h => h.EventoId == evento.EventoId).Estatus);

        // Un extracto con un rol en dos vistas queda en borrador con rechazos y no abre; se descarta.
        // La macro área sale del catálogo GOBIERNO → macro área; un gobierno sin catálogo (ni Área Responsable) se rechaza,
        // y el RESPONSABLE del extracto ya no crea certificadores.
        Dictionary<string, string> F(string u, string rol, string vista, string? gobierno = null) => new()
        {
            ["USUARIO"] = u, ["NOMBRE"] = u, ["ROL"] = rol, ["VISTA"] = vista, ["ACCESO ULTIMOS 3 MESES"] = "10",
            ["GOBIERNO"] = gobierno ?? "", ["Área Responsable"] = gobierno is null ? "TI" : "", ["Responsable"] = "Persona del extracto",
        };
        var id = recert.CrearEvento("Prueba rechazos", null, null,
            [F("U1", "ROL_X", "MABE.A/V1"), F("U2", "ROL_X", "MABE.A/V2"), F("U3", "CON_GEN_CALENDARIO_FISCAL_GOVERNANCE", "MABE.AGENERICO.GENERICO/CONTINENTAL_GENERICO_CALENDARIOFISCAL", "CSC"), F("U4", "ROL_Z", "MABE.A/V4", "NO_EXISTE")]);
        var t = recert.Tablero(id)!;
        Assert.Equal(1, t.Evento.Validacion.RolesConVariasVistas);
        Assert.Equal(3, t.Evento.Rechazos.Count);
        Assert.Contains(t.Evento.Rechazos, r => r.Usuario == "U4" && r.Motivo == "El gobierno «NO_EXISTE» no tiene macro área asignada");
        Assert.Equal("Finanzas", Assert.Single(t.MacroAreas).MacroArea);                          // CSC → Finanzas (catálogo); rol y vista ya existentes
        Assert.DoesNotContain(recert.Certificadores().SelectMany(m => m.Certificadores), x => x.Nombre == "Persona del extracto");
        Assert.Throws<DomainException>(() => recert.AbrirEvento(id));
        recert.DescartarBorrador(id);
        Assert.DoesNotContain(recert.Historial(), h => h.EventoId == id);

        recert.RetirarCertificador(ti.MacroAreaId, cert);
        Assert.DoesNotContain(recert.Certificadores().Single(m => m.MacroArea == "TI").Certificadores, c => c.UsuarioRed == cert);
    }

    // Directorio de prueba: solo existe MABENET\cprueba.
    private sealed class DirectorioDePrueba : IDirectorioUsuarios
    {
        public string NombreVisible(string login) => BuscarNombre(login) ?? login;
        public string? BuscarNombre(string usuarioRed) =>
            string.Equals(usuarioRed, @"MABENET\cprueba", StringComparison.OrdinalIgnoreCase) ? "Prueba, Certificador (MEX, TI)" : null;
        public string? BuscarCorreo(string usuarioRed) => BuscarNombre(usuarioRed) is null ? null : "cprueba@mabe.com.mx";
    }

    [SkippableFact]
    public void Tareas_de_etapa_sobre_la_base()
    {
        var db = Db();
        var vistas = new SqlVistasService(db);
        var tareas = new SqlTareasService(db);
        var vista = VistaId(db, "OEE Paradas");   // Escaneo Inicial (1)
        var ana = new Asignacion(@"MABENET\aperez", "Perez, Ana (MEX, TI)");
        var luis = new Asignacion(@"MABENET\lgomez", "Gomez, Luis (MEX, TI)");

        vistas.MoverEtapa(vista, 2, null, null, "admin", ana);
        var t = tareas.TareaAbierta(vista)!;
        Assert.Equal("Asignación Stake Holders", t.Etapa);
        Assert.Equal(ana.UsuarioRed, t.AsignadoA);

        Assert.Throws<DomainException>(() => tareas.Asignar(vista, ana, "admin"));
        tareas.Asignar(vista, luis, "admin");
        var actual = tareas.TareaAbierta(vista)!;
        Assert.Equal(luis.UsuarioRed, actual.AsignadoA);

        tareas.Terminar(actual.TareaId, luis.UsuarioRed);
        Assert.Throws<DomainException>(() => tareas.Terminar(actual.TareaId, luis.UsuarioRed));
        var terminada = tareas.Listar(new FiltroTareas(EstatusTarea.Terminada, null, null)).Single(x => x.TareaId == actual.TareaId);
        Assert.NotNull(terminada.FechaCierre);
        Assert.Equal(luis.UsuarioRed, terminada.CerradoPor);
        Assert.Contains(tareas.Listar(new FiltroTareas(EstatusTarea.Cancelada, ana.UsuarioRed, null)), x => x.MotivoCierre == "Reasignada");

        // La base impide dos tareas abiertas para la misma vista.
        tareas.Asignar(vista, ana, "admin");
        using var c = db.Abrir();
        Assert.ThrowsAny<Microsoft.Data.SqlClient.SqlException>(() => c.Execute("""
            INSERT INTO gd.tarea_vista (vista_id, etapa_id, asignado_a, asignado_nombre, asignado_por)
            SELECT @vista, 3, N'MABENET\otro', N'Otro', N'prueba'
            """, new { vista }));
        Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM gd.vw_seguimiento_tarea WHERE vista_id = @vista AND estatus = 'ABIERTA'", new { vista }));
    }

    [SkippableFact]
    public void Importar_seguimiento_sobre_la_base()
    {
        var seg = new SqlSeguimientoService(Db());
        var corte = new Semana(2026, 40);
        var lectura = ImportacionSeguimiento.Leer(
            Csv.LeerFilas(Csv.LeerTexto(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Datos", "seguimiento_prueba.csv")))), corte);

        var r = seg.ImportarSeguimiento(lectura.Filas, corte, "prueba");
        Assert.Equal(4, r.DataProductsNuevos);
        Assert.Equal(12, r.Avances);

        var t = seg.Tablero(corte, null);
        var dos = t.Filas.Single(f => f.DataProduct == "DP Dos");
        Assert.Equal(44.86m, dos.Avance);                        // la vista SQL usa el avance reportado de los rubros de vista
        Assert.Equal(70, dos.Rubros.Single(c => c.RubroId == 1).Pct);
        Assert.Equal(4, t.Filas.Single(f => f.DataProduct == "DP Uno").Vistas);
        Assert.True(seg.Detalle(dos.DpId, corte)!.UsaValoresIniciales);

        var otra = seg.ImportarSeguimiento(lectura.Filas, corte, "prueba");
        Assert.Equal(0, otra.Avances);
        Assert.Equal(0, otra.Notas);
    }

    [SkippableFact]
    public void Auditoria_lee_todos_los_accesos_de_la_evidencia()
    {
        var db = Db();
        var recert = new SqlRecertificacionService(db, new DirectorioDePrueba(), Options.Create(new GobiernoDatosOptions()));
        using var c = db.Abrir();
        var todas = recert.Auditoria(null);
        Assert.Equal(c.ExecuteScalar<int>("SELECT COUNT(*) FROM gd.vw_recert_evidencia"), todas.Count);
        Assert.NotEmpty(todas);
        var evento = todas[0].EventoId;
        Assert.All(recert.Auditoria(evento), f => Assert.Equal(evento, f.EventoId));
        Assert.All(todas, f => Assert.False(string.IsNullOrEmpty(f.AreaResponsable)));
        Assert.Contains(todas, f => f.Area is not null && f.Content is not null);
    }

    [SkippableFact]
    public void Catalogo_gobierno_macro_area_sobre_la_base()
    {
        var recert = new SqlRecertificacionService(Db(), new DirectorioDePrueba(), Options.Create(new GobiernoDatosOptions()));
        var mapeos = recert.MapeosGobierno();
        Assert.Equal(15, mapeos.Count);                                                          // precarga de la vista HANA
        Assert.Equal("Datos Maestros Material", mapeos.Single(m => m.Gobierno == "DM_Mat").MacroArea);

        recert.CrearMapeoGobierno(" NUEVO_GOB ", "Macro Nueva", "prueba");                       // crea la macro área
        Assert.Throws<DomainException>(() => recert.CrearMapeoGobierno("NUEVO_GOB", "TI", "prueba"));
        Assert.Contains(recert.Certificadores(), m => m.MacroArea == "Macro Nueva");
        recert.CambiarMapeoGobierno("NUEVO_GOB", "TI", "prueba");
        Assert.Equal(("TI", "prueba"), recert.MapeosGobierno().Where(m => m.Gobierno == "NUEVO_GOB").Select(m => (m.MacroArea, m.Usuario)).Single());
        recert.EliminarMapeoGobierno("NUEVO_GOB");
        Assert.DoesNotContain(recert.MapeosGobierno(), m => m.Gobierno == "NUEVO_GOB");
        Assert.Throws<DomainException>(() => recert.EliminarMapeoGobierno("NUEVO_GOB"));
        Assert.Throws<DomainException>(() => recert.CambiarMapeoGobierno("CSC", " ", "prueba"));
    }

    [SkippableFact]
    public void Abc_de_reglas_sobre_la_base()
    {
        var db = Db();
        var reglas = new SqlReglasService(db);
        DatosRegla Datos(string codigo, string descripcion, int etapa, string[] objetos) => new(codigo, "Duplicados", descripcion, "Funcional", null,
            "UNICIDAD", "Datos Maestros", "Responsable de regla", "MATL_TYPE", objetos, "STG_X", "m_DQ_X", 99, "99X.csv", "MENSUAL", false,
            "Asunto", ["datos@mabe.com.mx"], etapa, "SELECT 1");
        var id = reglas.Crear(Datos("rl_9_9", "Regla de prueba", 2, ["PMATERIAL", "ZNUEVO_O99"]), "prueba");
        Assert.Throws<DomainException>(() => reglas.Crear(Datos("RL_9_9", "Otra", 1, []), "prueba"));      // código repetido

        var r = reglas.Obtener(id)!;
        Assert.True(r.Activa);
        Assert.Equal(("RL_9_9", "Duplicados", "Datos Maestros", "MENSUAL", false, 2), (r.Datos.Codigo, r.Datos.TipoRegla, r.Datos.Gobierno, r.Datos.Frecuencia, r.Datos.EscribeLog, r.Datos.EtapaOrden));
        Assert.Equal(["PMATERIAL", "ZNUEVO_O99"], r.Datos.Objetos);
        Assert.Equal(["datos@mabe.com.mx"], r.Datos.Destinatarios);
        Assert.Contains(new SqlCalidadService(db).Tablero(202610, null).PorRegla, f => f.Codigo == "RL_9_9" && f.Medicion is null);
        Assert.Contains("Duplicados", reglas.Catalogos().Tipos);

        // Cambio: el código no cambia; objetos y destinatarios se reemplazan; la etapa nueva queda en el historial.
        reglas.Actualizar(id, Datos("OTRO", "Regla corregida", 6, ["PMATERIAL"]) with { Destinatarios = [], QueryValidacion = "SELECT 2" }, "prueba");
        r = reglas.Obtener(id)!;
        Assert.Equal(("RL_9_9", "Regla corregida", 6), (r.Datos.Codigo, r.Datos.Descripcion, r.Datos.EtapaOrden));
        Assert.Equal(["PMATERIAL"], r.Datos.Objetos);
        Assert.Empty(r.Datos.Destinatarios);
        using var c = db.Abrir();
        Assert.Equal(2, c.ExecuteScalar<int>("SELECT COUNT(*) FROM gd.regla_query_hist WHERE regla_id = @id", new { id }));   // trigger del query
        Assert.Throws<DomainException>(() => reglas.Actualizar(id, Datos("RL_9_9", "x", 8, []), "prueba"));                  // la baja va por su botón

        // Baja lógica con motivo y reactivación a la etapa anterior.
        Assert.Throws<DomainException>(() => reglas.DarDeBaja(id, " ", "prueba"));
        reglas.DarDeBaja(id, "Se reemplaza", "prueba");
        Assert.Contains(reglas.Listar(), f => f.ReglaId == id && !f.Activa && f.Etapa == "Baja");
        Assert.Throws<DomainException>(() => reglas.Actualizar(id, Datos("RL_9_9", "x", 6, []), "prueba"));                  // reactivar primero
        reglas.Reactivar(id, "prueba");
        Assert.Contains(reglas.Listar(), f => f.ReglaId == id && f.Activa && f.Etapa == "Operación PRD");
        Assert.Equal("Se reemplaza", c.ExecuteScalar<string>("""
            SELECT m.comentario FROM gd.mov_etapa_regla m JOIN gd.etapa_regla e ON e.etapa_regla_id = m.etapa_regla_id
            WHERE m.regla_id = @id AND e.orden = 8
            """, new { id }));
    }

    [SkippableFact]
    public void Editar_y_eliminar_data_product_sobre_la_base()
    {
        var db = Db();
        var seg = new SqlSeguimientoService(db);
        var dp = seg.CrearDataProduct("DP para eliminar", "Nominas", null, "prueba");   // propio: la base es compartida entre pruebas
        var vista = VistaId(db, "Costo de Nomina");
        new SqlVistasService(db).Actualizar(vista, "Costo de Nomina", "Nominas", dp, "Fernando Sosa", "Restringido/Privado", "prueba");
        seg.RegistrarAvanceDp(dp, 3, EstadoAvance.EN_CURSO, 40, "Nota de prueba", "prueba");

        seg.EditarDataProduct(dp, "  DP Editado ", "Finanzas", "MABENET\\prueba", "prueba");
        Assert.Throws<DomainException>(() => seg.EditarDataProduct(dp, "DP Editado", "No existe", null, "prueba"));
        Assert.Throws<DomainException>(() => seg.EditarDataProduct(dp, "Costo Tipo", "Finanzas", null, "prueba"));   // nombre duplicado
        var d = seg.Detalle(dp, seg.SemanaActual())!.Dp;
        Assert.Equal(("DP Editado", "Finanzas", "MABENET\\prueba"), (d.Nombre, d.Gobierno, d.Responsable));

        var r = seg.EliminarDataProduct(dp, "prueba");
        Assert.Equal("DP Editado", r.Nombre);
        Assert.True(r.Avances > 0 && r.Notas > 0 && r.Vistas == 1);
        Assert.Null(seg.Detalle(dp, seg.SemanaActual()));
        Assert.Throws<DomainException>(() => seg.EliminarDataProduct(dp, "prueba"));

        using var c = db.Abrir();
        Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM gd.vista_gobernada WHERE vista_id = @vista", new { vista }));   // la vista sigue en el inventario
    }
}
