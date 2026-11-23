// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Windows.Input;
using RemoteBuddy.Platform.Windows.Input;

namespace RemoteBuddy.App.ViewModels;

/// <summary>
/// Captures keyboard shortcuts including standalone Left/Right modifier keys (e.g., RAlt, RCtrl)
/// and multi-modifier combinations for voice-input activation.
/// </summary>
public sealed class VoiceShortcutRecorder
{
    private readonly List<Key> _heldModifiers = [];
    private bool _completedNonModifier;

    public void Reset()
    {
        _heldModifiers.Clear();
        _completedNonModifier = false;
    }

    public bool OnKeyDown(
        Key rawKey,
        Key systemKey,
        ModifierKeys activeModifiers,
        out string? capturedShortcut,
        out bool cancelled)
    {
        capturedShortcut = null;
        cancelled = false;

        Key key = rawKey == Key.System ? systemKey : rawKey;
        if (key == Key.Escape && activeModifiers == ModifierKeys.None && _heldModifiers.Count == 0)
        {
            cancelled = true;
            Reset();
            return true;
        }

        if (IsModifierKey(key))
        {
            if (!_heldModifiers.Contains(key))
            {
                _heldModifiers.Add(key);
            }
            _completedNonModifier = false;
            return true;
        }

        var parts = new List<string>();
        foreach (Key held in _heldModifiers)
        {
            parts.Add(FormatComboModifier(held));
        }

        if (activeModifiers.HasFlag(ModifierKeys.Control) && !HasAnyModifier(Key.LeftCtrl, Key.RightCtrl))
        {
            parts.Add("Ctrl");
        }
        if (activeModifiers.HasFlag(ModifierKeys.Alt) && !HasAnyModifier(Key.LeftAlt, Key.RightAlt))
        {
            parts.Add("Alt");
        }
        if (activeModifiers.HasFlag(ModifierKeys.Shift) && !HasAnyModifier(Key.LeftShift, Key.RightShift))
        {
            parts.Add("Shift");
        }
        if (activeModifiers.HasFlag(ModifierKeys.Windows) && !HasAnyModifier(Key.LWin, Key.RWin))
        {
            parts.Add("Win");
        }

        parts.Add(FormatRecordedKey(key));
        string candidate = string.Join("+", parts);
        if (!NativeInputSender.TryParseShortcut(candidate, out _, out _))
        {
            return false;
        }

        _completedNonModifier = true;
        _heldModifiers.Clear();
        capturedShortcut = candidate;
        return true;
    }

    public bool OnKeyUp(Key rawKey, Key systemKey, out string? capturedShortcut)
    {
        capturedShortcut = null;
        Key key = rawKey == Key.System ? systemKey : rawKey;

        if (_completedNonModifier || !IsModifierKey(key) || _heldModifiers.Count == 0)
        {
            return false;
        }

        string candidate = _heldModifiers.Count == 1
            ? FormatSingleModifier(_heldModifiers[0])
            : string.Join("+", _heldModifiers.Select(FormatComboModifier));

        Reset();
        if (!NativeInputSender.TryParseShortcut(candidate, out _, out _))
        {
            return false;
        }

        capturedShortcut = candidate;
        return true;
    }

    private bool HasAnyModifier(Key left, Key right) =>
        _heldModifiers.Contains(left) || _heldModifiers.Contains(right);

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;

    private static string FormatSingleModifier(Key key) => key switch
    {
        Key.RightAlt => "RAlt",
        Key.LeftAlt => "LAlt",
        Key.RightCtrl => "RCtrl",
        Key.LeftCtrl => "LCtrl",
        Key.RightShift => "RShift",
        Key.LeftShift => "LShift",
        Key.RWin => "RWin",
        Key.LWin => "LWin",
        _ => key.ToString()
    };

    private static string FormatComboModifier(Key key) => key switch
    {
        Key.RightCtrl => "RCtrl",
        Key.LeftCtrl => "Ctrl",
        Key.RightAlt => "RAlt",
        Key.LeftAlt => "Alt",
        Key.RightShift => "RShift",
        Key.LeftShift => "Shift",
        Key.RWin => "RWin",
        Key.LWin => "Win",
        _ => key.ToString()
    };

    private static string FormatRecordedKey(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9) return ((int)(key - Key.D0)).ToString();
        return key switch
        {
            Key.Return => "Enter",
            Key.Back => "Backspace",
            Key.Prior => "PageUp",
            Key.Next => "PageDown",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            _ => key.ToString()
        };
    }
}
