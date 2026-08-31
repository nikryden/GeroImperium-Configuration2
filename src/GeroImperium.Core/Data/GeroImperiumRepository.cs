using Microsoft.Data.Sqlite;
using GeroImperium.Core.Models;

namespace GeroImperium.Core.Data;

/// <summary>
/// CRUD access to the authoring DB for the App's editor pages. Kept as plain ADO.NET over
/// GeroImperiumDatabase's connection rather than an ORM -- the schema is small and fixed (Schema.cs), and
/// every write here must stay compatible with the exact column set the device (and the bulk S/D/F path)
/// expects.
/// </summary>
public sealed class GeroImperiumRepository
{
    private readonly GeroImperiumDatabase _database;

    public GeroImperiumRepository(GeroImperiumDatabase database)
    {
        _database = database;
    }

    private SqliteConnection Connection => _database.Connection;

    private static object ToDb(object? value) => value ?? DBNull.Value;

    // ----- Applications -----

    public List<Application> GetApplications()
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, ImageData, ImageDataRgb565, BackgroundColorArgb, SourceImageData FROM Applications ORDER BY Id;";
        using var reader = command.ExecuteReader();

        var results = new List<Application>();
        while (reader.Read())
        {
            results.Add(new Application
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                Name = reader.GetString(reader.GetOrdinal("Name")),
                ImageData = ReadNullableBlob(reader, "ImageData"),
                ImageDataRgb565 = ReadNullableBlob(reader, "ImageDataRgb565"),
                BackgroundColorArgb = ReadNullableInt(reader, "BackgroundColorArgb"),
                SourceImageData = ReadNullableBlob(reader, "SourceImageData"),
            });
        }

        return results;
    }

    public Application AddApplication(string name)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Applications (Name) VALUES (@name);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@name", name);
        var id = (long)command.ExecuteScalar()!;

        return new Application { Id = id, Name = name };
    }

    public void UpdateApplication(Application application)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = """
            UPDATE Applications
            SET Name = @name, ImageData = @imageData, ImageDataRgb565 = @imageRgb565, BackgroundColorArgb = @bg, SourceImageData = @source
            WHERE Id = @id;
            """;
        command.Parameters.AddWithValue("@name", application.Name);
        command.Parameters.AddWithValue("@imageData", ToDb(application.ImageData));
        command.Parameters.AddWithValue("@imageRgb565", ToDb(application.ImageDataRgb565));
        command.Parameters.AddWithValue("@bg", ToDb(application.BackgroundColorArgb));
        command.Parameters.AddWithValue("@source", ToDb(application.SourceImageData));
        command.Parameters.AddWithValue("@id", application.Id);
        command.ExecuteNonQuery();
    }

    /// <summary>Cascades to the application's KeyGroups and their GeroImperiumKeys. KeyActions are left in
    /// place -- they may be shared/reused by other keys, see UpsertAction.</summary>
    public void DeleteApplication(long id)
    {
        using var transaction = Connection.BeginTransaction();

        using (var deleteKeys = Connection.CreateCommand())
        {
            deleteKeys.Transaction = transaction;
            deleteKeys.CommandText = "DELETE FROM GeroImperiumKeys WHERE KeyGroupId IN (SELECT Id FROM KeyGroups WHERE ApplicationId = @appId);";
            deleteKeys.Parameters.AddWithValue("@appId", id);
            deleteKeys.ExecuteNonQuery();
        }

        using (var deleteGroups = Connection.CreateCommand())
        {
            deleteGroups.Transaction = transaction;
            deleteGroups.CommandText = "DELETE FROM KeyGroups WHERE ApplicationId = @appId;";
            deleteGroups.Parameters.AddWithValue("@appId", id);
            deleteGroups.ExecuteNonQuery();
        }

        using (var deleteApp = Connection.CreateCommand())
        {
            deleteApp.Transaction = transaction;
            deleteApp.CommandText = "DELETE FROM Applications WHERE Id = @id;";
            deleteApp.Parameters.AddWithValue("@id", id);
            deleteApp.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    // ----- Key groups -----

    public List<KeyGroup> GetKeyGroups(long applicationId)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT Id, ApplicationId, Name FROM KeyGroups WHERE ApplicationId = @appId ORDER BY Id;";
        command.Parameters.AddWithValue("@appId", applicationId);
        using var reader = command.ExecuteReader();

        var results = new List<KeyGroup>();
        while (reader.Read())
        {
            results.Add(new KeyGroup
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                ApplicationId = reader.GetInt64(reader.GetOrdinal("ApplicationId")),
                Name = reader.GetString(reader.GetOrdinal("Name")),
            });
        }

        return results;
    }

    /// <summary>Also creates the group's 6 fixed key slots (positions 0-5, matching GPA0-GPA5).</summary>
    public KeyGroup AddKeyGroup(long applicationId, string name)
    {
        using var transaction = Connection.BeginTransaction();

        long groupId;
        using (var insertGroup = Connection.CreateCommand())
        {
            insertGroup.Transaction = transaction;
            insertGroup.CommandText = """
                INSERT INTO KeyGroups (ApplicationId, Name) VALUES (@appId, @name);
                SELECT last_insert_rowid();
                """;
            insertGroup.Parameters.AddWithValue("@appId", applicationId);
            insertGroup.Parameters.AddWithValue("@name", name);
            groupId = (long)insertGroup.ExecuteScalar()!;
        }

        for (byte position = 0; position < 6; position++)
        {
            using var insertKey = Connection.CreateCommand();
            insertKey.Transaction = transaction;
            insertKey.CommandText = "INSERT INTO GeroImperiumKeys (KeyGroupId, Position) VALUES (@groupId, @position);";
            insertKey.Parameters.AddWithValue("@groupId", groupId);
            insertKey.Parameters.AddWithValue("@position", position);
            insertKey.ExecuteNonQuery();
        }

        transaction.Commit();

        return new KeyGroup { Id = groupId, ApplicationId = applicationId, Name = name };
    }

    public void UpdateKeyGroup(KeyGroup group)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "UPDATE KeyGroups SET Name = @name WHERE Id = @id;";
        command.Parameters.AddWithValue("@name", group.Name);
        command.Parameters.AddWithValue("@id", group.Id);
        command.ExecuteNonQuery();
    }

    /// <summary>Cascades to the group's GeroImperiumKeys. KeyActions are left in place.</summary>
    public void DeleteKeyGroup(long id)
    {
        using var transaction = Connection.BeginTransaction();

        using (var deleteKeys = Connection.CreateCommand())
        {
            deleteKeys.Transaction = transaction;
            deleteKeys.CommandText = "DELETE FROM GeroImperiumKeys WHERE KeyGroupId = @id;";
            deleteKeys.Parameters.AddWithValue("@id", id);
            deleteKeys.ExecuteNonQuery();
        }

        using (var deleteGroup = Connection.CreateCommand())
        {
            deleteGroup.Transaction = transaction;
            deleteGroup.CommandText = "DELETE FROM KeyGroups WHERE Id = @id;";
            deleteGroup.Parameters.AddWithValue("@id", id);
            deleteGroup.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    // ----- Keys -----

    /// <summary>Always returns exactly the 6 slots AddKeyGroup created, ordered by Position (0-5).</summary>
    public List<GeroImperiumKey> GetKeys(long keyGroupId)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT Id, KeyGroupId, Position, ImageData, ImageDataRgb565, KeyActionId, BackgroundColorArgb, SourceImageData FROM GeroImperiumKeys WHERE KeyGroupId = @groupId ORDER BY Position;";
        command.Parameters.AddWithValue("@groupId", keyGroupId);
        using var reader = command.ExecuteReader();

        var results = new List<GeroImperiumKey>();
        while (reader.Read())
        {
            results.Add(new GeroImperiumKey
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                KeyGroupId = reader.GetInt64(reader.GetOrdinal("KeyGroupId")),
                Position = reader.GetInt32(reader.GetOrdinal("Position")),
                ImageData = ReadNullableBlob(reader, "ImageData"),
                ImageDataRgb565 = ReadNullableBlob(reader, "ImageDataRgb565"),
                KeyActionId = ReadNullableLong(reader, "KeyActionId"),
                BackgroundColorArgb = ReadNullableInt(reader, "BackgroundColorArgb"),
                SourceImageData = ReadNullableBlob(reader, "SourceImageData"),
            });
        }

        return results;
    }

    public void UpdateKey(GeroImperiumKey key)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = """
            UPDATE GeroImperiumKeys
            SET ImageData = @imageData, ImageDataRgb565 = @imageRgb565, KeyActionId = @keyActionId, BackgroundColorArgb = @bg, SourceImageData = @source
            WHERE Id = @id;
            """;
        command.Parameters.AddWithValue("@imageData", ToDb(key.ImageData));
        command.Parameters.AddWithValue("@imageRgb565", ToDb(key.ImageDataRgb565));
        command.Parameters.AddWithValue("@keyActionId", ToDb(key.KeyActionId));
        command.Parameters.AddWithValue("@bg", ToDb(key.BackgroundColorArgb));
        command.Parameters.AddWithValue("@source", ToDb(key.SourceImageData));
        command.Parameters.AddWithValue("@id", key.Id);
        command.ExecuteNonQuery();
    }

    // ----- Key actions -----

    public KeyAction? GetKeyAction(long id)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT Id, ShortcutKey, CtrlModifier, AltModifier, ShiftModifier, WinModifier, ActionType, LaunchPath, ScriptId FROM KeyActions WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();

        return reader.Read() ? ReadKeyAction(reader) : null;
    }

    /// <summary>
    /// Reuses an existing KeyActions row matching every field rather than inserting a duplicate -- mirrors
    /// the device's own dedup behavior for 'T' (see pc_app_integration.md) so the authoring DB doesn't
    /// accumulate near-identical rows across edits.
    /// </summary>
    public KeyAction UpsertAction(ActionType actionType, string? shortcutKey, bool ctrl, bool shift, bool alt, bool win, string? launchPath, long? scriptId)
    {
        using (var find = Connection.CreateCommand())
        {
            find.CommandText = """
                SELECT Id FROM KeyActions
                WHERE ActionType = @actionType
                  AND ShortcutKey IS @shortcutKey
                  AND CtrlModifier = @ctrl
                  AND ShiftModifier = @shift
                  AND AltModifier = @alt
                  AND WinModifier = @win
                  AND LaunchPath IS @launchPath
                  AND ScriptId IS @scriptId
                LIMIT 1;
                """;
            AddActionParameters(find, actionType, shortcutKey, ctrl, shift, alt, win, launchPath, scriptId);

            if (find.ExecuteScalar() is long existingId)
            {
                return GetKeyAction(existingId)!;
            }
        }

        using var insert = Connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO KeyActions (ShortcutKey, CtrlModifier, AltModifier, ShiftModifier, WinModifier, ActionType, LaunchPath, ScriptId)
            VALUES (@shortcutKey, @ctrl, @alt, @shift, @win, @actionType, @launchPath, @scriptId);
            SELECT last_insert_rowid();
            """;
        AddActionParameters(insert, actionType, shortcutKey, ctrl, shift, alt, win, launchPath, scriptId);
        var id = (long)insert.ExecuteScalar()!;

        return new KeyAction
        {
            Id = id,
            ActionType = actionType,
            ShortcutKey = shortcutKey,
            CtrlModifier = ctrl,
            ShiftModifier = shift,
            AltModifier = alt,
            WinModifier = win,
            LaunchPath = launchPath,
            ScriptId = scriptId,
        };
    }

    private static void AddActionParameters(SqliteCommand command, ActionType actionType, string? shortcutKey, bool ctrl, bool shift, bool alt, bool win, string? launchPath, long? scriptId)
    {
        command.Parameters.AddWithValue("@actionType", (int)actionType);
        command.Parameters.AddWithValue("@shortcutKey", ToDb(shortcutKey));
        command.Parameters.AddWithValue("@ctrl", ctrl);
        command.Parameters.AddWithValue("@shift", shift);
        command.Parameters.AddWithValue("@alt", alt);
        command.Parameters.AddWithValue("@win", win);
        command.Parameters.AddWithValue("@launchPath", ToDb(launchPath));
        command.Parameters.AddWithValue("@scriptId", ToDb(scriptId));
    }

    private static KeyAction ReadKeyAction(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("Id")),
        ShortcutKey = reader.IsDBNull(reader.GetOrdinal("ShortcutKey")) ? null : reader.GetString(reader.GetOrdinal("ShortcutKey")),
        CtrlModifier = reader.GetBoolean(reader.GetOrdinal("CtrlModifier")),
        AltModifier = reader.GetBoolean(reader.GetOrdinal("AltModifier")),
        ShiftModifier = reader.GetBoolean(reader.GetOrdinal("ShiftModifier")),
        WinModifier = reader.GetBoolean(reader.GetOrdinal("WinModifier")),
        ActionType = (ActionType)reader.GetInt32(reader.GetOrdinal("ActionType")),
        LaunchPath = reader.IsDBNull(reader.GetOrdinal("LaunchPath")) ? null : reader.GetString(reader.GetOrdinal("LaunchPath")),
        ScriptId = ReadNullableLong(reader, "ScriptId"),
    };

    private static byte[]? ReadNullableBlob(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : (byte[])reader[ordinal];
    }

    private static int? ReadNullableInt(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static long? ReadNullableLong(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }
}
