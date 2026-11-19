using System.Globalization;
using System.Windows.Media;

namespace Memopad.Services;

/// <summary>色の文字列変換と、標準色 16 色の定義。</summary>
public static class ColorUtil
{
    /// <summary>"#RRGGBB" を Color に変換する。空や不正な文字列なら null。</summary>
    public static Color? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
        return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// 标准 16 色。v0.4.0 起改用 PICO-8 调色板（step3 FB-3 的参考文件）。
    /// 顺序按 PICO-8 编号（0〜15）。名称以中文显示。
    /// </summary>
    public static readonly (string Name, Color Color)[] StandardColors =
    {
        ("黑", Color.FromRgb(0x00, 0x00, 0x00)),
        ("深蓝", Color.FromRgb(0x1D, 0x2B, 0x53)),
        ("深紫", Color.FromRgb(0x7E, 0x25, 0x53)),
        ("深绿", Color.FromRgb(0x00, 0x87, 0x51)),
        ("棕", Color.FromRgb(0xAB, 0x52, 0x36)),
        ("深灰", Color.FromRgb(0x5F, 0x57, 0x4F)),
        ("浅灰", Color.FromRgb(0xC2, 0xC3, 0xC7)),
        ("白", Color.FromRgb(0xFF, 0xF1, 0xE8)),
        ("红", Color.FromRgb(0xFF, 0x00, 0x4D)),
        ("橙", Color.FromRgb(0xFF, 0xA3, 0x00)),
        ("黄", Color.FromRgb(0xFF, 0xEC, 0x27)),
        ("绿", Color.FromRgb(0x00, 0xE4, 0x36)),
        ("蓝", Color.FromRgb(0x29, 0xAD, 0xFF)),
        ("靛", Color.FromRgb(0x83, 0x76, 0x9C)),
        ("粉", Color.FromRgb(0xFF, 0x77, 0xA8)),
        ("桃", Color.FromRgb(0xFF, 0xCC, 0xAA)),
    };
}
