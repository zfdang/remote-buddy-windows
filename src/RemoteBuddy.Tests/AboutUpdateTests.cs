// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.IO;
using System.Net.Http;
using System.Text.Json;
using RemoteBuddy.App.Services;
using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Config;
using Xunit;

namespace RemoteBuddy.Tests;

public class AboutUpdateTests
{
    private static readonly WindowsRelease Published = new("v26-11-24.117", 76539880, new Version(26, 11, 24, 117));

    [Fact]
    public void ManifestMatchesTheAssemblyVersionAndRejectsAnUnrelatedPackage()
    {
        using var json = JsonDocument.Parse("""
            {
              "tag": "v26-11-24.117",
              "packages": {
                "x64": { "name": "Remote_Buddy_Windows_v26-11-24.117_x64.zip", "size": 76539880 }
              }
            }
            """);
        Assert.Equal(Published, WindowsRelease.Parse(json.RootElement));
        Assert.Equal(Published.Version, WindowsRelease.ParseVersion("v26.11.24.117"));
        Assert.Equal(Published.Version, WindowsRelease.ParseVersion("v26.11.24.1117"));
        Assert.Equal("v26-11-24.117", WindowsRelease.FormatTag(Published.Version));
        Assert.Equal("v26-01-02.009", WindowsRelease.FormatTag(new Version(26, 1, 2, 9)));
        Assert.Null(WindowsRelease.ParseVersion("v1.0.0"));

        using var wrongPackage = JsonDocument.Parse("""
            { "tag": "v26-11-24.117", "packages": { "x64": { "name": "other.zip", "size": 1 } } }
            """);
        Assert.Throws<InvalidDataException>(() => WindowsRelease.Parse(wrongPackage.RootElement));
    }

    [Fact]
    public async Task DisablingRemindersDiscardsAnInflightResultAndPersistsWhileManualChecksStillWork()
    {
        var path = Path.Combine(Path.GetTempPath(), $"remote-buddy-update-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var automaticResult = new TaskCompletionSource<WindowsRelease>();
            var checks = new List<CancellationToken>();
            var vm = new AboutViewModel(config, token =>
            {
                checks.Add(token);
                return checks.Count == 1 ? automaticResult.Task : Task.FromResult(Published);
            }) { AppVersion = "v26.11.24.116" };

            Task automaticCheck = vm.SetUpdatePageActiveAsync(true);
            Assert.Single(checks);

            vm.ReceiveUpdateNotifications = false;
            Assert.True(checks[0].IsCancellationRequested);
            automaticResult.SetResult(Published);
            await automaticCheck;
            Assert.False(vm.HasAvailableRelease);
            Assert.False(new ConfigService(path).Current.ReceiveUpdateNotifications);

            await vm.SetUpdatePageActiveAsync(false);
            await vm.SetUpdatePageActiveAsync(true);
            Assert.Single(checks);

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            Assert.Equal(2, checks.Count);
            Assert.Equal(Published, vm.AvailableRelease);
            Assert.Equal("官网最新版本 v26-11-24.117", vm.AvailableVersionText);
            Assert.True(vm.HasUpdateBadge);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }

    [Fact]
    public async Task EnteringAboutChecksOncePerVisitWhenRemindersAreEnabled()
    {
        int checks = 0;
        var vm = new AboutViewModel(null, _ =>
        {
            checks++;
            return Task.FromResult(Published);
        }) { AppVersion = "v26.11.24.116" };

        await vm.SetUpdatePageActiveAsync(true);
        Assert.Equal(Published, vm.AvailableRelease);
        await vm.SetUpdatePageActiveAsync(true);
        Assert.Equal(1, checks);

        await vm.SetUpdatePageActiveAsync(false);
        Assert.False(vm.HasAvailableRelease);
        await vm.SetUpdatePageActiveAsync(true);
        Assert.Equal(2, checks);
        Assert.Equal(Published, vm.AvailableRelease);
    }

    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 0)]
    public async Task StartupAndEachAboutVisitCheckOnlyWhenRemindersAreEnabled(bool enabled, int expectedChecks)
    {
        int checks = 0;
        var vm = new AboutViewModel(null, _ =>
        {
            checks++;
            return Task.FromResult(Published);
        }) { AppVersion = "v26.11.24.116", ReceiveUpdateNotifications = enabled };

        await vm.CheckForUpdateReminderAsync();
        Assert.Equal(enabled, vm.HasUpdateBadge);

        await vm.SetUpdatePageActiveAsync(true);
        await vm.SetUpdatePageActiveAsync(false);
        await vm.SetUpdatePageActiveAsync(true);
        Assert.Equal(expectedChecks, checks);
    }

    [Fact]
    public async Task SidebarBadgeRemainsVisibleAfterLeavingAboutUntilNoNewerReleaseIsAvailable()
    {
        var vm = new AboutViewModel(null, _ => Task.FromResult(Published)) { AppVersion = "v26.11.24.116" };

        await vm.SetUpdatePageActiveAsync(true);
        Assert.True(vm.HasUpdateBadge);
        await vm.SetUpdatePageActiveAsync(false);
        Assert.True(vm.HasUpdateBadge);
        Assert.False(vm.HasAvailableRelease);

        vm.AppVersion = "v26.11.24.117";
        await vm.SetUpdatePageActiveAsync(true);
        Assert.False(vm.HasUpdateBadge);
        Assert.False(vm.HasAvailableRelease);
    }

    [Fact]
    public async Task ManualCheckRestoresTheSidebarBadgeAfterTurningOffAutomaticReminders()
    {
        var vm = new AboutViewModel(null, _ => Task.FromResult(Published)) { AppVersion = "v26.11.24.116" };

        await vm.SetUpdatePageActiveAsync(true);
        Assert.True(vm.HasUpdateBadge);

        vm.ReceiveUpdateNotifications = false;
        Assert.False(vm.HasUpdateBadge);
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.True(vm.HasAvailableRelease);
        Assert.True(vm.HasUpdateBadge);
        await vm.SetUpdatePageActiveAsync(false);
        Assert.True(vm.HasUpdateBadge);
    }

    [Theory]
    [InlineData("v26-11-25.152", "v26.11.26.169", false, "当前版本高于官网已发布版本。")]
    [InlineData("v26-11-25.152", "v26-11-26.169", false, "当前版本高于官网已发布版本。")]
    [InlineData("v26-11-26.168", "v26.11.26.169", false, "当前版本高于官网已发布版本。")]
    [InlineData("v26-11-26.169", "v26.11.26.169", false, "已是最新版本。")]
    [InlineData("V26-11-26.169", "v26.11.26.169", false, "已是最新版本。")]
    [InlineData("v26-11-26.170", "v26.11.26.169", true, "")]
    [InlineData("v26-11-26.170", "v1.0.0", false, "当前构建没有可比较的正式版本号。")]
    public async Task OnlyNewerReleaseShowsDownloadAndBadge(
        string tag, string installedVersion, bool expectUpdate, string manualStatus)
    {
        var manifest = JsonSerializer.SerializeToElement(new
        {
            tag,
            packages = new { x64 = new { name = $"Remote_Buddy_Windows_{tag}_x64.zip", size = 72116327 } }
        });
        var release = WindowsRelease.Parse(manifest);
        var vm = new AboutViewModel(null, _ => Task.FromResult(release)) { AppVersion = installedVersion };

        await vm.CheckForUpdateReminderAsync();
        Assert.Equal(expectUpdate, vm.HasUpdateBadge);
        Assert.Equal(expectUpdate, vm.HasAvailableRelease);
        Assert.Empty(vm.UpdateStatus);
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(expectUpdate, vm.HasUpdateBadge);
        Assert.Equal(expectUpdate, vm.HasAvailableRelease);
        Assert.Equal(expectUpdate ? "官网最新版本 v26-11-26.170" : string.Empty, vm.AvailableVersionText);
        Assert.Equal(manualStatus, vm.UpdateStatus);
    }

    [Fact]
    public async Task ManualCheckDistinguishesCurrentVersionAndNetworkFailure()
    {
        var vm = new AboutViewModel(null, _ => Task.FromResult(Published)) { AppVersion = "v26.11.24.117" };
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.Equal("已是最新版本。", vm.UpdateStatus);
        Assert.False(vm.HasAvailableRelease);

        var unavailable = new AboutViewModel(null, _ => Task.FromException<WindowsRelease>(new HttpRequestException()))
        { AppVersion = "v26.11.24.116" };
        await unavailable.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.Equal("检查更新失败，请重试。", unavailable.UpdateStatus);
        Assert.False(unavailable.HasAvailableRelease);
    }
}
