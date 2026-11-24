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
        Assert.True(Published.Version > WindowsRelease.ParseVersion("v26.11.23.232"));
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
            Assert.Contains(Published.Tag, vm.AvailableVersionText);
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
