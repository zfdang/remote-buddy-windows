// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

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

public readonly record struct ButtonAssignment(
    ButtonActionType Action, string? Shortcut = null, MediaCommand Media = MediaCommand.None, string? Text = null);

public class ButtonMapping
{
    public RemoteButton Button { get; set; }
    public ButtonActionType ClickAction { get; set; } = ButtonActionType.None;
    public string? ClickShortcut { get; set; }
    public string? ClickText { get; set; }
    public MediaCommand ClickMedia { get; set; } = MediaCommand.None;

    public ButtonActionType DoubleClickAction { get; set; } = ButtonActionType.None;
    public string? DoubleClickShortcut { get; set; }
    public string? DoubleClickText { get; set; }
    public MediaCommand DoubleClickMedia { get; set; } = MediaCommand.None;

    public ButtonActionType LongPressAction { get; set; } = ButtonActionType.None;
    public string? LongPressShortcut { get; set; }
    public string? LongPressText { get; set; }
    public MediaCommand LongPressMedia { get; set; } = MediaCommand.None;

    public ButtonAssignment GetAssignment(ButtonTrigger trigger) => trigger switch
    {
        ButtonTrigger.SingleClick => new(ClickAction, ClickShortcut, ClickMedia, ClickText),
        ButtonTrigger.LongPress => new(LongPressAction, LongPressShortcut, LongPressMedia, LongPressText),
        ButtonTrigger.DoubleClick => new(DoubleClickAction, DoubleClickShortcut, DoubleClickMedia, DoubleClickText),
        _ => throw new ArgumentOutOfRangeException(nameof(trigger))
    };

    public void SetAssignment(ButtonTrigger trigger, ButtonAssignment assignment)
    {
        var (action, shortcut, media, text) = assignment;
        switch (trigger)
        {
            case ButtonTrigger.SingleClick:
                (ClickAction, ClickShortcut, ClickMedia, ClickText) = (action, shortcut, media, text);
                break;
            case ButtonTrigger.LongPress:
                (LongPressAction, LongPressShortcut, LongPressMedia, LongPressText) = (action, shortcut, media, text);
                break;
            case ButtonTrigger.DoubleClick:
                (DoubleClickAction, DoubleClickShortcut, DoubleClickMedia, DoubleClickText) = (action, shortcut, media, text);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(trigger));
        }
    }
}
