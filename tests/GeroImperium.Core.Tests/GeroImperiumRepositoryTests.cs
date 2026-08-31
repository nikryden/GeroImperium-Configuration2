using GeroImperium.Core.Data;
using GeroImperium.Core.Models;

namespace GeroImperium.Core.Tests;

public class GeroImperiumRepositoryTests : IDisposable
{
    private readonly GeroImperiumDatabase _database;
    private readonly GeroImperiumRepository _repository;

    public GeroImperiumRepositoryTests()
    {
        _database = new GeroImperiumDatabase(":memory:");
        _database.EnsureSchemaCreated();
        _repository = new GeroImperiumRepository(_database);
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public void AddApplication_ThenGetApplications_ReturnsIt()
    {
        var added = _repository.AddApplication("Discord");

        var apps = _repository.GetApplications();

        var app = Assert.Single(apps);
        Assert.Equal(added.Id, app.Id);
        Assert.Equal("Discord", app.Name);
        Assert.Null(app.ImageData);
        Assert.Null(app.BackgroundColorArgb);
    }

    [Fact]
    public void UpdateApplication_PersistsChanges()
    {
        var app = _repository.AddApplication("Original");
        app.Name = "Renamed";
        app.ImageData = [1, 2, 3];
        app.BackgroundColorArgb = unchecked((int)0xFF112233);

        _repository.UpdateApplication(app);

        var reloaded = Assert.Single(_repository.GetApplications());
        Assert.Equal("Renamed", reloaded.Name);
        Assert.Equal(new byte[] { 1, 2, 3 }, reloaded.ImageData);
        Assert.Equal(unchecked((int)0xFF112233), reloaded.BackgroundColorArgb);
    }

    [Fact]
    public void DeleteApplication_CascadesKeyGroupsAndKeys()
    {
        var app = _repository.AddApplication("ToDelete");
        var group = _repository.AddKeyGroup(app.Id, "Group 1");

        _repository.DeleteApplication(app.Id);

        Assert.Empty(_repository.GetApplications());
        Assert.Empty(_repository.GetKeyGroups(app.Id));
        Assert.Empty(_repository.GetKeys(group.Id));
    }

    [Fact]
    public void AddKeyGroup_CreatesSixKeySlotsAtPositions0To5()
    {
        var app = _repository.AddApplication("App");

        var group = _repository.AddKeyGroup(app.Id, "Group 1");
        var keys = _repository.GetKeys(group.Id);

        Assert.Equal(6, keys.Count);
        Assert.Equal([0, 1, 2, 3, 4, 5], keys.Select(k => k.Position));
        Assert.All(keys, k => Assert.Equal(group.Id, k.KeyGroupId));
    }

    [Fact]
    public void DeleteKeyGroup_RemovesItsKeys()
    {
        var app = _repository.AddApplication("App");
        var group = _repository.AddKeyGroup(app.Id, "Group 1");

        _repository.DeleteKeyGroup(group.Id);

        Assert.Empty(_repository.GetKeyGroups(app.Id));
        Assert.Empty(_repository.GetKeys(group.Id));
    }

    [Fact]
    public void UpdateKey_PersistsImageColorAndAction()
    {
        var app = _repository.AddApplication("App");
        var group = _repository.AddKeyGroup(app.Id, "Group 1");
        var key = _repository.GetKeys(group.Id)[0];
        var action = _repository.UpsertAction(ActionType.Shortcut, "F5", ctrl: true, shift: false, alt: false, win: false, launchPath: null, scriptId: null);

        key.ImageDataRgb565 = new byte[32768];
        key.BackgroundColorArgb = unchecked((int)0xFF00FF00);
        key.KeyActionId = action.Id;
        _repository.UpdateKey(key);

        var reloaded = _repository.GetKeys(group.Id)[0];
        Assert.Equal(32768, reloaded.ImageDataRgb565!.Length);
        Assert.Equal(unchecked((int)0xFF00FF00), reloaded.BackgroundColorArgb);
        Assert.Equal(action.Id, reloaded.KeyActionId);
    }

    [Fact]
    public void UpsertAction_ReusesExistingMatchingRow()
    {
        var first = _repository.UpsertAction(ActionType.Shortcut, "A", ctrl: true, shift: false, alt: false, win: false, launchPath: null, scriptId: null);
        var second = _repository.UpsertAction(ActionType.Shortcut, "A", ctrl: true, shift: false, alt: false, win: false, launchPath: null, scriptId: null);

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public void UpsertAction_DifferentModifiers_CreatesDistinctRows()
    {
        var first = _repository.UpsertAction(ActionType.Shortcut, "A", ctrl: true, shift: false, alt: false, win: false, launchPath: null, scriptId: null);
        var second = _repository.UpsertAction(ActionType.Shortcut, "A", ctrl: false, shift: true, alt: false, win: false, launchPath: null, scriptId: null);

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void UpsertAction_LaunchAppType_DedupesByPath()
    {
        var first = _repository.UpsertAction(ActionType.LaunchApp, null, ctrl: false, shift: false, alt: false, win: false, launchPath: @"C:\app.exe", scriptId: null);
        var second = _repository.UpsertAction(ActionType.LaunchApp, null, ctrl: false, shift: false, alt: false, win: false, launchPath: @"C:\app.exe", scriptId: null);
        var third = _repository.UpsertAction(ActionType.LaunchApp, null, ctrl: false, shift: false, alt: false, win: false, launchPath: @"C:\other.exe", scriptId: null);

        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Id, third.Id);
    }

    [Fact]
    public void GetKeyAction_RoundTripsAllFields()
    {
        var action = _repository.UpsertAction(ActionType.Shortcut, "ENTER", ctrl: true, shift: true, alt: false, win: false, launchPath: null, scriptId: null);

        var reloaded = _repository.GetKeyAction(action.Id);

        Assert.NotNull(reloaded);
        Assert.Equal("ENTER", reloaded!.ShortcutKey);
        Assert.True(reloaded.CtrlModifier);
        Assert.True(reloaded.ShiftModifier);
        Assert.False(reloaded.AltModifier);
        Assert.False(reloaded.WinModifier);
        Assert.Equal(ActionType.Shortcut, reloaded.ActionType);
    }
}
