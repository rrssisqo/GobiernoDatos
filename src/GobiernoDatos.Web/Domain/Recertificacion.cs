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

    public static string Etiqueta(Decision d) => d == Decision.CONTINUA ? "Continúa" : "Baja";

    public static string Etiqueta(EstatusEvento e) => e switch
    {
        EstatusEvento.BORRADOR => "Borrador",
        EstatusEvento.ABIERTO => "Abierto",
        _ => "Cerrado",
    };
}
