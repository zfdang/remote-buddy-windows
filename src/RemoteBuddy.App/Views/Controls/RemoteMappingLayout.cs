// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Windows;
using RemoteBuddy.Core.Models;

namespace RemoteBuddy.App.Views.Controls;

public enum RemoteMappingSide
{
    Left,
    Right
}

public sealed record RemoteMappingPlacement(
    RemoteButton Button,
    RemoteMappingSide Side,
    Point Anchor,
    double TargetY);

public static class RemoteMappingLayout
{
    public const double ArrowCardGap = 7;

    public static readonly IReadOnlyList<RemoteMappingPlacement> XiaomiPlacements =
    [
        new(RemoteButton.Power, RemoteMappingSide.Left, new Point(0.386, 0.099), 0.08),
        new(RemoteButton.Up, RemoteMappingSide.Left, new Point(0.502, 0.179), 0.23),
        new(RemoteButton.Left, RemoteMappingSide.Left, new Point(0.362, 0.246), 0.38),
        new(RemoteButton.Back, RemoteMappingSide.Left, new Point(0.406, 0.389), 0.53),
        new(RemoteButton.Home, RemoteMappingSide.Left, new Point(0.406, 0.479), 0.68),
        new(RemoteButton.Menu, RemoteMappingSide.Left, new Point(0.406, 0.569), 0.83),

        new(RemoteButton.Right, RemoteMappingSide.Right, new Point(0.638, 0.246), 0.215),
        new(RemoteButton.Ok, RemoteMappingSide.Right, new Point(0.502, 0.246), 0.36),
        new(RemoteButton.Down, RemoteMappingSide.Right, new Point(0.502, 0.317), 0.505),
        new(RemoteButton.VolumeUp, RemoteMappingSide.Right, new Point(0.604, 0.390), 0.65),
        new(RemoteButton.VolumeDown, RemoteMappingSide.Right, new Point(0.604, 0.480), 0.795),
        new(RemoteButton.LiveTv, RemoteMappingSide.Right, new Point(0.604, 0.569), 0.94)
    ];

    public static readonly IReadOnlyList<RemoteMappingPlacement> G9N9NPlacements =
    [
        new(RemoteButton.Up, RemoteMappingSide.Left, new Point(0.50, 0.072), 0.065),
        new(RemoteButton.Left, RemoteMappingSide.Left, new Point(0.15, 0.185), 0.21),
        new(RemoteButton.Back, RemoteMappingSide.Left, new Point(0.27, 0.38), 0.355),
        new(RemoteButton.Home, RemoteMappingSide.Left, new Point(0.27, 0.505), 0.50),
        new(RemoteButton.YouTube, RemoteMappingSide.Left, new Point(0.27, 0.625), 0.645),
        new(RemoteButton.VolumeUp, RemoteMappingSide.Left, new Point(0.95, 0.25), 0.79),
        new(RemoteButton.Power, RemoteMappingSide.Left, new Point(0.27, 0.74), 0.935),

        new(RemoteButton.Right, RemoteMappingSide.Right, new Point(0.83, 0.185), 0.195),
        new(RemoteButton.Ok, RemoteMappingSide.Right, new Point(0.50, 0.185), 0.32),
        new(RemoteButton.Down, RemoteMappingSide.Right, new Point(0.50, 0.295), 0.445),
        new(RemoteButton.Mute, RemoteMappingSide.Right, new Point(0.71, 0.505), 0.57),
        new(RemoteButton.Netflix, RemoteMappingSide.Right, new Point(0.71, 0.625), 0.695),
        new(RemoteButton.VolumeDown, RemoteMappingSide.Right, new Point(0.95, 0.30), 0.82),
        new(RemoteButton.Input, RemoteMappingSide.Right, new Point(0.71, 0.74), 0.94)
    ];

    public static readonly IReadOnlyList<RemoteMappingPlacement> Gy3lePlacements =
    [
        new(RemoteButton.Up, RemoteMappingSide.Left, new Point(0.50, 0.10), 0.07),
        new(RemoteButton.Left, RemoteMappingSide.Left, new Point(0.29, 0.17), 0.21),
        new(RemoteButton.Back, RemoteMappingSide.Left, new Point(0.33, 0.355), 0.35),
        new(RemoteButton.Mute, RemoteMappingSide.Left, new Point(0.33, 0.575), 0.63),
        new(RemoteButton.YouTube, RemoteMappingSide.Left, new Point(0.33, 0.685), 0.77),
        new(RemoteButton.Power, RemoteMappingSide.Left, new Point(0.33, 0.805), 0.91),

        new(RemoteButton.Right, RemoteMappingSide.Right, new Point(0.71, 0.17), 0.065),
        new(RemoteButton.Ok, RemoteMappingSide.Right, new Point(0.50, 0.17), 0.188),
        new(RemoteButton.Down, RemoteMappingSide.Right, new Point(0.50, 0.245), 0.311),
        new(RemoteButton.Home, RemoteMappingSide.Right, new Point(0.67, 0.355), 0.434),
        new(RemoteButton.VolumeUp, RemoteMappingSide.Right, new Point(0.67, 0.465), 0.557),
        new(RemoteButton.VolumeDown, RemoteMappingSide.Right, new Point(0.67, 0.575), 0.680),
        new(RemoteButton.Netflix, RemoteMappingSide.Right, new Point(0.67, 0.685), 0.803),
        new(RemoteButton.Custom, RemoteMappingSide.Right, new Point(0.67, 0.805), 0.926)
    ];

    public static readonly IReadOnlyList<RemoteMappingPlacement> T1Placements =
    [
        new(RemoteButton.Back, RemoteMappingSide.Left, new Point(0.284, 0.374), 0.36),
        new(RemoteButton.Mute, RemoteMappingSide.Left, new Point(0.291, 0.494), 0.56),

        new(RemoteButton.Power, RemoteMappingSide.Right, new Point(0.763, 0.070), 0.08),
        new(RemoteButton.VolumeUp, RemoteMappingSide.Right, new Point(0.985, 0.192), 0.22),
        new(RemoteButton.VolumeDown, RemoteMappingSide.Right, new Point(0.985, 0.256), 0.37),
        new(RemoteButton.Home, RemoteMappingSide.Right, new Point(0.690, 0.495), 0.76),
        new(RemoteButton.Menu, RemoteMappingSide.Right, new Point(0.690, 0.604), 0.91)
    ];

    public static double GetCanvasHeight(RemoteModelType model) => model switch
    {
        RemoteModelType.GoogleG9N9N or RemoteModelType.GoogleGY3LE or RemoteModelType.T1Remote => 670,
        _ => 570
    };

    public static Size GetRemoteSize(RemoteModelType model) => model switch
    {
        RemoteModelType.GoogleG9N9N or RemoteModelType.GoogleGY3LE => new Size(156, 468),
        RemoteModelType.T1Remote => new Size(156, 156.0 * 1443.0 / 480.0),
        _ => new Size(218, 218.0 * 1030.0 / 508.0)
    };

    public static IReadOnlyList<RemoteMappingPlacement> GetPlacements(RemoteModelType model) => model switch
    {
        RemoteModelType.GoogleG9N9N => G9N9NPlacements,
        RemoteModelType.GoogleGY3LE => Gy3lePlacements,
        RemoteModelType.T1Remote => T1Placements,
        _ => XiaomiPlacements
    };

    public static (RemoteMappingSide Side, Point Anchor, double TargetY) GetVoicePlacement(RemoteModelType model) => model switch
    {
        RemoteModelType.GoogleG9N9N => (RemoteMappingSide.Right, new Point(0.71, 0.38), 0.065),
        RemoteModelType.GoogleGY3LE => (RemoteMappingSide.Left, new Point(0.33, 0.465), 0.49),
        RemoteModelType.T1Remote => (RemoteMappingSide.Right, new Point(0.689, 0.373), 0.57),
        _ => (RemoteMappingSide.Right, new Point(0.630, 0.099), 0.07)
    };

    public static string GetIllustrationUri(RemoteModelType model) => model switch
    {
        RemoteModelType.GoogleG9N9N => "pack://application:,,,/Resources/ChromecastG9N9NPhoto.png",
        RemoteModelType.GoogleGY3LE => "pack://application:,,,/Resources/GoogleStreamerGY3LEIllustration.png",
        RemoteModelType.T1Remote => "pack://application:,,,/Resources/T1RemoteIllustration.png",
        _ => "pack://application:,,,/Resources/BuddyRemoteIllustration.png"
    };

    public static double GetCardWidth(double canvasWidth, Size remoteSize) =>
        Math.Clamp((canvasWidth - remoteSize.Width - 84) / 2.0, 195, 290);

    public static Point GetRemotePoint(Point anchor, double canvasWidth, double canvasHeight, Size remoteSize)
    {
        double originX = canvasWidth / 2.0 - remoteSize.Width / 2.0;
        double originY = canvasHeight / 2.0 - remoteSize.Height / 2.0;
        return new Point(originX + remoteSize.Width * anchor.X, originY + remoteSize.Height * anchor.Y);
    }

    public static Point GetCardEdgePoint(RemoteMappingSide side, double targetY, double canvasWidth, double canvasHeight, double cardWidth) =>
        new(side == RemoteMappingSide.Left ? cardWidth : canvasWidth - cardWidth, canvasHeight * targetY);

    public static Point GetArrowTip(Point cardEdge, RemoteMappingSide side)
    {
        double dir = side == RemoteMappingSide.Left ? -1 : 1;
        return new Point(cardEdge.X - dir * ArrowCardGap, cardEdge.Y);
    }

    public static (Point Control1, Point Control2) GetControlPoints(Point start, Point end, RemoteMappingSide side)
    {
        double dir = side == RemoteMappingSide.Left ? -1 : 1;
        double distance = Math.Min(70, Math.Max(34, Math.Abs(end.X - start.X) * 0.58));
        double endpointDistance = Math.Min(42, Math.Max(24, distance * 0.6));
        return (
            new Point(start.X + dir * distance, start.Y),
            new Point(end.X - dir * endpointDistance, end.Y));
    }
}
