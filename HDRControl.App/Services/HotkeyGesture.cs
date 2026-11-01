using System.Globalization;
using System.Runtime.InteropServices;
using Windows.System;

namespace HDRControl.App.Services;

public sealed record HotkeyGesture(uint VirtualKey, uint Modifiers, string DisplayText)
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWindows = 0x0008;

    public static bool TryParse(string? hotkey, out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            return true;
        }

        uint modifiers = 0;
        uint virtualKey = 0;
        var keyFound = false;
        foreach (var token in hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win":
                case "windows":
                    modifiers |= ModWindows;
                    break;
                default:
                    if (keyFound || !TryParseKey(token, out virtualKey))
                    {
                        return false;
                    }

                    keyFound = true;
                    break;
            }
        }

        if (!keyFound || (modifiers & (ModControl | ModAlt | ModWindows)) == 0)
        {
            return false;
        }

        gesture = new HotkeyGesture(virtualKey, modifiers, Format(modifiers, virtualKey));
        return true;
    }

    public static HotkeyGesture? Capture(VirtualKey key)
    {
        if (key is Windows.System.VirtualKey.Control
            or Windows.System.VirtualKey.LeftControl
            or Windows.System.VirtualKey.RightControl
            or Windows.System.VirtualKey.Menu
            or Windows.System.VirtualKey.LeftMenu
            or Windows.System.VirtualKey.RightMenu
            or Windows.System.VirtualKey.Shift
            or Windows.System.VirtualKey.LeftShift
            or Windows.System.VirtualKey.RightShift
            or Windows.System.VirtualKey.LeftWindows
            or Windows.System.VirtualKey.RightWindows)
        {
            return null;
        }

        uint modifiers = 0;
        if (IsDown(0x11))
        {
            modifiers |= ModControl;
        }

        if (IsDown(0x12))
        {
            modifiers |= ModAlt;
        }

        if (IsDown(0x10))
        {
            modifiers |= ModShift;
        }

        if (IsDown(0x5B) || IsDown(0x5C))
        {
            modifiers |= ModWindows;
        }

        var virtualKey = (uint)key;
        if ((modifiers & (ModControl | ModAlt | ModWindows)) == 0
            || !TryGetKeyName(virtualKey, out _))
        {
            return null;
        }

        return new HotkeyGesture(virtualKey, modifiers, Format(modifiers, virtualKey));
    }

    private static bool TryParseKey(string token, out uint virtualKey)
    {
        if (token.Length == 1 && char.IsAsciiLetterOrDigit(token[0]))
        {
            virtualKey = char.ToUpperInvariant(token[0]);
            return true;
        }

        if (token.Length is >= 2 and <= 3
            && token[0] is 'F' or 'f'
            && int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var functionKey)
            && functionKey is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + functionKey - 1);
            return true;
        }

        virtualKey = token.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "tab" => 0x09,
            "escape" or "esc" => 0x1B,
            "insert" => 0x2D,
            "delete" or "del" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            _ => 0
        };
        return virtualKey != 0;
    }

    private static string Format(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>(5);
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWindows) != 0) parts.Add("Win");
        if (TryGetKeyName(virtualKey, out var keyName)) parts.Add(keyName);
        return string.Join('+', parts);
    }

    private static bool TryGetKeyName(uint virtualKey, out string name)
    {
        if (virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
        {
            name = ((char)virtualKey).ToString();
            return true;
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            name = $"F{virtualKey - 0x6F}";
            return true;
        }

        name = virtualKey switch
        {
            0x09 => "Tab",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            _ => string.Empty
        };
        return name.Length > 0;
    }

    private static bool IsDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetKeyState(int virtualKey);
}
