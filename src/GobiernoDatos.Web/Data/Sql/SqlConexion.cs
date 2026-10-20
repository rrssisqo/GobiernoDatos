using System.Data;
using Dapper;
using GobiernoDatos.Web.Domain;
using Microsoft.Data.SqlClient;

namespace GobiernoDatos.Web.Data.Sql;

// Conexión a la base gd. La cadena sale de ConnectionStrings:GobiernoDatos (user-secrets o variables de entorno).
public sealed class SqlConexion(string cadena, TimeProvider reloj)
{
    public string Servidor { get; } = new SqlConnectionStringBuilder(cadena).DataSource;
    public string BaseDatos { get; } = new SqlConnectionStringBuilder(cadena).InitialCatalog;

    public DateOnly Hoy => DateOnly.FromDateTime(reloj.GetLocalNow().DateTime);
    public DateTime Ahora => reloj.GetLocalNow().DateTime;

    public SqlConnection Abrir()
    {
        var c = new SqlConnection(cadena);
        c.Open();
        return c;
    }

    // Ejecuta una escritura en transacción.
    public T EnTransaccion<T>(Func<SqlConnection, IDbTransaction, T> accion)
    {
        using var c = Abrir();
        using var tx = c.BeginTransaction();
        var r = accion(c, tx);
        tx.Commit();
        return r;
    }

    public void EnTransaccion(Action<SqlConnection, IDbTransaction> accion) =>
        EnTransaccion<object?>((c, tx) => { accion(c, tx); return null; });
}

internal static class SqlAux
{
    // DATE de SQL llega como DateTime; las entidades usan DateOnly.
    public static DateOnly D(DateTime f) => DateOnly.FromDateTime(f);
    public static DateOnly? D(DateTime? f) => f is null ? null : DateOnly.FromDateTime(f.Value);
    public static DateTime P(DateOnly f) => f.ToDateTime(TimeOnly.MinValue);

    // responsable no tiene llave natural en v2: se busca por nombre y se da de alta si no existe.
    public static int? ResponsableId(IDbConnection c, IDbTransaction tx, string? nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre)) return null;
        nombre = nombre.Trim();
        return c.ExecuteScalar<int?>("SELECT TOP (1) responsable_id FROM gd.responsable WHERE nombre = @nombre ORDER BY responsable_id", new { nombre }, tx)
            ?? c.ExecuteScalar<int>("INSERT INTO gd.responsable (nombre) OUTPUT inserted.responsable_id VALUES (@nombre)", new { nombre }, tx);
    }

    public static int GobiernoId(IDbConnection c, IDbTransaction tx, string nombre) =>
        c.ExecuteScalar<int?>("SELECT gobierno_id FROM gd.gobierno WHERE nombre = @nombre", new { nombre }, tx)
        ?? throw new DomainException("Selecciona un gobierno válido.");

    public static void NotaSemanal(IDbConnection c, IDbTransaction tx, int dpId, DateOnly hoy, string? nota, int? rubroId, int? vistaId, string usuario)
    {
        if (string.IsNullOrWhiteSpace(nota)) return;
        c.Execute("""
            INSERT INTO gd.log_semanal (dp_id, semana_id, vista_id, rubro_id, comentario, usuario)
            VALUES (@dpId, @semanaId, @vistaId, @rubroId, @comentario, @usuario)
            """, new { dpId, semanaId = Semana.De(hoy).Id, vistaId, rubroId, comentario = nota.Trim(), usuario }, tx);
    }
}
