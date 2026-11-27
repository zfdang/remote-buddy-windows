// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.App.Services;
using Xunit;

namespace RemoteBuddy.Tests;

public class CommonPhraseInserterTests
{
    private sealed class FakeClipboardAccess : IClipboardAccess
    {
        public ClipboardSnapshot? CurrentSnapshot { get; set; }
        public bool FailSnapshot { get; set; }
        public uint Sequence { get; private set; } = 1;
        public List<string> WrittenTexts { get; } = [];
        public int RestoreCalls { get; private set; }

        public void SetText(string text) =>
            CurrentSnapshot = new ClipboardSnapshot([new ClipboardFormatEntry(13, System.Text.Encoding.Unicode.GetBytes(text + "\0"))]);

        public string? CurrentText
        {
            get
            {
                var entry = CurrentSnapshot?.Entries.FirstOrDefault(e => e.Format == 13);
                return entry == null ? null : System.Text.Encoding.Unicode.GetString(entry.Data).TrimEnd('\0');
            }
            set
            {
                if (value == null) CurrentSnapshot = null;
                else SetText(value);
            }
        }

        public bool TryWriteTextWithSnapshot(string text, out ClipboardSnapshot? snapshot, out uint leaseSequence)
        {
            snapshot = null;
            leaseSequence = 0;
            if (FailSnapshot) return false;

            snapshot = CurrentSnapshot;
            SetText(text);
            Sequence++;
            leaseSequence = Sequence;
            WrittenTexts.Add(text);
            return true;
        }

        public bool TryRestoreSnapshotIfUnchanged(ClipboardSnapshot? snapshot, uint expectedSequence)
        {
            RestoreCalls++;
            if (Sequence != expectedSequence) return false;
            CurrentSnapshot = snapshot;
            Sequence++;
            return true;
        }

        public void SimulateExternalWrite(string text)
        {
            SetText(text);
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

    [Fact]
    public void InsertText_PreservesMultiFormatClipboardSnapshot_AndAbortsWhenSnapshotFails()
    {
        var original = new ClipboardSnapshot(
        [
            new ClipboardFormatEntry(15, [1, 2, 3, 4]), // CF_HDROP file list
            new ClipboardFormatEntry(8, [5, 6, 7, 8])   // CF_DIB image
        ]);
        var clipboard = new FakeClipboardAccess { CurrentSnapshot = original };
        Action? pendingRestore = null;

        var inserter = new WindowsCommonPhraseInserter(
            clipboard,
            () => true,
            () => null,
            _ => true,
            (_, callback) => pendingRestore = callback);

        Assert.True(inserter.InsertText("hello"));
        Assert.Equal("hello", clipboard.CurrentText);
        pendingRestore!();
        Assert.Same(original, clipboard.CurrentSnapshot);

        clipboard.FailSnapshot = true;
        Assert.False(inserter.InsertText("blocked"));
        Assert.Same(original, clipboard.CurrentSnapshot);
    }

    [Fact]
    public void Win32ClipboardAccess_PreservesDibAndFileDropFormats_RejectsUncopyableGdiOrNullOwner()
    {
        // Rejects null owner HWND without touching the clipboard
        var noOwner = new Win32ClipboardAccess(() => 0);
        Assert.False(noOwner.TryWriteTextWithSnapshot("test", out _, out _));

        // Image with CF_DIB + synthesized CF_BITMAP succeeds by capturing CF_DIB and skipping CF_BITMAP
        Assert.True(Win32ClipboardAccess.TryCaptureSnapshotFromFormats(
            [2, 8, 17],
            fmt => [(byte)fmt, 1, 2],
            out var dibSnapshot));
        Assert.NotNull(dibSnapshot);
        Assert.Equal([8u, 17u], dibSnapshot!.Entries.Select(e => e.Format));

        // Raw CF_BITMAP without CF_DIB/CF_DIBV5 is rejected so clipboard data is never lost
        Assert.False(Win32ClipboardAccess.TryCaptureSnapshotFromFormats(
            [2],
            _ => [1, 2, 3],
            out _));

        // Delayed rendering (null handle) or non-HGLOBAL format aborts snapshot
        Assert.False(Win32ClipboardAccess.TryCaptureSnapshotFromFormats(
            [15],
            _ => null,
            out _));
        Assert.False(Win32ClipboardAccess.TryCaptureSnapshotFromFormats(
            [14], // CF_ENHMETAFILE
            _ => [1, 2, 3],
            out _));
    }
}
