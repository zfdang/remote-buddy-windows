// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Config;
using RemoteBuddy.Core.Models;

namespace RemoteBuddy.App.Views;

public partial class CommonPhraseOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    private readonly Action<RemoteButton> _onInsert;
    private readonly Action _onCloseRequested;
    private nint _targetWindowHandle;
    private IntPtr _winEventHook;
    private WinEventDelegate? _winEventDelegate;

    public CommonPhraseOverlayWindow(
        RemoteModelType? model,
        CommonPhraseStore store,
        nint targetWindowHandle,
        Action<RemoteButton> onInsert,
        Action onCloseRequested)
    {
        _onInsert = onInsert;
        _onCloseRequested = onCloseRequested;
        InitializeComponent();
        BindContent(model, store, targetWindowHandle);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            nint style = GetWindowLongPtr(hwnd, GwlExStyle);
            SetWindowLongPtr(hwnd, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
            EnsureForegroundHook();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_winEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
        _winEventDelegate = null;
        base.OnClosed(e);
    }

    public void BindContent(RemoteModelType? model, CommonPhraseStore store, nint targetWindowHandle)
    {
        _targetWindowHandle = targetWindowHandle;
        EnsureForegroundHook();

        bool showBasePad = CommonPhraseStore.SupportsBaseButtons(model);
        DpadBorder.Visibility = showBasePad ? Visibility.Visible : Visibility.Collapsed;
        NativeKeysNotice.Visibility = showBasePad ? Visibility.Collapsed : Visibility.Visible;

        BtnUp.Tag = RemoteButton.Up;
        BtnDown.Tag = RemoteButton.Down;
        BtnLeft.Tag = RemoteButton.Left;
        BtnRight.Tag = RemoteButton.Right;
        BtnOk.Tag = RemoteButton.Ok;

        LabelUp.Text = store.GetPhrase(RemoteButton.Up)?.Label ?? "未分配";
        LabelDown.Text = store.GetPhrase(RemoteButton.Down)?.Label ?? "未分配";
        LabelLeft.Text = store.GetPhrase(RemoteButton.Left)?.Label ?? "未分配";
        LabelRight.Text = store.GetPhrase(RemoteButton.Right)?.Label ?? "未分配";
        LabelOk.Text = store.GetPhrase(RemoteButton.Ok)?.Label ?? "未分配";

        ExtraRowsPanel.Children.Clear();
        foreach (var row in CommonPhraseStore.GetExtraButtonRows(model))
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int col = 0; col < 2 && col < row.Length; col++)
            {
                if (row[col] is not { } btn) continue;
                var button = new Button
                {
                    Tag = btn,
                    Padding = new Thickness(8, 6, 8, 6),
                    Background = new SolidColorBrush(Color.FromRgb(0x28, 0x2B, 0x32)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
                    Foreground = Brushes.White
                };
                button.Click += OnButtonClick;

                var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                stack.Children.Add(new TextBlock
                {
                    Text = CommonPhrasesViewModel.GetShortButtonGlyph(btn),
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                stack.Children.Add(new TextBlock
                {
                    Text = store.GetPhrase(btn)?.Label ?? "未分配",
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD5, 0xDF)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 110,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                button.Content = stack;
                Grid.SetColumn(button, col == 0 ? 0 : 2);
                grid.Children.Add(button);
            }

            ExtraRowsPanel.Children.Add(grid);
        }
    }

    private void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RemoteButton btn })
        {
            _onInsert(btn);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => _onCloseRequested();

    private void EnsureForegroundHook()
    {
        if (_winEventHook != IntPtr.Zero || _targetWindowHandle == 0) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        _winEventDelegate ??= OnForegroundWindowChanged;
        _winEventHook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _winEventDelegate,
            0,
            0,
            WineventOutOfContext | WineventSkipOwnProcess);
    }

    private void OnForegroundWindowChanged(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (hwnd != IntPtr.Zero && _targetWindowHandle != 0 && hwnd != _targetWindowHandle)
        {
            Dispatcher.BeginInvoke(() => _onCloseRequested());
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(IntPtr hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
}
