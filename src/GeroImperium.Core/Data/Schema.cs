namespace GeroImperium.Core.Data;

/// <summary>
/// Local authoring cache schema. No longer schema-identical to a device DB file that gets pushed
/// byte-for-byte (that bulk S/D/F path is gone, see doc/plan2.md) -- instead this mirrors the shape of the
/// device's 5 REST tables (ApplicationPages, Applications, KeyGroups, KeyActions, Keys -- see
/// doc/windows_app_api_guide.md) closely enough that syncing is a straightforward per-row mapping, plus the
/// app-only Scripts table and app-only columns. Every synced table carries a nullable RemoteId (the row's Id
/// on the device) used by the diff-and-push sync model: null = never pushed (POST), set = pushed before
/// (PUT if changed). GeneralSettings is NOT one of the device's real REST tables (per the guide) -- it is
/// app-local-only (theme, cached device IP/BLE id).
/// "Order" is quoted throughout -- it is a reserved SQL word (see the guide's Gotchas).
/// Creation order respects foreign keys: ApplicationPages and Scripts first (no dependencies), then
/// Applications (references ApplicationPages), KeyActions (references Scripts), KeyGroups (references
/// Applications), Keys (references KeyGroups and KeyActions), then GeneralSettings.
/// Applications and GeroImperiumKeys (the two tables with image columns) carry ImageChangedAtUtc -- an
/// ISO-8601 UTC timestamp stamped whenever ImageData/ImageDataRgb565 is (re)written (see
/// ApplicationItemViewModel/KeySlotViewModel's ReconvertImage) -- and LastSyncedImageChangedAtUtc, the value
/// as of the last successful image push; Sync compares the two to skip re-uploading an unchanged 32768-byte
/// image. Every synced table also carries Dirty (default 1, so a brand-new row always pushes at least once);
/// Sync skips a PUT entirely when RemoteId is set and Dirty is 0. PendingDeletes holds tombstones for
/// already-pushed rows deleted locally (see Models.PendingDelete's doc comment) -- Sync issues their DELETE
/// first, before any create/update pass.
/// </summary>
internal static class Schema
{
    public const string CreateTablesSql = """
        CREATE TABLE IF NOT EXISTS ApplicationPages (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            "Order" TEXT NOT NULL DEFAULT '1',
            Name TEXT NOT NULL DEFAULT '',
            RemoteId INTEGER,
            Dirty INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS Scripts (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            Body TEXT NOT NULL DEFAULT '',
            Interpreter TEXT NOT NULL DEFAULT 'PowerShell'
        );

        CREATE TABLE IF NOT EXISTS Applications (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ApplicationPageId INTEGER NOT NULL REFERENCES ApplicationPages(Id),
            "Order" TEXT NOT NULL DEFAULT '1',
            Name TEXT NOT NULL DEFAULT '',
            ImageData BLOB,
            ImageDataRgb565 BLOB,
            ImageChangedAtUtc TEXT,
            BackgroundColorArgb INTEGER,
            SourceImageData BLOB,
            RemoteId INTEGER,
            Dirty INTEGER NOT NULL DEFAULT 1,
            LastSyncedImageChangedAtUtc TEXT
        );

        CREATE TABLE IF NOT EXISTS KeyActions (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Type INTEGER NOT NULL DEFAULT 0,
            TextContent TEXT,
            ActionType INTEGER NOT NULL DEFAULT 0,
            LaunchPath TEXT,
            ScriptId INTEGER REFERENCES Scripts(Id),
            RemoteId INTEGER,
            Dirty INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS KeyGroups (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ApplicationId INTEGER NOT NULL REFERENCES Applications(Id),
            "Order" TEXT NOT NULL DEFAULT '1',
            Name TEXT NOT NULL DEFAULT '',
            RemoteId INTEGER,
            Dirty INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS GeroImperiumKeys (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            KeyGroupId INTEGER NOT NULL REFERENCES KeyGroups(Id),
            Position INTEGER NOT NULL,
            ImageData BLOB,
            ImageDataRgb565 BLOB,
            ImageChangedAtUtc TEXT,
            KeyActionId INTEGER REFERENCES KeyActions(Id),
            BackgroundColorArgb INTEGER,
            SourceImageData BLOB,
            RemoteId INTEGER,
            Dirty INTEGER NOT NULL DEFAULT 1,
            LastSyncedImageChangedAtUtc TEXT
        );

        CREATE TABLE IF NOT EXISTS PendingDeletes (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            TableName TEXT NOT NULL,
            RemoteId INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS GeneralSettings (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Theme TEXT NOT NULL DEFAULT 'Dark',
            DeviceIpAddress TEXT,
            LastKnownBleDeviceId TEXT
        );
        """;
}
