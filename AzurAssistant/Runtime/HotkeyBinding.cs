using System.Globalization;

namespace AzurAssistant.Runtime;

/// <summary>A validated, platform-neutral global shortcut using Windows virtual-key values.</summary>
public readonly record struct HotkeyBinding(uint Modifiers, uint VirtualKey, string Text)
{
    public const uint Alt = 1;
    public const uint Control = 2;
    public const uint Shift = 4;
    private static readonly IReadOnlyDictionary<string, uint> NamedKeys = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
    {
        ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Esc"] = 0x1B,
        ["Space"] = 0x20, ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23,
        ["Home"] = 0x24, ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27,
        ["Down"] = 0x28, ["Insert"] = 0x2D, ["Delete"] = 0x2E
    };

    public static bool TryParse(string? text, out HotkeyBinding binding, out string? error)
    {
        binding = default;
        error = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 80)
        { error = "请按下有效的快捷键。"; return false; }
        var tokens = text.Split('+', StringSplitOptions.TrimEntries);
        uint modifiers = 0;
        for (var index = 0; index < tokens.Length - 1; index++)
        {
            var modifier = tokens[index].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => Control, "ALT" => Alt, "SHIFT" => Shift, _ => 0u
            };
            if (modifier == 0 || (modifiers & modifier) != 0)
            { error = "仅支持 Ctrl、Alt、Shift，且修饰键不能重复。"; return false; }
            modifiers |= modifier;
        }
        var key = tokens[^1].ToUpperInvariant();
        uint virtualKey;
        if (key.Length == 1 && (key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) virtualKey = key[0];
        else if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var f)
            && f is >= 1 and <= 24) virtualKey = (uint)(0x6F + f);
        else if (!NamedKeys.TryGetValue(key, out virtualKey))
        { error = "请选择 F 键，或 Ctrl / Alt 与字母、数字、方向等按键的组合。"; return false; }
        return TryFromVirtualKey(modifiers, virtualKey, out binding, out error);
    }

    public static bool TryFromVirtualKey(uint modifiers, uint virtualKey, out HotkeyBinding binding, out string? error)
    {
        binding = default;
        error = null;
        if ((modifiers & ~(Alt | Control | Shift)) != 0)
        { error = "不支持 Windows 键，请使用 Ctrl、Alt 或 Shift。"; return false; }
        if (virtualKey == 0x7B)
        { error = "F12 为系统调试保留键，不能用于全局快捷键。"; return false; }
        var functionKey = virtualKey is >= 0x70 and <= 0x87;
        var name = functionKey ? $"F{virtualKey - 0x6F}"
            : virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A ? ((char)virtualKey).ToString()
            : NamedKeys.FirstOrDefault(pair => pair.Value == virtualKey).Key;
        if (name is null)
        { error = "该按键不能单独绑定，请选择 F 键或 Ctrl / Alt 组合。"; return false; }
        if (!functionKey && (modifiers & (Control | Alt)) == 0)
        { error = "除 F 键外，至少需要同时按住 Ctrl 或 Alt。"; return false; }
        if (((modifiers & Alt) != 0 && virtualKey is 0x09 or 0x1B or 0x20 or 0x73)
            || ((modifiers & Control) != 0 && virtualKey == 0x1B)
            || ((modifiers & (Control | Alt)) == (Control | Alt) && virtualKey == 0x2E))
        { error = "该组合是 Windows 常用保留快捷键，请换一个组合。"; return false; }
        var prefix = ((modifiers & Control) != 0 ? "Ctrl+" : "")
            + ((modifiers & Alt) != 0 ? "Alt+" : "") + ((modifiers & Shift) != 0 ? "Shift+" : "");
        binding = new HotkeyBinding(modifiers, virtualKey, prefix + name);
        return true;
    }

    public static bool TryParsePair(string? pause, string? stop, out HotkeyBinding pauseBinding,
        out HotkeyBinding stopBinding, out string? error)
    {
        stopBinding = default;
        if (!TryParse(pause, out pauseBinding, out error)) { error = $"暂停 / 继续：{error}"; return false; }
        if (!TryParse(stop, out stopBinding, out error)) { error = $"停止所有任务：{error}"; return false; }
        if (pauseBinding.Modifiers == stopBinding.Modifiers && pauseBinding.VirtualKey == stopBinding.VirtualKey)
        { error = "暂停 / 继续与停止所有任务不能使用相同的快捷键；修改未保存。"; return false; }
        return true;
    }
}
