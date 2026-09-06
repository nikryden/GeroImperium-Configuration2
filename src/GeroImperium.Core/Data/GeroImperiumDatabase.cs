using Microsoft.Data.Sqlite;

namespace GeroImperium.Core.Data;

/// <summary>
/// Opens the authoring SQLite file (see Schema.CreateTablesSql for its shape and doc/plan2.md for why it's no
/// longer a byte-for-byte mirror of a device DB file).
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
        // Upgrades an existing older-shape DB in place first -- CREATE TABLE IF NOT EXISTS below only
        // creates brand-new tables, it never alters one that already exists (see SchemaMigration's doc
        // comment). No-ops entirely on a brand-new empty DB.
        SchemaMigration.Run(_connection);

        using var command = _connection.CreateCommand();
        command.CommandText = Schema.CreateTablesSql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
