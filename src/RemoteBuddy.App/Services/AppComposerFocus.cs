// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using RemoteBuddy.Core.Diagnostics;

namespace RemoteBuddy.App.Services;

internal readonly record struct AppFocusTarget(nint Window, uint ProcessId, string IntegrationId);

internal enum AppFocusResult
{
    ExistingInput,
    ComposerFocused,
    ComposerUnavailable,
    ForegroundChanged,
    TimedOut
}

internal interface IAppComposerFocus
{
    AppFocusTarget? Capture(IReadOnlyDictionary<string, bool>? enabledIntegrations);
    bool IsForeground(AppFocusTarget target);
    Task<AppFocusResult> FocusAsync(AppFocusTarget target, Func<bool> isVoiceLive, CancellationToken cancellationToken);
}

internal sealed class AppComposerFocus : IAppComposerFocus
{
    internal static readonly TimeSpan FocusTimeout = TimeSpan.FromMilliseconds(350);
    private const int MaximumElements = 180;
    private int _findingComposer;

    private static readonly Dictionary<string, string> ProcessIntegrations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WeChat"] = "wechat",
        ["Weixin"] = "wechat",
        ["Antigravity"] = "antigravity",
        ["Antigravity Agent"] = "antigravity",
        ["ChatGPT"] = "chatgpt",
        ["Claude"] = "claude",
        ["DeepSeek"] = "deepseek",
        ["DeepSeek Harness"] = "deepseek",
        ["WorkBuddy"] = "workbuddy"
    };

    public AppFocusTarget? Capture(IReadOnlyDictionary<string, bool>? enabledIntegrations)
    {
        if (enabledIntegrations == null || !enabledIntegrations.Values.Any(enabled => enabled)) return null;
        nint window = GetForegroundWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out uint processId) == 0 ||
            processId == Environment.ProcessId)
        {
            return null;
        }
        try
        {
            using var process = Process.GetProcessById((int)processId);
            string? id = GetIntegrationId(process.ProcessName);
            return id != null && enabledIntegrations.GetValueOrDefault(id)
                ? new AppFocusTarget(window, processId, id)
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal static string? GetIntegrationId(string processName) => ProcessIntegrations.GetValueOrDefault(processName);

    public bool IsForeground(AppFocusTarget target) =>
        GetForegroundWindow() == target.Window &&
        GetWindowThreadProcessId(target.Window, out uint processId) != 0 &&
        processId == target.ProcessId;

    public Task<AppFocusResult> FocusAsync(AppFocusTarget target, Func<bool> isVoiceLive, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _findingComposer, 1, 0) != 0)
            return Task.FromResult(AppFocusResult.ComposerUnavailable);

        // UI Automation providers can block. Keep their work off the UI thread and
        // allow only one outstanding traversal, including after a timeout.
        return Task.Run(() =>
        {
            try
            {
                return Focus(target, isVoiceLive, cancellationToken);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or
                InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            {
                AppLogger.Info($"Application integration provider error: app={target.IntegrationId}, type={ex.GetType().Name}, code=0x{ex.HResult:X8}", "AppComposerFocus");
                return AppFocusResult.ComposerUnavailable;
            }
            finally
            {
                Volatile.Write(ref _findingComposer, 0);
            }
        });
    }

    private AppFocusResult Focus(AppFocusTarget target, Func<bool> isVoiceLive, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        AppFocusResult? CheckTarget()
        {
            if (cancellationToken.IsCancellationRequested || elapsed.Elapsed >= FocusTimeout || !isVoiceLive())
                return AppFocusResult.TimedOut;
            return IsForeground(target) ? null : AppFocusResult.ForegroundChanged;
        }

        if (CheckTarget() is { } beforeScan) return beforeScan;
        if (HasEditableFocus()) return AppFocusResult.ExistingInput;

        var root = AutomationElement.FromHandle(target.Window);
        var walker = TreeWalker.ControlViewWalker;
        bool isWorkBuddy = target.IntegrationId == "workbuddy";
        var editType = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
        Condition inputType = isWorkBuddy ? editType : new OrCondition(
            editType,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox),
            new AndCondition(
                new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true),
                new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document))));
        var candidates = root.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.IsEnabledProperty, true),
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
            inputType));
        AppLogger.Info($"Application integration input controls: app={target.IntegrationId}, candidates={candidates.Count}, elapsedMs={elapsed.ElapsedMilliseconds}", "AppComposerFocus");
        AutomationElement? composer = null;
        int bestScore = 0;
        bool ambiguous = false;
        int matchingControls = 0;
        for (int index = 0; index < candidates.Count && index < MaximumElements; index++)
        {
            if (CheckTarget() is { } duringScan) return duringScan;
            var element = candidates[index];
            var item = element.Current;
            if (item.IsPassword) continue;
            var type = item.ControlType;
            if (type != ControlType.Edit && type != ControlType.ComboBox && !IsEditable(element, type))
                continue;
            int score = ComposerScore(target.IntegrationId, item.AutomationId, item.Name);
            if (score == 0 && isWorkBuddy && string.IsNullOrWhiteSpace(item.Name) &&
                CheckTarget() == null && HasWorkBuddyComposerControls(element, walker))
            {
                score = 1;
            }
            if (score == 0) continue;
            matchingControls++;
            if (matchingControls <= 4)
            {
                AppLogger.Info($"Application integration candidate: app={target.IntegrationId}, type={type.ProgrammaticName}, focusable={item.IsKeyboardFocusable}, score={score}", "AppComposerFocus");
            }
            if (!IsEditable(element, type)) continue;
            if (score > bestScore)
            {
                bestScore = score;
                composer = element;
                ambiguous = false;
            }
            else if (score == bestScore)
            {
                ambiguous = true;
            }
        }

        if (composer == null || ambiguous)
        {
            AppLogger.Info($"Application integration search: app={target.IntegrationId}, candidates={candidates.Count}, matching={matchingControls}, ambiguous={ambiguous}", "AppComposerFocus");
            return AppFocusResult.ComposerUnavailable;
        }
        if (CheckTarget() is { } beforeFocus) return beforeFocus;
        if (HasEditableFocus()) return AppFocusResult.ExistingInput;
        if (CheckTarget() is { } afterFocusCheck) return afterFocusCheck;
        composer.SetFocus();
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (CheckTarget() is { } afterFocus) return afterFocus;
            if (composer.Current.HasKeyboardFocus || FocusIsWithin(composer))
                return AppFocusResult.ComposerFocused;
            if (attempt < 3) Thread.Sleep(15);
        }
        AppLogger.Info($"Application integration focus unconfirmed: app={target.IntegrationId}", "AppComposerFocus");
        return AppFocusResult.ComposerUnavailable;
    }

    private static bool FocusIsWithin(AutomationElement composer)
    {
        var focus = AutomationElement.FocusedElement;
        for (int depth = 0; focus != null && depth < 8; depth++)
        {
            if (Automation.Compare(focus, composer)) return true;
            focus = TreeWalker.RawViewWalker.GetParent(focus);
        }
        return false;
    }

    private static bool HasEditableFocus()
    {
        var focused = AutomationElement.FocusedElement;
        if (focused == null) return false;
        var item = focused.Current;
        return item.IsEnabled && IsPotentialInput(item.ControlType) && IsEditable(focused, item.ControlType);
    }

    private static bool IsPotentialInput(ControlType type) =>
        type == ControlType.Edit || type == ControlType.Document || type == ControlType.Custom ||
        type == ControlType.ComboBox;

    private static bool IsEditable(AutomationElement element, ControlType type) =>
        element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern value
            ? !value.Current.IsReadOnly
            : type == ControlType.Edit;

    internal static int ComposerScore(string id, string automationId, string name)
    {
        string key = Normalize(automationId);
        string label = Normalize(name);
        if (IsExcluded(key) || IsExcluded(label)) return 0;

        if (key is "composer" or "chatinput" or "chatinputfield" or "messageinput" or "prompteditor")
            return 2;
        if (id == "chatgpt" && key == "prompttextarea") return 2;

        bool common = label is "messageinput" or "chatinput" or "promptinput" or "输入消息" or "消息输入" or "对话输入框";
        bool specific = id switch
        {
            "wechat" => label is "编辑消息",
            "antigravity" => label is "askagent" or "messageagent" or "agentinput" or "向agent提问",
            "chatgpt" => label is "askanything" or "messagechatgpt" or "询问任何问题",
            "claude" => label is "replytoclaude" or "messageclaude" or "回复claude",
            "deepseek" => label is "messagedeepseek" or "给deepseek发送消息" ||
                label.StartsWith("描述你想要构建的内容", StringComparison.Ordinal),
            "workbuddy" => label is "messageworkbuddy" or "给workbuddy发送消息",
            _ => false
        };
        return common || specific ? 1 : 0;
    }

    private static bool HasWorkBuddyComposerControls(AutomationElement editor, TreeWalker walker)
    {
        var container = walker.GetParent(editor);
        if (container == null) return false;
        var cache = new CacheRequest { AutomationElementMode = AutomationElementMode.None };
        cache.Add(AutomationElement.NameProperty);
        using (cache.Activate())
        {
            var buttons = container.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            bool voiceInput = false;
            bool send = false;
            foreach (AutomationElement button in buttons)
            {
                string label = Normalize(button.Cached.Name);
                voiceInput |= label.StartsWith("语音输入", StringComparison.Ordinal) ||
                    label.StartsWith("voiceinput", StringComparison.Ordinal);
                send |= label is "发送" or "发送消息" or "send" or "sendmessage";
                if (voiceInput && send) return true;
            }
        }
        return false;
    }

    private static string Normalize(string text) =>
        string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static bool IsExcluded(string text) =>
        text.Contains("search", StringComparison.Ordinal) || text.Contains("password", StringComparison.Ordinal) ||
        text.Contains("terminal", StringComparison.Ordinal) || text.Contains("codeeditor", StringComparison.Ordinal) ||
        text.Contains("搜索", StringComparison.Ordinal) || text.Contains("密码", StringComparison.Ordinal) ||
        text.Contains("终端", StringComparison.Ordinal);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
