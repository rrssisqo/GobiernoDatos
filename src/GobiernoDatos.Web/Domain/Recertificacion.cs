namespace GobiernoDatos.Web.Domain;

public enum Decision { CONTINUA, BAJA }

public enum EstatusEvento { BORRADOR, ABIERTO, CERRADO }

// Reglas críticas de recertificación (modelo v3). En SQL se implementarán como CHECK, trigger y procs.
public static class ReglasRecertificacion
{
    public static void ValidarDecision(EstatusEvento estatus, Decision decision, string? comentario)
    {
        if (estatus != EstatusEvento.ABIERTO)
            throw new DomainException(estatus == EstatusEvento.CERRADO
                ? "El evento está cerrado: ya no se pueden registrar decisiones."
                : "El evento todavía no está abierto a los certificadores.");
        if (decision == Decision.BAJA && string.IsNullOrWhiteSpace(comentario))
            throw new DomainException("La baja requiere un comentario para la auditoría.");
    }

    public static void ValidarApertura(EstatusEvento estatus, int rechazosSinResolver, int accesos)
    {
        if (estatus != EstatusEvento.BORRADOR) throw new DomainException("Solo se puede abrir un evento en borrador.");
        if (rechazosSinResolver > 0)
            throw new DomainException($"El evento no puede abrirse: hay {rechazosSinResolver} filas rechazadas sin resolver.");
        if (accesos == 0) throw new DomainException("El evento no tiene accesos cargados.");
    }

    public static void ValidarCierre(EstatusEvento estatus)
    {
        if (estatus != EstatusEvento.ABIERTO) throw new DomainException("Solo se puede cerrar un evento abierto.");
    }

    public static void ValidarEjecucionBaja(Decision? vigente, bool yaEjecutada, string? ticket)
    {
        if (vigente != Decision.BAJA) throw new DomainException("El acceso no tiene una decisión vigente de Baja.");
        if (yaEjecutada) throw new DomainException("La baja ya se registró como ejecutada.");
        if (string.IsNullOrWhiteSpace(ticket)) throw new DomainException("Indica el ticket de la ejecución.");
    }

    // Renglón del catálogo GOBIERNO → macro área: ambos obligatorios, sin espacios sobrantes.
    public static (string Gobierno, string MacroArea) MapeoGobierno(string? gobierno, string? macroArea)
    {
        var g = (gobierno ?? "").Trim();
        var m = (macroArea ?? "").Trim();
        if (g.Length == 0) throw new DomainException("Indica el GOBIERNO tal como llega en el extracto de HANA (por ejemplo CSC).");
        if (m.Length == 0) throw new DomainException("Indica la macro área.");
        if (g.Length > 100 || m.Length > 100) throw new DomainException("El gobierno y la macro área admiten máximo 100 caracteres.");
        return (g, m);
    }

    // Número de cambio de infraestructura (máximo 60, como el ticket).
    public static string Cambio(string? cambio)
    {
        var c = (cambio ?? "").Trim();
        if (c.Length == 0) throw new DomainException("Indica el número de cambio.");
        if (c.Length > 60) throw new DomainException("El número de cambio admite máximo 60 caracteres.");
        return c;
    }

    // Correo del responsable de infraestructura avisando que terminó (Auditoría): .eml o .msg de Outlook, máximo 10 MB.
    public const int MaxCierreInfraBytes = 10 * 1024 * 1024;

    public static string ArchivoCierreInfra(string? nombre, long tamano)
    {
        var n = Path.GetFileName((nombre ?? "").Trim());
        if (tamano <= 0 || n.Length == 0) throw new DomainException("Selecciona el correo del responsable de infraestructura (.eml o .msg).");
        var ext = Path.GetExtension(n).ToLowerInvariant();
        if (ext is not (".eml" or ".msg"))
            throw new DomainException("El correo debe ser .eml o .msg: en Outlook, arrástralo a una carpeta o usa Archivo → Guardar como.");
        if (tamano > MaxCierreInfraBytes) throw new DomainException("El correo admite máximo 10 MB.");
        return n.Length > 200 ? n[..(200 - ext.Length)] + ext : n;
    }

    // El cierre documenta que infraestructura terminó: solo cuando todas las bajas de la macro área tienen ticket.
    public static void ValidarCierreInfra(int bajas, int sinTicket)
    {
        if (bajas == 0) throw new DomainException("La macro área no tiene bajas: no hay cierre de infraestructura que documentar.");
        if (sinTicket > 0)
            throw new DomainException($"El correo de cierre se sube cuando todas las bajas de la macro área tienen ticket (faltan {sinTicket}).");
    }

    public static string? ComentarioCierreInfra(string? comentario)
    {
        var c = comentario?.Trim();
        if (string.IsNullOrEmpty(c)) return null;
        if (c.Length > 500) throw new DomainException("El comentario admite máximo 500 caracteres.");
        return c;
    }

    public static string Etiqueta(Decision d) => d == Decision.CONTINUA ? "Continúa" : "Baja";

    public static string Etiqueta(EstatusEvento e) => e switch
    {
        EstatusEvento.BORRADOR => "Borrador",
        EstatusEvento.ABIERTO => "Abierto",
        _ => "Cerrado",
    };
}
