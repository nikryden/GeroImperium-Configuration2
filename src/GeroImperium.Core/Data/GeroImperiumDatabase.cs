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

    /// <summary>Live online backup via SQLite's own backup API -- works even though this connection stays
    /// open the whole time (unlike a plain File.Copy, which Windows can refuse against an open, locked file).
    /// Used before any destructive local operation (Pull from Device) so there's always a way back.</summary>
    public void BackupTo(string destinationPath)
    {
        using var destination = new SqliteConnection($"Data Source={destinationPath}");
        destination.Open();
        _connection.BackupDatabase(destination);
    }

    /// <summary>Live online restore -- copies a backup file's pages into this already-open connection page by
    /// page, so the running app doesn't need to close/reopen its database file. Callers still need to reload
    /// any in-memory state (ViewModels loaded their collections once at construction) -- simplest correct fix
    /// today is telling the user to restart the app, rather than building live-reload plumbing that doesn't
    /// exist anywhere else in this codebase either.</summary>
    public void RestoreFrom(string backupFilePath)
    {
        using var source = new SqliteConnection($"Data Source={backupFilePath};Mode=ReadOnly");
        source.Open();
        source.BackupDatabase(_connection);
    }

    public void Dispose() => _connection.Dispose();
}
