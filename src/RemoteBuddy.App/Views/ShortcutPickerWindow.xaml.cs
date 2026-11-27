// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Platform.Windows.Input;

namespace RemoteBuddy.App.Views;

public partial class ShortcutPickerWindow : Window
{
    private sealed record KeyDef(string Token, string Label, double WidthUnits = 1.0, bool IsModifier = false, string? Family = null);

    private static readonly SolidColorBrush KeyDefaultBg = Freeze(Color.FromRgb(0x2D, 0x30, 0x36));
    private static readonly SolidColorBrush KeySelectedBg = Freeze(Color.FromRgb(0x00, 0x7A, 0xCC));
    private static readonly SolidColorBrush KeyBorder = Freeze(Color.FromRgb(0x45, 0x4A, 0x54));
    private static readonly SolidColorBrush KeySelectedBorder = Freeze(Color.FromRgb(0x4D, 0xA6, 0xFF));
    private static readonly SolidColorBrush PrimaryText = Freeze(Color.FromRgb(0xFF, 0xFF, 0xFF));

    private static readonly KeyDef[][] Rows =
    [
        [
            new("Escape", "Esc", 1.2), new("F1", "F1"), new("F2", "F2"), new("F3", "F3"), new("F4", "F4"),
            new("F5", "F5"), new("F6", "F6"), new("F7", "F7"), new("F8", "F8"),
            new("F9", "F9"), new("F10", "F10"), new("F11", "F11"), new("F12", "F12"),
            new("Delete", "Del", 1.2)
        ],
        [
            new("`", "`"), new("1", "1"), new("2", "2"), new("3", "3"), new("4", "4"), new("5", "5"),
            new("6", "6"), new("7", "7"), new("8", "8"), new("9", "9"), new("0", "0"),
            new("-", "-"), new("=", "="), new("Backspace", "⌫", 1.8)
        ],
        [
            new("Tab", "Tab", 1.4), new("Q", "Q"), new("W", "W"), new("E", "E"), new("R", "R"),
            new("T", "T"), new("Y", "Y"), new("U", "U"), new("I", "I"), new("O", "O"), new("P", "P"),
            new("[", "["), new("]", "]"), new("\\", "\\", 1.4)
        ],
        [
            new("Home", "Home", 1.5), new("A", "A"), new("S", "S"), new("D", "D"), new("F", "F"),
            new("G", "G"), new("H", "H"), new("J", "J"), new("K", "K"), new("L", "L"),
            new(";", ";"), new("'", "'"), new("Enter", "Enter", 2.0)
        ],
        [
            new("LShift", "LShift", 2.1, IsModifier: true, Family: "Shift"),
            new("Z", "Z"), new("X", "X"), new("C", "C"), new("V", "V"), new("B", "B"), new("N", "N"), new("M", "M"),
            new(",", ","), new(".", "."), new("/", "/"),
            new("RShift", "RShift", 1.6, IsModifier: true, Family: "Shift"),
            new("Up", "↑")
        ],
        [
            new("LCtrl", "LCtrl", 1.3, IsModifier: true, Family: "Ctrl"),
            new("LWin", "Win", 1.2, IsModifier: true, Family: "Win"),
            new("LAlt", "LAlt", 1.2, IsModifier: true, Family: "Alt"),
            new("Space", "Space", 4.4),
            new("RAlt", "RAlt", 1.2, IsModifier: true, Family: "Alt"),
            new("RCtrl", "RCtrl", 1.3, IsModifier: true, Family: "Ctrl"),
            new("Left", "←"),
            new("Down", "↓"),
            new("Right", "→")
        ]
    ];

    private readonly VoiceShortcutRecorder _recorder = new();
    private readonly List<KeyDef> _selectedModifiers = [];
    private string? _selectedMainKey;
    private readonly List<(KeyDef Def, Button Button)> _keyButtons = [];

    public string? SelectedShortcut { get; private set; }

    public ShortcutPickerWindow(string? initialShortcut)
    {
        InitializeComponent();
        BuildKeyboard();
        LoadInitialShortcut(initialShortcut);
        RefreshSelectionState();
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void BuildKeyboard()
    {
        KeyboardRowsPanel.Children.Clear();
        _keyButtons.Clear();

        foreach (var row in Rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            for (int i = 0; i < row.Length; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(row[i].WidthUnits, GridUnitType.Star) });
                var def = row[i];
                var btn = new Button
                {
                    Content = def.Label,
                    Height = 34,
                    Margin = new Thickness(2),
                    Padding = new Thickness(4, 2, 4, 2),
                    FontSize = 12,
                    Foreground = PrimaryText,
                    Background = KeyDefaultBg,
                    BorderBrush = KeyBorder,
                    Cursor = Cursors.Hand
                };
                btn.Click += (_, _) => OnVirtualKeyClick(def);
                Grid.SetColumn(btn, i);
                grid.Children.Add(btn);
                _keyButtons.Add((def, btn));
            }
            KeyboardRowsPanel.Children.Add(grid);
        }
    }

    private void LoadInitialShortcut(string? shortcut)
    {
        _selectedModifiers.Clear();
        _selectedMainKey = null;
        if (string.IsNullOrWhiteSpace(shortcut)) return;

        foreach (string part in shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var mod = FindModifierToken(part);
            if (mod != null)
            {
                _selectedModifiers.RemoveAll(m => m.Family == mod.Family);
                _selectedModifiers.Add(mod);
            }
            else
            {
                _selectedMainKey = NormalizeMainToken(part);
            }
        }
    }

    private static KeyDef? FindModifierToken(string token)
    {
        string upper = token.ToUpperInvariant();
        string? normalized = upper switch
        {
            "CTRL" or "CONTROL" or "LCTRL" or "LEFTCTRL" or "LEFTCONTROL" => "LCtrl",
            "RCTRL" or "RIGHTCTRL" or "RIGHTCONTROL" => "RCtrl",
            "SHIFT" or "LSHIFT" or "LEFTSHIFT" => "LShift",
            "RSHIFT" or "RIGHTSHIFT" => "RShift",
            "ALT" or "LALT" or "LEFTALT" => "LAlt",
            "RALT" or "RIGHTALT" => "RAlt",
            "WIN" or "WINDOWS" or "LWIN" or "LEFTWIN" or "RWIN" or "RIGHTWIN" => "LWin",
            _ => null
        };
        if (normalized == null) return null;
        return Rows.SelectMany(r => r).FirstOrDefault(k => k.IsModifier && k.Token == normalized);
    }

    private static string NormalizeMainToken(string token) => token.ToUpperInvariant() switch
    {
        "RETURN" or "ENTER" => "Enter",
        "ESC" or "ESCAPE" => "Escape",
        "BACK" or "BACKSPACE" => "Backspace",
        "DEL" or "DELETE" => "Delete",
        _ => token.Length == 1 ? token.ToUpperInvariant() : token
    };

    private void OnVirtualKeyClick(KeyDef def)
    {
        RecordButton.IsChecked = false;
        if (def.IsModifier)
        {
            if (_selectedModifiers.Any(m => m.Token == def.Token))
            {
                _selectedModifiers.RemoveAll(m => m.Token == def.Token);
            }
            else
            {
                _selectedModifiers.RemoveAll(m => m.Family == def.Family);
                _selectedModifiers.Add(def);
            }
        }
        else
        {
            _selectedMainKey = string.Equals(_selectedMainKey, def.Token, StringComparison.OrdinalIgnoreCase)
                ? null
                : def.Token;
        }
        RefreshSelectionState();
    }

    private string? BuildCurrentCandidate()
    {
        var tokens = new List<string>();
        foreach (var mod in _selectedModifiers)
        {
            if (_selectedMainKey == null && _selectedModifiers.Count == 1)
            {
                tokens.Add(mod.Token);
            }
            else
            {
                tokens.Add(mod.Token switch
                {
                    "LCtrl" => "Ctrl",
                    "LShift" => "Shift",
                    "LAlt" => "Alt",
                    "LWin" => "Win",
                    _ => mod.Token
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(_selectedMainKey))
        {
            tokens.Add(_selectedMainKey);
        }

        return tokens.Count == 0 ? null : string.Join("+", tokens);
    }

    private void RefreshSelectionState()
    {
        foreach (var (def, button) in _keyButtons)
        {
            bool selected = def.IsModifier
                ? _selectedModifiers.Any(m => m.Token == def.Token)
                : string.Equals(_selectedMainKey, def.Token, StringComparison.OrdinalIgnoreCase);

            button.Background = selected ? KeySelectedBg : KeyDefaultBg;
            button.BorderBrush = selected ? KeySelectedBorder : KeyBorder;
        }

        string? candidate = BuildCurrentCandidate();
        bool valid = !string.IsNullOrWhiteSpace(candidate) && NativeInputSender.TryParseShortcut(candidate, out _, out _);
        PreviewText.Text = candidate ?? "请选择按键组合";
        ConfirmButton.IsEnabled = valid;

        if (!string.IsNullOrWhiteSpace(candidate) && !valid)
        {
            ValidationHintText.Text = "请选择有效的快捷键组合。";
            ValidationHintText.Visibility = Visibility.Visible;
        }
        else
        {
            ValidationHintText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnRecordChecked(object sender, RoutedEventArgs e)
    {
        _recorder.Reset();
        RecordButton.Focus();
    }

    private void OnRecordKeyDown(object sender, KeyEventArgs e)
    {
        if (RecordButton.IsChecked != true) return;
        e.Handled = true;
        if (_recorder.OnKeyDown(e.Key, e.SystemKey, Keyboard.Modifiers, out string? captured, out bool cancelled))
        {
            if (cancelled)
            {
                RecordButton.IsChecked = false;
            }
            else if (!string.IsNullOrEmpty(captured))
            {
                LoadInitialShortcut(captured);
                RecordButton.IsChecked = false;
                RefreshSelectionState();
            }
        }
    }

    private void OnRecordKeyUp(object sender, KeyEventArgs e)
    {
        if (RecordButton.IsChecked != true) return;
        e.Handled = true;
        if (_recorder.OnKeyUp(e.Key, e.SystemKey, out string? captured) && !string.IsNullOrEmpty(captured))
        {
            LoadInitialShortcut(captured);
            RecordButton.IsChecked = false;
            RefreshSelectionState();
        }
    }

    private void OnRecordLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _recorder.Reset();
        RecordButton.IsChecked = false;
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _selectedModifiers.Clear();
        _selectedMainKey = null;
        RefreshSelectionState();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        string? candidate = BuildCurrentCandidate();
        if (string.IsNullOrWhiteSpace(candidate) || !NativeInputSender.TryParseShortcut(candidate, out _, out _))
        {
            return;
        }
        SelectedShortcut = candidate;
        DialogResult = true;
        Close();
    }
}
