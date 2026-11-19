using System.Windows.Media;

namespace Memopad.Services;

/// <summary>配色パターン 1 つ分（背景色・文字色・見出し用のアクセント色）。</summary>
public sealed record ColorScheme(string Id, bool IsDark, string Name, Color Background, Color Foreground, Color Accent)
{
    public string BackgroundHex => ColorUtil.ToHex(Background);
    public string ForegroundHex => ColorUtil.ToHex(Foreground);
}

/// <summary>
/// 目に優しい配色 16 パターン（step3 FB-4 の参照ファイル「テキストエディタ配色パターン」から）。
/// ライト 8 種＋ダーク 8 種。選ぶと背景色と文字色を一度に変える。
/// </summary>
public static class ColorSchemes
{
    private static ColorScheme Make(string id, bool dark, string name, string bg, string fg, string accent) =>
        new(id, dark, name, ColorUtil.TryParse(bg)!.Value, ColorUtil.TryParse(fg)!.Value, ColorUtil.TryParse(accent)!.Value);

    public static readonly ColorScheme[] All =
    {
        // ライト（浅色）
        Make("kinari", false, "米白", "#F7F3E9", "#3B372E", "#B08D57"),
        Make("softgray", false, "柔灰", "#F2F2F0", "#333333", "#7A7A78"),
        Make("sepia", false, "泛黄", "#F4ECD8", "#4A3F35", "#A9762F"),
        Make("mint", false, "薄荷", "#EAF4F0", "#2C3E3A", "#3E8E7E"),
        Make("lavender", false, "薰衣草", "#F1EEF7", "#35313F", "#7C6A9C"),
        Make("peach", false, "蜜桃", "#FBEEE6", "#4A3226", "#C97B53"),
        Make("sky", false, "天空", "#EAF2F8", "#2A3B4A", "#3E76A0"),
        Make("sand", false, "沙色", "#F5EFE6", "#453D33", "#9C8358"),
        // ダーク（深色）
        Make("charcoal", true, "炭黑", "#2B2B2B", "#DCDCDC", "#8FA6B3"),
        Make("forest", true, "森林", "#1F2B24", "#D6E4DA", "#6FAE8B"),
        Make("night", true, "夜色", "#1B2430", "#D7DEE8", "#6E8FBF"),
        Make("sumi", true, "墨色", "#232323", "#E0DCD3", "#C9A45C"),
        Make("darkpurple", true, "暗紫", "#241F2E", "#E1DCE8", "#A78BC9"),
        Make("coffee", true, "咖啡", "#2A211B", "#E5DACB", "#C99A6B"),
        Make("slate", true, "板岩", "#262B30", "#DCE1E5", "#7FA6B8"),
        Make("wine", true, "酒红", "#2A1E22", "#E5D9DB", "#C77E8C"),
    };

    /// <summary>現在の背景色・文字色に一致するパターン（無ければ null）。</summary>
    public static ColorScheme? Find(string backgroundHex, string foregroundHex) =>
        All.FirstOrDefault(s =>
            string.Equals(s.BackgroundHex, backgroundHex, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.ForegroundHex, foregroundHex, StringComparison.OrdinalIgnoreCase));
}
