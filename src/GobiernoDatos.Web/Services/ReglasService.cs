using GobiernoDatos.Web.Data;
using GobiernoDatos.Web.Domain;

namespace GobiernoDatos.Web.Services;

// Renglón del catálogo de reglas (menú Reglas).
public sealed record FilaRegla(int ReglaId, string Codigo, string Descripcion, string? Tipo, string? Gobierno, string? Dimension,
                               string? Responsable, IReadOnlyList<string> Objetos, string? Etapa, bool Activa);

// Regla para el formulario de cambio: sus datos actuales y su estado.
public sealed record ReglaEdicion(int ReglaId, bool Activa, DatosRegla Datos);

// Listas para el formulario de alta y cambio.
public sealed record CatalogosRegla(IReadOnlyList<string> Tipos, IReadOnlyList<string> Gobiernos, IReadOnlyList<string> Responsables,
                                    IReadOnlyList<string> Objetos);

// ABC de reglas de calidad. La baja es lógica: la regla queda inactiva en la etapa "Baja" y conserva su historial de resultados.
public interface IReglasService
{
    IReadOnlyList<FilaRegla> Listar();
    ReglaEdicion? Obtener(int reglaId);
    CatalogosRegla Catalogos();
    // Tipos y objetos BW nuevos se dan de alta. Devuelve el id.
    int Crear(DatosRegla datos, string usuario);
    // El código no cambia (identifica los resultados que llegan de Informatica). Si cambia la etapa, se registra el movimiento.
    void Actualizar(int reglaId, DatosRegla datos, string usuario);
    void DarDeBaja(int reglaId, string motivo, string usuario);
    // Vuelve a la última etapa anterior a la baja.
    void Reactivar(int reglaId, string usuario);
}

public sealed class ReglasService(GobiernoStore s) : IReglasService
{
    private const int OrdenBaja = 8;

    public IReadOnlyList<FilaRegla> Listar()
    {
        lock (s.Sync)
            return s.Reglas.OrderBy(r => r.Codigo).Select(r => new FilaRegla(r.ReglaId, r.Codigo, r.Descripcion, r.TipoRegla, r.Gobierno,
                r.DimensionDq, r.Responsable, r.Objetos.Select(Objeto).ToList(), r.Etapa, r.Activa)).ToList();
    }

    public ReglaEdicion? Obtener(int reglaId)
    {
        lock (s.Sync)
        {
            var r = s.Reglas.FirstOrDefault(x => x.ReglaId == reglaId);
            if (r is null) return null;
            var etapa = DefinicionRegla.Etapas.FirstOrDefault(e => e.Nombre == r.Etapa)?.Orden ?? 1;
            return new ReglaEdicion(r.ReglaId, r.Activa, new DatosRegla(r.Codigo, r.TipoRegla, r.Descripcion, r.ReglaFuncional, r.PropositoNegocio,
                r.DimensionDq, r.Gobierno, r.Responsable, r.CamposEvaluados, r.Objetos.Select(Objeto).ToList(), r.TablaStg, r.Mapping,
                r.NoCsv, r.NombreCsv, r.Frecuencia, r.EscribeLog, r.CorreoAsunto, [.. r.Destinatarios], etapa, r.QueryValidacion));
        }
    }

    public CatalogosRegla Catalogos()
    {
        lock (s.Sync)
            return new([.. s.Reglas.Select(r => r.TipoRegla).OfType<string>().Append("Nulos").Distinct().Order()],
                       [.. s.Gobiernos.Order()], [.. s.Responsables.Order()], [.. s.Objetos.Select(o => o.Nombre).Order()]);
    }

    public int Crear(DatosRegla datos, string usuario)
    {
        var d = DefinicionRegla.Normalizar(datos);
        lock (s.Sync)
        {
            if (s.Reglas.Any(x => x.Codigo.Equals(d.Codigo, StringComparison.OrdinalIgnoreCase)))
                throw new DomainException($"Ya existe una regla con el código {d.Codigo}.");
            var regla = new Regla { ReglaId = (int)s.NuevoId(), Codigo = d.Codigo, Descripcion = d.Descripcion, FechaAlta = s.Hoy };
            Aplicar(regla, d);
            s.Reglas.Add(regla);
            return regla.ReglaId;
        }
    }

    public void Actualizar(int reglaId, DatosRegla datos, string usuario)
    {
        lock (s.Sync)
        {
            var regla = Regla(reglaId);
            var d = DefinicionRegla.Normalizar(datos with { Codigo = regla.Codigo });
            if (!regla.Activa && d.EtapaOrden != OrdenBaja) throw new DomainException("La regla está dada de baja: reactívala antes de cambiar su etapa.");
            if (regla.Activa && d.EtapaOrden == OrdenBaja) throw new DomainException("Para dar de baja la regla usa el botón «Dar de baja».");
            Aplicar(regla, d);
        }
    }

    public void DarDeBaja(int reglaId, string motivo, string usuario)
    {
        if (string.IsNullOrWhiteSpace(motivo)) throw new DomainException("Indica el motivo de la baja.");
        lock (s.Sync)
        {
            var regla = Regla(reglaId);
            if (!regla.Activa) throw new DomainException("La regla ya está dada de baja.");
            regla.Activa = false;
            regla.Etapa = Etapa(OrdenBaja);
        }
    }

    public void Reactivar(int reglaId, string usuario)
    {
        lock (s.Sync)
        {
            var regla = Regla(reglaId);
            if (regla.Activa) throw new DomainException("La regla ya está activa.");
            regla.Activa = true;
            regla.Etapa = Etapa(1);
        }
    }

    private void Aplicar(Regla regla, DatosRegla d)
    {
        // El gobierno que ya tiene la regla se acepta aunque no esté en el catálogo.
        if (d.Gobierno is not null && d.Gobierno != regla.Gobierno && !s.Gobiernos.Contains(d.Gobierno))
            throw new DomainException("Selecciona un gobierno válido.");
        s.AsegurarResponsable(d.Responsable);
        regla.TipoRegla = d.TipoRegla; regla.Descripcion = d.Descripcion; regla.ReglaFuncional = d.ReglaFuncional;
        regla.PropositoNegocio = d.PropositoNegocio; regla.DimensionDq = d.DimensionDq; regla.Gobierno = d.Gobierno;
        regla.Responsable = d.Responsable; regla.CamposEvaluados = d.CamposEvaluados; regla.TablaStg = d.TablaStg; regla.Mapping = d.Mapping;
        regla.NoCsv = d.NoCsv; regla.NombreCsv = d.NombreCsv; regla.Frecuencia = d.Frecuencia; regla.EscribeLog = d.EscribeLog;
        regla.CorreoAsunto = d.CorreoAsunto; regla.QueryValidacion = d.QueryValidacion; regla.Etapa = Etapa(d.EtapaOrden);
        regla.Objetos.Clear();
        foreach (var nombre in d.Objetos)
        {
            var obj = s.Objetos.FirstOrDefault(o => o.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase));
            if (obj is null) s.Objetos.Add(obj = new ObjetoFuente { ObjetoId = (int)s.NuevoId(), Nombre = nombre });
            regla.Objetos.Add(obj.ObjetoId);
        }
        regla.Destinatarios.Clear();
        regla.Destinatarios.AddRange(d.Destinatarios);
    }

    private Regla Regla(int reglaId) => s.Reglas.FirstOrDefault(x => x.ReglaId == reglaId) ?? throw new DomainException("La regla no existe.");
    private string Objeto(int objetoId) => s.Objetos.First(o => o.ObjetoId == objetoId).Nombre;
    private static string Etapa(int orden) => DefinicionRegla.Etapas.First(e => e.Orden == orden).Nombre;
}
