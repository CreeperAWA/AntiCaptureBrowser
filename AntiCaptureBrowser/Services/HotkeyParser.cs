namespace AntiCaptureBrowser.Services;

/// <summary>解析 "Ctrl+Alt+B" 这类组合键文本为 Win32 修饰键与虚拟键码。</summary>
public static class HotkeyParser
{
    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;
    private const uint ModShift = 0x4;
    private const uint ModWin = 0x8;

    public static bool TryParse(string? combo, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(combo)) return false;

        var parts = combo
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant())
            .ToArray();
        if (parts.Length == 0) return false;

        foreach (var part in parts.Take(parts.Length - 1))
        {
            modifiers |= part switch
            {
                "ctrl" or "control" => ModControl,
                "alt" => ModAlt,
                "shift" => ModShift,
                "win" or "windows" => ModWin,
                _ => 0,
            };
        }
        if (modifiers == 0) return false;
        modifiers &= ModAlt | ModControl | ModShift | ModWin;
        if (modifiers == 0) return false;

        var key = parts[^1];
        // 字母 / 数字
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            virtualKey = (uint)char.ToUpperInvariant(key[0]);
            return true;
        }
        // D0..D9（WPF Key 命名）
        if (key.Length == 2 && key[0] == 'd' && char.IsAsciiDigit(key[1]))
        {
            virtualKey = (uint)('0' + (key[1] - '0'));
            return true;
        }
        // F1..F24
        if (key.Length is 2 or 3 && key[0] == 'f' && int.TryParse(key[1..], out var fn) && fn is >= 1 and <= 24)
        {
            virtualKey = 0x70u + (uint)(fn - 1);
            return true;
        }
        virtualKey = key switch
        {
            "space" => 0x20,
            "enter" => 0x0D,
            "escape" or "esc" => 0x1B,
            "tab" => 0x09,
            "backspace" => 0x08,
            "delete" or "del" => 0x2E,
            "insert" or "ins" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            "pause" => 0x13,
            _ => 0,
        };
        return virtualKey != 0;
    }
}
