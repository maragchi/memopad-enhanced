using System.ComponentModel;
using System.Runtime.InteropServices;
using ScintillaNET;
using WinForms = System.Windows.Forms;

namespace Memopad.Views;

/// <summary>
/// 本文の編集コントロール。Scintilla（scintilla.org のエディタ部品）をプレーン テキストとして使う。
///
/// v0.2.0〜v0.9.2 は Windows 標準の RichEdit を使っていたが、日本語と英字が混ざった行の
/// レイアウトが極端に遅く、10 MB・12 万行のファイルを開くと 43 秒、その後もリサイズのたびに
/// 28 秒画面が固まった（FB-19）。原因は GDI への文字幅の問い合わせで、RichEdit でも Win32 の
/// Edit コントロールでも、Windows App SDK の WinUI でも避けられなかった。
/// Scintilla は同じ計算を「空き時間に細切れで」行うため、合計時間はかかっても画面が止まらない。
/// 実測では最長の無応答が 0〜22 ms（メモ帳と同等）。経緯は docs/site/step3/index.html の 15 節。
///
/// 改行はエディタ内部で LF（"\n"）1 文字に統一する（保存時に <see cref="Services.TextFileService"/> が
/// ファイルの改行コードへ戻す）。RichEdit 時代と同じ約束なので、周りのコードは変えなくてよい。
/// </summary>
public sealed class PlainTextEdit : Scintilla
{
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_GETOBJECT = 0x003D;
    private const int OBJID_CLIENT = -4;
    private const int WM_TIMER = 0x0113;

    /// <summary>Scintilla が空き時間の処理（折り返しの計算）に使うタイマーの番号。</summary>
    private const int ScintillaIdleTimerId = 2;

    /// <summary>本文の左の余白（96 dpi のときの px）。メモ帳の見た目に合わせる。</summary>
    private const int LeftPadding = 8;

    // --- MemoPad 增强：彩虹模式（文字按红橙黄绿青蓝紫循环着色，全局连续分组）---
    // 32-39 是 Scintilla 的预定义样式（默认样式/行号/括号/控制字符等），正文着色避开它们
    private const int RainbowStyleFirst = 40;
    // 每 N 个字符一组同色。默认 4；「查看 → 彩虹分组」里可改成 7 / 10 / 20 对比效果。
    private int _rainbowGroupChars = 4;
    // 关键：分组必须按「字符」而不是按「字节」。SCI_SETSTYLING 的长度单位是字节，
    // 一个中文占 3 字节，若按固定 4 字节切段就会把一个汉字的字节劈成两段、分属两个样式，
    // Scintilla 无法绘制被劈开的多字节字符，会退化成显示 UTF-8 字节转义（\xE7\x94\xA8）。
    // 因此下面按字符累计字节长度，保证每个样式段的起止都落在字符边界上。
    // Scintilla 原生消息号（ScintillaNET 7.0 对部分 API 改名/缺重载，直接用消息最稳；
    // 注意：不得在 StyleNeeded/TextChanged 等反射回调里发消息回 Scintilla，否则嵌套 SendMessage 无限递归）
    private const int SciStartStyling = 2032;
    private const int SciSetStyling = 2033;
    private const int SciStyleSetFore = 2051;
    // 用 Scintilla 自己的字符边界 API 求「往后数 N 个字符」的字节位置，
    // 比自己按 UTF-8 字节推算更可靠（官方保证返回值一定落在字符边界上）。
    private const int SciPositionRelative = 2670;
    // 按行着色用：总行数 / 行首字节位置 / 行长度（含行尾换行符，字节）。号数以本机 Scintilla5.NET 封装 XML 为准
    private const int SciGetLineCount = 2154;
    private const int SciPositionFromLine = 2167;
    private const int SciLineLength = 2350;
    // 文档长度（字节）。注意不能用 ScintillaNET 的 TextLength——它给的是「字符数」，
    // 中文一篇里字节数远大于字符数，拿字符数当字节长度会让着色循环提前结束、
    // 并在末尾把最后一组截断到某个汉字的中间，于是那个字显示成 \xHH 转义。
    private const int SciGetLength = 2006;
    // 样式背景色（方案②：每 4 字一组的浅色底块）
    private const int SciStyleSetBack = 2052;

    // --- MemoPad 增强：手动标记（荧光笔）。用 Scintilla 的「指示器」图层实现：
    // 它是独立于样式的一层，可以半透明绘制在文字下方，所以不抢彩虹的样式号、也不改文字颜色。---
    private const int SciIndicSetStyle = 2080;
    private const int SciIndicSetFore = 2082;
    private const int SciIndicSetAlpha = 2523;       // 填充透明度（0-255）
    private const int SciIndicSetOutlineAlpha = 2558; // 圆角框描边透明度
    private const int SciIndicSetUnder = 2510;       // 1 = 画在文字下方，不遮字
    private const int SciGetModify = 2159;           // 「已修改」标志
    private const int SciGetSelectionStart = 2143;   // 选区起点（字节，原生值）
    private const int SciGetSelectionEnd = 2145;     // 选区终点（字节，原生值）
    private const int SciSetIndicatorCurrent = 2500;
    private const int SciIndicatorFillRange = 2504;
    private const int SciIndicatorClearRange = 2505;
    private const int SciIndicatorAllOnFor = 2506;
    private const int IndicRoundBox = 7;             // 半透明圆角框，最像荧光笔

    /// <summary>标记用的指示器槽位。0-7 留给查找高亮、括号匹配等内置用途，从 8 起步。</summary>
    private const int MarkSlotFirst = 8;
    public const int MarkColorCount = 5;
    public static readonly string[] MarkColorNames = { "黄色", "绿色", "粉色", "蓝色", "紫色" };
    private static readonly int[] MarkColors =
    {
        0xFFD600, // 荧光黄
        0x4CAF50, // 绿
        0xFF4081, // 粉
        0x29B6F6, // 蓝
        0xAB47BC, // 紫
    };

    /// <summary>取第 index 个标记颜色的 .NET 值。MarkColors 存的是 0xRRGGBB，
    /// 这里拆成 R/G/B 给界面画色块用（菜单图标必须是正确通道，不能再走 COLORREF）。</summary>
    public static System.Drawing.Color GetMarkColor(int index)
    {
        var c = MarkColors[Math.Clamp(index, 0, MarkColorCount - 1)];
        return System.Drawing.Color.FromArgb((c >> 16) & 0xFF, (c >> 8) & 0xFF, c & 0xFF);
    }
    private static readonly System.Drawing.Color[] RainbowColors =
    {
        System.Drawing.Color.FromArgb(229, 57, 53),   // 红
        System.Drawing.Color.FromArgb(251, 140, 0),  // 橙
        System.Drawing.Color.FromArgb(253, 216, 53),  // 黄
        System.Drawing.Color.FromArgb(67, 160, 71),   // 绿
        System.Drawing.Color.FromArgb(0, 172, 193),   // 青
        System.Drawing.Color.FromArgb(30, 136, 229),  // 蓝
        System.Drawing.Color.FromArgb(142, 36, 170),  // 紫
    };

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? subAppName, string? subIdList);

    [DllImport("ole32.dll")]
    private static extern int RegisterDragDrop(IntPtr hWnd, IOleDropTarget target);

    [DllImport("ole32.dll")]
    private static extern int RevokeDragDrop(IntPtr hWnd);

    [DllImport("oleacc.dll")]
    private static extern IntPtr LresultFromObject(ref Guid riid, IntPtr wParam,
        [MarshalAs(UnmanagedType.Interface)] object pAcc);

    private bool _wordWrap = true;
    private bool _darkScrollBars;
    private bool _rainbowMode;
    private bool _rainbowBlocks;
    private bool _rainbowFlow;
    private bool _rainbowByLine;
    private bool _rainbowScheduled;
    private readonly System.Windows.Forms.Timer _rainbowTimer;
    private readonly System.Windows.Forms.Timer _flowTimer;
    /// <summary>「彩虹流动」当前平移了几格（0..6），每帧 +1。</summary>
    private int _flowPhase;
    private int _zoomPercent = 100;
    private double _baseFontSize = 11;
    private FileDropTarget? _dropTarget;
    private int _lastSelStart = -1;
    private int _lastSelEnd = -1;
    private System.Drawing.Color _foldMarkerFore = System.Drawing.Color.Gray;
    private System.Drawing.Color _foldMarkerBack = System.Drawing.Color.White;
    /// <summary>句柄创建前收到的标记，等 OnHandleCreated 再画。</summary>
    private IReadOnlyList<(int Start, int Length, int Color)>? _pendingMarks;

    /// <summary>Scintilla 的「已修改」标志（SCI_GETMODIFY）。标记操作会顺带置位，需要判断后再还原保存点。</summary>
    private bool IsModified => (int)DirectMessage(SciGetModify, IntPtr.Zero, IntPtr.Zero) != 0;

    // 右键菜单弹出前先把「用户当时选的那段」定格。
    // Scintilla 收到右键按下会把插入点挪到点击处，点在选区外时选区当场就没了
    // （实测选中 6..22，右键后直接变成 107..107），等菜单点下去再读 SelectionStart 就会标到别的地方。
    private int _ctxSelectionStart = -1;
    private int _ctxSelectionLength;
    private bool _ctxFromRightClick;

    // 选区的「字节」起点/终点。ScintillaNET 的 SelectionStart / SelectionEnd / TextLength
    // 给的是「字符数」而不是字节数：中文每字 3 字节，同一位置实测 282（属性）对 628（原生）。
    // 指示器（SCI_INDICATORFILLRANGE）要的是字节，所以标记一律走这两个原生值。
    private int RawSelectionStart => (int)DirectMessage(SciGetSelectionStart, IntPtr.Zero, IntPtr.Zero);
    private int RawSelectionEnd => (int)DirectMessage(SciGetSelectionEnd, IntPtr.Zero, IntPtr.Zero);

    private void CaptureContextSelection()
    {
        var s = RawSelectionStart;
        var e = RawSelectionEnd;
        _ctxSelectionStart = Math.Min(s, e);
        _ctxSelectionLength = Math.Abs(e - s);
    }

    /// <summary>右键菜单要操作的那段字节范围（start, length）。
    /// 右键呼出时用按下前的定格，键盘呼出（Shift+F10 之类）时退回实时选区。</summary>
    public (int Start, int Length) ContextMarkRange
    {
        get
        {
            if (_ctxSelectionStart >= 0 && _ctxSelectionLength > 0) return (_ctxSelectionStart, _ctxSelectionLength);
            var s = RawSelectionStart;
            var e = RawSelectionEnd;
            return (Math.Min(s, e), Math.Abs(e - s));
        }
    }

    /// <summary>Scintilla の折りたたみマーカー番号（Box+/Box- など）。</summary>
    private const int FoldMarkerMin = 25;
    private const int FoldMarkerMax = 31;
    private const int SciMarkerSetFore = 2041;
    private const int SciMarkerSetBack = 2042;

    public PlainTextEdit()
    {
        BorderStyle = ScintillaNET.BorderStyle.None;
        // 彩虹延迟重刷用 Timer（不能用 BeginInvoke，见 ScheduleRainbow 注释）
        _rainbowTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _rainbowTimer.Tick += (_, _) =>
        {
            _rainbowTimer.Stop();
            _rainbowScheduled = false;
            if (!_rainbowMode || IsDisposed || !IsHandleCreated) return;
            // Scintilla 的样式缓冲在窗口显示前未就绪：此时调 StartStyling/SetStyling 会原生访问违规。
            // 窗口未显示就重新排队，直到显示后再真正着色。
            if (!Visible)
            {
                _rainbowScheduled = true;
                _rainbowTimer.Start();
                return;
            }
            RainbowColouriseAll();
            // 上色只改"行样式数组"，走的是增量刷新；窗口刚显示时首次着色，
            // 个别空行的旧像素没被擦掉，会留下一条整行纯色块。
            // 这里再把样式底色重写一遍——Scintilla 收到 STYLESETBACK 会整窗重画，脏块随之消失。
            ApplyRainbowStyles();
        };
        // 「彩虹流动」（跑马灯）：每帧把 7 个色位整体平移一格。
        // 只改样式颜色、不动正文，所以光标、选区、点选定位全都不受影响。
        _flowTimer = new System.Windows.Forms.Timer { Interval = 120 };
        _flowTimer.Tick += (_, _) =>
        {
            if (!_rainbowFlow || !_rainbowMode || IsDisposed || !IsHandleCreated || !Visible) return;
            _flowPhase = (_flowPhase + 1) % RainbowColors.Length;
            // 注意：不要用 WM_SETREDRAW 包这一批 STYLESETFORE。Scintilla 是自己调 RedrawWindow
            // 重画的，不受 WM_SETREDRAW 抑制，实测包了之后 CPU 一点没降（13% vs 14%）。
            ApplyRainbowStylesCore(false);
        };
        // 标签页切换显示时补着色：隐藏标签页的 Scintilla 控件 Visible=false 会跳过着色，
        // 切回显示（Visible=true）时必须重新全量着色，否则新标签页文字仍是默认黑。
        VisibleChanged += (_, _) => { if (Visible) ScheduleRainbow(); };
        // 構文の色分け・行番号・自動インデントは使わない（メモ帳と同じ素のエディタ）。
        // 折りたたみは OnHandleCreated で ApplyFoldMargin を呼んで有効化する（選択行の折りたたみ機能）
        LexerName = null;
        ViewWhitespace = WhitespaceMode.Invisible;
        ViewEol = false;
        IndentationGuides = IndentView.None;
        TabIndents = false;
        BackspaceUnindents = false;
        UseTabs = true;
        TabWidth = 8;
        // 改行は LF に統一し、貼り付けた文字列の改行もそろえる
        EolMode = Eol.Lf;
        PasteConvertEndings = true;
        // 最終行より先へはスクロールさせない（メモ帳と同じ。ホイールで最後の行まで届く）
        EndAtLastLine = true;
        // 右クリック メニューは MemoPad 側で出すので、Scintilla の既定メニューは使わない
        UsePopup(PopupMode.Never);
        // 折り返さないときの横スクロール範囲は、表示した行に合わせて自動で広げる
        ScrollWidth = 1;
        ScrollWidthTracking = true;
    }

    /// <summary>
    /// 支援技術（スクリーン リーダー）と <c>docs\tools\test-tabclick.ps1</c> から本文を見えるようにする。
    /// Scintilla は素の Control なので、既定では UI Automation に Pane としてしか出ない。
    /// </summary>
    protected override WinForms.AccessibleObject CreateAccessibilityInstance() => new EditorAccessibleObject(this);

    private sealed class EditorAccessibleObject(PlainTextEdit owner) : WinForms.Control.ControlAccessibleObject(owner)
    {
        public override WinForms.AccessibleRole Role => WinForms.AccessibleRole.Text;
        public override string? Name { get => "本文"; set { } }
        public override string? Value { get => owner.Text; set => owner.ReplaceAllText(value ?? ""); }
        public override WinForms.AccessibleStates State => WinForms.AccessibleStates.Focusable
            | (owner.Focused ? WinForms.AccessibleStates.Focused : WinForms.AccessibleStates.None);
    }

    /// <summary>支援技術から本文を差し替えられたときに使う（読み取りが主なので通常は呼ばれない）。</summary>
    private void ReplaceAllText(string text) => Text = text;


    /// <summary>
    /// true の間は Scintilla の空き時間の処理（折り返しの計算）を止める。
    ///
    /// Scintilla は折り返し位置の計算を 10 ms ずつに区切り、空き時間（10 ms 間隔のタイマー）で進める。
    /// 12 万行では約 4 秒かかり、その間にウィンドウを動かすと、マウスの移動と計算が同じ順番待ちの列に
    /// 交互に並んで 1 コマごとに最大 10 ms 待たされる（FB-21。実測でウィンドウ位置の更新間隔が
    /// 16.7 ms → 71 ms に落ちた）。移動・リサイズのあいだだけ止めると 16.7 ms に戻る。
    ///
    /// 画面に見えている範囲の折り返しは描画のときに計算されるので、止めていても表示は正しい。
    /// 止めた分の計算は、<see cref="MainForm"/> がフラグを下ろした時点で再開して最後まで終わる。
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool PauseIdleWork { get; set; }

    /// <summary>Ctrl＋ホイール（delta は WM_MOUSEWHEEL の値）。</summary>
    public event Action<int>? ZoomWheel;

    /// <summary>右クリックまたはメニュー キー。</summary>
    public event Action? ContextMenuRequested;

    /// <summary>ファイルがドロップされた。</summary>
    public event Action<string[]>? FilesDropped;

    /// <summary>カーソル位置や選択範囲が変わった。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>右端で折り返すか。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool WrapText
    {
        get => _wordWrap;
        set
        {
            _wordWrap = value;
            WrapMode = value ? WrapMode.Word : WrapMode.None;
        }
    }

    /// <summary>ダーク テーマ用のスクロールバーにするか。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool DarkScrollBars
    {
        get => _darkScrollBars;
        set
        {
            _darkScrollBars = value;
            if (IsHandleCreated) ApplyScrollBarTheme();
        }
    }

    /// <summary>選択範囲の長さ（文字数）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectionLength
    {
        // 反向選択（下から上へドラッグ）では SelectionStart > SelectionEnd になるので、
        // 差そのままだと負になって「選択していない」扱いになる。絶対値を返す。
        get => Math.Abs(SelectionEnd - SelectionStart);
        set => SelectionEnd = SelectionStart + Math.Max(0, value);
    }

    /// <summary>
    /// MemoPad 增强：彩虹模式。开启后文字按红橙黄绿青蓝紫循环着色（全局连续每 4 字符一组），
    /// 只改显示样式不影响文件内容。
    /// 注意：绝不能在 StyleNeeded 回调里发消息回 Scintilla（ScintillaNET 的 StyleNeeded 走 WM_REFLECT
    /// 反射链，回调里 SendMessage 会触发新的 SCN_STYLENEEDED 无限递归导致栈溢出）。
    /// 因此本实现不订阅 StyleNeeded，改为「普通消息上下文全量着色 + TextChanged 延迟重刷」。
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool RainbowMode
    {
        get => _rainbowMode;
        set
        {
            if (_rainbowMode == value) return;
            _rainbowMode = value;
            if (!IsHandleCreated) return;
            if (value)
            {
                ApplyRainbowStyles();
                ScheduleRainbow();
                if (_rainbowFlow) _flowTimer.Start();
            }
            else
            {
                _flowTimer.Stop();
                StyleClearAll();
            }
        }
    }

    /// <summary>把彩虹 7 色写进 Style 40-46：前景色 + 同色系的浅底块（方案②）。
    /// 底块不在代码里写死"白底浅彩"，而是与当前默认背景色按比例混合——浅色主题取 22%、
    /// 深色主题取 35%，这样换背景色/深色主题都不会出现刺眼的色块。22% 是实测下来的平衡点：
    /// 再淡（12%）几乎看不出分块，再浓（35%）就偏荧光笔、干扰阅读。</summary>
    private void ApplyRainbowStyles()
    {
        ApplyRainbowStylesCore(true);
        // 改完样式定义要整块重绘一次：Scintilla 只在文本变化时按"脏行"刷新，
        // 首次上色后某些行（尤其是空行）的旧内容没被擦掉，会留下一条整行纯色块。
        if (IsHandleCreated) Invalidate();
    }

    /// <summary>把 7 个色写进 Style 40-46。_flowPhase 用来做「彩虹流动」（跑马灯）：
    /// 每帧把色位整体平移一格，字符的样式号不动、只是同一个样式号换了颜色，
    /// 看上去就是彩色波浪沿文字滚动，而正文、光标、选区一概不受影响。
    /// setBack=false 时只改前景色（流动帧用，避免多余的整窗重绘）。</summary>
    private void ApplyRainbowStylesCore(bool setBack)
    {
        var back = Styles[Style.Default].BackColor;
        var light = back.GetBrightness() > 0.5;
        var ratio = light ? 0.22 : 0.35;
        for (var i = 0; i < RainbowColors.Length; i++)
        {
            var color = RainbowColors[(_flowPhase + i) % RainbowColors.Length];
            // Scintilla 的 STYLESETFORE 收的是 COLORREF（0x00BBGGRR），不是 0xRRGGBB。
            // 早先直接传 ToArgb() 的低 24 位，等于把红蓝通道对调：红色显示成深蓝紫，
            // 屏幕上几乎看不到红/橙/黄，整篇偏蓝。这里按 BGR 摆位，七色才是真彩虹。
            var rgb = (color.B << 16) | (color.G << 8) | color.R;
            DirectMessage(SciStyleSetFore, new IntPtr(RainbowStyleFirst + i), new IntPtr(rgb));
            if (!setBack) continue;

            // 底块关掉时把背景写回默认色，免得沿用上一轮残留的浅色块
            var block = _rainbowBlocks ? BlendColor(back, color, ratio) : back;
            var blockRgb = (block.B << 16) | (block.G << 8) | block.R;
            DirectMessage(SciStyleSetBack, new IntPtr(RainbowStyleFirst + i), new IntPtr(blockRgb));
        }
    }

    /// <summary>彩虹流动（跑马灯）：开启后 7 个色位每 120ms 平移一格，颜色沿文字滚动。
    /// 只在彩虹模式下生效；关掉时立刻停表并把色位归零，颜色回到初始顺序。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool RainbowFlow
    {
        get => _rainbowFlow;
        set
        {
            if (_rainbowFlow == value) return;
            _rainbowFlow = value;
            if (value)
            {
                if (IsHandleCreated && _rainbowMode) _flowTimer.Start();
                return;
            }
            _flowTimer.Stop();
            _flowPhase = 0;
            if (IsHandleCreated && _rainbowMode) ApplyRainbowStyles();
        }
    }

    /// <summary>每 N 个字符换一个色。改这个值必须重新给正文分配样式号，
    /// 所以走 ScheduleRainbow 延迟重刷，不在这里直接动 Scintilla（未显示时调会崩）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int RainbowGroupChars
    {
        get => _rainbowGroupChars;
        set
        {
            var n = Math.Max(1, value);
            if (_rainbowGroupChars == n) return;
            _rainbowGroupChars = n;
            if (IsHandleCreated && _rainbowMode) ScheduleRainbow();
        }
    }

    /// <summary>彩虹是否按行着色（一行一色，行号循环七色）。开启后忽略分组字数、整行同色；
    /// 改完走 ScheduleRainbow 延迟全量重刷（未显示时直接动 Scintilla 会崩）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool RainbowByLine
    {
        get => _rainbowByLine;
        set
        {
            if (_rainbowByLine == value) return;
            _rainbowByLine = value;
            if (IsHandleCreated && _rainbowMode) ScheduleRainbow();
        }
    }

    /// <summary>彩虹文字是否叠加同色系浅底块（默认关）：关掉后只剩文字本身的颜色，
    /// 荧光笔标记铺上去最干净、最像真荧光笔。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool RainbowBlocks
    {
        get => _rainbowBlocks;
        set
        {
            if (_rainbowBlocks == value) return;
            _rainbowBlocks = value;
            if (IsHandleCreated && _rainbowMode) ApplyRainbowStyles();
        }
    }

    /// <summary>按 ratio 把 fore 混进 back（ratio=0 得到 back，ratio=1 得到 fore）。</summary>
    private static System.Drawing.Color BlendColor(System.Drawing.Color back, System.Drawing.Color fore, double ratio)
    {
        int Mix(int b, int f) => (int)Math.Round(b + (f - b) * ratio);
        return System.Drawing.Color.FromArgb(Mix(back.R, fore.R), Mix(back.G, fore.G), Mix(back.B, fore.B));
    }

    // =====================================================================
    // 手动标记（荧光笔）：给选中的文字铺一层半透明的彩色底
    // 用独立的指示器图层，不占样式号，所以彩虹色照旧、文字颜色不变
    // =====================================================================

    /// <summary>初始化 5 个标记指示器槽（句柄创建后调用一次）。</summary>
    private void ConfigureMarkIndicators()
    {
        for (var i = 0; i < MarkColorCount; i++)
        {
            var slot = MarkSlotFirst + i;
            DirectMessage(SciIndicSetStyle, new IntPtr(slot), new IntPtr(IndicRoundBox));
            // INDICSETFORE 和 STYLESETFORE 一样收 COLORREF（0x00BBGGRR），而 MarkColors 里存的是
            // 0xRRGGBB，必须换位。不换的话：黄标记画成天蓝、粉画成紫、蓝画成棕黄、紫画成粉红
            // （用户实测对照：黄→蓝、绿→绿、粉→紫、蓝→棕、紫→粉）。
            var cref = ((MarkColors[i] & 0xFF) << 16) | (MarkColors[i] & 0xFF00) | ((MarkColors[i] >> 16) & 0xFF);
            DirectMessage(SciIndicSetFore, new IntPtr(slot), new IntPtr(cref));
            DirectMessage(SciIndicSetAlpha, new IntPtr(slot), new IntPtr(90));
            DirectMessage(SciIndicSetOutlineAlpha, new IntPtr(slot), new IntPtr(110));
            DirectMessage(SciIndicSetUnder, new IntPtr(slot), new IntPtr(1));
        }
    }

    /// <summary>MarkSelection</summary>
    public bool MarkSelection(int colorIndex)
    {
        var s = RawSelectionStart;
        var e = RawSelectionEnd;
        return MarkSelection(colorIndex, Math.Min(s, e), Math.Abs(e - s));
    }

    /// <summary>给一段字节范围上色：直接用传进来的 start/length，不再读实时选区。
    /// 右键菜单必须走这个版本——Scintilla 收到右键按下会挪插入点，等菜单点下去再读选区就已经变了。</summary>
    public bool MarkSelection(int colorIndex, int start, int length)
    {
        if (!IsHandleCreated || length <= 0) return false;
        var wasClean = !IsModified;
        var slot = MarkSlotFirst + Math.Clamp(colorIndex, 0, MarkColorCount - 1);
        // 先清掉这段上其它颜色的旧标记，避免两层颜色叠在一起发脏
        ClearMarksInRange(start, length);
        DirectMessage(SciSetIndicatorCurrent, new IntPtr(slot), IntPtr.Zero);
        DirectMessage(SciIndicatorFillRange, new IntPtr(start), new IntPtr(length));
        TraceMark(start, length, colorIndex);
        // 改指示器也会让 Scintilla 置「已修改」位；标记不是正文改动，
        // 原来干净就把保存点挪回来，免得标签冒出 *、关闭时白白重写一遍正文
        if (wasClean) SetSavePoint();
        return true;
    }

    /// <summary>把每次标记实际落的字节范围写进 %TEMP%\memopad-mark.log，用于核对「标的是不是用户选的那段」。</summary>
    private void TraceMark(int start, int length, int colorIndex)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(DateTime.Now.ToString("HH:mm:ss.fff"))
              .Append(" color=").Append(colorIndex)
              .Append(" start=").Append(start)
              .Append(" len=").Append(length)
              .Append(" selStart=").Append(SelectionStart)
              .Append(" selEnd=").Append(SelectionEnd)
              .Append(" rawSelStart=").Append((int)DirectMessage(SciGetSelectionStart, IntPtr.Zero, IntPtr.Zero))
              .Append(" rawSelEnd=").Append((int)DirectMessage(SciGetSelectionEnd, IntPtr.Zero, IntPtr.Zero))
              .Append(" textLengthProp=").Append(TextLength)
              .Append(" freezeStart=").Append(_ctxSelectionStart)
              .Append(" freezeLen=").Append(_ctxSelectionLength)
              .Append(" line=").Append(LineFromPosition(start))
              .Append(" docLen=").Append((int)DirectMessage(SciGetLength, IntPtr.Zero, IntPtr.Zero))
              .Append(" textChar=[").Append(GetTextRange(start, length).Replace("\r", "\\r").Replace("\n", "\\n")).Append("]");
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "memopad-mark.log"), sb.ToString() + Environment.NewLine);
        }
        catch
        {
            // 日志写不进去不影响标记本身
        }
    }

    /// <summary>清掉当前选区内的所有标记。</summary>
    public bool ClearMarkInSelection()
    {
        var s = RawSelectionStart;
        var e = RawSelectionEnd;
        return ClearMarkInSelection(Math.Min(s, e), Math.Abs(e - s));
    }

    /// <summary>清掉指定字节范围内的标记（右键菜单用定格范围，理由同 MarkSelection）。</summary>
    public bool ClearMarkInSelection(int start, int length)
    {
        if (!IsHandleCreated || length <= 0) return false;
        var wasClean = !IsModified;
        ClearMarksInRange(start, length);
        if (wasClean) SetSavePoint();
        return true;
    }

    /// <summary>清掉全文所有标记。</summary>
    public void ClearAllMarks()
    {
        if (!IsHandleCreated) return;
        var byteLen = (int)DirectMessage(SciGetLength, IntPtr.Zero, IntPtr.Zero);
        if (byteLen <= 0) return;
        var wasClean = !IsModified;
        ClearMarksInRange(0, byteLen);
        if (wasClean) SetSavePoint();
    }

    private void ClearMarksInRange(int start, int len)
    {
        for (var i = 0; i < MarkColorCount; i++)
        {
            DirectMessage(SciSetIndicatorCurrent, new IntPtr(MarkSlotFirst + i), IntPtr.Zero);
            DirectMessage(SciIndicatorClearRange, new IntPtr(start), new IntPtr(len));
        }
    }

    /// <summary>把当前所有标记读成 (起始字节, 长度字节, 颜色序号) 供持久化。
    /// Scintilla 的 position 本身就是字节偏移；逐位置查指示器掩码，相邻同色合并成一段。</summary>
    public List<(int Start, int Length, int Color)> ReadMarks()
    {
        var result = new List<(int, int, int)>();
        if (!IsHandleCreated) return result;
        var byteLen = (int)DirectMessage(SciGetLength, IntPtr.Zero, IntPtr.Zero);
        if (byteLen <= 0) return result;

        var runStart = 0;
        var runColor = -1;
        for (var pos = 0; pos <= byteLen; pos++)
        {
            var color = -1;
            if (pos < byteLen)
            {
                var mask = (int)DirectMessage(SciIndicatorAllOnFor, new IntPtr(pos), IntPtr.Zero);
                for (var i = 0; i < MarkColorCount; i++)
                {
                    if ((mask & (1 << (MarkSlotFirst + i))) != 0) { color = i; break; }
                }
            }
            if (color != runColor)
            {
                if (runColor >= 0 && pos > runStart) result.Add((runStart, pos - runStart, runColor));
                runColor = color;
                runStart = pos;
            }
        }
        return result;
    }

    /// <summary>按持久化列表还原标记（先清空现有）。句柄还没创建时先挂起，等 OnHandleCreated 再画。</summary>
    public void ApplyMarks(IReadOnlyList<(int Start, int Length, int Color)> marks)
    {
        if (IsDisposed) return;
        if (!IsHandleCreated)
        {
            _pendingMarks = marks;
            return;
        }
        ApplyMarksNow(marks);
    }

    private void ApplyMarksNow(IReadOnlyList<(int Start, int Length, int Color)> marks)
    {
        try
        {
            var wasClean = !IsModified;
            ClearAllMarks();
            var byteLen = (int)DirectMessage(SciGetLength, IntPtr.Zero, IntPtr.Zero);
            if (byteLen <= 0 || marks.Count == 0) return;
            foreach (var m in marks)
            {
                var s = Math.Clamp(m.Start, 0, byteLen);
                var e = Math.Clamp(m.Start + m.Length, 0, byteLen);
                if (e <= s) continue;
                var slot = MarkSlotFirst + Math.Clamp(m.Color, 0, MarkColorCount - 1);
                DirectMessage(SciSetIndicatorCurrent, new IntPtr(slot), IntPtr.Zero);
                DirectMessage(SciIndicatorFillRange, new IntPtr(s), new IntPtr(e - s));
            }
            // 还原标记同样会动「已修改」位，原来干净的话恢复保存点
            if (wasClean) SetSavePoint();
        }
        catch (Exception ex)
        {
            LogRainbowError("ApplyMarks", ex);
        }
    }

    /// <summary>全量着色：把全文按「每 4 个字符一组」分配彩虹色。只在普通消息上下文调用
    /// （不在任何 Scintilla 反射回调栈里），此时发消息安全：即便 Scintilla 因 StartStyling 再发
    /// SCN_STYLENEEDED，反射回来也没有 StyleNeeded 订阅者，递归自然终止。
    /// 窗口关闭/句柄销毁竞态下 DirectMessage 可能对失效句柄发消息，全部用 try/catch 包住并写日志。
    /// 分组必须按「字符」：SCI_SETSTYLING 以字节计长，中文 1 字 3 字节，按字节硬切会把汉字的字节
    /// 劈到两个样式段上，Scintilla 绘制不出来就退化显示成 \xHH 字节转义（本机实测复现过）。</summary>
    private void RainbowColouriseAll()
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            // 必须取「字节长度」：SCI_GETLENGTH 返回文档的字节数。TextLength 是字符数，
            // 中文文档里它比字节数小得多，当字节长度用会让循环提前结束、末组被截断到字中间。
            var byteLen = (int)DirectMessage(SciGetLength, IntPtr.Zero, IntPtr.Zero);
            if (byteLen <= 0) return;

            // 只调用一次 StartStyling，后续 SetStyling 会在 Scintilla 内部自动向前推进
            DirectMessage(SciStartStyling, IntPtr.Zero, new IntPtr(0x1F));

            if (_rainbowByLine) ColouriseByLineCore(byteLen);
            else ColouriseGroupCore(byteLen);
        }
        catch (Exception ex)
        {
            LogRainbowError("彩虹着色失败", ex);
        }
    }

    /// <summary>按「每 N 个字符一组」分配彩虹色（默认模式）。分组必须按字符：SCI_SETSTYLING 以字节计长，
    /// 中文 1 字 3 字节，按字节硬切会把汉字劈到两个样式段，退化显示成 \xHH 字节转义（本机实测复现过）。</summary>
    private void ColouriseGroupCore(int byteLen)
    {
        var pos = 0;
        var groupIndex = 0;
        while (pos < byteLen)
        {
            // 让 Scintilla 自己往后数 _rainbowGroupChars 个字符，返回的字节位置必然落在字符边界上
            var next = (int)DirectMessage(SciPositionRelative, new IntPtr(pos), new IntPtr(_rainbowGroupChars));
            if (next <= pos) next = byteLen;        // 防御：位置没前进就直接收尾，避免死循环
            if (next > byteLen) next = byteLen;

            DirectMessage(SciSetStyling, new IntPtr(next - pos),
                new IntPtr(RainbowStyleFirst + (groupIndex % RainbowColors.Length)));
            pos = next;
            groupIndex++;
        }
    }

    /// <summary>按行分配彩虹色：第 N 行整行同色，行号循环七色。行首/行尾天然落在字符边界上，
    /// 不存在劈汉字问题；行长度含行尾换行符（EOL 不可见），整行一段样式。空行也要消耗掉 EOL 长度，
    /// 否则样式游标错位、后续行颜色串行。</summary>
    private void ColouriseByLineCore(int byteLen)
    {
        var lineCount = (int)DirectMessage(SciGetLineCount, IntPtr.Zero, IntPtr.Zero);
        for (var line = 0; line < lineCount; line++)
        {
            var lineStart = (int)DirectMessage(SciPositionFromLine, new IntPtr(line), IntPtr.Zero);
            if (lineStart >= byteLen) break;
            var lineLen = (int)DirectMessage(SciLineLength, new IntPtr(line), IntPtr.Zero);
            if (lineLen <= 0) continue;            // 末尾无 EOL 的空行，无可着色内容
            var style = RainbowStyleFirst + (line % RainbowColors.Length);
            DirectMessage(SciSetStyling, new IntPtr(lineLen), new IntPtr(style));
        }
    }

    /// <summary>文本变化后延迟重刷彩虹。注意：不能用 BeginInvoke——其回调可能在 WM_NOTIFY 反射
    /// 消息的 WndProc 栈内执行（InvokeMarshaledCallbacks），此时 SendMessage 会重入正在处理通知的
    /// Scintilla 原生层导致访问违规。用 WinForms Timer 调度：WM_TIMER 是独立消息，绝不进通知栈。</summary>
    private void ScheduleRainbow()
    {
        if (!_rainbowMode || !IsHandleCreated) return;
        if (_rainbowScheduled) return;
        _rainbowScheduled = true;
        _rainbowTimer.Stop();
        _rainbowTimer.Start();
    }

    /// <summary>彩虹相关异常写固定日志，便于现场定位（始终启用）。</summary>
    private static void LogRainbowError(string what, Exception ex)
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "memopad-rainbow.log");
            System.IO.File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {what}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n");
        }
        catch { }
    }

    /// <summary>開始位置と長さで選択する（RichTextBox と同じ呼び出し方にするための別名）。</summary>
    public void Select(int start, int length)
    {
        var max = TextLength;
        start = Math.Clamp(start, 0, max);
        var end = Math.Clamp(start + Math.Max(0, length), start, max);
        SetSelection(end, start);
    }

    /// <summary>カーソルが見える位置までスクロールする（RichTextBox と同じ名前にしておく）。</summary>
    public void ScrollToCaret() => ScrollCaret();

    /// <summary>元に戻す履歴を捨てる。</summary>
    public void ClearUndo() => EmptyUndoBuffer();

    /// <summary>
    /// 今の状態を「保存済み」として記録する（読み込み直後と保存直後に呼ぶ）。
    /// これ以降に編集されると <see cref="Scintilla.SavePointLeft"/> が上がり、
    /// 元に戻して同じ内容へ戻ると <see cref="Scintilla.SavePointReached"/> が上がる。
    /// 「編集済み」の判定はこの仕組みで行う（文字が変わったかどうかで見ると、
    /// コントロールの初期化でも変更とみなされてしまう）。
    /// </summary>
    public void MarkClean() => SetSavePoint();

    /// <summary>選択範囲を置き換える（RichTextBox の SelectedText セッター相当）。</summary>
    public void ReplaceSelectedText(string text) => ReplaceSelection(text ?? "");

    /// <summary>カーソル位置の「論理行（1 始まり）, 列（1 始まり）」。全文を走査せずに求める。</summary>
    public (int Line, int Column) GetCaretPosition()
    {
        var pos = CurrentPosition;
        var line = LineFromPosition(pos);
        return (line + 1, pos - Lines[line].Position + 1);
    }

    /// <summary>指定した論理行（1 始まり）の先頭へカーソルを移動する。</summary>
    public void GoToLine(int line)
    {
        var index = Math.Clamp(line - 1, 0, Math.Max(0, Lines.Count - 1));
        GotoPosition(Lines[index].Position);
        ScrollCaret();
        Focus();
    }

    /// <summary>
    /// フォント・色・ズーム倍率をまとめて反映する。
    /// ズームは Scintilla の Zoom（整数ポイントの増減）ではなく、フォント サイズを倍率で直接計算する
    /// （メモ帳と同じく % 指定なので、こちらのほうが表示と一致する）。
    /// </summary>
    public void ApplyTextAppearance(string family, double sizePoints, bool bold, bool italic,
                                    System.Drawing.Color back, System.Drawing.Color fore, int zoomPercent)
    {
        _baseFontSize = Math.Max(1, sizePoints);
        _zoomPercent = Math.Clamp(zoomPercent, 10, 500);
        var scaled = Math.Max(1, _baseFontSize * _zoomPercent / 100.0);

        var style = Styles[Style.Default];
        style.Font = family;
        style.SizeF = (float)scaled;
        style.Bold = bold;
        style.Italic = italic;
        style.BackColor = back;
        style.ForeColor = fore;
        // 既定スタイルを他のスタイル番号へ複製する（プレーン テキストなので全体が既定スタイル）
        StyleClearAll();

        // 本文の外側（最終行より下など）も同じ背景色にする
        BackColor = back;
        ForeColor = fore;
        CaretForeColor = fore;
        SelectionBackColor = SelectionBackFor(back, fore);
        SelectionTextColor = back;
        ApplyLeftPadding();
        // 折りたたみマーカーの色も本文の配色に合わせる
        _foldMarkerFore = fore;
        _foldMarkerBack = back;
        if (IsHandleCreated) ApplyFoldMarkerColors();

        // MemoPad 增强：StyleClearAll 会把彩虹自定义样式也清成默认，开启时重设色并延迟全量重着色
        // （着色必须在窗口显示后进行，见 ScheduleRainbow/彩虹 Timer 注释）
        if (_rainbowMode && IsHandleCreated)
        {
            ApplyRainbowStyles();
            ScheduleRainbow();
        }
    }

    /// <summary>行間隔（論理 px）。0 は既定。上半分を ExtraAscent、下半分を ExtraDescent に配る。</summary>
    public void SetLineSpacing(int logicalPx)
    {
        var px = Math.Max(0, logicalPx);
        var scale = DeviceDpi / 96.0;
        ExtraAscent = (int)(px / 2 * scale);
        ExtraDescent = (int)((px - px / 2) * scale);
    }

    /// <summary>折りたたみ用の欄（1 番）とマーカーを設定する。Scintilla の折りたたみは既定では無効なので明示的に有効化する。</summary>
    private void ApplyFoldMargin()
    {
        var scale = DeviceDpi / 96.0;
        var margin = Margins[1];
        margin.Type = MarginType.Symbol;
        margin.Mask = 0xFE000000u;
        margin.Sensitive = true;
        margin.Width = (int)(14 * scale);
        Markers[FoldMarkerMin].Symbol = MarkerSymbol.BoxPlusConnected;
        Markers[FoldMarkerMin + 1].Symbol = MarkerSymbol.BoxMinusConnected;
        Markers[FoldMarkerMin + 2].Symbol = MarkerSymbol.TCorner;
        Markers[FoldMarkerMin + 3].Symbol = MarkerSymbol.LCorner;
        Markers[FoldMarkerMin + 4].Symbol = MarkerSymbol.VLine;
        Markers[FoldMarkerMin + 5].Symbol = MarkerSymbol.BoxPlus;
        Markers[FoldMarkerMin + 6].Symbol = MarkerSymbol.BoxMinus;
        ApplyFoldMarkerColors();
        AutomaticFold = AutomaticFold.Show | AutomaticFold.Click;
    }

    private void ApplyFoldMarkerColors()
    {
        for (var i = FoldMarkerMin; i <= FoldMarkerMax; i++)
        {
            DirectMessage(SciMarkerSetFore, new IntPtr(i), new IntPtr(System.Drawing.ColorTranslator.ToWin32(_foldMarkerFore)));
            DirectMessage(SciMarkerSetBack, new IntPtr(i), new IntPtr(System.Drawing.ColorTranslator.ToWin32(_foldMarkerBack)));
        }
    }

    /// <summary>選択範囲の複数行を折りたたむ。1 行だけの選択や、範囲内に既存の折りたたみがある場合は false（何もしない）。</summary>
    public bool FoldSelectedLines()
    {
        if (SelectionStart == SelectionEnd) return false;
        var first = LineFromPosition(SelectionStart);
        var last = LineFromPosition(SelectionEnd);
        if (first == last) return false;

        var head = Lines[first];
        // 先頭行が既に折りたたみの先頭なら、折りたたみを切り替える（折りたたまれていれば展開）
        if ((head.FoldLevelFlags & FoldLevelFlags.Header) != 0)
        {
            head.ToggleFold();
            return true;
        }
        // 範囲内に既存の折りたたみがあると壊れるので、その場合は何もしない
        for (var i = first + 1; i <= last; i++)
        {
            if ((Lines[i].FoldLevelFlags & FoldLevelFlags.Header) != 0) return false;
        }
        // 先頭行をヘッダー（SC_FOLDLEVELHEADERFLAG=0x2400）、残りを折りたたみ行（0x401）にする
        head.FoldLevel = 0x2400;
        for (var j = first + 1; j <= last; j++)
        {
            Lines[j].FoldLevel = 0x401;
        }
        head.ToggleFold();
        SetSelection(head.Position, head.Position);
        return true;
    }

    /// <summary>カーソル位置の折りたたみを展開する。折りたたみの内部にいる場合はその外側のヘッダーを展開する。</summary>
    public void UnfoldAtCaret()
    {
        var index = LineFromPosition(CurrentPosition);
        var line = Lines[index];
        if ((line.FoldLevelFlags & FoldLevelFlags.Header) != 0)
        {
            if (!line.Expanded) line.ToggleFold();
            return;
        }
        var parent = line.FoldParent;
        if (parent >= 0)
        {
            var header = Lines[parent];
            if (!header.Expanded) header.ToggleFold();
        }
    }

    /// <summary>すべての折りたたみを展開し、折りたたみレベルを既定（0x400）に戻す。</summary>
    public void UnfoldAllFolds()
    {
        FoldAll(FoldAction.Expand);
        for (var i = 0; i < Lines.Count; i++)
        {
            Lines[i].FoldLevel = 0x400;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        Services.PerfLog.Mark("生成 Scintilla 句柄");
        base.OnHandleCreated(e);
        ApplyScrollBarTheme();
        ApplyLeftPadding();
        ApplyFoldMargin();
        RegisterFileDropTarget();
        // MemoPad 增强：标记（荧光笔）指示器要先配好样式；若句柄创建前已收到标记，这时补画
        ConfigureMarkIndicators();
        if (_pendingMarks is { } pending)
        {
            ApplyMarksNow(pending);
            _pendingMarks = null;
        }
        // MemoPad 增强：彩虹模式可能在句柄创建前已设置（那时无法着色），这里补挂；
        // 只设色（SCI_STYLESETFORE 这类 setter 消息安全），全量着色留给 ScheduleRainbow
        // （Timer 会等窗口显示后再执行 StartStyling/SetStyling，避免样式缓冲未就绪崩溃）
        TextChanged += (_, _) => ScheduleRainbow();
        if (_rainbowMode)
        {
            ApplyRainbowStyles();
            ScheduleRainbow();
        }
        Services.PerfLog.Mark("Scintilla の初期化を完了");
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (IsHandleCreated) RevokeDragDrop(Handle);
        base.OnHandleDestroyed(e);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        if (IsHandleCreated) ApplyLeftPadding();
    }

    /// <summary>カーソル・選択範囲が動いたときに <see cref="SelectionChanged"/> を上げる。</summary>
    protected override void OnUpdateUI(UpdateUIEventArgs e)
    {
        base.OnUpdateUI(e);
        var start = SelectionStart;
        var end = SelectionEnd;
        if (start == _lastSelStart && end == _lastSelEnd) return;
        _lastSelStart = start;
        _lastSelEnd = end;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void WndProc(ref WinForms.Message m)
    {
        // 本文はウィンドウの左右いっぱいに広がるので、縁のリサイズ判定は親フォームへ譲る
        if (ChromeHitTest.TryPassToFrame(this, ref m)) return;
        switch (m.Msg)
        {
            // 折り返しの計算（空き時間の処理）を止めているあいだは、そのタイマーを通さない（FB-21）
            case WM_TIMER when PauseIdleWork && (int)m.WParam == ScintillaIdleTimerId:
                return;
            // Scintilla は Windows では支援技術に何も返さない（アクセシビリティ対応は GTK 版だけ）。
            // 本文が読み上げソフトから読めなくなるので、Windows Forms のアクセシブル オブジェクトを自分で返す
            case WM_GETOBJECT when (int)m.LParam == OBJID_CLIENT && AccessibilityObject is { } acc:
                var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");   // IID_IAccessible
                m.Result = LresultFromObject(ref iid, m.WParam, acc);
                return;
            case WM_MOUSEWHEEL when (WinForms.Control.ModifierKeys & WinForms.Keys.Control) != 0:
                // Scintilla 自身のズームは使わず、アプリのズーム（ステータスバーの % と連動）に回す
                ZoomWheel?.Invoke((short)((long)m.WParam >> 16));
                return;
            case WM_RBUTTONDOWN:
                // 右键按下：先定格此刻的选区，再把消息交给 Scintilla（它会把插入点挪到点击处，
                // 点在选区外时选区当场就被清掉，菜单里再读就标偏了）
                CaptureContextSelection();
                _ctxFromRightClick = true;
                break;
            case WM_CONTEXTMENU:
                if (!_ctxFromRightClick) CaptureContextSelection();   // 键盘呼出的菜单没有先前右键，用实时选区
                _ctxFromRightClick = false;
                ContextMenuRequested?.Invoke();
                return;
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// Scintilla が自分で登録しているドロップ先を外し、ファイルだけを受け取る自前のものに差し替える。
    /// エディタに任せるとファイル名が本文に貼り付けられてしまう（RichEdit のときと同じ理由。
    /// 経緯は <see cref="FileDropTarget"/>）。
    /// </summary>
    private void RegisterFileDropTarget()
    {
        WinForms.Application.OleRequired();
        RevokeDragDrop(Handle);
        // ドロップの処理中に開くと、ファイルが大きいときにドラッグ元を待たせてしまうので、
        // メッセージを処理し終えてから開く
        _dropTarget ??= new FileDropTarget(files => BeginInvoke(() => FilesDropped?.Invoke(files)));
        var hr = RegisterDragDrop(Handle, _dropTarget);
        Services.PerfLog.Write($"RegisterDragDrop hr=0x{hr:X8}");
    }

    private void ApplyScrollBarTheme()
    {
        SetWindowTheme(Handle, _darkScrollBars ? "DarkMode_Explorer" : "Explorer", null);
    }

    /// <summary>本文の左に余白を入れる（メモ帳の左余白に合わせる）。Scintilla の余白は 0 番の欄で作る。</summary>
    private void ApplyLeftPadding()
    {
        var scale = DeviceDpi / 96.0;
        for (var i = 0; i < Margins.Count; i++) Margins[i].Width = 0;
        Margins[0].Type = MarginType.Symbol;
        Margins[0].Mask = 0;
        Margins[0].Sensitive = false;
        Margins[0].Width = (int)(LeftPadding * scale);
    }

    /// <summary>選択範囲の背景色。背景色と文字色の中間に置き、どの配色でも文字が読めるようにする。</summary>
    private static System.Drawing.Color SelectionBackFor(System.Drawing.Color back, System.Drawing.Color fore)
    {
        static int Mix(int a, int b) => (int)Math.Round(a * 0.35 + b * 0.65);
        return System.Drawing.Color.FromArgb(Mix(back.R, fore.R), Mix(back.G, fore.G), Mix(back.B, fore.B));
    }
}
