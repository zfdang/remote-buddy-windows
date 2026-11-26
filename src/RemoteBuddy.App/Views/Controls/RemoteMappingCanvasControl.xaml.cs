// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Models;

namespace RemoteBuddy.App.Views.Controls;

public partial class RemoteMappingCanvasControl : UserControl
{
    private const double CardHeight = 68;

    private static readonly Brush CardDefaultBg = Freeze(Color.FromRgb(0x27, 0x29, 0x2E));
    private static readonly Brush CardSelectedBg = Freeze(Color.FromArgb(0x28, 0x00, 0x7A, 0xCC));
    private static readonly Brush CardActiveBg = Freeze(Color.FromArgb(0x30, 0xFF, 0x9F, 0x43));
    private static readonly Brush CardDefaultBorder = Freeze(Color.FromRgb(0x3D, 0x41, 0x49));
    private static readonly Brush CardSelectedBorder = Freeze(Color.FromRgb(0x33, 0x99, 0xFF));
    private static readonly Brush CardActiveBorder = Freeze(Color.FromRgb(0xFF, 0x9F, 0x43));
    private static readonly Brush ChipDefaultBg = Freeze(Color.FromRgb(0x1D, 0x1F, 0x24));
    private static readonly Brush ChipSelectedBg = Freeze(Color.FromArgb(0x38, 0x00, 0x7A, 0xCC));
    private static readonly Brush ChipSelectedBorder = Freeze(Color.FromRgb(0x4D, 0xA6, 0xFF));
    private static readonly Brush LineDefaultBrush = Freeze(Color.FromArgb(0x88, 0x8A, 0x92, 0xA0));
    private static readonly Brush LineSelectedBrush = Freeze(Color.FromRgb(0x33, 0x99, 0xFF));
    private static readonly Brush AnchorActiveBrush = Freeze(Color.FromRgb(0xFF, 0x9F, 0x43));
    private static readonly Brush PrimaryText = Freeze(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush SecondaryText = Freeze(Color.FromRgb(0xA6, 0xAD, 0xB9));
    private static readonly Brush VoiceBadgeBg = Freeze(Color.FromArgb(0x2A, 0x8A, 0x92, 0xA0));
    private static readonly Brush VoiceBadgeActiveBg = Freeze(Color.FromArgb(0x35, 0xFF, 0x9F, 0x43));

    private MappingViewModel? _vm;

    public RemoteMappingCanvasControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) => RebuildCanvas();
        Loaded += (_, _) => RebuildCanvas();
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.CanvasStateChanged -= OnCanvasStateChanged;
        }
        _vm = DataContext as MappingViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.CanvasStateChanged += OnCanvasStateChanged;
        }
        RebuildCanvas();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MappingViewModel.SelectedDevice) or
            nameof(MappingViewModel.SelectedMapping) or
            nameof(MappingViewModel.EditingButton) or
            nameof(MappingViewModel.EditingTrigger) or
            nameof(MappingViewModel.MappingRevision) or
            nameof(MappingViewModel.IsVoiceActive))
        {
            RebuildCanvas();
        }
    }

    private void OnCanvasStateChanged() => RebuildCanvas();

    private void RebuildCanvas()
    {
        RootCanvas.Children.Clear();
        if (_vm?.SelectedDevice == null) return;

        var model = _vm.SelectedDevice.ModelType;
        double canvasHeight = RemoteMappingLayout.GetCanvasHeight(model);
        double canvasWidth = ActualWidth > 200 ? ActualWidth : 780;
        var remoteSize = RemoteMappingLayout.GetRemoteSize(model);
        double cardWidth = RemoteMappingLayout.GetCardWidth(canvasWidth, remoteSize);

        RootCanvas.Height = canvasHeight;
        Height = canvasHeight;

        var selectedButton = _vm.SelectedMapping?.Button ?? RemoteButton.Ok;
        var activeButtons = _vm.ActiveButtons;
        bool voiceActive = _vm.IsVoiceActive;

        // 1. Center remote illustration
        double remoteLeft = canvasWidth / 2.0 - remoteSize.Width / 2.0;
        double remoteTop = canvasHeight / 2.0 - remoteSize.Height / 2.0;
        var imageBorder = new Border
        {
            Width = remoteSize.Width,
            Height = remoteSize.Height,
            CornerRadius = new CornerRadius(14),
            ClipToBounds = true,
            Child = new Image
            {
                Source = new BitmapImage(new Uri(RemoteMappingLayout.GetIllustrationUri(model))),
                Stretch = Stretch.Uniform
            }
        };
        Canvas.SetLeft(imageBorder, remoteLeft);
        Canvas.SetTop(imageBorder, remoteTop);
        RootCanvas.Children.Add(imageBorder);

        // 2. Bezier connection lines & arrowheads
        var placements = RemoteMappingLayout.GetPlacements(model);
        foreach (var placement in placements)
        {
            var start = RemoteMappingLayout.GetRemotePoint(placement.Anchor, canvasWidth, canvasHeight, remoteSize);
            var end = RemoteMappingLayout.GetCardEdgePoint(placement.Side, placement.TargetY, canvasWidth, canvasHeight, cardWidth);
            bool isSelected = selectedButton == placement.Button;
            bool isActive = activeButtons.Contains(placement.Button);
            DrawConnection(start, end, placement.Side, isSelected, isActive);
        }

        var voice = RemoteMappingLayout.GetVoicePlacement(model);
        var voiceStart = RemoteMappingLayout.GetRemotePoint(voice.Anchor, canvasWidth, canvasHeight, remoteSize);
        var voiceEnd = RemoteMappingLayout.GetCardEdgePoint(voice.Side, voice.TargetY, canvasWidth, canvasHeight, cardWidth);
        DrawConnection(voiceStart, voiceEnd, voice.Side, voiceActive, voiceActive);

        // 3. Button cards
        foreach (var placement in placements)
        {
            var card = BuildButtonCard(placement.Button, cardWidth, selectedButton == placement.Button, activeButtons.Contains(placement.Button));
            double left = placement.Side == RemoteMappingSide.Left ? 0 : canvasWidth - cardWidth;
            double top = canvasHeight * placement.TargetY - CardHeight / 2.0;
            Canvas.SetLeft(card, left);
            Canvas.SetTop(card, top);
            RootCanvas.Children.Add(card);
        }

        // 4. Voice card
        var voiceCard = BuildVoiceCard(cardWidth, voiceActive);
        double voiceLeft = voice.Side == RemoteMappingSide.Left ? 0 : canvasWidth - cardWidth;
        double voiceTop = canvasHeight * voice.TargetY - CardHeight / 2.0;
        Canvas.SetLeft(voiceCard, voiceLeft);
        Canvas.SetTop(voiceCard, voiceTop);
        RootCanvas.Children.Add(voiceCard);
    }

    private void DrawConnection(Point start, Point end, RemoteMappingSide side, bool selected, bool showsAnchor)
    {
        var arrowTip = RemoteMappingLayout.GetArrowTip(end, side);
        var (c1, c2) = RemoteMappingLayout.GetControlPoints(start, arrowTip, side);
        Brush strokeBrush = showsAnchor ? AnchorActiveBrush : (selected ? LineSelectedBrush : LineDefaultBrush);

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new BezierSegment(c1, c2, arrowTip, isStroked: true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        var path = new Path
        {
            Data = geometry,
            Stroke = strokeBrush,
            StrokeThickness = (selected || showsAnchor) ? 1.7 : 1.1,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        RootCanvas.Children.Add(path);

        if (showsAnchor)
        {
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = AnchorActiveBrush,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(dot, start.X - 4);
            Canvas.SetTop(dot, start.Y - 4);
            RootCanvas.Children.Add(dot);
        }

        double dir = side == RemoteMappingSide.Left ? -1 : 1;
        var arrow = new Polygon
        {
            Fill = strokeBrush,
            IsHitTestVisible = false,
            Points =
            [
                arrowTip,
                new Point(arrowTip.X - dir * 6, arrowTip.Y - 4),
                new Point(arrowTip.X - dir * 6, arrowTip.Y + 4)
            ]
        };
        RootCanvas.Children.Add(arrow);
    }

    private Border BuildButtonCard(RemoteButton button, double width, bool selected, bool active)
    {
        var border = new Border
        {
            Width = width,
            Height = CardHeight,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 6, 9, 6),
            Background = active ? CardActiveBg : (selected ? CardSelectedBg : CardDefaultBg),
            BorderBrush = active ? CardActiveBorder : (selected ? CardSelectedBorder : CardDefaultBorder),
            BorderThickness = new Thickness(active || selected ? 1.5 : 1),
            Cursor = Cursors.Hand
        };
        border.MouseLeftButtonDown += (_, _) => _vm?.SelectButton(button);

        var root = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        header.Children.Add(new TextBlock
        {
            Text = GetButtonGlyph(button),
            FontSize = 12,
            Foreground = active ? AnchorActiveBrush : (selected ? ChipSelectedBorder : SecondaryText),
            Width = 18,
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(new TextBlock
        {
            Text = MappingItemViewModel.GetButtonDisplayName(button),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = PrimaryText,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        root.Children.Add(header);

        var triggersGrid = new Grid();
        triggersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        triggersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        triggersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        triggersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        triggersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var orderedTriggers = new[]
        {
            (Trigger: ButtonTrigger.SingleClick, Title: "单击", Col: 0),
            (Trigger: ButtonTrigger.DoubleClick, Title: "双击", Col: 2),
            (Trigger: ButtonTrigger.LongPress, Title: "长按", Col: 4)
        };

        foreach (var (trigger, title, col) in orderedTriggers)
        {
            bool isEditingThis = _vm?.EditingButton == button && _vm?.EditingTrigger == trigger;
            string summary = _vm?.GetActionSummary(button, trigger) ?? "未设置";
            string detailed = _vm?.GetActionSummary(button, trigger, detailed: true) ?? summary;

            var chipBtn = new Button
            {
                Padding = new Thickness(4, 3, 4, 3),
                Background = isEditingThis ? ChipSelectedBg : ChipDefaultBg,
                BorderBrush = isEditingThis ? ChipSelectedBorder : Brushes.Transparent,
                BorderThickness = new Thickness(isEditingThis ? 1 : 0),
                Cursor = Cursors.Hand,
                ToolTip = $"{MappingItemViewModel.GetButtonDisplayName(button)} · {title}\n{detailed}"
            };
            chipBtn.Click += (_, e) =>
            {
                e.Handled = true;
                _vm?.BeginEdit(button, trigger);
            };

            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 10.5,
                Foreground = SecondaryText,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            stack.Children.Add(new TextBlock
            {
                Text = summary,
                FontSize = 11,
                FontWeight = trigger == ButtonTrigger.SingleClick ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = summary == "未设置" ? SecondaryText : PrimaryText,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = Math.Max(40, (width - 36) / 3.0),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            chipBtn.Content = stack;
            Grid.SetColumn(chipBtn, col);
            triggersGrid.Children.Add(chipBtn);
        }

        root.Children.Add(triggersGrid);
        border.Child = root;
        return border;
    }

    private Border BuildVoiceCard(double width, bool voiceActive)
    {
        var border = new Border
        {
            Width = width,
            Height = CardHeight,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 6, 9, 6),
            Background = voiceActive ? CardActiveBg : CardDefaultBg,
            BorderBrush = voiceActive ? CardActiveBorder : CardDefaultBorder,
            BorderThickness = new Thickness(voiceActive ? 1.5 : 1),
            Cursor = Cursors.Hand
        };
        border.MouseLeftButtonDown += (_, _) => _vm?.OpenVoiceIntegrationCommand.Execute(null);

        var root = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var badge = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 2, 7, 2),
            Background = voiceActive ? VoiceBadgeActiveBg : VoiceBadgeBg,
            Child = new TextBlock
            {
                Text = "语音触发键",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = voiceActive ? AnchorActiveBrush : SecondaryText
            }
        };
        DockPanel.SetDock(badge, Dock.Right);
        header.Children.Add(badge);

        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = "🎙️",
            FontSize = 12,
            Width = 18,
            VerticalAlignment = VerticalAlignment.Center
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "语音键",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = PrimaryText,
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(titleStack);
        root.Children.Add(header);

        root.Children.Add(new TextBlock
        {
            Text = "请在输入法集成向导中设置语音热键和点按模式。",
            FontSize = 11,
            Foreground = SecondaryText,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        border.Child = root;
        return border;
    }

    private static string GetButtonGlyph(RemoteButton button) => button switch
    {
        RemoteButton.Power => "⏻",
        RemoteButton.Up => "⌃",
        RemoteButton.Down => "⌄",
        RemoteButton.Left => "‹",
        RemoteButton.Right => "›",
        RemoteButton.Ok => "◎",
        RemoteButton.Back => "↩",
        RemoteButton.Home => "⌂",
        RemoteButton.Menu => "≡",
        RemoteButton.VolumeUp => "🔊",
        RemoteButton.VolumeDown => "🔉",
        RemoteButton.LiveTv => "📺",
        RemoteButton.Mute => "🔇",
        RemoteButton.YouTube => "▶",
        RemoteButton.Netflix => "N",
        RemoteButton.Input => "⏏",
        RemoteButton.Custom => "★",
        _ => "•"
    };
}
