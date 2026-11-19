namespace Memopad.Services;

/// <summary>
/// 永続化する設定。%APPDATA%\MemoPad\settings.json に JSON で保存する。
/// 色は "#RRGGBB" の文字列で持ち、空文字ならテーマ既定の色を使う。
/// </summary>
public sealed class AppSettings
{
    // --- フォント
    public string FontFamily { get; set; } = "Consolas";
    public double FontSize { get; set; } = 11;
    public bool FontBold { get; set; }
    public bool FontItalic { get; set; }

    // --- 表示
    public bool WordWrap { get; set; } = true;
    public bool ShowStatusBar { get; set; } = true;

    /// <summary>MemoPad 增强：彩虹模式（文字按红橙黄绿青蓝紫循环着色），默认开启。</summary>
    public bool RainbowMode { get; set; } = true;

    /// <summary>MemoPad 增强：彩虹文字是否再铺一层同色系浅底块（方案②）。默认关闭——
    /// 底块和「荧光笔标记」都是铺底，两层叠在一起会互相干扰，标记要看清楚就得先去掉它。</summary>
    public bool RainbowBlocks { get; set; } = false;

    /// <summary>MemoPad 增强：彩虹流动（跑马灯）——7 个色位每 120ms 平移一格，颜色沿文字滚动。
    /// 默认关闭：一直动着容易分散注意力，需要时在「查看」菜单里打开即可。</summary>
    public bool RainbowFlow { get; set; } = false;

    /// <summary>MemoPad 增强：彩虹每 N 个字符换一个颜色。默认 4；可在「查看 → 彩虹分组」里改。</summary>
    public int RainbowGroupChars { get; set; } = 4;

    /// <summary>MemoPad 增强：彩虹按行着色（一行一种颜色，行号循环七色）。默认关闭——
    /// 关闭时按「每 N 字一组」着色；开启后忽略 RainbowGroupChars，整行同色。</summary>
    public bool RainbowByLine { get; set; } = false;

    /// <summary>"Light" / "Dark" / "System"</summary>
    public string Theme { get; set; } = "System";

    // --- MemoPad の追加機能：背景色・文字色
    public string BackgroundColor { get; set; } = "";
    public string ForegroundColor { get; set; } = "";

    // --- 最近使ったファイル
    public bool RecentFilesEnabled { get; set; } = true;
    public List<string> RecentFiles { get; set; } = new();

    // --- ウィンドウ
    public double WindowWidth { get; set; } = 1000;
    public double WindowHeight { get; set; } = 700;
    public bool WindowMaximized { get; set; }

    // --- MemoPad 增强：缩放记忆・行间距
    public int ZoomPercent { get; set; } = 100;
    public int LineSpacing { get; set; }

    // --- MemoPad 增强：云盘笔记目录（null 时自动探测坚果云默认位置）
    public string? NotesDirectory { get; set; }

    /// <summary>
    /// MemoPad 增强：自定义快捷键。键 = 命令 id（如 "rainbow"），值 = 组合键文本（如 "Ctrl+Shift+R"）。
    /// 只保存与默认值不同的项；值为空字符串表示「不设快捷键」。
    /// </summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new();

    // --- ページ設定（余白は mm）
    public bool PrintLandscape { get; set; }
    public double MarginLeft { get; set; } = 20;
    public double MarginRight { get; set; } = 20;
    public double MarginTop { get; set; } = 25;
    public double MarginBottom { get; set; } = 25;
    public string PrintHeader { get; set; } = "&f";
    public string PrintFooter { get; set; } = "第 &p 页";

    public const int MaxRecentFiles = 10;

    /// <summary>最近使ったファイルの先頭に追加する（重複は前へ移動、上限を超えた分は捨てる）。</summary>
    public void PushRecentFile(string path)
    {
        if (!RecentFilesEnabled) return;
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecentFiles) RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
    }
}
