// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.Core.Models;
using Xunit;

namespace RemoteBuddy.Tests;

public sealed class RemoteButtonGesturesTests
{
    private sealed class Fixture
    {
        public List<ButtonAction> Fired { get; } = new();
        public RemoteButtonGestures Gestures { get; }
        public ButtonMapping Mapping { get; } = new()
        {
            Button = RemoteButton.Up,
            SingleClick = new ConfiguredButtonAction { Action = ButtonAction.ArrowUp }
        };
        private readonly Dictionary<int, Action> _timers = new();
        private int _now;

        public Fixture()
        {
            Gestures = new RemoteButtonGestures((delay, action) =>
            {
                var deadline = _now + delay;
                _timers.Add(deadline, action);
                return () => _timers.Remove(deadline);
            }, assignment => Fired.Add(assignment.Action));
        }

        public void Down() => Gestures.HandleDown(Mapping);
        public void Up() => Gestures.HandleUp(Mapping.Button);
        public void Tap() { Down(); Up(); }

        public void Advance(int milliseconds)
        {
            int target = _now + milliseconds;
            while (_timers.Count > 0 && _timers.Keys.Min() <= target)
            {
                _now = _timers.Keys.Min();
                _timers.Remove(_now, out var action);
                action!();
            }
            _now = target;
        }
    }

    [Fact]
    public void UnambiguousClickFiresImmediatelyAndIgnoresDuplicateDown()
    {
        var fixture = new Fixture();
        fixture.Down();
        fixture.Down();
        Assert.Equal([ButtonAction.ArrowUp], fixture.Fired);
        fixture.Up();
        fixture.Tap();
        Assert.Equal([ButtonAction.ArrowUp, ButtonAction.ArrowUp], fixture.Fired);
    }

    [Fact]
    public void DoubleClickReplacesPendingSingleClick()
    {
        var fixture = new Fixture();
        fixture.Mapping.DoubleClick = new ConfiguredButtonAction { Action = ButtonAction.ScrollUp };
        fixture.Tap();
        fixture.Advance(299);
        Assert.Empty(fixture.Fired);
        fixture.Advance(1);
        Assert.Equal([ButtonAction.ArrowUp], fixture.Fired);

        fixture.Tap();
        fixture.Advance(100);
        fixture.Tap();
        fixture.Advance(300);
        Assert.Equal([ButtonAction.ArrowUp, ButtonAction.ScrollUp], fixture.Fired);
    }

    [Fact]
    public void LongPressFiresOnceAndSuppressesBothClicksOnRelease()
    {
        var fixture = new Fixture();
        fixture.Mapping.LongPress = new ConfiguredButtonAction { Action = ButtonAction.CommandReturn };
        fixture.Mapping.DoubleClick = new ConfiguredButtonAction { Action = ButtonAction.ScrollUp };
        fixture.Tap();
        fixture.Advance(100);
        fixture.Down();
        fixture.Advance(549);
        Assert.Empty(fixture.Fired);
        fixture.Advance(1);
        Assert.Equal([ButtonAction.CommandReturn], fixture.Fired);
        fixture.Up();
        fixture.Advance(500);
        Assert.Equal([ButtonAction.CommandReturn], fixture.Fired);
    }

    [Fact]
    public void HoldingRepeatWithDoubleClickCancelsTheDeferredClickAndStopsOnRelease()
    {
        var fixture = new Fixture();
        fixture.Mapping.LongPress = new ConfiguredButtonAction { Action = ButtonAction.RepeatSingleClick };
        fixture.Mapping.DoubleClick = new ConfiguredButtonAction { Action = ButtonAction.ScrollUp };
        fixture.Down();
        fixture.Advance(450);
        fixture.Up();
        fixture.Advance(500);
        Assert.Equal([ButtonAction.ArrowUp, ButtonAction.ArrowUp], fixture.Fired);

        fixture.Tap();
        fixture.Gestures.Reset();
        fixture.Advance(1000);
        Assert.Equal([ButtonAction.ArrowUp, ButtonAction.ArrowUp], fixture.Fired);
    }

    [Fact]
    public void DirectPassthroughIgnoresGestureRecognizer()
    {
        var fixture = new Fixture();
        fixture.Mapping.SingleClick = new ConfiguredButtonAction { Action = ButtonAction.DirectPassthrough };
        fixture.Mapping.DoubleClick = new ConfiguredButtonAction { Action = ButtonAction.ScrollUp };
        fixture.Tap();
        fixture.Advance(500);
        Assert.Empty(fixture.Fired);
    }

    [Fact]
    public void CancelledStaleCallbackDoesNotFireOrRescheduleRepeat()
    {
        var queued = new List<(Action Invoke, Action Cancel)>();
        var fired = new List<ButtonAction>();
        var gestures = new RemoteButtonGestures(
            (_, cb) =>
            {
                Action cancel = () => { };
                queued.Add((cb, cancel));
                return () => { };
            },
            a => fired.Add(a.Action));

        var mapping = new ButtonMapping
        {
            Button = RemoteButton.Down,
            SingleClick = new ConfiguredButtonAction { Action = ButtonAction.ArrowDown },
            LongPress = new ConfiguredButtonAction { Action = ButtonAction.RepeatSingleClick }
        };

        gestures.HandleDown(mapping);
        Assert.Single(fired);
        Assert.Single(queued);

        gestures.HandleUp(RemoteButton.Down);
        queued[0].Invoke();
        Assert.Single(fired);
        Assert.Single(queued);
    }

    [Fact]
    public void ReentrantReleaseDuringSingleClickFireDoesNotFireTwice()
    {
        RemoteButtonGestures? gestures = null;
        int fireCount = 0;
        var mapping = new ButtonMapping
        {
            Button = RemoteButton.Ok,
            SingleClick = new ConfiguredButtonAction { Action = ButtonAction.ReturnKey }
        };

        gestures = new RemoteButtonGestures(
            (_, _) => () => { },
            _ =>
            {
                fireCount++;
                gestures!.HandleUp(RemoteButton.Ok);
            });

        gestures.HandleDown(mapping);
        Assert.Equal(1, fireCount);
    }
}
