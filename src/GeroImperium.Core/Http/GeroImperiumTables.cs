namespace GeroImperium.Core.Http;

/// <summary>REST path segments for the device's 5 generic CRUD tables (doc/windows_app_api_guide.md --
/// "One data-driven engine backs all 5 tables"). Case-insensitive on the wire, but use these exact constants
/// so a typo doesn't silently 404.</summary>
public static class GeroImperiumTables
{
    public const string ApplicationPages = "applicationpages";
    public const string Applications = "applications";
    public const string KeyGroups = "keygroups";
    public const string KeyActions = "keyactions";
    public const string Keys = "keys";
}
