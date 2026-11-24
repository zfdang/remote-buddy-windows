// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace RemoteBuddy.Core.Models;

// Input edges and scheduled callbacks are delivered on the same thread.
public sealed class RemoteButtonGestures
{
    private const int DoubleClickMilliseconds = 300;
    private const int LongPressMilliseconds = 550;
    private const int RepeatStartMilliseconds = 350;
    private const int RepeatIntervalMilliseconds = 100;
    private const int MaximumRepeatMilliseconds = 60_000;

    private readonly Func<int, Action, Action> _schedule;
    private readonly Action<RemoteButton, ButtonAssignment> _execute;

    public RemoteButtonGestures(Func<int, Action, Action> schedule, Action<ButtonAssignment> execute)
        : this(schedule, (_, a) => execute(a)) { }

    public RemoteButtonGestures(Func<int, Action, Action> schedule, Action<RemoteButton, ButtonAssignment> execute)
    {
        _schedule = schedule;
        _execute = execute;
    }

    private sealed class Press(ButtonMapping mapping)
    {
        public RemoteButton Button => mapping.Button;
        public ButtonAssignment SingleClick { get; } = mapping.GetAssignment(ButtonTrigger.SingleClick);
        public ButtonAssignment LongPress { get; } = mapping.GetAssignment(ButtonTrigger.LongPress);
        public ButtonAssignment DoubleClick { get; } = mapping.GetAssignment(ButtonTrigger.DoubleClick);
        public bool IsDown { get; set; } = true;
        public bool IsSecondPress { get; set; }
        public bool FiredOnPress { get; set; }
        public Action? CancelTimer { get; set; }
        public bool Repeats => LongPress.Action == ButtonActionType.ContinuousRepeat && SingleClick.Action != ButtonActionType.None;
        public bool HasLongPress => LongPress.Action is not (ButtonActionType.None or ButtonActionType.ContinuousRepeat);
        public bool HasDoubleClick => DoubleClick.Action is not (ButtonActionType.None or ButtonActionType.ContinuousRepeat);

        public void StopTimer()
        {
            CancelTimer?.Invoke();
            CancelTimer = null;
        }
    }

    private readonly Dictionary<RemoteButton, Press> _presses = new();

    public void HandleDown(ButtonMapping mapping)
    {
        var button = mapping.Button;
        if (_presses.TryGetValue(button, out var press))
        {
            if (press.IsDown) return;
            press.StopTimer();
            press.IsDown = true;
            press.IsSecondPress = true;
        }
        else
        {
            press = new Press(mapping);
            _presses.Add(button, press);
            if (!press.HasDoubleClick && !press.HasLongPress)
            {
                press.FiredOnPress = true;
                Fire(button, press.SingleClick);
            }
        }

        if (press.Repeats)
            Schedule(press, RepeatStartMilliseconds, () => Repeat(press, RepeatStartMilliseconds));
        else if (press.HasLongPress)
            Schedule(press, LongPressMilliseconds, () =>
            {
                press.FiredOnPress = true;
                Fire(button, press.LongPress);
            });
    }

    public void HandleUp(RemoteButton button)
    {
        if (!_presses.TryGetValue(button, out var press) || !press.IsDown) return;

        press.IsDown = false;
        press.StopTimer();
        if (!press.FiredOnPress && !press.IsSecondPress && press.HasDoubleClick)
        {
            Schedule(press, DoubleClickMilliseconds, () =>
            {
                _presses.Remove(button);
                Fire(button, press.SingleClick);
            });
            return;
        }

        _presses.Remove(button);
        if (!press.FiredOnPress)
            Fire(button, press.IsSecondPress ? press.DoubleClick : press.SingleClick);
    }

    public void Reset()
    {
        foreach (var press in _presses.Values) press.StopTimer();
        _presses.Clear();
    }

    private void Repeat(Press press, int elapsed)
    {
        if (!press.IsDown || !_presses.TryGetValue(press.Button, out var current) || !ReferenceEquals(current, press))
            return;

        press.FiredOnPress = true;
        Fire(press.Button, press.SingleClick);
        int next = elapsed + RepeatIntervalMilliseconds;
        if (next <= MaximumRepeatMilliseconds &&
            press.IsDown &&
            _presses.TryGetValue(press.Button, out current) &&
            ReferenceEquals(current, press))
        {
            Schedule(press, RepeatIntervalMilliseconds, () => Repeat(press, next));
        }
    }

    private void Schedule(Press press, int delay, Action callback)
    {
        bool cancelled = false;
        Action? innerCancel = null;
        innerCancel = _schedule(delay, () =>
        {
            if (cancelled) return;
            press.CancelTimer = null;
            callback();
        });
        press.CancelTimer = () =>
        {
            cancelled = true;
            innerCancel?.Invoke();
        };
    }

    private void Fire(RemoteButton button, ButtonAssignment assignment)
    {
        if (assignment.Action != ButtonActionType.None) _execute(button, assignment);
    }
}
