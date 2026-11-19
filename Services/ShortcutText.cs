using System;
using System.Windows.Forms;

namespace Memopad.Services;

/// <summary>
/// 快捷键组合文本（"Ctrl+Shift+R"）与 <see cref="Keys"/> 的互转，以及能否绑定的判断。
/// 只服务「可自定义快捷键」的菜单命令；编辑器内置的撤销/复制等不经过这里。
/// </summary>
public static class ShortcutText
{
    /// <summary>把组合键格式化成“修饰键+主键”，修饰键顺序固定为 Ctrl→Alt→Shift。</summary>
    public static string Format(Keys keys)
    {
        if (keys == Keys.None) return string.Empty;
        var parts = new System.Collections.Generic.List<string>(4);
        if ((keys & Keys.Control) == Keys.Control) parts.Add("Ctrl");
        if ((keys & Keys.Alt) == Keys.Alt) parts.Add("Alt");
        if ((keys & Keys.Shift) == Keys.Shift) parts.Add("Shift");
        parts.Add(KeyName(keys & Keys.KeyCode));
        return string.Join("+", parts);
    }

    /// <summary>解析文本；无法识别或只有修饰键时返回 <see cref="Keys.None"/>。</summary>
    public static Keys Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Keys.None;
        var raw = text.Trim();
        // "Ctrl++"（主键就是加号）结尾的那个 '+' 属于主键，不能当分隔符切掉
        var plusAsKey = raw.Length > 1 && raw[^1] == '+';
        var body = plusAsKey ? raw[..^1] : raw;

        var mods = Keys.None;
        var code = Keys.None;
        foreach (var part in body.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            if (p.Length == 0) continue;
            switch (p.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= Keys.Control;
                    break;
                case "alt":
                case "menu":
                    mods |= Keys.Alt;
                    break;
                case "shift":
                    mods |= Keys.Shift;
                    break;
                default:
                    var parsed = ParseKey(p);
                    if (parsed != Keys.None) code = parsed;
                    break;
            }
        }

        if (plusAsKey && code == Keys.None) code = Keys.Oemplus;
        if (code == Keys.None) return Keys.None;
        return mods | code;
    }

    /// <summary>
    /// 能否绑定：空组合表示“不设快捷键”，允许；纯修饰键不允许；
    /// 有 Ctrl/Alt/Shift 之外，只额外接受不带修饰的 F1–F12、PrintScreen。
    /// </summary>
    public static bool IsUsable(Keys keys)
    {
        if (keys == Keys.None) return true;
        var code = keys & Keys.KeyCode;
        if (code == Keys.None) return false;
        if ((keys & (Keys.Control | Keys.Alt | Keys.Shift)) != Keys.None) return true;
        if (code >= Keys.F1 && code <= Keys.F12) return true;
        return code == Keys.PrintScreen;
    }

    private static string KeyName(Keys code)
    {
        if (code >= Keys.D0 && code <= Keys.D9) return ((char)('0' + (int)(code - Keys.D0))).ToString();
        if (code >= Keys.NumPad0 && code <= Keys.NumPad9) return "NumPad" + (int)(code - Keys.NumPad0);
        if (code >= Keys.A && code <= Keys.Z) return ((char)(int)code).ToString();
        if (code >= Keys.F1 && code <= Keys.F24) return "F" + (int)(code - Keys.F1 + 1);

        switch (code)
        {
            case Keys.Oemplus: return "+";
            case Keys.OemMinus: return "-";
            case Keys.Space: return "Space";
            case Keys.Enter: return "Enter";
            case Keys.Tab: return "Tab";
            case Keys.Escape: return "Esc";
            case Keys.Back: return "Backspace";
            case Keys.Delete: return "Delete";
            case Keys.Insert: return "Insert";
            case Keys.Home: return "Home";
            case Keys.End: return "End";
            case Keys.PageUp: return "PageUp";
            case Keys.PageDown: return "PageDown";
            case Keys.Up: return "Up";
            case Keys.Down: return "Down";
            case Keys.Left: return "Left";
            case Keys.Right: return "Right";
        }
        return code.ToString();
    }

    private static Keys ParseKey(string name)
    {
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c >= 'A' && c <= 'Z') return (Keys)(int)c;
            if (c >= '0' && c <= '9') return (Keys)((int)Keys.D0 + (c - '0'));
            if (name[0] == '+') return Keys.Oemplus;
            if (name[0] == '-') return Keys.OemMinus;
        }

        var lower = name.ToLowerInvariant();
        if (lower.Length > 1 && lower[0] == 'f' && int.TryParse(lower.AsSpan(1), out var fn) && fn >= 1 && fn <= 24)
            return (Keys)((int)Keys.F1 + fn - 1);
        if (lower.StartsWith("numpad", StringComparison.Ordinal) && int.TryParse(lower.AsSpan(6), out var nd) && nd >= 0 && nd <= 9)
            return (Keys)((int)Keys.NumPad0 + nd);

        switch (lower)
        {
            case "space": return Keys.Space;
            case "enter":
            case "return": return Keys.Enter;
            case "tab": return Keys.Tab;
            case "esc":
            case "escape": return Keys.Escape;
            case "del":
            case "delete": return Keys.Delete;
            case "back":
            case "backspace": return Keys.Back;
            case "ins":
            case "insert": return Keys.Insert;
            case "home": return Keys.Home;
            case "end": return Keys.End;
            case "pgup":
            case "pageup": return Keys.PageUp;
            case "pgdn":
            case "pagedown": return Keys.PageDown;
            case "up": return Keys.Up;
            case "down": return Keys.Down;
            case "left": return Keys.Left;
            case "right": return Keys.Right;
        }

        return Enum.TryParse<Keys>(name, true, out var parsed) ? parsed & Keys.KeyCode : Keys.None;
    }
}
