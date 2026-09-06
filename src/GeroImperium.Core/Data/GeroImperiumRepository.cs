using Microsoft.Data.Sqlite;
using GeroImperium.Core.Models;

namespace GeroImperium.Core.Data;

/// <summary>
/// CRUD access to the authoring DB for the App's editor pages. Kept as plain ADO.NET over
/// GeroImperiumDatabase's connection rather than an ORM -- the schema is small and fixed (Schema.cs).
/// AddApplication(name)/AddKeyGroup still assign an auto-created default page and a trailing "Order" for
/// callers that don't care (existing tests, scripted paths); the page-editing UI (doc/plan2.md phase 12.6)
/// uses the explicit-page AddApplication(name, applicationPageId) overload instead.
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

    // ----- Application pages -----

    public List<ApplicationPage> GetApplicationPages()
    {
        using var command = Connection.CreateCommand();
        command.CommandText = """SELECT Id, "Order", Name, RemoteId FROM ApplicationPages ORDER BY CAST("Order" AS INTEGER), Id;""";
        using var reader = command.ExecuteReader();

        var results = new List<ApplicationPage>();
        while (reader.Read())
        {
            results.Add(ReadApplicationPage(reader));
        }

        return results;
    }

    public void UpdateApplicationPage(ApplicationPage page)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = """UPDATE ApplicationPages SET "Order" = @order, Name = @name, RemoteId = @remoteId WHERE Id = @id;""";
        command.Parameters.AddWithValue("@order", page.Order);
        command.Parameters.AddWithValue("@name", page.Name);
        command.Parameters.AddWithValue("@remoteId", ToDb(page.RemoteId));
        command.Parameters.AddWithValue("@id", page.Id);
        command.ExecuteNonQuery();
    }

    public ApplicationPage AddApplicationPage(string order, string name)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ApplicationPages ("Order", Name) VALUES (@order, @name);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@order", order);
        command.Parameters.AddWithValue("@name", name);
        var id = (long)command.ExecuteScalar()!;

        return new ApplicationPage { Id = id, Order = order, Name = name };
    }

    /// <summary>Convenience overload for the page-editing UI -- appends after every existing page rather than
    /// requiring the caller to compute the next "Order".</summary>
    public ApplicationPage AddApplicationPage(string name)
    {
        var order = (GetApplicationPages().Count + 1).ToString();
        return AddApplicationPage(order, name);
    }

    /// <summary>Cascades to every Application on this page (which itself cascades to KeyGroups/GeroImperiumKeys,
    /// see DeleteApplication) before deleting the page row. Each DeleteApplication call is its own transaction
    /// (it opens one internally) rather than one big transaction wrapping all of them -- SQLite/
    /// Microsoft.Data.Sqlite doesn't support nesting BeginTransaction calls on the same connection.</summary>
    public void DeleteApplicationPage(long id)
    {
        var applicationIds = new List<long>();
        using (var selectApps = Connection.CreateCommand())
        {
            selectApps.CommandText = "SELECT Id FROM Applications WHERE ApplicationPageId = @pageId;";
            selectApps.Parameters.AddWithValue("@pageId", id);
            using var reader = selectApps.ExecuteReader();
            while (reader.Read())
            {
                applicationIds.Add(reader.GetInt64(0));
            }
        }

        foreach (var applicationId in applicationIds)
        {
            DeleteApplication(applicationId);
        }

        using var deletePage = Connection.CreateCommand();
        deletePage.CommandText = "DELETE FROM ApplicationPages WHERE Id = @id;";
        deletePage.Parameters.AddWithValue("@id", id);
        deletePage.ExecuteNonQuery();
    }

    /// <summary>Returns the first ApplicationPage, creating one ("1", "Page 1") if none exist yet. Used by
    /// AddApplication until a real page-management UI exists (doc/plan2.md phase 12.6).</summary>
    private ApplicationPage EnsureDefaultApplicationPage()
    {
        var pages = GetApplicationPages();
        return pages.Count > 0 ? pages[0] : AddApplicationPage("1", "Page 1");
    }

    private static ApplicationPage ReadApplicationPage(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("Id")),
        Order = reader.GetString(reader.GetOrdinal("Order")),
        Name = reader.GetString(reader.GetOrdinal("Name")),
        RemoteId = ReadNullableLong(reader, "RemoteId"),
    };

    // ----- Applications -----

    public List<Application> GetApplications()
    {
        using var command = Connection.CreateCommand();
        command.CommandText = """SELECT Id, ApplicationPageId, "Order", Name, ImageData, ImageDataRgb565, ImageChangedAtUtc, BackgroundColorArgb, SourceImageData, RemoteId FROM Applications ORDER BY CAST("Order" AS INTEGER), Id;""";
        using var reader = command.ExecuteReader();

        var results = new List<Application>();
        while (reader.Read())
        {
            results.Add(new Application
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                ApplicationPageId = reader.GetInt64(reader.GetOrdinal("ApplicationPageId")),
                Order = reader.GetString(reader.GetOrdinal("Order")),
                Name = reader.GetString(reader.GetOrdinal("Name")),
                ImageData = ReadNullableBlob(reader, "ImageData"),
                ImageDataRgb565 = ReadNullableBlob(reader, "ImageDataRgb565"),
                ImageChangedAtUtc = ReadNullableDateTimeUtc(reader, "ImageChangedAtUtc"),
                BackgroundColorArgb = ReadNullableInt(reader, "BackgroundColorArgb"),
                SourceImageData = ReadNullableBlob(reader, "SourceImageData"),
                RemoteId = ReadNullableLong(reader, "RemoteId"),
            });
        }

        return results;
    }

    /// <summary>Assigns the auto-created default page -- kept for callers that don't care about pages
    /// (existing tests, and any future scripted/bulk-import path). The page-editing UI uses the explicit-page
    /// overload below instead.</summary>
    public Application AddApplication(string name) => AddApplication(name, EnsureDefaultApplicationPage().Id);

    public Application AddApplication(string name, long applicationPageId)
    {
        using var countCommand = Connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM Applications WHERE ApplicationPageId = @pageId;";
        countCommand.Parameters.AddWithValue("@pageId", applicationPageId);
        var order = ((long)countCommand.ExecuteScalar()! + 1).ToString();

        using var command = Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Applications (ApplicationPageId, "Order", Name) VALUES (@pageId, @order, @name);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@pageId", applicationPageId);
        command.Parameters.AddWithValue("@order", order);
        command.Parameters.AddWithValue("@name", name);
        var id = (long)command.ExecuteScalar()!;

        return new Application { Id = id, ApplicationPageId = applicationPageId, Order = order, Name = name };
    }

    public void UpdateApplication(Application application)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = """
            UPDATE Applications
            SET ApplicationPageId = @pageId, "Order" = @order, Name = @name, ImageData = @imageData,
                ImageDataRgb565 = @imageRgb565, ImageChangedAtUtc = @imageChangedAt, BackgroundColorArgb = @bg,
                SourceImageData = @source, RemoteId = @remoteId
            WHERE Id = @id;
            """;
        command.Parameters.AddWithValue("@pageId", application.ApplicationPageId);
        command.Parameters.AddWithValue("@order", application.Order);
        command.Parameters.AddWithValue("@name", application.Name);
        command.Parameters.AddWithValue("@imageData", ToDb(application.ImageData));
        command.Parameters.AddWithValue("@imageRgb565", ToDb(application.ImageDataRgb565));
        command.Parameters.AddWithValue("@imageChangedAt", ToDb(DateTimeToDb(application.ImageChangedAtUtc)));
        command.Parameters.AddWithValue("@bg", ToDb(application.BackgroundColorArgb));
        command.Parameters.AddWithValue("@source", ToDb(application.SourceImageData));
        command.Parameters.AddWithValue("@remoteId", ToDb(application.RemoteId));
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
        command.CommandText = """SELECT Id, ApplicationId, "Order", Name, RemoteId FROM KeyGroups WHERE ApplicationId = @appId ORDER BY CAST("Order" AS INTEGER), Id;""";
        command.Parameters.AddWithValue("@appId", applicationId);
        using var reader = command.ExecuteReader();

        var results = new List<KeyGroup>();
        while (reader.Read())
        {
            results.Add(new KeyGroup
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                ApplicationId = reader.GetInt64(reader.GetOrdinal("ApplicationId")),
                Order = reader.GetString(reader.GetOrdinal("Order")),
                Name = reader.GetString(reader.GetOrdinal("Name")),
                RemoteId = ReadNullableLong(reader, "RemoteId"),
            });
        }

        return results;
    }

    /// <summary>Also creates the group's 6 fixed key slots (positions 0-5, matching GPA0-GPA5).</summary>
    public KeyGroup AddKeyGroup(long applicationId, string name)
    {
        using var transaction = Connection.BeginTransaction();

        long groupId;
        string order;
        using (var countCommand = Connection.CreateCommand())
        {
            countCommand.Transaction = transaction;
            countCommand.CommandText = "SELECT COUNT(*) FROM KeyGroups WHERE ApplicationId = @appId;";
            countCommand.Parameters.AddWithValue("@appId", applicationId);
            order = ((long)countCommand.ExecuteScalar()! + 1).ToString();
        }

        using (var insertGroup = Connection.CreateCommand())
        {
            insertGroup.Transaction = transaction;
            insertGroup.CommandText = """
                INSERT INTO KeyGroups (ApplicationId, "Order", Name) VALUES (@appId, @order, @name);
                SELECT last_insert_rowid();
                """;
            insertGroup.Parameters.AddWithValue("@appId", applicationId);
            insertGroup.Parameters.AddWithValue("@order", order);
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

        return new KeyGroup { Id = groupId, ApplicationId = applicationId, Order = order, Name = name };
    }

    public void UpdateKeyGroup(KeyGroup group)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = """UPDATE KeyGroups SET "Order" = @order, Name = @name, RemoteId = @remoteId WHERE Id = @id;""";
        command.Parameters.AddWithValue("@order", group.Order);
        command.Parameters.AddWithValue("@name", group.Name);
        command.Parameters.AddWithValue("@remoteId", ToDb(group.RemoteId));
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
        command.CommandText = "SELECT Id, KeyGroupId, Position, ImageData, ImageDataRgb565, ImageChangedAtUtc, KeyActionId, BackgroundColorArgb, SourceImageData, RemoteId FROM GeroImperiumKeys WHERE KeyGroupId = @groupId ORDER BY Position;";
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
                ImageChangedAtUtc = ReadNullableDateTimeUtc(reader, "ImageChangedAtUtc"),
                KeyActionId = ReadNullableLong(reader, "KeyActionId"),
                BackgroundColorArgb = ReadNullableInt(reader, "BackgroundColorArgb"),
                SourceImageData = ReadNullableBlob(reader, "SourceImageData"),
                RemoteId = ReadNullableLong(reader, "RemoteId"),
            });
        }

        return results;
    }

    public void UpdateKey(GeroImperiumKey key)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = """
            UPDATE GeroImperiumKeys
            SET ImageData = @imageData, ImageDataRgb565 = @imageRgb565, ImageChangedAtUtc = @imageChangedAt,
                KeyActionId = @keyActionId, BackgroundColorArgb = @bg, SourceImageData = @source, RemoteId = @remoteId
            WHERE Id = @id;
            """;
        command.Parameters.AddWithValue("@imageData", ToDb(key.ImageData));
        command.Parameters.AddWithValue("@imageRgb565", ToDb(key.ImageDataRgb565));
        command.Parameters.AddWithValue("@imageChangedAt", ToDb(DateTimeToDb(key.ImageChangedAtUtc)));
        command.Parameters.AddWithValue("@keyActionId", ToDb(key.KeyActionId));
        command.Parameters.AddWithValue("@bg", ToDb(key.BackgroundColorArgb));
        command.Parameters.AddWithValue("@source", ToDb(key.SourceImageData));
        command.Parameters.AddWithValue("@remoteId", ToDb(key.RemoteId));
        command.Parameters.AddWithValue("@id", key.Id);
        command.ExecuteNonQuery();
    }

    // ----- Key actions -----

    public List<KeyAction> GetKeyActions()
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT Id, Type, TextContent, ActionType, LaunchPath, ScriptId, RemoteId FROM KeyActions ORDER BY Id;";
        using var reader = command.ExecuteReader();

        var results = new List<KeyAction>();
        while (reader.Read())
        {
            results.Add(ReadKeyAction(reader));
        }

        return results;
    }

    /// <summary>Only RemoteId is ever updated post-creation by the sync path -- Type/TextContent/ActionType/
    /// LaunchPath/ScriptId are set once at UpsertAction time and otherwise immutable (a changed shortcut
    /// dedups to a different, possibly-new row rather than mutating a shared one -- see UpsertAction).</summary>
    public void UpdateKeyActionRemoteId(KeyAction action)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "UPDATE KeyActions SET RemoteId = @remoteId WHERE Id = @id;";
        command.Parameters.AddWithValue("@remoteId", ToDb(action.RemoteId));
        command.Parameters.AddWithValue("@id", action.Id);
        command.ExecuteNonQuery();
    }

    public KeyAction? GetKeyAction(long id)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT Id, Type, TextContent, ActionType, LaunchPath, ScriptId, RemoteId FROM KeyActions WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();

        return reader.Read() ? ReadKeyAction(reader) : null;
    }

    /// <summary>
    /// Reuses an existing KeyActions row matching every field rather than inserting a duplicate -- mirrors
    /// the device's own dedup behavior for the old 'T' command (see pc_app_integration.md) so the authoring
    /// DB doesn't accumulate near-identical rows across edits.
    /// </summary>
    public KeyAction UpsertAction(ActionType actionType, HidActionKind type, string? textContent, string? launchPath, long? scriptId)
    {
        using (var find = Connection.CreateCommand())
        {
            find.CommandText = """
                SELECT Id FROM KeyActions
                WHERE ActionType = @actionType
                  AND Type = @type
                  AND TextContent IS @textContent
                  AND LaunchPath IS @launchPath
                  AND ScriptId IS @scriptId
                LIMIT 1;
                """;
            AddActionParameters(find, actionType, type, textContent, launchPath, scriptId);

            if (find.ExecuteScalar() is long existingId)
            {
                return GetKeyAction(existingId)!;
            }
        }

        using var insert = Connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO KeyActions (Type, TextContent, ActionType, LaunchPath, ScriptId)
            VALUES (@type, @textContent, @actionType, @launchPath, @scriptId);
            SELECT last_insert_rowid();
            """;
        AddActionParameters(insert, actionType, type, textContent, launchPath, scriptId);
        var id = (long)insert.ExecuteScalar()!;

        return new KeyAction
        {
            Id = id,
            Type = type,
            TextContent = textContent,
            ActionType = actionType,
            LaunchPath = launchPath,
            ScriptId = scriptId,
        };
    }

    private static void AddActionParameters(SqliteCommand command, ActionType actionType, HidActionKind type, string? textContent, string? launchPath, long? scriptId)
    {
        command.Parameters.AddWithValue("@actionType", (int)actionType);
        command.Parameters.AddWithValue("@type", (int)type);
        command.Parameters.AddWithValue("@textContent", ToDb(textContent));
        command.Parameters.AddWithValue("@launchPath", ToDb(launchPath));
        command.Parameters.AddWithValue("@scriptId", ToDb(scriptId));
    }

    private static KeyAction ReadKeyAction(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("Id")),
        Type = (HidActionKind)reader.GetInt32(reader.GetOrdinal("Type")),
        TextContent = reader.IsDBNull(reader.GetOrdinal("TextContent")) ? null : reader.GetString(reader.GetOrdinal("TextContent")),
        ActionType = (ActionType)reader.GetInt32(reader.GetOrdinal("ActionType")),
        LaunchPath = reader.IsDBNull(reader.GetOrdinal("LaunchPath")) ? null : reader.GetString(reader.GetOrdinal("LaunchPath")),
        ScriptId = ReadNullableLong(reader, "ScriptId"),
        RemoteId = ReadNullableLong(reader, "RemoteId"),
    };

    // ----- General settings -----

    /// <summary>Returns the single GeneralSettings row, creating it with defaults if this is a brand-new
    /// database (app-only settings, not one of the device's REST tables -- see GeneralSettings' doc comment).</summary>
    public GeneralSettings GetGeneralSettings()
    {
        using (var command = Connection.CreateCommand())
        {
            command.CommandText = "SELECT Id, Theme, DeviceIpAddress, LastKnownBleDeviceId FROM GeneralSettings ORDER BY Id LIMIT 1;";
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                return new GeneralSettings
                {
                    Id = reader.GetInt64(reader.GetOrdinal("Id")),
                    Theme = reader.GetString(reader.GetOrdinal("Theme")),
                    DeviceIpAddress = reader.IsDBNull(reader.GetOrdinal("DeviceIpAddress")) ? null : reader.GetString(reader.GetOrdinal("DeviceIpAddress")),
                    LastKnownBleDeviceId = reader.IsDBNull(reader.GetOrdinal("LastKnownBleDeviceId")) ? null : reader.GetString(reader.GetOrdinal("LastKnownBleDeviceId")),
                };
            }
        }

        using var insert = Connection.CreateCommand();
        insert.CommandText = "INSERT INTO GeneralSettings DEFAULT VALUES; SELECT last_insert_rowid();";
        var id = (long)insert.ExecuteScalar()!;
        return new GeneralSettings { Id = id };
    }

    public void UpdateGeneralSettings(GeneralSettings settings)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = """UPDATE GeneralSettings SET Theme = @theme, DeviceIpAddress = @ip, LastKnownBleDeviceId = @bleId WHERE Id = @id;""";
        command.Parameters.AddWithValue("@theme", settings.Theme);
        command.Parameters.AddWithValue("@ip", ToDb(settings.DeviceIpAddress));
        command.Parameters.AddWithValue("@bleId", ToDb(settings.LastKnownBleDeviceId));
        command.Parameters.AddWithValue("@id", settings.Id);
        command.ExecuteNonQuery();
    }

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

    /// <summary>ImageChangedAtUtc is stored as a round-trip ("o") ISO-8601 string -- see DateTimeToDb.</summary>
    private static DateTime? ReadNullableDateTimeUtc(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal)
            ? null
            : DateTime.Parse(reader.GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    private static string? DateTimeToDb(DateTime? value) => value?.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
}
