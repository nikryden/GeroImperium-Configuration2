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
        Assert.Null(app.RemoteId);
        Assert.True(app.ApplicationPageId > 0);
        Assert.Equal("1", app.Order);
    }

    [Fact]
    public void AddApplication_ReusesTheSameDefaultPage_AndIncrementsOrder()
    {
        var first = _repository.AddApplication("First");
        var second = _repository.AddApplication("Second");

        Assert.Equal(first.ApplicationPageId, second.ApplicationPageId);
        Assert.Equal("1", first.Order);
        Assert.Equal("2", second.Order);
    }

    [Fact]
    public void UpdateApplication_PersistsChanges()
    {
        var app = _repository.AddApplication("Original");
        app.Name = "Renamed";
        app.ImageData = [1, 2, 3];
        app.BackgroundColorArgb = unchecked((int)0xFF112233);
        app.RemoteId = 42;

        _repository.UpdateApplication(app);

        var reloaded = Assert.Single(_repository.GetApplications());
        Assert.Equal("Renamed", reloaded.Name);
        Assert.Equal(new byte[] { 1, 2, 3 }, reloaded.ImageData);
        Assert.Equal(unchecked((int)0xFF112233), reloaded.BackgroundColorArgb);
        Assert.Equal(42, reloaded.RemoteId);
    }

    [Fact]
    public void UpdateApplication_RoundTripsImageChangedAtUtc()
    {
        var app = _repository.AddApplication("App");
        var stamp = new DateTime(2026, 9, 6, 12, 30, 0, DateTimeKind.Utc);
        app.ImageChangedAtUtc = stamp;

        _repository.UpdateApplication(app);

        var reloaded = Assert.Single(_repository.GetApplications());
        Assert.Equal(stamp, reloaded.ImageChangedAtUtc);
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
        Assert.Equal("1", group.Order);
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
        var action = _repository.UpsertAction(ActionType.Shortcut, HidActionKind.Hid, "[ctrl]+F5", launchPath: null, scriptId: null);

        key.ImageDataRgb565 = new byte[32768];
        key.BackgroundColorArgb = unchecked((int)0xFF00FF00);
        key.KeyActionId = action.Id;
        key.RemoteId = 7;
        _repository.UpdateKey(key);

        var reloaded = _repository.GetKeys(group.Id)[0];
        Assert.Equal(32768, reloaded.ImageDataRgb565!.Length);
        Assert.Equal(unchecked((int)0xFF00FF00), reloaded.BackgroundColorArgb);
        Assert.Equal(action.Id, reloaded.KeyActionId);
        Assert.Equal(7, reloaded.RemoteId);
    }

    [Fact]
    public void UpdateKey_RoundTripsImageChangedAtUtc()
    {
        var app = _repository.AddApplication("App");
        var group = _repository.AddKeyGroup(app.Id, "Group 1");
        var key = _repository.GetKeys(group.Id)[0];
        var stamp = new DateTime(2026, 9, 6, 12, 30, 0, DateTimeKind.Utc);
        key.ImageChangedAtUtc = stamp;

        _repository.UpdateKey(key);

        var reloaded = _repository.GetKeys(group.Id)[0];
        Assert.Equal(stamp, reloaded.ImageChangedAtUtc);
    }

    [Fact]
    public void UpsertAction_ReusesExistingMatchingRow()
    {
        var first = _repository.UpsertAction(ActionType.Shortcut, HidActionKind.Hid, "[ctrl]+a", launchPath: null, scriptId: null);
        var second = _repository.UpsertAction(ActionType.Shortcut, HidActionKind.Hid, "[ctrl]+a", launchPath: null, scriptId: null);

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public void UpsertAction_DifferentTextContent_CreatesDistinctRows()
    {
        var first = _repository.UpsertAction(ActionType.Shortcut, HidActionKind.Hid, "[ctrl]+a", launchPath: null, scriptId: null);
        var second = _repository.UpsertAction(ActionType.Shortcut, HidActionKind.Hid, "[shift]+a", launchPath: null, scriptId: null);

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void UpsertAction_LaunchAppType_DedupesByPath()
    {
        var first = _repository.UpsertAction(ActionType.LaunchApp, HidActionKind.Hid, null, launchPath: @"C:\app.exe", scriptId: null);
        var second = _repository.UpsertAction(ActionType.LaunchApp, HidActionKind.Hid, null, launchPath: @"C:\app.exe", scriptId: null);
        var third = _repository.UpsertAction(ActionType.LaunchApp, HidActionKind.Hid, null, launchPath: @"C:\other.exe", scriptId: null);

        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Id, third.Id);
    }

    [Fact]
    public void GetKeyAction_RoundTripsAllFields()
    {
        var action = _repository.UpsertAction(ActionType.Shortcut, HidActionKind.Hid, "[ctrl]+[shift]+ENTER", launchPath: null, scriptId: null);

        var reloaded = _repository.GetKeyAction(action.Id);

        Assert.NotNull(reloaded);
        Assert.Equal("[ctrl]+[shift]+ENTER", reloaded!.TextContent);
        Assert.Equal(HidActionKind.Hid, reloaded.Type);
        Assert.Equal(ActionType.Shortcut, reloaded.ActionType);
    }

    [Fact]
    public void GetApplicationPages_ReturnsAddedPage()
    {
        var page = _repository.AddApplicationPage("1", "Main");

        var pages = _repository.GetApplicationPages();

        var reloaded = Assert.Single(pages);
        Assert.Equal(page.Id, reloaded.Id);
        Assert.Equal("Main", reloaded.Name);
        Assert.Equal("1", reloaded.Order);
    }

    [Fact]
    public void GetApplicationPages_OrdersByOrder_NotCreationOrder()
    {
        var second = _repository.AddApplicationPage("2", "Second");
        var first = _repository.AddApplicationPage("1", "First");

        var pages = _repository.GetApplicationPages();

        Assert.Equal([first.Id, second.Id], pages.Select(p => p.Id));
    }

    [Fact]
    public void AddApplicationPage_NameOnly_AppendsAfterExistingPages()
    {
        _repository.AddApplicationPage("1", "First");

        var second = _repository.AddApplicationPage("Second");

        Assert.Equal("2", second.Order);
    }

    [Fact]
    public void AddApplication_WithExplicitPage_UsesThatPageAndOwnOrderSequence()
    {
        var pageA = _repository.AddApplicationPage("1", "A");
        var pageB = _repository.AddApplicationPage("2", "B");
        _repository.AddApplication("A1", pageA.Id);

        var b1 = _repository.AddApplication("B1", pageB.Id);

        Assert.Equal(pageB.Id, b1.ApplicationPageId);
        Assert.Equal("1", b1.Order); // independent per-page order sequence, not a global counter
    }

    [Fact]
    public void GetApplications_OrdersByOrder_NotCreationOrder()
    {
        var page = _repository.AddApplicationPage("1", "Page");
        var second = _repository.AddApplication("Second", page.Id);
        second.Order = "1";
        _repository.UpdateApplication(second);
        var first = _repository.AddApplication("First", page.Id);
        first.Order = "0";
        _repository.UpdateApplication(first);

        var apps = _repository.GetApplications();

        Assert.Equal([first.Id, second.Id], apps.Select(a => a.Id));
    }

    [Fact]
    public void DeleteApplicationPage_CascadesApplicationsGroupsAndKeys()
    {
        var page = _repository.AddApplicationPage("1", "ToDelete");
        var app = _repository.AddApplication("App", page.Id);
        var group = _repository.AddKeyGroup(app.Id, "Group 1");

        _repository.DeleteApplicationPage(page.Id);

        Assert.Empty(_repository.GetApplicationPages());
        Assert.Empty(_repository.GetApplications());
        Assert.Empty(_repository.GetKeyGroups(app.Id));
        Assert.Empty(_repository.GetKeys(group.Id));
    }

    // ----- Dirty tracking (doc/plan2.md's "skip if unchanged") -----

    [Fact]
    public void NewRows_DefaultToDirtyTrue()
    {
        var page = _repository.AddApplicationPage("1", "Page");
        var app = _repository.AddApplication("App", page.Id);
        var group = _repository.AddKeyGroup(app.Id, "Group");
        var action = _repository.UpsertAction(ActionType.Shortcut, HidActionKind.Hid, "a", null, null);

        Assert.True(_repository.GetApplicationPages().Single().Dirty);
        Assert.True(_repository.GetApplications().Single().Dirty);
        Assert.True(_repository.GetKeyGroups(app.Id).Single().Dirty);
        Assert.True(_repository.GetKeys(group.Id)[0].Dirty);
        Assert.True(_repository.GetKeyAction(action.Id)!.Dirty);
    }

    [Fact]
    public void MarkSynced_ClearsDirty_AndUpdateEditPathSetsItAgain()
    {
        var page = _repository.AddApplicationPage("1", "Page");
        var app = _repository.AddApplication("App", page.Id);
        var group = _repository.AddKeyGroup(app.Id, "Group");
        var key = _repository.GetKeys(group.Id)[0];
        var action = _repository.UpsertAction(ActionType.Shortcut, HidActionKind.Hid, "a", null, null);

        _repository.MarkApplicationPageSynced(page.Id, remoteId: 1);
        _repository.MarkApplicationSynced(app.Id, remoteId: 2, lastSyncedImageChangedAtUtc: null);
        _repository.MarkKeyGroupSynced(group.Id, remoteId: 3);
        _repository.MarkKeySynced(key.Id, remoteId: 4, lastSyncedImageChangedAtUtc: null);
        _repository.MarkKeyActionSynced(action.Id, remoteId: 5);

        Assert.False(_repository.GetApplicationPages().Single().Dirty);
        Assert.False(_repository.GetApplications().Single().Dirty);
        Assert.False(_repository.GetKeyGroups(app.Id).Single().Dirty);
        Assert.False(_repository.GetKeys(group.Id)[0].Dirty);
        Assert.False(_repository.GetKeyAction(action.Id)!.Dirty);

        // Editing again (not the sync-bookkeeping Mark*Synced path) must re-dirty the row.
        var reloadedPage = _repository.GetApplicationPages().Single();
        reloadedPage.Name = "Renamed";
        _repository.UpdateApplicationPage(reloadedPage);
        Assert.True(_repository.GetApplicationPages().Single().Dirty);
    }

    // ----- Tombstones (doc/plan2.md's "tombstone tracking for deletes") -----

    [Fact]
    public void DeletingARowWithNoRemoteId_RecordsNoTombstone()
    {
        var app = _repository.AddApplication("Never synced");

        _repository.DeleteApplication(app.Id);

        Assert.Empty(_repository.GetPendingDeletes());
    }

    [Fact]
    public void DeletingASyncedRow_RecordsOneTombstone_EvenWhenItCascadesChildren()
    {
        var page = _repository.AddApplicationPage("1", "Page");
        var app = _repository.AddApplication("App", page.Id);
        _repository.MarkApplicationSynced(app.Id, remoteId: 42, lastSyncedImageChangedAtUtc: null);
        var group = _repository.AddKeyGroup(app.Id, "Group"); // never synced -- no RemoteId

        _repository.DeleteApplication(app.Id);

        var tombstone = Assert.Single(_repository.GetPendingDeletes());
        Assert.Equal("applications", tombstone.TableName);
        Assert.Equal(42, tombstone.RemoteId);
    }

    [Fact]
    public void RemovePendingDelete_RemovesIt()
    {
        var page = _repository.AddApplicationPage("1", "Page");
        _repository.MarkApplicationPageSynced(page.Id, remoteId: 7);

        _repository.DeleteApplicationPage(page.Id);
        var tombstone = Assert.Single(_repository.GetPendingDeletes());

        _repository.RemovePendingDelete(tombstone.Id);

        Assert.Empty(_repository.GetPendingDeletes());
    }

    // ----- Full Sync support -----

    [Fact]
    public void ClearAllRemoteIds_ResetsRemoteIdDirtyAndTombstones()
    {
        var page = _repository.AddApplicationPage("1", "Page");
        var app = _repository.AddApplication("App", page.Id);
        _repository.MarkApplicationPageSynced(page.Id, remoteId: 1);
        _repository.MarkApplicationSynced(app.Id, remoteId: 2, lastSyncedImageChangedAtUtc: DateTime.UtcNow);
        _repository.DeleteApplication(app.Id); // records a tombstone (RemoteId was set)

        _repository.ClearAllRemoteIds();

        var reloadedPage = _repository.GetApplicationPages().Single();
        Assert.Null(reloadedPage.RemoteId);
        Assert.True(reloadedPage.Dirty);
        Assert.Empty(_repository.GetPendingDeletes());
    }

    // ----- Pull from device -----

    [Fact]
    public void PullInserts_RoundTripAsAlreadySynced()
    {
        var pageId = _repository.InsertPulledApplicationPage(remoteId: 100, order: "1", name: "Pulled Page");
        var appId = _repository.InsertPulledApplication(remoteId: 200, applicationPageId: pageId, order: "1",
            name: "Pulled App", imageData: [1, 2, 3], imageDataRgb565: new byte[32768], imageChangedAtUtc: DateTime.UtcNow);
        var groupId = _repository.InsertPulledKeyGroup(remoteId: 300, applicationId: appId, order: "1", name: "Pulled Group");
        var actionId = _repository.InsertPulledKeyAction(remoteId: 400, HidActionKind.Hid, "[ctrl]+c");
        var keyId = _repository.InsertPulledKey(remoteId: 500, keyGroupId: groupId, position: 0, keyActionId: actionId,
            imageData: null, imageDataRgb565: null, imageChangedAtUtc: null);

        var page = _repository.GetApplicationPages().Single();
        Assert.Equal(100, page.RemoteId);
        Assert.False(page.Dirty);

        var app = _repository.GetApplications().Single();
        Assert.Equal(200, app.RemoteId);
        Assert.False(app.Dirty);
        Assert.NotNull(app.LastSyncedImageChangedAtUtc);
        Assert.Equal(app.ImageChangedAtUtc, app.LastSyncedImageChangedAtUtc);

        var group = _repository.GetKeyGroups(appId).Single();
        Assert.Equal(300, group.RemoteId);

        var action = _repository.GetKeyAction(actionId)!;
        Assert.Equal(400, action.RemoteId);
        Assert.Equal(ActionType.Shortcut, action.ActionType);

        var key = _repository.GetKeys(groupId).Single(k => k.Id == keyId);
        Assert.Equal(500, key.RemoteId);
        Assert.Equal(actionId, key.KeyActionId);
    }

    [Fact]
    public void ClearAllSyncedDataForPull_RemovesEverySyncedRowAndTombstones()
    {
        var page = _repository.AddApplicationPage("1", "Page");
        var app = _repository.AddApplication("App", page.Id);
        _repository.AddKeyGroup(app.Id, "Group");
        _repository.UpsertAction(ActionType.Shortcut, HidActionKind.Hid, "a", null, null);
        _repository.MarkApplicationSynced(app.Id, remoteId: 1, lastSyncedImageChangedAtUtc: null);
        _repository.DeleteApplication(app.Id); // leaves a tombstone

        _repository.ClearAllSyncedDataForPull();

        Assert.Empty(_repository.GetApplicationPages());
        Assert.Empty(_repository.GetApplications());
        Assert.Empty(_repository.GetKeyActions());
        Assert.Empty(_repository.GetPendingDeletes());
    }
}
