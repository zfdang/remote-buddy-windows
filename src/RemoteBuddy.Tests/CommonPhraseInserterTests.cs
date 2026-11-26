// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.App.Services;
using Xunit;

namespace RemoteBuddy.Tests;

public class CommonPhraseInserterTests
{
    private sealed class FakeClipboardAccess : IClipboardAccess
    {
        public string? CurrentText { get; set; }
        public uint Sequence { get; private set; } = 1;
        public List<string> WrittenTexts { get; } = [];
        public int RestoreCalls { get; private set; }

        public bool TryWriteTextWithSnapshot(string text, out string? previousText, out bool hadPreviousText, out uint leaseSequence)
        {
            previousText = CurrentText;
            hadPreviousText = CurrentText != null;
            CurrentText = text;
            Sequence++;
            leaseSequence = Sequence;
            WrittenTexts.Add(text);
            return true;
        }

        public bool TryRestoreTextIfUnchanged(string? previousText, bool hadPreviousText, uint expectedSequence)
        {
            RestoreCalls++;
            if (Sequence != expectedSequence) return false;
            CurrentText = hadPreviousText ? previousText : null;
            Sequence++;
            return true;
        }

        public void SimulateExternalWrite(string text)
        {
            CurrentText = text;
            Sequence++;
        }
    }

    [Theory]
    [InlineData("a\nb", "a\r\nb")]
    [InlineData("a\r\nb", "a\r\nb")]
    [InlineData("a\rb\nc", "a\r\nb\r\nc")]
    [InlineData("", "")]
    public void NormalizeClipboardText_NormalizesLineEndingsToWindowsCrLf(string input, string expected)
    {
        Assert.Equal(expected, WindowsCommonPhraseInserter.NormalizeClipboardText(input));
    }

    [Fact]
    public void InsertText_MultiLinePhrase_PastesViaClipboardAndRestoresOriginalText()
    {
        var clipboard = new FakeClipboardAccess { CurrentText = "previous clipboard" };
        int pasteCalls = 0;
        Action? pendingRestore = null;
        var target = new CommonPhraseTarget(new nint(0x1234), 42, new nint(0x5678), "1.2.3");

        var inserter = new WindowsCommonPhraseInserter(
            clipboard,
            () => { pasteCalls++; return true; },
            () => target,
            _ => true,
            (_, callback) => pendingRestore = callback);

        bool ok = inserter.InsertText("第一行\n第二行", target);

        Assert.True(ok);
        Assert.Equal(1, pasteCalls);
        Assert.Equal(["第一行\r\n第二行"], clipboard.WrittenTexts);
        Assert.Equal("第一行\r\n第二行", clipboard.CurrentText);
        Assert.NotNull(pendingRestore);

        pendingRestore!();
        Assert.Equal("previous clipboard", clipboard.CurrentText);
    }

    [Fact]
    public void InsertText_WhenClipboardChangesBeforeRestore_DoesNotOverwriteNewClipboard()
    {
        var clipboard = new FakeClipboardAccess { CurrentText = "previous" };
        Action? pendingRestore = null;

        var inserter = new WindowsCommonPhraseInserter(
            clipboard,
            () => true,
            () => null,
            _ => true,
            (_, callback) => pendingRestore = callback);

        Assert.True(inserter.InsertText("multi\nline"));
        clipboard.SimulateExternalWrite("user copied something else");

        pendingRestore!();
        Assert.Equal("user copied something else", clipboard.CurrentText);
    }

    [Fact]
    public void InsertText_WhenTargetChangesBeforePaste_RollsBackClipboardImmediatelyAndSkipsPaste()
    {
        var clipboard = new FakeClipboardAccess { CurrentText = "safe backup" };
        int pasteCalls = 0;
        Action? pendingRestore = null;
        int targetChecks = 0;
        var target = new CommonPhraseTarget(new nint(0x1234), 42, new nint(0x5678), null);

        var inserter = new WindowsCommonPhraseInserter(
            clipboard,
            () => { pasteCalls++; return true; },
            () => target,
            // Pass pre-check (call 1), fail re-check right before Ctrl+V (call 2)
            _ => ++targetChecks == 1,
            (_, callback) => pendingRestore = callback);

        bool ok = inserter.InsertText("line1\nline2", target);

        Assert.False(ok);
        Assert.Equal(0, pasteCalls);
        Assert.Null(pendingRestore);
        Assert.Equal("safe backup", clipboard.CurrentText);
    }

    [Fact]
    public void InsertText_WhenPasteFails_RollsBackClipboardImmediately()
    {
        var clipboard = new FakeClipboardAccess { CurrentText = "safe backup" };
        Action? pendingRestore = null;

        var inserter = new WindowsCommonPhraseInserter(
            clipboard,
            () => false,
            () => null,
            _ => true,
            (_, callback) => pendingRestore = callback);

        Assert.False(inserter.InsertText("line1\nline2"));
        Assert.Null(pendingRestore);
        Assert.Equal("safe backup", clipboard.CurrentText);
    }
}
