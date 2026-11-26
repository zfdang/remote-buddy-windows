// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace RemoteBuddy.Core.Models;

public enum CustomApplicationFocusStrategy
{
    None = 0,
    KeyboardShortcut = 1
}

public class CustomApplicationProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public CustomApplicationFocusStrategy FocusStrategy { get; set; } = CustomApplicationFocusStrategy.None;
    public string? FocusShortcut { get; set; }

    public CustomApplicationProfile Clone() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        ExecutablePath = ExecutablePath,
        FocusStrategy = FocusStrategy,
        FocusShortcut = FocusShortcut
    };
}
