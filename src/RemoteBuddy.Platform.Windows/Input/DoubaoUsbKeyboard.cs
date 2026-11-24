// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using HIDMaestro;
using RemoteBuddy.Core.Diagnostics;

namespace RemoteBuddy.Platform.Windows.Input;

/// <summary>Submits whole keyboard states through the signed USB/IP controller.</summary>
public sealed class DoubaoUsbKeyboard : IDisposable
{
    private readonly object _sync = new();
    private HMContext? _context;
    private HMController? _keyboard;
    private bool _keysHeld;
    private bool _disposed;

    public bool SendDown(string shortcut)
    {
        if (!TryEncodeShortcut(shortcut, out byte modifiers, out byte key))
        {
            AppLogger.Warn($"Unsupported USB/IP keyboard shortcut '{shortcut}'.", nameof(DoubaoUsbKeyboard));
            return false;
        }

        lock (_sync)
        {
            if (_disposed) return false;
            try
            {
                EnsureKeyboard();
                _keysHeld = true;
                HMKey[] keys = key == 0 ? [] : [(HMKey)key];
                _keyboard!.SubmitKeyboard((HMKeyModifiers)modifiers, keys);
                return true;
            }
            catch (Exception error)
            {
                AppLogger.Error("USB/IP keyboard press failed", error, nameof(DoubaoUsbKeyboard));
                ResetControllerLocked();
                return false;
            }
        }
    }

    public bool Release()
    {
        lock (_sync)
        {
            if (!_keysHeld || _keyboard is null) return true;
            try
            {
                _keyboard.SubmitKeyboard(HMKeyModifiers.None, []);
                _keysHeld = false;
                return true;
            }
            catch (Exception error)
            {
                AppLogger.Error("USB/IP keyboard release failed", error, nameof(DoubaoUsbKeyboard));
                ResetControllerLocked();
                return false;
            }
        }
    }

    public bool SendClick(string shortcut)
    {
        if (!SendDown(shortcut)) return false;
        Thread.Sleep(45);
        return Release();
    }

    private void EnsureKeyboard()
    {
        if (_keyboard is not null) return;

        _context = new HMContext(HMBackend.Usbip);
        // Load only Buddy's generic keyboard profile. The SDK's default
        // profiles represent named physical devices.
        string directory = Directory.CreateTempSubdirectory("remote-buddy-keyboard-").FullName;
        string profilePath = Path.Combine(directory, "keyboard.json");
        try
        {
            File.WriteAllText(profilePath, """
                {
                  "id": "remote-buddy-keyboard",
                  "name": "Remote Buddy Keyboard",
                  "vendor": "Remote Buddy",
                  "vid": "0xFFFF",
                  "pid": "0x0001",
                  "productString": "Remote Buddy Keyboard",
                  "manufacturerString": "Remote Buddy",
                  "type": "keyboard",
                  "connection": "usb",
                  "backend": "usbip",
                  "descriptor": "05010906a101050719e029e7150025019508750181029501750881010507190029ff150026ff00950675088100050819012905950575019102950175039101c0"
                }
                """);
            _context.LoadProfilesFromDirectory(directory);
            var profile = _context.GetProfile("remote-buddy-keyboard")
                ?? throw new InvalidOperationException("Could not load the generic USB keyboard profile.");
            _keyboard = _context.CreateController(profile, "remote-buddy-keyboard", HMBackend.Usbip);
        }
        finally
        {
            try { if (File.Exists(profilePath)) File.Delete(profilePath); } catch { }
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    internal static bool TryEncodeShortcut(string shortcut, out byte modifiers, out byte key)
    {
        modifiers = 0;
        key = 0;
        if (!NativeInputSender.TryParseShortcut(shortcut, out var virtualModifiers, out ushort virtualKey))
            return false;

        foreach (ushort modifier in virtualModifiers)
        {
            modifiers |= (byte)(modifier switch
            {
                0xA2 => 0x01, // Left Ctrl
                0xA0 => 0x02, // Left Shift
                0xA4 => 0x04, // Left Alt
                0x5B => 0x08, // Left Windows
                0xA3 => 0x10, // Right Ctrl
                0xA1 => 0x20, // Right Shift
                0xA5 => 0x40, // Right Alt
                0x5C => 0x80, // Right Windows
                _ => 0
            });
        }

        key = virtualKey switch
        {
            >= 'A' and <= 'Z' => (byte)(0x04 + virtualKey - 'A'),
            >= '1' and <= '9' => (byte)(0x1E + virtualKey - '1'),
            '0' => 0x27,
            >= 0x70 and <= 0x7B => (byte)(0x3A + virtualKey - 0x70), // F1–F12
            >= 0x7C and <= 0x87 => (byte)(0x68 + virtualKey - 0x7C), // F13–F24
            0x0D => 0x28, 0x1B => 0x29, 0x08 => 0x2A, 0x09 => 0x2B, 0x20 => 0x2C,
            0xBD => 0x2D, 0xBB => 0x2E, 0xDB => 0x2F, 0xDD => 0x30, 0xDC => 0x31,
            0xBA => 0x33, 0xDE => 0x34, 0xC0 => 0x35, 0xBC => 0x36, 0xBE => 0x37, 0xBF => 0x38,
            0x2D => 0x49, 0x24 => 0x4A, 0x21 => 0x4B, 0x2E => 0x4C, 0x23 => 0x4D, 0x22 => 0x4E,
            0x27 => 0x4F, 0x25 => 0x50, 0x28 => 0x51, 0x26 => 0x52, 0x5D => 0x65,
            _ => 0
        };
        return virtualKey == 0 ? modifiers != 0 : key != 0;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            ResetControllerLocked();
        }
    }

    private void ResetControllerLocked()
    {
        var keyboard = _keyboard;
        var context = _context;
        _keyboard = null;
        _context = null;
        try
        {
            if (_keysHeld) keyboard?.SubmitKeyboard(HMKeyModifiers.None, []);
            keyboard?.Dispose();
        }
        catch (Exception error)
        {
            AppLogger.Error("USB/IP keyboard cleanup failed", error, nameof(DoubaoUsbKeyboard));
        }
        finally
        {
            _keysHeld = false;
            context?.Dispose();
        }
    }
}
