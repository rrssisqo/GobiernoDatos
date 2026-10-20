namespace GobiernoDatos.Web.Domain;

// Catálogos fijos. Reflejan los INSERT de db/gobierno_datos_ddl_v2.sql (sección 2).

public sealed record EtapaVista(int EtapaId, int Orden, string Nombre, string? TareaPendiente, bool EsFinal);

public enum NivelRubro { DP, VISTA }

public sealed record Rubro(int RubroId, string Nombre, string Corto, NivelRubro Nivel, string GrupoResponsable,
                           decimal Peso, int Orden, int? EtapaOrdenIni, int? EtapaOrdenFin)
{
    public bool ActivoEnEtapa(int ordenEtapa) =>
        Nivel == NivelRubro.VISTA && ordenEtapa >= EtapaOrdenIni && ordenEtapa <= EtapaOrdenFin;
}

public sealed record Motivo(int MotivoId, string Nombre, string AplicaA);

public static class Catalogos
{
    public static readonly IReadOnlyList<EtapaVista> Etapas =
    [
        new(1,  0, "Por Iniciar",                  "Priorizar e iniciar la vista", false),
        new(2,  1, "Escaneo Inicial",              "Lanzar escaneo de metadatos en Informatica", false),
        new(3,  2, "Asignación Stake Holders",     "Asignar owners y stakeholders", false),
        new(4,  3, "Generación Glosarios Copilot", "Generar glosarios con Copilot", false),
        new(5,  4, "Import de Glosarios",          "Importar glosarios a Informatica", false),
        new(6,  5, "Reescaneo",                    "Relanzar escaneo de metadatos", false),
        new(7,  6, "Certificación Glosarios",      "Certificación de glosarios por el owner", false),
        new(8,  7, "Asignación Relaciones",        "Relacionar términos con columnas", false),
        new(9,  8, "Linaje",                       "Completar linaje", false),
        new(10, 9, "Data Product",                 "Documentar la vista dentro del data product", false),
        new(11, 10, "Perfilado",                   "Perfilar la vista", false),
        new(12, 11, "Calidad de Datos",            "Definir y crear reglas de calidad", false),
        new(13, 12, "Completada",                  null, true),
    ];

    // Orden de columnas del Excel: Glosario, Catalogado, DF, Arq., Linaje, Perfil, Reglas.
    public static readonly IReadOnlyList<Rubro> Rubros =
    [
        new(1, "Glosario",         "Glosario",   NivelRubro.VISTA, "Owner",           1, 1, 3, 6),
        new(2, "Catalogado",       "Catalogado", NivelRubro.DP,    "Owner",           1, 2, null, null),
        new(3, "Diseño Funcional", "DF",         NivelRubro.DP,    "Owner",           1, 3, null, null),
        new(4, "Arquitectura",     "Arq.",       NivelRubro.DP,    "Funcional",       1, 4, null, null),
        new(5, "Linaje",           "Linaje",     NivelRubro.VISTA, "Funcional",       1, 5, 7, 8),
        new(6, "Perfilado",        "Perfil",     NivelRubro.VISTA, "Owner-Funcional", 1, 6, 10, 10),
        new(7, "Reglas",           "Reglas",     NivelRubro.VISTA, "Owner-Funcional", 1, 7, 11, 11),
    ];

    public static readonly IReadOnlyList<Motivo> Motivos =
    [
        new(1, "Flujo inicial",        "AMBOS"),
        new(2, "Cambio de estructura", "AMBOS"),
        new(3, "Corrección",           "AMBOS"),
        new(4, "Solicitud del owner",  "AMBOS"),
        new(5, "Cambio de query",      "REGLA"),
        new(9, "Otro",                 "AMBOS"),
    ];

    public static readonly IReadOnlyList<string> NivelesSeguridad = ["Interno/Confidencial", "Restringido/Privado"];

    public static readonly IReadOnlyList<string> EtapasRegla =
        ["Definición", "Desarrollo", "Pruebas DEV", "Validación", "Pase a PRD", "Operación PRD", "Suspendida", "Baja"];

    public static EtapaVista EtapaPorOrden(int orden) => Etapas.First(e => e.Orden == orden);
    public static Rubro Rubro(int rubroId) => Rubros.First(r => r.RubroId == rubroId);
    public static Rubro? RubroActivo(int ordenEtapa) => Rubros.FirstOrDefault(r => r.ActivoEnEtapa(ordenEtapa));
    public static string NombreMotivo(int? motivoId) => Motivos.FirstOrDefault(m => m.MotivoId == motivoId)?.Nombre ?? "";
}
