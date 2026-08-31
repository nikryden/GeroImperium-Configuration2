namespace GeroImperium.Core.Data;

/// <summary>
/// The 5 tables the device validates on boot (Applications, KeyGroups, GeroImperiumKeys, KeyActions,
/// GeneralSettings -- schema mismatch is error E02, see pc_app_integration.md), plus the app-only Scripts
/// table and app-only columns documented in pc_app_plan.md. Column names/types for device-read columns must
/// stay exactly as documented -- the bulk S/D/F path pushes this file as-is, no transform step.
/// Creation order respects foreign keys: Scripts and Applications first (no dependencies), then KeyActions
/// (references Scripts), KeyGroups (references Applications), GeroImperiumKeys (references KeyGroups and
/// KeyActions), then GeneralSettings.
/// </summary>
internal static class Schema
{
    public const string CreateTablesSql = """
        CREATE TABLE IF NOT EXISTS Applications (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL DEFAULT '',
            ImageData BLOB,
            ImageDataRgb565 BLOB,
            BackgroundColorArgb INTEGER
        );

        CREATE TABLE IF NOT EXISTS Scripts (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            Body TEXT NOT NULL DEFAULT '',
            Interpreter TEXT NOT NULL DEFAULT 'PowerShell'
        );

        CREATE TABLE IF NOT EXISTS KeyActions (
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

        CREATE TABLE IF NOT EXISTS KeyGroups (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ApplicationId INTEGER NOT NULL REFERENCES Applications(Id),
            Name TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS GeroImperiumKeys (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            KeyGroupId INTEGER NOT NULL REFERENCES KeyGroups(Id),
            Position INTEGER NOT NULL,
            ImageData BLOB,
            ImageDataRgb565 BLOB,
            KeyActionId INTEGER REFERENCES KeyActions(Id),
            BackgroundColorArgb INTEGER
        );

        CREATE TABLE IF NOT EXISTS GeneralSettings (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Theme TEXT NOT NULL DEFAULT 'Dark',
            DefaultDevicePort TEXT
        );
        """;
}
