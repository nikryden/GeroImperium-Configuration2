using GeroImperium.Core.Data;
using Microsoft.Data.Sqlite;

namespace GeroImperium.Core.Tests;

public class SchemaTests
{
    private static GeroImperiumDatabase CreateInMemoryDatabase()
    {
        var db = new GeroImperiumDatabase(":memory:");
        db.EnsureSchemaCreated();
        return db;
    }

    [Fact]
    public void EnsureSchemaCreated_CreatesAllSixTables()
    {
        using var db = CreateInMemoryDatabase();

        var tables = new List<string>();
        using var command = db.Connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Equal(
            new[] { "Applications", "GeneralSettings", "GeroImperiumKeys", "KeyActions", "KeyGroups", "Scripts" },
            tables);
    }

    [Fact]
    public void EnsureSchemaCreated_IsIdempotent()
    {
        using var db = CreateInMemoryDatabase();
        db.EnsureSchemaCreated();
    }

    [Theory]
    [InlineData("Applications", new[] { "Id", "ImageDataRgb565", "ImageData" })]
    [InlineData("KeyGroups", new[] { "Id", "ApplicationId" })]
    [InlineData("GeroImperiumKeys", new[] { "KeyGroupId", "Position", "ImageDataRgb565", "ImageData", "KeyActionId" })]
    [InlineData("KeyActions", new[] { "Id", "ShortcutKey", "CtrlModifier", "AltModifier", "ShiftModifier", "WinModifier" })]
    public void DeviceReadColumns_ExistWithExactNames(string table, string[] expectedColumns)
    {
        using var db = CreateInMemoryDatabase();

        var actualColumns = new HashSet<string>();
        using var command = db.Connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            actualColumns.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        foreach (var expected in expectedColumns)
        {
            Assert.Contains(expected, actualColumns);
        }
    }

    [Fact]
    public void GeneralSettingsTable_Exists()
    {
        using var db = CreateInMemoryDatabase();

        using var command = db.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'GeneralSettings';";
        var count = (long)command.ExecuteScalar()!;

        Assert.Equal(1, count);
    }

    [Fact]
    public void ForeignKeys_AreEnforced()
    {
        using var db = CreateInMemoryDatabase();

        using var command = db.Connection.CreateCommand();
        command.CommandText = "INSERT INTO KeyGroups (ApplicationId, Name) VALUES (999, 'orphan');";

        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }
}
