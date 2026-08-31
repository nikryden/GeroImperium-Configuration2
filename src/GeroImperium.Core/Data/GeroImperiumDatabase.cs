using Microsoft.Data.Sqlite;

namespace GeroImperium.Core.Data;

/// <summary>
/// Opens the authoring SQLite file. Kept schema-identical to the device's DB (see Schema.CreateTablesSql) so
/// this file can be pushed as-is over the bulk S/D/F path -- no transform step.
/// </summary>
public sealed class GeroImperiumDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public GeroImperiumDatabase(string databasePath)
    {
        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();

        using var pragma = _connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
    }

    public SqliteConnection Connection => _connection;

    public void EnsureSchemaCreated()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = Schema.CreateTablesSql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
