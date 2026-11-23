// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.Platform.Windows.Input;
using Xunit;

namespace RemoteBuddy.Tests;

public class NativeInputSenderTests
{
    [Theory]
    [InlineData("RAlt", 0x40, 0x00)]
    [InlineData("Ctrl+Shift+D", 0x03, 0x07)]
    [InlineData("RCtrl+RShift+D", 0x30, 0x07)]
    [InlineData("Ctrl+Win", 0x09, 0x00)]
    [InlineData("LAlt+F24", 0x04, 0x73)]
    [InlineData("Ctrl+/", 0x01, 0x38)]
    [InlineData("Tab", 0x00, 0x2B)]
    public void DoubaoKeyboard_EncodesWholeShortcut(string shortcut, int modifiers, int key)
    {
        Assert.True(DoubaoUsbKeyboard.TryEncodeShortcut(shortcut, out byte actualModifiers, out byte actualKey));
        Assert.Equal((byte)modifiers, actualModifiers);
        Assert.Equal((byte)key, actualKey);
    }

    [Fact]
    public void ShortcutRelease_AlwaysBalancesInjectedModifierAndMainKey()
    {
        var inputs = NativeInputSender.CreateShortcutReleaseInputs("Ctrl+F5");
        Assert.Equal(new ushort[] { 0x74, 0xA2 }, inputs.Select(input => input.u.ki.wVk));
        Assert.All(inputs, input => Assert.NotEqual(0u, input.u.ki.dwFlags & NativeInputSender.KEYEVENTF_KEYUP));
    }

    [Theory]
    [InlineData("Ctrl+Alt+V", new ushort[] { 0xA2, 0xA4 }, (ushort)'V')]
    [InlineData("RCtrl+RAlt+V", new ushort[] { 0xA3, 0xA5 }, (ushort)'V')]
    [InlineData("RAlt", new ushort[] { 0xA5 }, (ushort)0)]
    [InlineData("RightAlt", new ushort[] { 0xA5 }, (ushort)0)]
    [InlineData("LShift+RShift", new ushort[] { 0xA0, 0xA1 }, (ushort)0)]
    [InlineData("Win+F5", new ushort[] { 0x5B }, (ushort)0x74)]
    [InlineData("Win+Tab", new ushort[] { 0x5B }, (ushort)0x09)]
    [InlineData("Backspace", new ushort[] { }, (ushort)0x08)]
    [InlineData("RWin+Space", new ushort[] { 0x5C }, (ushort)0x20)]
    [InlineData("Up", new ushort[] { }, (ushort)0x26)]
    [InlineData("Down", new ushort[] { }, (ushort)0x28)]
    [InlineData("Left", new ushort[] { }, (ushort)0x25)]
    [InlineData("Right", new ushort[] { }, (ushort)0x27)]
    public void TryParseShortcut_ParsesValidShortcutsAndModifiers(string shortcut, ushort[] expectedModifiers, ushort expectedMainKey)
    {
        bool success = NativeInputSender.TryParseShortcut(shortcut, out var modifiers, out var mainKey);

        Assert.True(success);
        Assert.Equal(expectedModifiers, modifiers);
        Assert.Equal(expectedMainKey, mainKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+NonExistentKey")]
    [InlineData("InvalidModifier+A")]
    [InlineData("Ctrl+A+B")]
    [InlineData("F0")]
    [InlineData("F25")]
    public void TryParseShortcut_RejectsInvalidShortcuts(string shortcut)
    {
        bool success = NativeInputSender.TryParseShortcut(shortcut, out _, out _);
        Assert.False(success);
    }

    [Theory]
    [InlineData("F1", true)]
    [InlineData("F12", true)]
    [InlineData("F13", true)]
    [InlineData("F24", true)]
    [InlineData("Alt+9", true)]
    [InlineData("Home", true)]
    [InlineData("End", true)]
    [InlineData("PageUp", true)]
    [InlineData("PageDown", true)]
    public void TryParseShortcut_ValidatesNavigationAndFunctionKeys(string shortcut, bool expected)
    {
        bool success = NativeInputSender.TryParseShortcut(shortcut, out _, out _);
        Assert.Equal(expected, success);
    }

    [Theory]
    [InlineData(0x25, true)]  // VK_LEFT
    [InlineData(0x28, true)]  // VK_DOWN
    [InlineData(0x5B, true)]  // VK_LWIN
    [InlineData(0xA3, true)]  // VK_RCONTROL
    [InlineData(0xA5, true)]  // VK_RMENU (Right Alt)
    [InlineData(0xA2, false)] // VK_LCONTROL
    [InlineData(0x41, false)] // 'A'
    public void IsExtendedKey_ClassifiesExtendedKeysCorrectly(ushort vk, bool expected)
    {
        Assert.Equal(expected, NativeInputSender.IsExtendedKey(vk));
    }

    [Fact]
    public void CreateShortcutReleaseInputs_RAlt_ReleasesRightMenuAsExtendedKey()
    {
        var inputs = NativeInputSender.CreateShortcutReleaseInputs("RAlt");
        var release = Assert.Single(inputs);
        Assert.Equal(NativeInputSender.VK_RMENU, release.u.ki.wVk);
        Assert.NotEqual(0u, release.u.ki.dwFlags & NativeInputSender.KEYEVENTF_KEYUP);
        Assert.NotEqual(0u, release.u.ki.dwFlags & NativeInputSender.KEYEVENTF_EXTENDEDKEY);
    }

    [Fact]
    public void CreateKeyInput_AttachesSyntheticEventMarker()
    {
        var input = NativeInputSender.CreateKeyInput(0x41, false);
        Assert.Equal(NativeInputSender.SyntheticEventMarker, input.u.ki.dwExtraInfo);
    }

    [Theory]
    [InlineData(0u, new ushort[0])]
    [InlineData(1u, new ushort[] { 0xA2 })]
    [InlineData(2u, new ushort[] { 0xA0, 0xA2 })]
    [InlineData(3u, new ushort[] { 0x41, 0xA0, 0xA2 })]
    [InlineData(4u, new ushort[] { 0xA0, 0xA2 })]
    [InlineData(5u, new ushort[] { 0xA2 })]
    [InlineData(6u, new ushort[0])]
    public void BuildUnclosedReleaseInputs_ReturnsMatchingKeyUpsAtEachSentBoundary(uint sentCount, ushort[] expectedReleaseVks)
    {
        var sequence = new INPUT[]
        {
            NativeInputSender.CreateKeyInput(0xA2, false), // Ctrl Down
            NativeInputSender.CreateKeyInput(0xA0, false), // Shift Down
            NativeInputSender.CreateKeyInput(0x41, false), // A Down
            NativeInputSender.CreateKeyInput(0x41, true),  // A Up
            NativeInputSender.CreateKeyInput(0xA0, true),  // Shift Up
            NativeInputSender.CreateKeyInput(0xA2, true)   // Ctrl Up
        };

        var releases = NativeInputSender.BuildUnclosedReleaseInputs(sequence, sentCount);
        Assert.Equal(expectedReleaseVks, releases.Select(i => i.u.ki.wVk).ToArray());
        Assert.All(releases, r => Assert.NotEqual(0u, r.u.ki.dwFlags & NativeInputSender.KEYEVENTF_KEYUP));
    }

    [Theory]
    [InlineData(",", 0xBC)]
    [InlineData("OemComma", 0xBC)]
    [InlineData(".", 0xBE)]
    [InlineData("OemPeriod", 0xBE)]
    [InlineData("/", 0xBF)]
    [InlineData("OemQuestion", 0xBF)]
    [InlineData("[", 0xDB)]
    [InlineData("OemOpenBrackets", 0xDB)]
    [InlineData("]", 0xDD)]
    [InlineData("OemCloseBrackets", 0xDD)]
    [InlineData(";", 0xBA)]
    [InlineData("OemSemicolon", 0xBA)]
    [InlineData("'", 0xDE)]
    [InlineData("OemQuotes", 0xDE)]
    [InlineData("-", 0xBD)]
    [InlineData("OemMinus", 0xBD)]
    [InlineData("=", 0xBB)]
    [InlineData("OemPlus", 0xBB)]
    [InlineData("\\", 0xDC)]
    [InlineData("OemPipe", 0xDC)]
    [InlineData("`", 0xC0)]
    [InlineData("OemTilde", 0xC0)]
    public void TryParseShortcut_SupportsAllUsOemSymbolsAndWpfAliases(string token, ushort expectedVk)
    {
        Assert.True(NativeInputSender.TryParseShortcut($"Ctrl+{token}", out var modifiers, out ushort mainKey));
        Assert.Equal(new ushort[] { 0xA2 }, modifiers);
        Assert.Equal(expectedVk, mainKey);
    }
}
