// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace RemoteBuddy.Core.Models;

public enum RemoteButton
{
    None = 0,
    Up,
    Down,
    Left,
    Right,
    Ok,
    Back,
    Home,
    Menu,
    Power,
    VolumeUp,
    VolumeDown,
    Mute,
    Voice,
    YouTube,
    Netflix,
    Input,
    PlayPause,
    LiveTv,
    Custom
}

public enum ButtonActionType
{
    None = 0,
    MediaControl,
    KeyboardShortcut,
    TextSnippet,
    ContinuousRepeat
}

public enum MediaCommand
{
    None = 0,
    PlayPause,
    VolumeUp,
    VolumeDown,
    Mute,
    NextTrack,
    PreviousTrack
}

public enum ButtonTrigger
{
    SingleClick,
    LongPress,
    DoubleClick
}

public class ButtonMapping
{
    public RemoteButton Button { get; set; }

    public ConfiguredButtonAction? SingleClick { get; set; }
    public ConfiguredButtonAction? DoubleClick { get; set; }
    public ConfiguredButtonAction? LongPress { get; set; }

    // Legacy v1 fields retained strictly for backward-compatible migration when reading older config.json files.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ButtonActionType? ClickAction { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClickShortcut { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClickText { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MediaCommand? ClickMedia { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ButtonActionType? DoubleClickAction { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DoubleClickShortcut { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DoubleClickText { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MediaCommand? DoubleClickMedia { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ButtonActionType? LongPressAction { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LongPressShortcut { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LongPressText { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MediaCommand? LongPressMedia { get; set; }

    public ConfiguredButtonAction GetConfiguredAction(ButtonTrigger trigger) => trigger switch
    {
        ButtonTrigger.SingleClick => SingleClick ?? ConfiguredButtonAction.Disabled,
        ButtonTrigger.DoubleClick => DoubleClick ?? ConfiguredButtonAction.Disabled,
        ButtonTrigger.LongPress => LongPress ?? ConfiguredButtonAction.Disabled,
        _ => throw new ArgumentOutOfRangeException(nameof(trigger))
    };

    public void SetConfiguredAction(ButtonTrigger trigger, ConfiguredButtonAction configured)
    {
        var copy = configured.Clone();
        switch (trigger)
        {
            case ButtonTrigger.SingleClick:
                SingleClick = copy;
                break;
            case ButtonTrigger.DoubleClick:
                DoubleClick = copy;
                break;
            case ButtonTrigger.LongPress:
                LongPress = copy;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(trigger));
        }
    }

    public ConfiguredButtonAction GetEffectiveAction(ButtonTrigger trigger)
    {
        var single = GetConfiguredAction(ButtonTrigger.SingleClick);
        if (trigger != ButtonTrigger.SingleClick && single.Action == ButtonAction.DirectPassthrough)
        {
            return ConfiguredButtonAction.Disabled;
        }

        var configured = GetConfiguredAction(trigger);
        if (configured.Action == ButtonAction.DirectPassthrough &&
            (!ButtonActionCatalog.IsDirectional(Button) || trigger != ButtonTrigger.SingleClick))
        {
            return ConfiguredButtonAction.Disabled;
        }

        if (configured.Action == ButtonAction.RepeatSingleClick &&
            (trigger != ButtonTrigger.LongPress || single.Action == ButtonAction.OpenUrl))
        {
            return ConfiguredButtonAction.Disabled;
        }

        return configured;
    }

    public void MigrateLegacyIfNeeded()
    {
        if (SingleClick == null && (ClickAction != null || ClickShortcut != null || ClickText != null || ClickMedia != null))
        {
            SingleClick = ConvertLegacy(ClickAction ?? ButtonActionType.None, ClickShortcut, ClickMedia ?? MediaCommand.None, ClickText);
        }

        if (DoubleClick == null && (DoubleClickAction != null || DoubleClickShortcut != null || DoubleClickText != null || DoubleClickMedia != null))
        {
            DoubleClick = ConvertLegacy(DoubleClickAction ?? ButtonActionType.None, DoubleClickShortcut, DoubleClickMedia ?? MediaCommand.None, DoubleClickText);
        }

        if (LongPress == null && (LongPressAction != null || LongPressShortcut != null || LongPressText != null || LongPressMedia != null))
        {
            LongPress = ConvertLegacy(LongPressAction ?? ButtonActionType.None, LongPressShortcut, LongPressMedia ?? MediaCommand.None, LongPressText);
        }

        SingleClick ??= ConfiguredButtonAction.Disabled;
        DoubleClick ??= ConfiguredButtonAction.Disabled;
        LongPress ??= ConfiguredButtonAction.Disabled;

        ClearLegacyFields();
    }

    public void ClearLegacyFields()
    {
        ClickAction = null;
        ClickShortcut = null;
        ClickText = null;
        ClickMedia = null;
        DoubleClickAction = null;
        DoubleClickShortcut = null;
        DoubleClickText = null;
        DoubleClickMedia = null;
        LongPressAction = null;
        LongPressShortcut = null;
        LongPressText = null;
        LongPressMedia = null;
    }

    public ButtonMapping Clone() => new()
    {
        Button = Button,
        SingleClick = GetConfiguredAction(ButtonTrigger.SingleClick).Clone(),
        DoubleClick = GetConfiguredAction(ButtonTrigger.DoubleClick).Clone(),
        LongPress = GetConfiguredAction(ButtonTrigger.LongPress).Clone()
    };

    private static ConfiguredButtonAction ConvertLegacy(
        ButtonActionType actionType,
        string? shortcut,
        MediaCommand media,
        string? text)
    {
        switch (actionType)
        {
            case ButtonActionType.None:
                return ConfiguredButtonAction.Disabled;
            case ButtonActionType.ContinuousRepeat:
                return new ConfiguredButtonAction { Action = ButtonAction.RepeatSingleClick };
            case ButtonActionType.MediaControl:
                return new ConfiguredButtonAction { Action = ButtonActionCatalog.FromLegacyMedia(media) };
            case ButtonActionType.TextSnippet:
                return string.IsNullOrWhiteSpace(text)
                    ? ConfiguredButtonAction.Disabled
                    : new ConfiguredButtonAction { Action = ButtonAction.TextSnippet, Text = text };
            case ButtonActionType.KeyboardShortcut:
                if (string.IsNullOrWhiteSpace(shortcut)) return ConfiguredButtonAction.Disabled;
                var preset = ButtonActionCatalog.TryMatchPresetFromLegacyShortcut(shortcut);
                return preset is { } matched
                    ? new ConfiguredButtonAction { Action = matched }
                    : new ConfiguredButtonAction { Action = ButtonAction.CustomShortcut, Shortcut = shortcut.Trim() };
            default:
                return ConfiguredButtonAction.Disabled;
        }
    }
}
