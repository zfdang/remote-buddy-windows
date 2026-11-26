// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteBuddy.App.Services;
using RemoteBuddy.Core.Config;
using RemoteBuddy.Core.Models;
using RemoteBuddy.Platform.Windows.Input;

namespace RemoteBuddy.App.ViewModels;

public sealed class CategoryFilterItemViewModel
{
    public ButtonActionCategory Category { get; init; }
    public string Title { get; init; } = string.Empty;
    public bool IsSelected { get; init; }
}

public sealed class ActionOptionViewModel
{
    public ButtonAction Action { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? ShortcutBadge { get; init; }
    public bool HasShortcutBadge => !string.IsNullOrEmpty(ShortcutBadge);
    public bool IsSelected { get; init; }
    public bool IsAvailable { get; init; } = true;
}

public sealed class CustomApplicationItemViewModel
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public bool IsSelected { get; init; }
}

public partial class MappingGestureViewModel : ObservableObject
{
    private readonly ButtonMapping _mapping;
    private readonly ButtonTrigger _trigger;
    private readonly Action? _onModified;
    private ConfiguredButtonAction _configured;

    public ConfiguredButtonAction Configured => _configured;

    public ButtonAction Action
    {
        get => _configured.Action;
        set
        {
            if (_configured.Action == value) return;
            _configured.Action = value;
            Commit();
        }
    }

    public string Shortcut
    {
        get => _configured.Shortcut ?? string.Empty;
        set
        {
            string normalized = value?.Trim() ?? string.Empty;
            if (Action == ButtonAction.CustomShortcut && !string.IsNullOrEmpty(normalized) &&
                !NativeInputSender.TryParseShortcut(normalized, out _, out _))
            {
                ShortcutValidationError = $"无法识别快捷键“{normalized}”，请使用如 Ctrl+C、Alt+Tab、F11 等格式";
                return;
            }
            ShortcutValidationError = string.Empty;
            _configured.Shortcut = string.IsNullOrEmpty(normalized) ? null : normalized;
            Commit();
        }
    }

    public string Text
    {
        get => _configured.Text ?? string.Empty;
        set
        {
            _configured.Text = string.IsNullOrEmpty(value) ? null : value;
            Commit();
        }
    }

    public string Url
    {
        get => _configured.Url ?? string.Empty;
        set
        {
            _configured.Url = CustomActionUrlPolicy.NormalizedInput(value);
            Commit();
        }
    }

    public string? ApplicationProfileId
    {
        get => _configured.ApplicationProfileId;
        set
        {
            _configured.ApplicationProfileId = string.IsNullOrWhiteSpace(value) ? null : value;
            Commit();
        }
    }

    private string _shortcutValidationError = string.Empty;
    public string ShortcutValidationError
    {
        get => _shortcutValidationError;
        private set
        {
            if (SetProperty(ref _shortcutValidationError, value))
            {
                OnPropertyChanged(nameof(HasShortcutError));
            }
        }
    }

    public bool HasShortcutError => !string.IsNullOrEmpty(ShortcutValidationError);

    public bool ShowRepeatNeedsClickWarning =>
        _trigger == ButtonTrigger.LongPress &&
        Action == ButtonAction.RepeatSingleClick &&
        !HasConfiguredSingleClick();

    public bool IsConfigured => _configured.Action switch
    {
        ButtonAction.Disabled => false,
        ButtonAction.CustomShortcut => !string.IsNullOrWhiteSpace(_configured.Shortcut),
        ButtonAction.OpenUrl => !string.IsNullOrWhiteSpace(_configured.Url),
        ButtonAction.OpenCustomApplication => !string.IsNullOrWhiteSpace(_configured.ApplicationProfileId),
        ButtonAction.TextSnippet => !string.IsNullOrWhiteSpace(_configured.Text),
        _ => true
    };

    public string DisplaySummary => ButtonActionCatalog.FormatActionSummary(
        _mapping.Button,
        _trigger,
        _configured,
        _mapping.GetConfiguredAction(ButtonTrigger.SingleClick));

    public MappingGestureViewModel(ButtonMapping mapping, ButtonTrigger trigger, Action? onModified)
    {
        _mapping = mapping;
        _trigger = trigger;
        _onModified = onModified;
        _configured = mapping.GetConfiguredAction(trigger).Clone();
    }

    public void SetConfigured(ConfiguredButtonAction configured)
    {
        ShortcutValidationError = string.Empty;
        _configured = configured.Clone();
        Commit();
    }

    public void Clear()
    {
        ShortcutValidationError = string.Empty;
        _configured = ConfiguredButtonAction.Disabled;
        Commit();
    }

    internal void NotifyRepeatDependencyChanged() => OnPropertyChanged(nameof(ShowRepeatNeedsClickWarning));

    private bool HasConfiguredSingleClick()
    {
        var single = _mapping.GetConfiguredAction(ButtonTrigger.SingleClick);
        return single.Action is not (ButtonAction.Disabled or ButtonAction.OpenUrl);
    }

    private void Commit()
    {
        _mapping.SetConfiguredAction(_trigger, _configured);
        OnPropertyChanged(nameof(Action));
        OnPropertyChanged(nameof(Shortcut));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Url));
        OnPropertyChanged(nameof(ApplicationProfileId));
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(DisplaySummary));
        OnPropertyChanged(nameof(ShowRepeatNeedsClickWarning));
        _onModified?.Invoke();
    }
}

public partial class MappingItemViewModel : ObservableObject
{
    public ButtonMapping Source { get; }
    public string OwnerDeviceId { get; }
    public RemoteButton Button => Source.Button;
    public string ButtonName => GetButtonDisplayName(Button);

    public MappingGestureViewModel SingleClick { get; }
    public MappingGestureViewModel DoubleClick { get; }
    public MappingGestureViewModel LongPress { get; }

    public string SingleClickSummary => SingleClick.DisplaySummary;
    public string DoubleClickSummary => DoubleClick.DisplaySummary;
    public string LongPressSummary => LongPress.DisplaySummary;

    public MappingItemViewModel(ButtonMapping source, Action? onModified = null, string ownerDeviceId = "")
    {
        Source = source;
        OwnerDeviceId = ownerDeviceId;
        void WrapModified()
        {
            OnPropertyChanged(nameof(SingleClickSummary));
            OnPropertyChanged(nameof(DoubleClickSummary));
            OnPropertyChanged(nameof(LongPressSummary));
            LongPress?.NotifyRepeatDependencyChanged();
            onModified?.Invoke();
        }
        SingleClick = new MappingGestureViewModel(source, ButtonTrigger.SingleClick, WrapModified);
        DoubleClick = new MappingGestureViewModel(source, ButtonTrigger.DoubleClick, WrapModified);
        LongPress = new MappingGestureViewModel(source, ButtonTrigger.LongPress, WrapModified);
    }

    public MappingGestureViewModel GetGesture(ButtonTrigger trigger) => trigger switch
    {
        ButtonTrigger.SingleClick => SingleClick,
        ButtonTrigger.DoubleClick => DoubleClick,
        ButtonTrigger.LongPress => LongPress,
        _ => throw new ArgumentOutOfRangeException(nameof(trigger))
    };

    public void Clear()
    {
        SingleClick.Clear();
        DoubleClick.Clear();
        LongPress.Clear();
    }

    public static string GetButtonDisplayName(RemoteButton button) => button switch
    {
        RemoteButton.Power => "电源键",
        RemoteButton.Voice => "语音键",
        RemoteButton.Up => "上键",
        RemoteButton.Down => "下键",
        RemoteButton.Left => "左键",
        RemoteButton.Right => "右键",
        RemoteButton.Ok => "确定键",
        RemoteButton.Back => "返回键",
        RemoteButton.Home => "主页键",
        RemoteButton.Menu => "菜单键",
        RemoteButton.VolumeUp => "音量 +",
        RemoteButton.VolumeDown => "音量 −",
        RemoteButton.LiveTv => "TV 键",
        RemoteButton.Mute => "静音键",
        RemoteButton.PlayPause => "播放 / 暂停",
        RemoteButton.YouTube => "YouTube 键",
        RemoteButton.Netflix => "Netflix 键",
        RemoteButton.Input => "信源键",
        RemoteButton.Custom => "自定义键 (★)",
        _ => button.ToString()
    };
}

public partial class MappingViewModel : ObservableObject
{
    private readonly ConfigService _config;
    private readonly Action<WizardMode>? _openWizard;
    private readonly Action<string>? _navigate;
    private readonly Dictionary<string, List<MappingItemViewModel>> _drafts = new();
    private readonly HashSet<RemoteButton> _activeButtons = new();
    private IReadOnlySet<PresetApplication>? _installedAppsOverride;

    public int MappingRevision { get; private set; }

    [ObservableProperty]
    private ObservableCollection<MappingItemViewModel> _currentMappings = new();

    public ObservableCollection<RemoteDevice> Devices { get; } = new();
    public bool HasDevices => Devices.Count > 0;

    [ObservableProperty]
    private RemoteDevice? _selectedDevice;

    [ObservableProperty]
    private MappingItemViewModel? _selectedMapping;

    [ObservableProperty]
    private bool _isMappingSelectionLocked = true;

    private bool _customMappingEnabled;
    public bool CustomMappingEnabled
    {
        get => _customMappingEnabled;
        set
        {
            if (SetProperty(ref _customMappingEnabled, value))
            {
                _config.Update(cfg => cfg.CustomMappingEnabled = value);
                OnPropertyChanged(nameof(MappingStatusText));
            }
        }
    }

    private bool _isVoiceActive;
    public bool IsVoiceActive
    {
        get => _isVoiceActive;
        set
        {
            if (SetProperty(ref _isVoiceActive, value))
            {
                CanvasStateChanged?.Invoke();
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorOpen))]
    [NotifyPropertyChangedFor(nameof(EditingButtonDisplayName))]
    [NotifyPropertyChangedFor(nameof(CanRestoreEditingDefault))]
    private RemoteButton? _editingButton;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorOpen))]
    [NotifyPropertyChangedFor(nameof(EditingTriggerDisplayName))]
    [NotifyPropertyChangedFor(nameof(CanRestoreEditingDefault))]
    private ButtonTrigger? _editingTrigger;

    [ObservableProperty]
    private ButtonActionCategory _selectedCategory = ButtonActionCategory.BasicKeys;

    [ObservableProperty]
    private string _customShortcutInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShortcutError))]
    private string _shortcutValidationError = string.Empty;

    [ObservableProperty]
    private string _urlInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUrlTestFeedback))]
    private string _urlTestFeedback = string.Empty;

    [ObservableProperty]
    private bool _isUrlTestSuccess;

    [ObservableProperty]
    private string _snippetInput = string.Empty;

    [ObservableProperty]
    private string _customAppFocusShortcutInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomAppShortcutError))]
    private string _customAppShortcutError = string.Empty;

    public ObservableCollection<CategoryFilterItemViewModel> VisibleCategories { get; } = new();
    public ObservableCollection<ActionOptionViewModel> VisibleActionOptions { get; } = new();
    public ObservableCollection<CustomApplicationItemViewModel> CustomApplications { get; } = new();

    public IReadOnlySet<RemoteButton> ActiveButtons => _activeButtons;

    internal event Action<RemoteButton>? HardwareButtonPressed;
    internal event Action? CanvasStateChanged;
    internal event Action<string?>? RequestOpenShortcutPicker;
    internal event Action? RequestScrollEditorIntoView;

    public bool IsEditorOpen => EditingButton.HasValue && EditingTrigger.HasValue;
    public bool HasShortcutError => !string.IsNullOrEmpty(ShortcutValidationError);
    public bool HasCustomAppShortcutError => !string.IsNullOrEmpty(CustomAppShortcutError);
    public bool HasUrlTestFeedback => !string.IsNullOrEmpty(UrlTestFeedback);
    public bool CanRestoreEditingDefault => EditingTrigger is ButtonTrigger.SingleClick or ButtonTrigger.LongPress;

    public string EditingButtonDisplayName => EditingButton is { } b ? MappingItemViewModel.GetButtonDisplayName(b) : string.Empty;
    public string EditingTriggerDisplayName => EditingTrigger switch
    {
        ButtonTrigger.SingleClick => "单击",
        ButtonTrigger.DoubleClick => "双击",
        ButtonTrigger.LongPress => "长按",
        _ => string.Empty
    };

    public ConfiguredButtonAction EditingConfiguredAction =>
        GetEditingGesture()?.Configured ?? ConfiguredButtonAction.Disabled;

    public bool IsEditingDisabled => EditingConfiguredAction.Action == ButtonAction.Disabled;

    public bool IsDirectPassthroughLockActive =>
        EditingButton is { } btn &&
        EditingTrigger is { } trig &&
        trig != ButtonTrigger.SingleClick &&
        GetConfiguredAction(btn, ButtonTrigger.SingleClick).Action == ButtonAction.DirectPassthrough;

    public bool ShowLongPressHoldRepeatOption =>
        EditingTrigger == ButtonTrigger.LongPress && !IsDirectPassthroughLockActive;

    public bool IsHoldRepeatSelected => EditingConfiguredAction.Action == ButtonAction.RepeatSingleClick;

    public bool ShowRepeatNeedsClickWarning =>
        IsHoldRepeatSelected &&
        EditingButton is { } btn &&
        GetConfiguredAction(btn, ButtonTrigger.SingleClick).Action is ButtonAction.Disabled or ButtonAction.OpenUrl;

    public bool ShowCategoryGrid => !IsDirectPassthroughLockActive;
    public bool ShowDirectPassthroughHelp => !IsDirectPassthroughLockActive && SelectedCategory == ButtonActionCategory.DirectPassthrough;
    public bool ShowCommonPhrasesAdjustTile => !IsDirectPassthroughLockActive && SelectedCategory == ButtonActionCategory.CommonPhrases;
    public bool ShowCustomShortcutEditor => !IsDirectPassthroughLockActive && SelectedCategory == ButtonActionCategory.Custom && EditingConfiguredAction.Action == ButtonAction.CustomShortcut;
    public bool ShowOpenUrlEditor => !IsDirectPassthroughLockActive && SelectedCategory == ButtonActionCategory.Custom && EditingConfiguredAction.Action == ButtonAction.OpenUrl;
    public bool ShowTextSnippetEditor => !IsDirectPassthroughLockActive && SelectedCategory == ButtonActionCategory.CommonPhrases && EditingConfiguredAction.Action == ButtonAction.TextSnippet;
    public bool ShowCustomApplicationEditor => !IsDirectPassthroughLockActive && SelectedCategory == ButtonActionCategory.Applications && EditingConfiguredAction.Action == ButtonAction.OpenCustomApplication;

    public string CustomShortcutDisplay =>
        string.IsNullOrWhiteSpace(EditingConfiguredAction.Shortcut) ? "未录入" : EditingConfiguredAction.Shortcut!;

    public bool HasCustomShortcutValue => !string.IsNullOrWhiteSpace(EditingConfiguredAction.Shortcut);

    public bool HasUrlInput => !string.IsNullOrWhiteSpace(UrlInput);
    public bool IsUrlValid => CustomActionUrlPolicy.TryValidateUrl(UrlInput, out _);
    public bool ShowInvalidUrlHint => HasUrlInput && !IsUrlValid;

    public CustomApplicationProfile? SelectedCustomApplication =>
        EditingConfiguredAction.ApplicationProfileId is { } id
            ? _config.Current.CustomApplications.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal))
            : null;

    public bool HasSelectedCustomApplication => SelectedCustomApplication != null;
    public bool IsCustomAppNoneStrategy => SelectedCustomApplication?.FocusStrategy == CustomApplicationFocusStrategy.None;
    public bool IsCustomAppShortcutStrategy => SelectedCustomApplication?.FocusStrategy == CustomApplicationFocusStrategy.KeyboardShortcut;
    public bool HasCustomAppFocusShortcut => !string.IsNullOrWhiteSpace(SelectedCustomApplication?.FocusShortcut);

    public string TriggerTimingHint => EditingTrigger switch
    {
        ButtonTrigger.DoubleClick when EditingConfiguredAction.Action != ButtonAction.Disabled =>
            "设置双击动作后，单击同一按键将等待约 0.3 秒以区分单/双击。",
        ButtonTrigger.LongPress when EditingConfiguredAction.Action is not (ButtonAction.Disabled or ButtonAction.RepeatSingleClick) =>
            "设置独立长按动作后，长按约 0.55 秒触发该动作，短按将在松开时触发。",
        ButtonTrigger.SingleClick => "按下即触发；若同时配置了双击或长按，则按手势规则区分执行。",
        _ => string.Empty
    };

    public bool IsTriggerTimingWarning =>
        (EditingTrigger == ButtonTrigger.DoubleClick && EditingConfiguredAction.Action != ButtonAction.Disabled) ||
        (EditingTrigger == ButtonTrigger.LongPress && EditingConfiguredAction.Action is not (ButtonAction.Disabled or ButtonAction.RepeatSingleClick));

    public bool HasTriggerTimingHint => !string.IsNullOrEmpty(TriggerTimingHint);

    public bool IsG9N9N => SelectedDevice?.ModelType is RemoteModelType.GoogleG9N9N or RemoteModelType.GoogleGY3LE;
    public bool IsG9N9NLayout => SelectedDevice?.ModelType == RemoteModelType.GoogleG9N9N;
    public bool IsGY3LELayout => SelectedDevice?.ModelType == RemoteModelType.GoogleGY3LE;
    public bool IsT1 => SelectedDevice?.ModelType == RemoteModelType.T1Remote;
    public bool IsXiaomi => SelectedDevice != null && !IsG9N9N && !IsT1;

    public string DeviceNote => SelectedDevice == null
        ? "请先添加并连接遥控器后再配置自定义按键映射。"
        : IsGY3LELayout
            ? "Google TV Streamer (GY3LE) 支持自定义单击、双击和长按动作；语音键固定用于遥控器麦克风。"
            : IsG9N9NLayout
                ? "Chromecast (G9N9N) 支持自定义单击、双击和长按动作；语音键固定用于遥控器麦克风。"
                : IsT1
                    ? "T1 飞鼠遥控器正面 7 颗按键支持自定义；语音键固定用于遥控器麦克风。"
                    : "选择遥控器上的按键，可分别为单击、双击与长按设置不同动作；语音键固定用于遥控器麦克风。";

    public string MappingStatusText => !CustomMappingEnabled
        ? "按键映射未启用（所有按键保持 Windows 原生输入）"
        : SelectedDevice == null
            ? "尚未选择遥控器"
            : SelectedDevice.IsConnected
                ? $"按键映射已启用 · {SelectedDevice.Name} 已连接"
                : $"按键映射已启用 · 等待 {SelectedDevice.Name} 连接";

    public MappingViewModel(ConfigService config) : this(config, null, null)
    {
    }

    public MappingViewModel(ConfigService config, Action<WizardMode>? openWizard, Action<string>? navigate = null)
    {
        _config = config;
        _openWizard = openWizard;
        _navigate = navigate;
        _customMappingEnabled = config.Current.CustomMappingEnabled;
        SyncDevicesCollection();
        _selectedDevice = Devices.FirstOrDefault(d => d.Id == _config.Current.SelectedDeviceId);
        LoadMappings();
    }

    internal void SetInstalledApplicationsForTest(IReadOnlySet<PresetApplication>? installedApps)
    {
        _installedAppsOverride = installedApps;
        RefreshEditorCollections();
    }

    private bool _isSyncingDevices;
    private bool _isSyncingSubEditorInputs;

    [RelayCommand]
    public void AddDevice() => _openWizard?.Invoke(WizardMode.DeviceOnly);

    [RelayCommand]
    public void OpenCommonPhrases() => _navigate?.Invoke("CommonPhrases");

    [RelayCommand]
    public void OpenVoiceIntegration() => _navigate?.Invoke("Integrations");

    [RelayCommand]
    public void OpenDeviceGuide() => AppLinks.OpenExternalUrl(AppLinks.DevicesFaqUrl);

    partial void OnSelectedDeviceChanged(RemoteDevice? value)
    {
        EditingButton = null;
        EditingTrigger = null;
        OnPropertyChanged(nameof(DeviceNote));
        OnPropertyChanged(nameof(MappingStatusText));
        OnPropertyChanged(nameof(IsG9N9N));
        OnPropertyChanged(nameof(IsG9N9NLayout));
        OnPropertyChanged(nameof(IsGY3LELayout));
        OnPropertyChanged(nameof(IsT1));
        OnPropertyChanged(nameof(IsXiaomi));
        if (!_isSyncingDevices)
        {
            LoadMappings();
        }
    }

    partial void OnCustomShortcutInputChanged(string value)
    {
        if (_isSyncingSubEditorInputs || EditingConfiguredAction.Action != ButtonAction.CustomShortcut) return;
        string trimmed = value?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(trimmed) && !NativeInputSender.TryParseShortcut(trimmed, out _, out _))
        {
            ShortcutValidationError = $"无法识别快捷键“{trimmed}”，请使用如 Ctrl+C、Alt+Tab、F11 等格式";
            return;
        }
        ShortcutValidationError = string.Empty;
        if (GetEditingGesture() is { } gesture)
        {
            var next = gesture.Configured.Clone();
            next.Shortcut = string.IsNullOrEmpty(trimmed) ? null : trimmed;
            gesture.SetConfigured(next);
            NotifyEditorProperties();
        }
    }

    partial void OnUrlInputChanged(string value)
    {
        if (_isSyncingSubEditorInputs) return;
        UrlTestFeedback = string.Empty;
        if (GetEditingGesture() is { } gesture && gesture.Action == ButtonAction.OpenUrl)
        {
            var next = gesture.Configured.Clone();
            next.Url = CustomActionUrlPolicy.NormalizedInput(value);
            gesture.SetConfigured(next);
        }
        OnPropertyChanged(nameof(HasUrlInput));
        OnPropertyChanged(nameof(IsUrlValid));
        OnPropertyChanged(nameof(ShowInvalidUrlHint));
    }

    partial void OnSnippetInputChanged(string value)
    {
        if (_isSyncingSubEditorInputs) return;
        if (GetEditingGesture() is { } gesture && gesture.Action == ButtonAction.TextSnippet)
        {
            var next = gesture.Configured.Clone();
            next.Text = string.IsNullOrEmpty(value) ? null : value;
            gesture.SetConfigured(next);
        }
    }

    partial void OnCustomAppFocusShortcutInputChanged(string value)
    {
        if (_isSyncingSubEditorInputs || SelectedCustomApplication is not { } profile) return;
        string trimmed = value?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(trimmed) && !NativeInputSender.TryParseShortcut(trimmed, out _, out _))
        {
            CustomAppShortcutError = $"无法识别快捷键“{trimmed}”";
            return;
        }
        CustomAppShortcutError = string.Empty;
        SetCustomAppFocusShortcut(string.IsNullOrEmpty(trimmed) ? null : trimmed);
    }

    [RelayCommand]
    public void SelectButton(string buttonName)
    {
        if (Enum.TryParse<RemoteButton>(buttonName, out var btn))
        {
            var match = CurrentMappings.FirstOrDefault(m => m.Button == btn);
            if (match != null)
            {
                SelectedMapping = match;
                CanvasStateChanged?.Invoke();
            }
        }
    }

    public void SelectButton(RemoteButton button)
    {
        var match = CurrentMappings.FirstOrDefault(m => m.Button == button);
        if (match != null)
        {
            SelectedMapping = match;
            CanvasStateChanged?.Invoke();
        }
    }

    public void BeginEdit(RemoteButton button, ButtonTrigger trigger)
    {
        SelectButton(button);
        EditingButton = button;
        EditingTrigger = trigger;

        var configured = GetConfiguredAction(button, trigger);
        var visibleFilters = ButtonActionCatalog.GetVisibleCategories(button, trigger);
        var category = ButtonActionCatalog.GetCategory(configured.Action);
        if (configured.Action is ButtonAction.Disabled or ButtonAction.RepeatSingleClick)
        {
            SelectedCategory = ButtonActionCategory.BasicKeys;
        }
        else if (visibleFilters.Contains(category))
        {
            SelectedCategory = category;
        }
        else
        {
            SelectedCategory = ButtonActionCategory.BasicKeys;
        }

        SyncSubEditorInputs(configured);
        RefreshEditorCollections();
        NotifyEditorProperties();
        RequestScrollEditorIntoView?.Invoke();
    }

    [RelayCommand]
    public void CloseEditor()
    {
        EditingButton = null;
        EditingTrigger = null;
        ShortcutValidationError = string.Empty;
        UrlTestFeedback = string.Empty;
        NotifyEditorProperties();
        CanvasStateChanged?.Invoke();
    }

    [RelayCommand]
    public void SelectCategory(ButtonActionCategory category)
    {
        if (SelectedCategory == category) return;
        SelectedCategory = category;
        RefreshEditorCollections();
        NotifyEditorProperties();
    }

    [RelayCommand]
    public void SelectAction(ButtonAction action)
    {
        if (GetEditingGesture() is not { } gesture) return;

        if (action == ButtonAction.CustomShortcut)
        {
            var next = gesture.Configured.Clone();
            next.Action = ButtonAction.CustomShortcut;
            gesture.SetConfigured(next);
            SyncSubEditorInputs(next);
            RefreshEditorCollections();
            NotifyEditorProperties();
            if (string.IsNullOrWhiteSpace(next.Shortcut))
            {
                RequestOpenShortcutPicker?.Invoke(next.Shortcut);
            }
            return;
        }

        var updated = gesture.Configured.Clone();
        updated.Action = action;
        gesture.SetConfigured(updated);
        SyncSubEditorInputs(updated);
        RefreshEditorCollections();
        NotifyEditorProperties();
    }

    [RelayCommand]
    public void SelectHoldRepeat()
    {
        if (EditingTrigger != ButtonTrigger.LongPress) return;
        SelectAction(ButtonAction.RepeatSingleClick);
    }

    [RelayCommand]
    public void RestoreDefaultAction()
    {
        if (EditingButton is not { } btn || EditingTrigger is not { } trig || SelectedDevice == null) return;
        if (trig == ButtonTrigger.DoubleClick) return;
        if (GetEditingGesture() is not { } gesture) return;

        var def = RemoteDevice.GetDefaultAction(SelectedDevice.ModelType, btn, trig);
        gesture.SetConfigured(def);
        var visibleFilters = ButtonActionCatalog.GetVisibleCategories(btn, trig);
        var targetCat = ButtonActionCatalog.GetCategory(def.Action);
        SelectedCategory = def.Action is ButtonAction.Disabled or ButtonAction.RepeatSingleClick
            ? ButtonActionCategory.BasicKeys
            : (visibleFilters.Contains(targetCat) ? targetCat : ButtonActionCategory.BasicKeys);

        SyncSubEditorInputs(def);
        RefreshEditorCollections();
        NotifyEditorProperties();
    }

    [RelayCommand]
    public void ClearEditingAction()
    {
        if (GetEditingGesture() is not { } gesture) return;
        gesture.Clear();
        SyncSubEditorInputs(ConfiguredButtonAction.Disabled);
        RefreshEditorCollections();
        NotifyEditorProperties();
    }

    [RelayCommand]
    public void OpenShortcutPicker()
    {
        RequestOpenShortcutPicker?.Invoke(EditingConfiguredAction.Shortcut);
    }

    public void ApplyCustomShortcut(string? shortcut)
    {
        if (GetEditingGesture() is not { } gesture) return;
        string? normalized = string.IsNullOrWhiteSpace(shortcut) ? null : shortcut.Trim();
        if (normalized != null && !NativeInputSender.TryParseShortcut(normalized, out _, out _))
        {
            ShortcutValidationError = $"无法识别快捷键“{normalized}”";
            return;
        }
        ShortcutValidationError = string.Empty;
        var next = gesture.Configured.Clone();
        next.Action = ButtonAction.CustomShortcut;
        next.Shortcut = normalized;
        gesture.SetConfigured(next);
        SyncSubEditorInputs(next);
        RefreshEditorCollections();
        NotifyEditorProperties();
    }

    [RelayCommand]
    public void ClearCustomShortcut()
    {
        if (GetEditingGesture() is not { } gesture) return;
        ShortcutValidationError = string.Empty;
        var next = gesture.Configured.Clone();
        next.Shortcut = null;
        gesture.SetConfigured(next);
        SyncSubEditorInputs(next);
        NotifyEditorProperties();
    }

    [RelayCommand]
    public void ApplySettingsUrlTemplate()
    {
        UrlInput = "ms-settings:bluetooth";
    }

    [RelayCommand]
    public void ClearUrl()
    {
        UrlInput = string.Empty;
        UrlTestFeedback = string.Empty;
    }

    [RelayCommand]
    public void TestOpenUrl()
    {
        bool ok = WindowsApplicationLauncher.OpenUrl(UrlInput);
        IsUrlTestSuccess = ok;
        UrlTestFeedback = ok ? "已尝试打开目标链接。" : "无法打开该链接，请确认本机已安装对应应用或网址有效。";
    }

    public void AddCustomApplication(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || GetEditingGesture() is not { } gesture) return;
        string normalizedPath = executablePath.Trim();
        var existing = _config.Current.CustomApplications.FirstOrDefault(a =>
            string.Equals(a.ExecutablePath, normalizedPath, StringComparison.OrdinalIgnoreCase));

        string profileId;
        if (existing != null)
        {
            profileId = existing.Id;
        }
        else
        {
            string displayName = Path.GetFileNameWithoutExtension(normalizedPath);
            if (string.IsNullOrWhiteSpace(displayName)) displayName = normalizedPath;
            var profile = new CustomApplicationProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                DisplayName = displayName,
                ExecutablePath = normalizedPath,
                FocusStrategy = CustomApplicationFocusStrategy.None
            };
            _config.Update(cfg => cfg.CustomApplications.Add(profile));
            profileId = profile.Id;
        }

        SelectCustomApplication(profileId);
    }

    [RelayCommand]
    public void SelectCustomApplication(string profileId)
    {
        if (GetEditingGesture() is not { } gesture) return;
        var next = gesture.Configured.Clone();
        next.Action = ButtonAction.OpenCustomApplication;
        next.ApplicationProfileId = profileId;
        gesture.SetConfigured(next);
        SyncSubEditorInputs(next);
        RefreshEditorCollections();
        NotifyEditorProperties();
    }

    public void SetCustomAppFocusStrategy(CustomApplicationFocusStrategy strategy)
    {
        if (SelectedCustomApplication is not { } profile) return;
        _config.Update(cfg =>
        {
            var stored = cfg.CustomApplications.FirstOrDefault(a => a.Id == profile.Id);
            if (stored != null) stored.FocusStrategy = strategy;
        });
        NotifyEditorProperties();
    }

    public void SetCustomAppFocusShortcut(string? shortcut)
    {
        if (SelectedCustomApplication is not { } profile) return;
        string? normalized = string.IsNullOrWhiteSpace(shortcut) ? null : shortcut.Trim();
        if (normalized != null && !NativeInputSender.TryParseShortcut(normalized, out _, out _))
        {
            CustomAppShortcutError = $"无法识别快捷键“{normalized}”";
            return;
        }
        CustomAppShortcutError = string.Empty;
        _config.Update(cfg =>
        {
            var stored = cfg.CustomApplications.FirstOrDefault(a => a.Id == profile.Id);
            if (stored != null) stored.FocusShortcut = normalized;
        });
        CustomAppFocusShortcutInput = normalized ?? string.Empty;
        NotifyEditorProperties();
    }

    [RelayCommand]
    public void ClearCustomAppFocusShortcut() => SetCustomAppFocusShortcut(null);

    [RelayCommand]
    public void DeleteCustomApplication(string profileId)
    {
        _config.Update(cfg => cfg.CustomApplications.RemoveAll(a => a.Id == profileId));
        if (EditingConfiguredAction.ApplicationProfileId == profileId && GetEditingGesture() is { } gesture)
        {
            var next = gesture.Configured.Clone();
            next.ApplicationProfileId = null;
            gesture.SetConfigured(next);
        }
        RefreshEditorCollections();
        NotifyEditorProperties();
    }

    [RelayCommand]
    public void TestCustomApplication()
    {
        if (SelectedCustomApplication is { } profile)
        {
            WindowsApplicationLauncher.LaunchOrActivateCustom(profile);
        }
    }

    public ConfiguredButtonAction GetConfiguredAction(RemoteButton button, ButtonTrigger trigger)
    {
        var item = CurrentMappings.FirstOrDefault(m => m.Button == button);
        return item?.GetGesture(trigger).Configured ?? ConfiguredButtonAction.Disabled;
    }

    public string GetActionSummary(RemoteButton button, ButtonTrigger trigger, bool detailed = false)
    {
        var configured = GetConfiguredAction(button, trigger);
        var single = GetConfiguredAction(button, ButtonTrigger.SingleClick);
        return ButtonActionCatalog.FormatActionSummary(
            button,
            trigger,
            configured,
            single,
            ResolveCustomApplicationName,
            detailed);
    }

    private string? ResolveCustomApplicationName(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return null;
        return _config.Current.CustomApplications
            .FirstOrDefault(a => string.Equals(a.Id, profileId, StringComparison.Ordinal))
            ?.DisplayName;
    }

    internal void NotifyHardwarePress(RemoteButton button) => NotifyHardwareButtonState(button, isDown: true);

    internal void NotifyHardwareButtonState(RemoteButton button, bool isDown)
    {
        if (isDown)
        {
            _activeButtons.Add(button);
            if (!IsMappingSelectionLocked) SelectButton(button);
            HardwareButtonPressed?.Invoke(button);
        }
        else
        {
            _activeButtons.Remove(button);
        }
        CanvasStateChanged?.Invoke();
    }

    [RelayCommand]
    public void ClearSelectedMapping()
    {
        if (SelectedMapping != null)
        {
            SelectedMapping.Clear();
            SaveMappings();
            RefreshEditorCollections();
            NotifyEditorProperties();
        }
    }

    public void ResetToDefaults()
    {
        var selectedDev = SelectedDevice;
        if (selectedDev != null && _config.Current.Devices.Contains(selectedDev))
        {
            var defaults = RemoteDevice.CreateDefaultMappings(selectedDev.ModelType);
            _drafts.Remove(selectedDev.Id);
            _config.Update(cfg =>
            {
                if (cfg.Devices.Contains(selectedDev))
                {
                    selectedDev.Mappings = defaults;
                }
            });
            EditingButton = null;
            EditingTrigger = null;
            LoadMappings();
            NotifyEditorProperties();
        }
    }

    public void SyncSelectedDeviceWithConfig()
    {
        SyncDevicesCollection();
        var targetId = _config.Current.SelectedDeviceId;
        if (!string.IsNullOrEmpty(targetId))
        {
            var match = Devices.FirstOrDefault(d => d.Id == targetId);
            if (match != null)
            {
                if (SelectedDevice == null || SelectedDevice.Id != match.Id)
                {
                    SelectedDevice = match;
                }
                else
                {
                    LoadMappings();
                }
                return;
            }
        }
        else if (SelectedDevice == null && Devices.Count > 0)
        {
            SelectedDevice = Devices[0];
            return;
        }
    }

    private void SyncDevicesCollection()
    {
        if (_isSyncingDevices) return;
        _isSyncingDevices = true;
        try
        {
            var desired = ConfigService.DeduplicateDevices(_config.Current.Devices, _config.Current.SelectedDeviceId);
            var configuredIds = desired.Select(d => d.Id).ToHashSet();

            foreach (var staleId in _drafts.Keys.Where(id => !configuredIds.Contains(id)).ToArray())
            {
                _drafts.Remove(staleId);
            }

            for (int i = Devices.Count - 1; i >= 0; i--)
            {
                if (!desired.Contains(Devices[i]))
                {
                    Devices.RemoveAt(i);
                }
            }

            for (int targetIndex = 0; targetIndex < desired.Count; targetIndex++)
            {
                var item = desired[targetIndex];
                int currentIndex = Devices.IndexOf(item);
                if (currentIndex < 0)
                {
                    Devices.Insert(targetIndex, item);
                }
                else if (currentIndex != targetIndex)
                {
                    Devices.Move(currentIndex, targetIndex);
                }
            }

            OnPropertyChanged(nameof(HasDevices));
        }
        finally
        {
            _isSyncingDevices = false;
        }
    }

    public void SyncWithDevices()
    {
        SyncDevicesCollection();
        if (SelectedDevice != null && !_config.Current.Devices.Contains(SelectedDevice))
        {
            var replacement = !string.IsNullOrEmpty(_config.Current.SelectedDeviceId)
                ? Devices.FirstOrDefault(d => d.Id == _config.Current.SelectedDeviceId)
                : null;
            SelectedDevice = replacement ?? Devices.FirstOrDefault();
            if (SelectedDevice == null)
            {
                LoadMappings();
            }
            return;
        }
        LoadMappings();
    }

    public void LoadMappings()
    {
        SyncDevicesCollection();
        CurrentMappings.Clear();

        if (SelectedDevice != null && !_config.Current.Devices.Contains(SelectedDevice))
        {
            SelectedDevice = Devices.FirstOrDefault(d => d.Id == _config.Current.SelectedDeviceId)
                ?? Devices.FirstOrDefault();
            return;
        }

        if (SelectedDevice == null && Devices.Count > 0)
        {
            SelectedDevice = Devices.FirstOrDefault(d => d.Id == _config.Current.SelectedDeviceId)
                ?? Devices[0];
            return;
        }

        var selectedDev = SelectedDevice;
        if (selectedDev != null)
        {
            if (_drafts.TryGetValue(selectedDev.Id, out var draft))
            {
                foreach (var item in draft) CurrentMappings.Add(item);
                SelectedMapping = CurrentMappings.FirstOrDefault(m => m.Button == RemoteButton.Ok)
                    ?? CurrentMappings.FirstOrDefault();
                CanvasStateChanged?.Invoke();
                return;
            }

            if (selectedDev.Mappings == null || selectedDev.Mappings.Count == 0)
            {
                var defaults = RemoteDevice.CreateDefaultMappings(selectedDev.ModelType);
                _config.Update(cfg =>
                {
                    if (cfg.Devices.Contains(selectedDev))
                    {
                        selectedDev.Mappings = defaults;
                    }
                });
            }

            var ownerMappings = selectedDev.Mappings ?? [];
            var buttons = GetSupportedButtonsForModel(selectedDev.ModelType, ownerMappings);
            var owner = selectedDev;
            foreach (var button in buttons)
            {
                var mapping = ownerMappings.FirstOrDefault(m => m.Button == button)
                    ?? new ButtonMapping { Button = button };
                mapping.MigrateLegacyIfNeeded();
                CurrentMappings.Add(new MappingItemViewModel(mapping, () => OnMappingItemModified(owner), owner.Id));
            }
            _drafts[owner.Id] = CurrentMappings.ToList();
        }

        SelectedMapping = CurrentMappings.FirstOrDefault(m => m.Button == RemoteButton.Ok)
            ?? CurrentMappings.FirstOrDefault();
        OnPropertyChanged(nameof(MappingStatusText));
        CanvasStateChanged?.Invoke();
    }

    private static RemoteButton[] GetSupportedButtonsForModel(RemoteModelType modelType, IReadOnlyList<ButtonMapping> ownerMappings)
    {
        return modelType switch
        {
            RemoteModelType.XiaomiRC001 or RemoteModelType.XiaomiRC003 or RemoteModelType.XiaomiARN9 or RemoteModelType.Unknown =>
                [
                    RemoteButton.Power, RemoteButton.Up, RemoteButton.Left, RemoteButton.Ok, RemoteButton.Right, RemoteButton.Down,
                    RemoteButton.Back, RemoteButton.Home, RemoteButton.Menu, RemoteButton.VolumeUp, RemoteButton.VolumeDown, RemoteButton.LiveTv
                ],
            RemoteModelType.GoogleG9N9N =>
                [
                    RemoteButton.Power, RemoteButton.Up, RemoteButton.Left, RemoteButton.Ok, RemoteButton.Right, RemoteButton.Down,
                    RemoteButton.Back, RemoteButton.Home, RemoteButton.VolumeUp, RemoteButton.VolumeDown, RemoteButton.Mute,
                    RemoteButton.YouTube, RemoteButton.Netflix, RemoteButton.Input
                ],
            RemoteModelType.GoogleGY3LE =>
                [
                    RemoteButton.Power, RemoteButton.Up, RemoteButton.Left, RemoteButton.Ok, RemoteButton.Right, RemoteButton.Down,
                    RemoteButton.Back, RemoteButton.Home, RemoteButton.VolumeUp, RemoteButton.VolumeDown, RemoteButton.Mute,
                    RemoteButton.YouTube, RemoteButton.Netflix, RemoteButton.Custom
                ],
            RemoteModelType.T1Remote =>
                [
                    RemoteButton.Power, RemoteButton.Back, RemoteButton.Home, RemoteButton.Menu,
                    RemoteButton.VolumeUp, RemoteButton.VolumeDown, RemoteButton.Mute
                ],
            _ => ownerMappings.Select(m => m.Button).ToArray()
        };
    }

    private void OnMappingItemModified(RemoteDevice ownerDevice)
    {
        if (!_config.Current.Devices.Contains(ownerDevice))
        {
            _drafts.Remove(ownerDevice.Id);
            return;
        }

        if (!_drafts.TryGetValue(ownerDevice.Id, out var ownerDraft))
        {
            return;
        }

        if (ReferenceEquals(SelectedDevice, ownerDevice))
        {
            MappingRevision++;
            OnPropertyChanged(nameof(MappingRevision));
            CanvasStateChanged?.Invoke();
        }

        var visibleButtons = ownerDraft.Select(i => i.Button).ToHashSet();
        var merged = (ownerDevice.Mappings ?? []).Where(m => !visibleButtons.Contains(m.Button))
            .Concat(ownerDraft.Select(i => i.Source)).ToList();
        _config.Update(cfg =>
        {
            if (cfg.Devices.Contains(ownerDevice))
            {
                ownerDevice.Mappings = merged;
            }
        });
    }

    [RelayCommand]
    public void SaveMappings()
    {
        var selectedDev = SelectedDevice;
        if (selectedDev != null && _config.Current.Devices.Contains(selectedDev))
        {
            var sourceItems = _drafts.TryGetValue(selectedDev.Id, out var draft) ? draft : CurrentMappings.ToList();
            var visibleButtons = sourceItems.Select(i => i.Button).ToHashSet();
            var merged = (selectedDev.Mappings ?? []).Where(m => !visibleButtons.Contains(m.Button))
                .Concat(sourceItems.Select(i => i.Source)).ToList();
            _config.Update(cfg =>
            {
                if (cfg.Devices.Contains(selectedDev))
                {
                    selectedDev.Mappings = merged;
                }
            });
        }
    }

    private MappingGestureViewModel? GetEditingGesture()
    {
        if (EditingButton is not { } btn || EditingTrigger is not { } trig) return null;
        return CurrentMappings.FirstOrDefault(m => m.Button == btn)?.GetGesture(trig);
    }

    private void SyncSubEditorInputs(ConfiguredButtonAction configured)
    {
        _isSyncingSubEditorInputs = true;
        try
        {
            CustomShortcutInput = configured.Shortcut ?? string.Empty;
            ShortcutValidationError = string.Empty;

            UrlInput = configured.Url ?? string.Empty;
            UrlTestFeedback = string.Empty;
            OnPropertyChanged(nameof(HasUrlInput));
            OnPropertyChanged(nameof(IsUrlValid));
            OnPropertyChanged(nameof(ShowInvalidUrlHint));

            SnippetInput = configured.Text ?? string.Empty;

            CustomAppFocusShortcutInput = SelectedCustomApplication?.FocusShortcut ?? string.Empty;
            CustomAppShortcutError = string.Empty;
        }
        finally
        {
            _isSyncingSubEditorInputs = false;
        }
    }

    private void RefreshEditorCollections()
    {
        VisibleCategories.Clear();
        VisibleActionOptions.Clear();
        CustomApplications.Clear();

        if (EditingButton is not { } btn || EditingTrigger is not { } trig) return;

        var visibleCats = ButtonActionCatalog.GetVisibleCategories(btn, trig);
        if (!visibleCats.Contains(SelectedCategory))
        {
            SelectedCategory = ButtonActionCategory.BasicKeys;
        }

        foreach (var cat in visibleCats)
        {
            VisibleCategories.Add(new CategoryFilterItemViewModel
            {
                Category = cat,
                Title = ButtonActionCatalog.GetCategoryDisplayName(cat),
                IsSelected = cat == SelectedCategory
            });
        }

        var installedApps = _installedAppsOverride ?? WindowsApplicationLauncher.DetectInstalledPresetApplications();
        var currentAction = EditingConfiguredAction.Action;
        var pickerActions = ButtonActionCatalog.GetPickerActions(installedApps, currentAction, btn, trig);

        foreach (var action in pickerActions.Where(a => ButtonActionCatalog.GetCategory(a) == SelectedCategory))
        {
            var preset = ButtonActionCatalog.GetPresetApplication(action);
            bool available = preset == null || installedApps.Contains(preset.Value);
            string title = ButtonActionCatalog.GetDisplayName(action) + (available ? string.Empty : " (未安装)");
            VisibleActionOptions.Add(new ActionOptionViewModel
            {
                Action = action,
                DisplayName = title,
                ShortcutBadge = ButtonActionCatalog.GetShortcutBadge(action),
                IsSelected = currentAction == action,
                IsAvailable = available
            });
        }

        string? selectedProfileId = EditingConfiguredAction.ApplicationProfileId;
        foreach (var profile in _config.Current.CustomApplications)
        {
            CustomApplications.Add(new CustomApplicationItemViewModel
            {
                Id = profile.Id,
                DisplayName = profile.DisplayName,
                ExecutablePath = profile.ExecutablePath,
                IsSelected = string.Equals(profile.Id, selectedProfileId, StringComparison.Ordinal)
            });
        }
    }

    private void NotifyEditorProperties()
    {
        OnPropertyChanged(nameof(EditingConfiguredAction));
        OnPropertyChanged(nameof(IsEditingDisabled));
        OnPropertyChanged(nameof(IsDirectPassthroughLockActive));
        OnPropertyChanged(nameof(ShowLongPressHoldRepeatOption));
        OnPropertyChanged(nameof(IsHoldRepeatSelected));
        OnPropertyChanged(nameof(ShowRepeatNeedsClickWarning));
        OnPropertyChanged(nameof(ShowCategoryGrid));
        OnPropertyChanged(nameof(ShowDirectPassthroughHelp));
        OnPropertyChanged(nameof(ShowCommonPhrasesAdjustTile));
        OnPropertyChanged(nameof(ShowCustomShortcutEditor));
        OnPropertyChanged(nameof(ShowOpenUrlEditor));
        OnPropertyChanged(nameof(ShowTextSnippetEditor));
        OnPropertyChanged(nameof(ShowCustomApplicationEditor));
        OnPropertyChanged(nameof(CustomShortcutDisplay));
        OnPropertyChanged(nameof(HasCustomShortcutValue));
        OnPropertyChanged(nameof(SelectedCustomApplication));
        OnPropertyChanged(nameof(HasSelectedCustomApplication));
        OnPropertyChanged(nameof(IsCustomAppNoneStrategy));
        OnPropertyChanged(nameof(IsCustomAppShortcutStrategy));
        OnPropertyChanged(nameof(HasCustomAppFocusShortcut));
        OnPropertyChanged(nameof(TriggerTimingHint));
        OnPropertyChanged(nameof(IsTriggerTimingWarning));
        OnPropertyChanged(nameof(HasTriggerTimingHint));
        OnPropertyChanged(nameof(MappingStatusText));
    }
}
