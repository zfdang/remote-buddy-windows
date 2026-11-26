// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Config;
using RemoteBuddy.Core.Models;
using Xunit;

namespace RemoteBuddy.Tests;

public sealed class CommonPhraseStoreTests : IDisposable
{
    private readonly string _tempDir;

    public CommonPhraseStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rb-phrases-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Store_Initializes12BuiltInsAndModelButtons()
    {
        var store = new CommonPhraseStore(Path.Combine(_tempDir, "phrases.json"));
        Assert.Equal(12, store.Archive.Entries.Count);
        Assert.Equal("好的", store.GetPhrase(RemoteButton.Ok)?.Text);
        Assert.Equal("继续", store.GetPhrase(RemoteButton.Left)?.Text);
        Assert.Equal("按这个方案执行", store.GetPhrase(RemoteButton.Right)?.Text);
        Assert.Equal("解释清楚一点", store.GetPhrase(RemoteButton.Up)?.Text);
        Assert.Equal("先给我一个计划", store.GetPhrase(RemoteButton.Down)?.Text);

        Assert.Contains(RemoteButton.YouTube, CommonPhraseStore.GetButtons(RemoteModelType.GoogleG9N9N));
        Assert.Contains(RemoteButton.Custom, CommonPhraseStore.GetButtons(RemoteModelType.GoogleGY3LE));
        Assert.DoesNotContain(RemoteButton.Power, CommonPhraseStore.GetButtons(RemoteModelType.T1Remote));
    }

    [Fact]
    public void EditingBuiltInForksToUserEntryAndRebindsAssignedButtons()
    {
        string path = Path.Combine(_tempDir, "phrases.json");
        var store = new CommonPhraseStore(path);

        string newId = store.SavePhrase(new CommonPhrase { Label = "收到", Text = "收到，马上处理" }, "builtin-1");
        Assert.StartsWith("user-", newId);
        Assert.Equal("收到，马上处理", store.GetPhrase(RemoteButton.Ok)?.Text);

        var reloaded = new CommonPhraseStore(path);
        Assert.Equal("收到，马上处理", reloaded.GetPhrase(RemoteButton.Ok)?.Text);
    }

    [Fact]
    public void ExportImportRoundTrip_AndValidationRejectsInvalidData()
    {
        var store = new CommonPhraseStore(Path.Combine(_tempDir, "phrases.json"));
        string addedId = store.SavePhrase(new CommonPhrase { Label = "测试", Text = "测试内容 " }, null);
        store.Assign(RemoteButton.Home, addedId);

        string exported = store.ExportJson();
        Assert.Contains("remote.zfdang.com", exported);

        var secondStore = new CommonPhraseStore(Path.Combine(_tempDir, "phrases2.json"));
        secondStore.ImportJson(exported);
        Assert.Equal("测试内容 ", secondStore.GetPhrase(RemoteButton.Home)?.Text);
        Assert.Equal(CommonPhraseBoundaryWhitespace.TrailingOnly,
            CommonPhrase.GetBoundaryWhitespaceState(secondStore.GetPhrase(RemoteButton.Home)?.Text));

        Assert.Throws<InvalidDataException>(() => secondStore.ImportJson("{ \"schemaVersion\": 99, \"entries\": [] }"));
    }

    [Fact]
    public void Routing_ClaimsInsertAndExitButtons_AndConsumesHeldReleases()
    {
        var routing = new CommonPhraseRouting();
        routing.Open("hid:01", CommonPhraseStore.GetButtons(RemoteModelType.XiaomiRC003));

        Assert.True(routing.Claims(RemoteButton.Ok, "hid:01"));
        Assert.True(routing.Claims(RemoteButton.Back, "hid:01"));
        Assert.False(routing.Claims(RemoteButton.Ok, "hid:02"));

        var insert = routing.Handle(RemoteButton.Ok, isDown: true, "hid:01");
        Assert.Equal(CommonPhraseRoutingAction.Insert, insert.Action);
        Assert.Equal(RemoteButton.Ok, insert.Button);

        // Held release after selection must still be consumed
        Assert.True(routing.Claims(RemoteButton.Ok, "hid:01"));
        var release = routing.Handle(RemoteButton.Ok, isDown: false, "hid:01");
        Assert.Equal(CommonPhraseRoutingAction.Consumed, release.Action);
        Assert.False(routing.Claims(RemoteButton.Ok, "hid:01"));
    }

    [Fact]
    public void CommonPhrasesViewModel_AssignAndSearchWorkTogether()
    {
        var store = new CommonPhraseStore(Path.Combine(_tempDir, "phrases-vm.json"));
        bool returned = false;
        var vm = new CommonPhrasesViewModel(store, () => RemoteModelType.XiaomiRC003, () => returned = true);

        vm.SelectPosition(RemoteButton.Menu);
        vm.ChoosePhrase("builtin-12");
        Assert.Equal("builtin-12", store.GetPhrase(RemoteButton.Menu)?.Id);

        vm.SearchText = "补充";
        Assert.Single(vm.FilteredPhrases);

        vm.Return();
        Assert.True(returned);
    }
}
