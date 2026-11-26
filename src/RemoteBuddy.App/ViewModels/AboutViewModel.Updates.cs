// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteBuddy.App.Services;
using RemoteBuddy.Core.Diagnostics;

namespace RemoteBuddy.App.ViewModels;

public partial class AboutViewModel
{
    private readonly Func<CancellationToken, Task<WindowsRelease>> _fetchRelease;
    private CancellationTokenSource? _updateCheck;
    private bool _updatePageActive;
    private bool _receiveUpdateNotifications;

    public bool ReceiveUpdateNotifications
    {
        get => _receiveUpdateNotifications;
        set
        {
            if (!SetProperty(ref _receiveUpdateNotifications, value)) return;

            _config?.Update(cfg => cfg.ReceiveUpdateNotifications = value);
            if (!value) HasUpdateBadge = false;
            ResetUpdateCheck();
            if (_updatePageActive) _ = CheckForUpdateReminderAsync();
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    private bool _isCheckingForUpdates;

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateBadgeDescription))]
    private bool _hasUpdateBadge;

    public string UpdateBadgeDescription => HasUpdateBadge ? "有可下载的新版本" : string.Empty;

    private WindowsRelease? _availableRelease;
    internal WindowsRelease? AvailableRelease
    {
        get => _availableRelease;
        private set
        {
            if (!SetProperty(ref _availableRelease, value)) return;
            OnPropertyChanged(nameof(AvailableVersionText));
            OnPropertyChanged(nameof(AvailablePackageText));
            OnPropertyChanged(nameof(HasAvailableRelease));
        }
    }

    public bool HasAvailableRelease => AvailableRelease != null;
    public string AvailableVersionText => AvailableRelease is { } release ? $"官网最新版本 {release.Tag}" : string.Empty;
    public string AvailablePackageText => AvailableRelease?.PackageDescription ?? string.Empty;

    internal Task SetUpdatePageActiveAsync(bool active)
    {
        if (_updatePageActive == active) return Task.CompletedTask;
        _updatePageActive = active;
        ResetUpdateCheck();
        return active ? CheckForUpdateReminderAsync() : Task.CompletedTask;
    }

    internal Task CheckForUpdateReminderAsync() =>
        ReceiveUpdateNotifications ? QueryReleaseAsync(manual: false) : Task.CompletedTask;

    internal void ResetUpdateCheck()
    {
        var pending = _updateCheck;
        _updateCheck = null;
        pending?.Cancel();
        IsCheckingForUpdates = false;
        AvailableRelease = null;
        UpdateStatus = string.Empty;
    }

    private bool CanCheckForUpdates() => !IsCheckingForUpdates;

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private Task CheckForUpdatesAsync() => QueryReleaseAsync(manual: true);

    private async Task QueryReleaseAsync(bool manual)
    {
        if (_updateCheck != null) return;

        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _updateCheck = request;
        IsCheckingForUpdates = true;
        AvailableRelease = null;
        UpdateStatus = manual ? "正在检查更新…" : string.Empty;

        try
        {
            var latest = await _fetchRelease(request.Token);
            if (!ReferenceEquals(_updateCheck, request)) return;

            if (WindowsRelease.ParseVersion(AppVersion) is not { } installed)
            {
                HasUpdateBadge = false;
                UpdateStatus = manual ? "当前构建没有可比较的正式版本号。" : string.Empty;
                return;
            }

            int comparison = latest.Version.CompareTo(installed);
            HasUpdateBadge = comparison > 0;
            if (comparison > 0)
            {
                AvailableRelease = latest;
            }
            UpdateStatus = manual ? comparison switch
            {
                < 0 => "当前版本高于官网已发布版本。",
                0 => "已是最新版本。",
                _ => string.Empty
            } : string.Empty;
        }
        catch (Exception error)
        {
            if (!ReferenceEquals(_updateCheck, request)) return;

            AppLogger.Warn($"Could not check for a Windows release: {error.Message}", "AboutViewModel");
            UpdateStatus = manual ? "检查更新失败，请重试。" : string.Empty;
        }
        finally
        {
            if (ReferenceEquals(_updateCheck, request))
            {
                _updateCheck = null;
                IsCheckingForUpdates = false;
            }
        }
    }

    [RelayCommand]
    private void OpenUpdateDownload()
    {
        if (HasAvailableRelease) OpenUrl(WindowsRelease.DownloadsUrl);
    }
}
