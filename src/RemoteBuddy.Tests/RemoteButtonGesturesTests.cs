// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.Core.Models;
using Xunit;

namespace RemoteBuddy.Tests;

public sealed class RemoteButtonGesturesTests
{
    private sealed class Fixture
    {
        public List<string?> Fired { get; } = new();
        public RemoteButtonGestures Gestures { get; }
        public ButtonMapping Mapping { get; } = new()
        {
            Button = RemoteButton.Up, ClickAction = ButtonActionType.KeyboardShortcut, ClickShortcut = "Up"
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
            }, assignment => Fired.Add(assignment.Shortcut));
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
        Assert.Equal(["Up"], fixture.Fired);
        fixture.Up();
        fixture.Tap();
        Assert.Equal(["Up", "Up"], fixture.Fired);
    }

    [Fact]
    public void DoubleClickReplacesPendingSingleClick()
    {
        var fixture = new Fixture();
        fixture.Mapping.DoubleClickAction = ButtonActionType.KeyboardShortcut;
        fixture.Mapping.DoubleClickShortcut = "Home";
        fixture.Tap();
        fixture.Advance(299);
        Assert.Empty(fixture.Fired);
        fixture.Advance(1);
        Assert.Equal(["Up"], fixture.Fired);

        fixture.Tap();
        fixture.Advance(100);
        fixture.Tap();
        fixture.Advance(300);
        Assert.Equal(["Up", "Home"], fixture.Fired);
    }

    [Fact]
    public void LongPressFiresOnceAndSuppressesBothClicksOnRelease()
    {
        var fixture = new Fixture();
        fixture.Mapping.LongPressAction = ButtonActionType.KeyboardShortcut;
        fixture.Mapping.LongPressShortcut = "End";
        fixture.Mapping.DoubleClickAction = ButtonActionType.KeyboardShortcut;
        fixture.Mapping.DoubleClickShortcut = "Home";
        fixture.Tap();
        fixture.Advance(100);
        fixture.Down();
        fixture.Advance(549);
        Assert.Empty(fixture.Fired);
        fixture.Advance(1);
        Assert.Equal(["End"], fixture.Fired);
        fixture.Up();
        fixture.Advance(500);
        Assert.Equal(["End"], fixture.Fired);
    }

    [Fact]
    public void HoldingRepeatWithDoubleClickCancelsTheDeferredClickAndStopsOnRelease()
    {
        var fixture = new Fixture();
        fixture.Mapping.LongPressAction = ButtonActionType.ContinuousRepeat;
        fixture.Mapping.DoubleClickAction = ButtonActionType.KeyboardShortcut;
        fixture.Mapping.DoubleClickShortcut = "Home";
        fixture.Down();
        fixture.Advance(450);
        fixture.Up();
        fixture.Advance(500);
        Assert.Equal(["Up", "Up"], fixture.Fired);

        fixture.Tap();
        fixture.Gestures.Reset();
        fixture.Advance(1000);
        Assert.Equal(["Up", "Up"], fixture.Fired);
    }

    [Fact]
    public void CancelledStaleCallbackDoesNotFireOrRescheduleRepeat()
    {
        var queued = new List<(Action Invoke, Action Cancel)>();
        var fired = new List<string?>();
        var gestures = new RemoteButtonGestures(
            (_, cb) =>
            {
                Action cancel = () => { };
                queued.Add((cb, cancel));
                return () => { }; // Simulate host timer whose cancel doesn't pull an already-queued callback
            },
            a => fired.Add(a.Shortcut));

        var mapping = new ButtonMapping
        {
            Button = RemoteButton.Down,
            ClickAction = ButtonActionType.KeyboardShortcut,
            ClickShortcut = "Down",
            LongPressAction = ButtonActionType.ContinuousRepeat
        };

        gestures.HandleDown(mapping);
        Assert.Single(fired);
        Assert.Single(queued);

        gestures.HandleUp(RemoteButton.Down);
        // Fire stale callback that was already queued before HandleUp cancelled it
        queued[0].Invoke();
        Assert.Single(fired);
        Assert.Single(queued); // Must not reschedule repeat after release
    }

    [Fact]
    public void ReentrantReleaseDuringSingleClickFireDoesNotFireTwice()
    {
        RemoteButtonGestures? gestures = null;
        int fireCount = 0;
        var mapping = new ButtonMapping
        {
            Button = RemoteButton.Ok,
            ClickAction = ButtonActionType.KeyboardShortcut,
            ClickShortcut = "Return"
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
