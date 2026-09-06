using Microsoft.Data.Sqlite;
using GeroImperium.Core.Http;

namespace GeroImperium.Core.Data;

/// <summary>
/// Upgrades an authoring DB created before doc/plan2.md's schema migration (see Schema.cs) in place, so an
/// existing local authoring cache doesn't need to be deleted when this app updates. `CREATE TABLE IF NOT
/// EXISTS` (Schema.CreateTablesSql) only creates brand-new tables -- it never alters an existing one, so
/// GeroImperiumDatabase.EnsureSchemaCreated runs this first. Idempotent and safe on a brand-new empty DB
/// (the very first check finds no "Applications" table and returns immediately, leaving Schema.CreateTablesSql
/// to create everything fresh) and on an already-current one (every step checks before acting).
/// </summary>
internal static class SchemaMigration
{
    public static void Run(SqliteConnection connection)
    {
        if (!TableExists(connection, "Applications"))
        {
            return; // brand-new DB -- Schema.CreateTablesSql (run right after this) creates everything fresh
        }

        MigrateApplicationsAndPages(connection);
        MigrateKeyGroupsOrder(connection);
        MigrateKeyActionsToTextContent(connection);
        AddColumnIfMissing(connection, "GeroImperiumKeys", "RemoteId", "RemoteId", "INTEGER");
        MigrateGeneralSettings(connection);
        AddColumnIfMissing(connection, "Applications", "ImageChangedAtUtc", "ImageChangedAtUtc", "TEXT");
        AddColumnIfMissing(connection, "GeroImperiumKeys", "ImageChangedAtUtc", "ImageChangedAtUtc", "TEXT");
    }

    /// <summary>Adds the ApplicationPages table (didn't exist pre-migration) plus Applications.ApplicationPageId/
    /// "Order"/RemoteId, and backfills every existing Application onto one auto-created default page, ordered
    /// by its existing Id -- matching what AddApplication would have assigned if built fresh.</summary>
    private static void MigrateApplicationsAndPages(SqliteConnection connection)
    {
        if (HasColumn(connection, "Applications", "ApplicationPageId"))
        {
            return;
        }

        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS ApplicationPages (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    "Order" TEXT NOT NULL DEFAULT '1',
                    Name TEXT NOT NULL DEFAULT '',
                    RemoteId INTEGER
                );
                """;
            create.ExecuteNonQuery();
        }

        long defaultPageId;
        using (var insertPage = connection.CreateCommand())
        {
            insertPage.CommandText = """
                INSERT INTO ApplicationPages ("Order", Name) VALUES ('1', 'Page 1');
                SELECT last_insert_rowid();
                """;
            defaultPageId = (long)insertPage.ExecuteScalar()!;
        }

        AddColumnIfMissing(connection, "Applications", "ApplicationPageId", "ApplicationPageId", "INTEGER");
        AddColumnIfMissing(connection, "Applications", "Order", "\"Order\"", "TEXT");
        AddColumnIfMissing(connection, "Applications", "RemoteId", "RemoteId", "INTEGER");

        using var backfill = connection.CreateCommand();
        backfill.CommandText = """
            UPDATE Applications
            SET ApplicationPageId = @pageId,
                "Order" = CAST((SELECT COUNT(*) FROM Applications AS earlier WHERE earlier.Id <= Applications.Id) AS TEXT)
            WHERE ApplicationPageId IS NULL;
            """;
        backfill.Parameters.AddWithValue("@pageId", defaultPageId);
        backfill.ExecuteNonQuery();
    }

    /// <summary>Backfills "Order" per-Application, sequential by existing Id -- matching what AddKeyGroup
    /// would have assigned if built fresh.</summary>
    private static void MigrateKeyGroupsOrder(SqliteConnection connection)
    {
        if (!TableExists(connection, "KeyGroups") || HasColumn(connection, "KeyGroups", "Order"))
        {
            return;
        }

        AddColumnIfMissing(connection, "KeyGroups", "Order", "\"Order\"", "TEXT");
        AddColumnIfMissing(connection, "KeyGroups", "RemoteId", "RemoteId", "INTEGER");

        using var backfill = connection.CreateCommand();
        backfill.CommandText = """
            UPDATE KeyGroups
            SET "Order" = CAST(
                (SELECT COUNT(*) FROM KeyGroups AS earlier
                 WHERE earlier.ApplicationId = KeyGroups.ApplicationId AND earlier.Id <= KeyGroups.Id)
                AS TEXT)
            WHERE "Order" IS NULL;
            """;
        backfill.ExecuteNonQuery();
    }

    /// <summary>Converts the old ShortcutKey + 4 modifier-bool columns into Type/TextContent via ChordSyntax
    /// (same compiled shape KeySlotViewModel now writes), then drops the old columns -- see doc/plan2.md's
    /// KeyActions shortcut-shape migration.</summary>
    private static void MigrateKeyActionsToTextContent(SqliteConnection connection)
    {
        if (!TableExists(connection, "KeyActions"))
        {
            return;
        }

        if (!HasColumn(connection, "KeyActions", "ShortcutKey"))
        {
            AddColumnIfMissing(connection, "KeyActions", "RemoteId", "RemoteId", "INTEGER");
            return; // already migrated, or a table with no old columns to convert
        }

        AddColumnIfMissing(connection, "KeyActions", "Type", "Type", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "KeyActions", "TextContent", "TextContent", "TEXT");
        AddColumnIfMissing(connection, "KeyActions", "RemoteId", "RemoteId", "INTEGER");

        var rows = new List<(long Id, string? ShortcutKey, bool Ctrl, bool Shift, bool Alt, bool Win)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT Id, ShortcutKey, CtrlModifier, ShiftModifier, AltModifier, WinModifier FROM KeyActions;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetBoolean(2),
                    reader.GetBoolean(3),
                    reader.GetBoolean(4),
                    reader.GetBoolean(5)));
            }
        }

        foreach (var row in rows)
        {
            string? textContent = null;
            if (!string.IsNullOrEmpty(row.ShortcutKey))
            {
                var step = new ChordStep(row.Ctrl, row.Shift, row.Alt, row.Win, [row.ShortcutKey]);
                textContent = ChordSyntax.Build([step]);
            }

            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE KeyActions SET Type = 0, TextContent = @textContent WHERE Id = @id;";
            update.Parameters.AddWithValue("@textContent", (object?)textContent ?? DBNull.Value);
            update.Parameters.AddWithValue("@id", row.Id);
            update.ExecuteNonQuery();
        }

        DropColumnIfPresent(connection, "KeyActions", "ShortcutKey");
        DropColumnIfPresent(connection, "KeyActions", "CtrlModifier");
        DropColumnIfPresent(connection, "KeyActions", "ShiftModifier");
        DropColumnIfPresent(connection, "KeyActions", "AltModifier");
        DropColumnIfPresent(connection, "KeyActions", "WinModifier");
    }

    /// <summary>DefaultDevicePort was a CDC COM-port name -- meaningless now (see doc/plan2.md), so this
    /// drops it rather than migrating its value.</summary>
    private static void MigrateGeneralSettings(SqliteConnection connection)
    {
        if (!TableExists(connection, "GeneralSettings") || !HasColumn(connection, "GeneralSettings", "DefaultDevicePort"))
        {
            return;
        }

        AddColumnIfMissing(connection, "GeneralSettings", "DeviceIpAddress", "DeviceIpAddress", "TEXT");
        AddColumnIfMissing(connection, "GeneralSettings", "LastKnownBleDeviceId", "LastKnownBleDeviceId", "TEXT");
        DropColumnIfPresent(connection, "GeneralSettings", "DefaultDevicePort");
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;";
        command.Parameters.AddWithValue("@name", table);
        return (long)command.ExecuteScalar()! > 0;
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(reader.GetOrdinal("name")), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <param name="checkName">Bare column name (no quotes) to probe PRAGMA table_info for.</param>
    /// <param name="declareSql">Column name as it should appear in the ALTER statement -- quoted for
    /// reserved words like "Order".</param>
    private static void AddColumnIfMissing(SqliteConnection connection, string table, string checkName, string declareSql, string columnTypeSql)
    {
        if (HasColumn(connection, table, checkName))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {declareSql} {columnTypeSql};";
        command.ExecuteNonQuery();
    }

    private static void DropColumnIfPresent(SqliteConnection connection, string table, string column)
    {
        if (!HasColumn(connection, table, column))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {table} DROP COLUMN {column};";
        command.ExecuteNonQuery();
    }
}
