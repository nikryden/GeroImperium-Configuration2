using GeroImperium.Core.Data;
using GeroImperium.Core.Models;
using Microsoft.Data.Sqlite;

namespace GeroImperium.Core.Tests;

/// <summary>Simulates a real pre-plan2.md authoring DB (exact old Schema.CreateTablesSql shape) and verifies
/// GeroImperiumDatabase.EnsureSchemaCreated upgrades it in place -- this is the scenario that crashed a real
/// existing local DB with "no such column: ApplicationPageId" the first time this app ran post-migration.</summary>
public class SchemaMigrationTests : IDisposable
{
    private const string OldSchemaSql = """
        CREATE TABLE Applications (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL DEFAULT '',
            ImageData BLOB,
            ImageDataRgb565 BLOB,
            BackgroundColorArgb INTEGER,
            SourceImageData BLOB
        );

        CREATE TABLE Scripts (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            Body TEXT NOT NULL DEFAULT '',
            Interpreter TEXT NOT NULL DEFAULT 'PowerShell'
        );

        CREATE TABLE KeyActions (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ShortcutKey TEXT,
            CtrlModifier INTEGER NOT NULL DEFAULT 0,
            AltModifier INTEGER NOT NULL DEFAULT 0,
            ShiftModifier INTEGER NOT NULL DEFAULT 0,
            WinModifier INTEGER NOT NULL DEFAULT 0,
            ActionType INTEGER NOT NULL DEFAULT 0,
            LaunchPath TEXT,
            ScriptId INTEGER REFERENCES Scripts(Id)
        );

        CREATE TABLE KeyGroups (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ApplicationId INTEGER NOT NULL REFERENCES Applications(Id),
            Name TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE GeroImperiumKeys (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            KeyGroupId INTEGER NOT NULL REFERENCES KeyGroups(Id),
            Position INTEGER NOT NULL,
            ImageData BLOB,
            ImageDataRgb565 BLOB,
            KeyActionId INTEGER REFERENCES KeyActions(Id),
            BackgroundColorArgb INTEGER,
            SourceImageData BLOB
        );

        CREATE TABLE GeneralSettings (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Theme TEXT NOT NULL DEFAULT 'Dark',
            DefaultDevicePort TEXT
        );
        """;

    private readonly GeroImperiumDatabase _database;

    public SchemaMigrationTests()
    {
        _database = new GeroImperiumDatabase(":memory:");

        using var seed = _database.Connection.CreateCommand();
        seed.CommandText = OldSchemaSql;
        seed.ExecuteNonQuery();
    }

    public void Dispose() => _database.Dispose();

    private void SeedOldApplication(long id, string name)
    {
        using var command = _database.Connection.CreateCommand();
        command.CommandText = "INSERT INTO Applications (Id, Name) VALUES (@id, @name);";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@name", name);
        command.ExecuteNonQuery();
    }

    private long SeedOldKeyAction(string? shortcutKey, bool ctrl, bool shift, bool alt, bool win)
    {
        using var command = _database.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO KeyActions (ShortcutKey, CtrlModifier, ShiftModifier, AltModifier, WinModifier)
            VALUES (@key, @ctrl, @shift, @alt, @win);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@key", (object?)shortcutKey ?? DBNull.Value);
        command.Parameters.AddWithValue("@ctrl", ctrl);
        command.Parameters.AddWithValue("@shift", shift);
        command.Parameters.AddWithValue("@alt", alt);
        command.Parameters.AddWithValue("@win", win);
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void EnsureSchemaCreated_OldApplicationsTable_GetsApplicationPageIdAndOrderBackfilled()
    {
        SeedOldApplication(1, "First");
        SeedOldApplication(2, "Second");

        _database.EnsureSchemaCreated();

        var repository = new GeroImperiumRepository(_database);
        var apps = repository.GetApplications();

        Assert.Equal(2, apps.Count);
        Assert.All(apps, a => Assert.True(a.ApplicationPageId > 0));
        Assert.Equal(apps[0].ApplicationPageId, apps[1].ApplicationPageId); // same auto-created default page
        Assert.Equal("1", apps.Single(a => a.Name == "First").Order);
        Assert.Equal("2", apps.Single(a => a.Name == "Second").Order);

        var pages = repository.GetApplicationPages();
        var page = Assert.Single(pages);
        Assert.Equal("Page 1", page.Name);
    }

    [Fact]
    public void EnsureSchemaCreated_IsIdempotent_OnAnAlreadyMigratedDatabase()
    {
        SeedOldApplication(1, "First");

        _database.EnsureSchemaCreated();
        _database.EnsureSchemaCreated(); // must not throw or duplicate the default page

        var repository = new GeroImperiumRepository(_database);
        Assert.Single(repository.GetApplicationPages());
        Assert.Single(repository.GetApplications());
    }

    [Fact]
    public void EnsureSchemaCreated_OldKeyActionsColumns_ConvertToTextContentAndAreDropped()
    {
        var id = SeedOldKeyAction("c", ctrl: true, shift: true, alt: false, win: false);

        _database.EnsureSchemaCreated();

        var repository = new GeroImperiumRepository(_database);
        var action = repository.GetKeyAction(id);

        Assert.NotNull(action);
        Assert.Equal(HidActionKind.Hid, action!.Type);
        Assert.Equal("[ctrl]+[shift]+c", action.TextContent);

        using var command = _database.Connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(KeyActions);";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        Assert.DoesNotContain("ShortcutKey", columns);
        Assert.DoesNotContain("CtrlModifier", columns);
    }

    [Fact]
    public void EnsureSchemaCreated_OldKeyActionWithNoShortcut_GetsNullTextContent()
    {
        var id = SeedOldKeyAction(null, ctrl: false, shift: false, alt: false, win: false);

        _database.EnsureSchemaCreated();

        var action = new GeroImperiumRepository(_database).GetKeyAction(id);

        Assert.NotNull(action);
        Assert.Null(action!.TextContent);
    }

    [Fact]
    public void EnsureSchemaCreated_OldGeneralSettingsTable_DropsDefaultDevicePortAddsNewColumns()
    {
        _database.EnsureSchemaCreated();

        using var command = _database.Connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(GeneralSettings);";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        Assert.DoesNotContain("DefaultDevicePort", columns);
        Assert.Contains("DeviceIpAddress", columns);
        Assert.Contains("LastKnownBleDeviceId", columns);
    }

    [Fact]
    public void EnsureSchemaCreated_OldTables_GetImageChangedAtUtcColumnAdded()
    {
        SeedOldApplication(1, "First");

        _database.EnsureSchemaCreated();

        using var appColumns = _database.Connection.CreateCommand();
        appColumns.CommandText = "PRAGMA table_info(Applications);";
        using var appReader = appColumns.ExecuteReader();
        var applicationColumns = new HashSet<string>();
        while (appReader.Read())
        {
            applicationColumns.Add(appReader.GetString(appReader.GetOrdinal("name")));
        }

        Assert.Contains("ImageChangedAtUtc", applicationColumns);

        using var keyColumns = _database.Connection.CreateCommand();
        keyColumns.CommandText = "PRAGMA table_info(GeroImperiumKeys);";
        using var keyReader = keyColumns.ExecuteReader();
        var keyColumnNames = new HashSet<string>();
        while (keyReader.Read())
        {
            keyColumnNames.Add(keyReader.GetString(keyReader.GetOrdinal("name")));
        }

        Assert.Contains("ImageChangedAtUtc", keyColumnNames);
    }

    [Fact]
    public void EnsureSchemaCreated_BrandNewEmptyDatabase_StillCreatesEverythingFresh()
    {
        using var fresh = new GeroImperiumDatabase(":memory:");

        fresh.EnsureSchemaCreated();

        var repository = new GeroImperiumRepository(fresh);
        Assert.Empty(repository.GetApplications());
        var app = repository.AddApplication("New");
        Assert.True(app.ApplicationPageId > 0);
    }
}
