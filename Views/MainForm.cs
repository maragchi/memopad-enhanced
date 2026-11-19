using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Memopad.Dialogs;
using Memopad.Models;
using Memopad.Services;

namespace Memopad.Views;

/// <summary>
/// メイン ウィンドウ（v0.8.0 で WPF の MainWindow から Windows Forms に置き換え。起動時間をメモ帳並みにするため）。
/// タブの管理、ファイル操作、検索／置換、表示設定、色の変更をまとめて扱う。
/// 標準のタイトル バーは WM_NCCALCSIZE で外し、TitleBar コントロールにタブとキャプション ボタンを描く。
/// </summary>
public sealed partial class MainForm : Form
{
    private const int ZoomStep = 10;
    private const int ZoomMin = 10;
    private const int ZoomMax = 500;
    private const int TitleHeight = 42;
    private const int MenuHeight = 32;
    private const int StatusHeight = 32;
    private const int ResizeBorder = 6;

    private readonly List<DocumentTab> _tabs = new();
    private readonly string[] _startupFiles;
    private DocumentTab? _current;
    private int _zoomPercent = 100;
    private ThemePalette _palette;
    private bool _firstPaintLogged;

    private readonly TitleBar _titleBar;
    private readonly MenuStrip _menu;
    private readonly FluentMenuRenderer _renderer;
    private readonly Panel _editorHost;
    private readonly StatusBarPanel _statusBar;
    private readonly Panel _findBar;

    private static AppSettings Settings => Program.Settings;

    private DocumentTab? Current => _current;

    public MainForm(string[] args)
    {
        PerfLog.Mark("主窗体构造函数开始");
        _startupFiles = args;
        _palette = ThemeService.Palette(Settings.Theme);
        _renderer = new FluentMenuRenderer(_palette);

        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "MemoPad";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.WindowsDefaultLocation;
        BackColor = _palette.Window;
        Font = UiStyle.PixelFont(UiStyle.UiFontName, S(13));
        AllowDrop = true;
        MinimumSize = new Size(S(420), S(300));
        // 設定は論理 px。クライアント領域がウィンドウ全体になるので Size をそのまま使う
        Size = new Size(S((int)Settings.WindowWidth), S((int)Settings.WindowHeight));
        Icon = LoadAppIcon(32);

        _editorHost = new ChromePanel { Dock = DockStyle.Fill, BackColor = _palette.Window };
        _findBar = BuildFindBar();
        _menu = BuildMenu();
        _statusBar = new StatusBarPanel
        {
            Dock = DockStyle.Bottom,
            Height = S(StatusHeight),
            Palette = _palette,
            Font = UiStyle.PixelFont(UiStyle.UiFontName, S(12)),
        };
        _statusBar.LineEndingClicked += ShowLineEndingMenu;
        _statusBar.EncodingClicked += ShowEncodingMenu;
        _titleBar = new TitleBar
        {
            Dock = DockStyle.Top,
            Height = S(TitleHeight),
            Palette = _palette,
            AppIcon = LoadAppIcon(16),
        };
        _titleBar.TabSelected += tab => SelectTab(tab);
        _titleBar.TabCloseRequested += tab => CloseTab(tab);
        _titleBar.NewTabRequested += () => AddTab(new Document());
        _titleBar.MinimizeRequested += () => WindowState = FormWindowState.Minimized;
        _titleBar.MaximizeRequested += ToggleMaximize;
        _titleBar.CloseRequested += Close;
        _titleBar.Paint += (_, _) =>
        {
            if (_firstPaintLogged) return;
            _firstPaintLogged = true;
            PerfLog.Mark("最初の描画");
        };

        // Dock は Controls の後ろにあるものから外側に置かれる：タイトル行→ステータスバー→メニュー→検索バー→本文
        Controls.Add(_editorHost);
        Controls.Add(_findBar);
        Controls.Add(_menu);
        Controls.Add(_statusBar);
        Controls.Add(_titleBar);
        MainMenuStrip = _menu;
        ResumeLayout(false);

        UpdateViewMenu();
        UpdateThemeMenu();
        if (Settings.WindowMaximized) WindowState = FormWindowState.Maximized;

        // MemoPad 增强：缩放记忆・会话恢复（上次快照存在则无条件恢复，否则新建空标签）
        _zoomPercent = Math.Clamp(Settings.ZoomPercent, 10, 500);
        _statusBar.ZoomText = $"{_zoomPercent}%";
        RestoreSession();
        PerfLog.Mark("主窗体构造函数完成");
    }

    /// <summary>論理 px を現在の表示スケールの物理 px にする。</summary>
    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    /// <summary>exe に埋め込んだアプリ アイコンを指定サイズで取り出す。</summary>
    private static Icon? LoadAppIcon(int size)
    {
        try
        {
            if (Environment.ProcessPath is not { } exe) return null;
            return Icon.ExtractIcon(exe, 0, size) ?? Icon.ExtractAssociatedIcon(exe);
        }
        catch
        {
            // アイコンが取れなくても動作には影響しない
            return null;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        PerfLog.Mark("窗口句柄已生成");
        ThemeService.ApplyWindowFrame(Handle, _palette.IsDark);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        PerfLog.Mark("Load");
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PerfLog.Mark("Shown");
        // 起動引数に渡されたファイルを開く（エクスプローラーからの「プログラムから開く」に対応）
        foreach (var path in _startupFiles)
        {
            if (File.Exists(path)) OpenFile(path);
        }
        Current?.Editor.FocusEditor();
    }

    // =====================================================================
    // 自前のタイトル行：標準の枠を外す（WM_NCCALCSIZE）とヒット テスト（WM_NCHITTEST）
    // =====================================================================

    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int WM_COPYDATA = 0x004A;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CYSIZEFRAME = 33;
    private const int SM_CXPADDEDBORDER = 92;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            // 移動・リサイズのあいだは本文の空き時間の処理（折り返しの計算）を止め、動きを優先する。
            // 大きなファイルを開いた直後にウィンドウを動かすとカクついたため（FB-21。
            // 詳細は PlainTextEdit.PauseIdleWork）。放したら再開して最後まで計算される
            case WM_ENTERSIZEMOVE:
                foreach (var t in _tabs) t.Editor.Edit.PauseIdleWork = true;
                break;

            case WM_EXITSIZEMOVE:
                foreach (var t in _tabs) t.Editor.Edit.PauseIdleWork = false;
                break;

            case WM_NCCALCSIZE:
                // 標準のタイトル バーと枠を外し、ウィンドウ全体をクライアント領域にする。
                // 最大化中は見えない枠の分だけ画面外にはみ出すので、その分を内側に寄せる
                if (IsZoomed(Handle))
                {
                    var dpi = (uint)DeviceDpi;
                    var fx = GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
                    var fy = GetSystemMetricsForDpi(SM_CYSIZEFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
                    // wParam の真偽に関わらず lParam の先頭は対象の矩形
                    var rc = Marshal.PtrToStructure<RECT>(m.LParam);
                    rc.Left += fx;
                    rc.Top += fy;
                    rc.Right -= fx;
                    rc.Bottom -= fy;
                    Marshal.StructureToPtr(rc, m.LParam, false);
                }
                m.Result = IntPtr.Zero;
                return;

            case WM_NCHITTEST:
            {
                var lp = (int)(long)m.LParam;
                var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                var hit = HitTestChrome(p);
                if (hit != 0)
                {
                    m.Result = hit;
                    return;
                }
                break;
            }

            case WM_COPYDATA:
                // 单实例：第二个实例把要打开的文件路径转发过来
                HandleCopyData(ref m);
                return;
        }
        base.WndProc(ref m);
    }

    /// <summary>处理 WM_COPYDATA（dwData=1，UTF-8 文件路径）。打开文件并激活窗口。</summary>
    private void HandleCopyData(ref Message m)
    {
        try
        {
            var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(m.LParam);
            if (cds.dwData == (IntPtr)1 && cds.lpData != IntPtr.Zero && cds.cbData > 0)
            {
                var path = Marshal.PtrToStringUTF8(cds.lpData, cds.cbData);
                if (File.Exists(path)) OpenFile(path);
                ShowWindow(Handle, 9);
                SetForegroundWindow(Handle);
                m.Result = (IntPtr)1;
                return;
            }
        }
        catch
        {
            // 收到的数据不合法时忽略
        }
        m.Result = IntPtr.Zero;
    }

    // =====================================================================
    // MemoPad 增强：被另一个实例请求「亮出来」时调用（由 Program 的监听线程转到 UI 线程）
    // =====================================================================

    /// <summary>把窗口从最小化还原、提到最前并抢回焦点。</summary>
    public void ActivateFromOtherInstance()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        ShowWindow(Handle, 9); // SW_RESTORE（双保险：WindowState 与原生状态有时不同步）
        BringToFront();
        Activate();
        SetForegroundWindow(Handle);
        Current?.Editor.FocusEditor();
    }

    /// <summary>打开另一个实例转交过来的文件（双击 txt「用 MemoPad 打开」时走这里）。</summary>
    public void OpenPathsFromOtherInstance(string[] paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path)) OpenFile(path);
        }
    }

    /// <summary>ウィンドウの縁ならリサイズ、タイトル行ならキャプション（移動）として答える。0 なら既定の判定。</summary>
    private int HitTestChrome(Point p)
    {
        var w = ClientSize.Width;
        var h = ClientSize.Height;
        if (WindowState != FormWindowState.Maximized)
        {
            var b = S(ResizeBorder);
            var left = p.X < b;
            var right = p.X >= w - b;
            var top = p.Y < b;
            var bottom = p.Y >= h - b;
            if (top && left) return HTTOPLEFT;
            if (top && right) return HTTOPRIGHT;
            if (bottom && left) return HTBOTTOMLEFT;
            if (bottom && right) return HTBOTTOMRIGHT;
            if (top) return HTTOP;
            if (bottom) return HTBOTTOM;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
        }
        if (p.Y >= 0 && p.Y < _titleBar.Height && p.X >= 0 && p.X < w)
        {
            // タブやボタンの上は通常のクリックとして扱う（0 を返すと既定の判定＝HTCLIENT になる）
            return _titleBar.HasInteractiveChildAt(_titleBar.PointToClient(PointToScreen(p))) ? 0 : HTCAPTION;
        }
        return 0;
    }

    /// <summary>
    /// その位置（クライアント座標）がタイトル行の空きかウィンドウの縁か。
    /// 子コントロールが <see cref="ChromeHitTest"/> から呼び、true なら当たり判定を親（このフォーム）へ譲る。
    /// </summary>
    internal bool IsWindowFrameAt(Point clientPoint) => HitTestChrome(clientPoint) != 0;

    private void ToggleMaximize() =>
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_titleBar is not null) _titleBar.IsMaximized = WindowState == FormWindowState.Maximized;
    }

    // =====================================================================
    // タブ
    // =====================================================================

    private DocumentTab AddTab(Document document)
    {
        var tab = new DocumentTab(document);
        tab.Editor.ApplyAppearance(Settings, _zoomPercent);
        tab.Editor.CaretChanged += (_, _) => { if (tab == _current) UpdateCaretStatus(); };
        tab.Editor.ContentChanged += (_, _) =>
        {
            if (tab != _current) return;
            UpdatePreviewTitle(tab);
            UpdateContentStatus();
        };
        // RichEdit はホイールや右クリックを自分で受け取るので、アプリ側の操作へ橋渡しする
        tab.Editor.Edit.ZoomWheel += delta => SetZoom(_zoomPercent + (delta > 0 ? ZoomStep : -ZoomStep));
        tab.Editor.Edit.ContextMenuRequested += () => ShowEditorContextMenu(tab);
        tab.Editor.Edit.FilesDropped += files => { foreach (var f in files) if (File.Exists(f)) OpenFile(f); };
        document.PropertyChanged += (_, _) =>
        {
            if (tab == _current) UpdateTitle();
            _titleBar.RefreshTabs();
        };
        tab.Editor.Edit.Visible = false;
        _editorHost.Controls.Add(tab.Editor.Edit);
        _tabs.Add(tab);
        // MemoPad 增强：未保存标签显示正文开头文字作标签名
        UpdatePreviewTitle(tab);
        SelectTab(tab);
        return tab;
    }

    /// <summary>标签标题取正文开头一行（首行前 12 字符 + "…"）。已保存/未保存标签统一使用；预览为空时标签自动回退文件名或未命名。</summary>
    private void UpdatePreviewTitle(DocumentTab tab)
    {
        tab.Document.PreviewText = GetPreviewText(tab.Editor.Text);
    }

    private void SelectTab(DocumentTab? tab)
    {
        _current = tab;
        foreach (var t in _tabs) t.Editor.Edit.Visible = t == tab;
        _titleBar.SetTabs(_tabs, tab);
        if (tab is not null) UpdatePreviewTitle(tab);
        UpdateTitle();
        UpdateCaretStatus();
        UpdateContentStatus();
        if (tab is not null && IsHandleCreated)
        {
            BeginInvoke(() => tab.Editor.FocusEditor());
        }
    }

    private void SelectAdjacentTab(int delta)
    {
        if (_tabs.Count < 2) return;
        var index = _current is null ? 0 : _tabs.IndexOf(_current);
        SelectTab(_tabs[(index + delta + _tabs.Count) % _tabs.Count]);
    }

    /// <summary>タブを閉じる。未保存なら確認する。閉じられなかった（キャンセル）場合は false。</summary>
    private bool CloseTab(DocumentTab tab)
    {
        if (!ConfirmDiscard(tab)) return false;

        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        _editorHost.Controls.Remove(tab.Editor.Edit);
        tab.Editor.Edit.Dispose();
        if (_tabs.Count == 0)
        {
            // 最後のタブを閉じたらメモ帳と同じくウィンドウを閉じる
            _current = null;
            Close();
            return true;
        }
        SelectTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
        return true;
    }

    /// <summary>未保存の変更があれば「保存／保存しない／キャンセル」を確認する。続行してよければ true。</summary>
    private bool ConfirmDiscard(DocumentTab tab)
    {
        if (!tab.Document.IsDirty) return true;
        SelectTab(tab);
        return AskSaveChanges(tab.Document.Title) switch
        {
            SaveChangesResult.Save => SaveTab(tab),
            SaveChangesResult.DontSave => true,
            _ => false,
        };
    }

    // WPF のダイアログは専用のメソッドから呼ぶ（呼び出し側のメソッドが JIT されるときに WPF が読み込まれないようにする）
    private SaveChangesResult AskSaveChanges(string title)
    {
        WpfHost.Ensure(Settings.Theme);
        return SaveChangesDialog.Ask(Handle, title);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel) return;
        // MemoPad 增强：不逐个询问，关闭时把所有标签（含未保存内容）写入会话快照，下次启动无条件恢复
        Settings.WindowMaximized = WindowState == FormWindowState.Maximized;
        if (WindowState == FormWindowState.Normal)
        {
            Settings.WindowWidth = Math.Round(Width * 96.0 / DeviceDpi);
            Settings.WindowHeight = Math.Round(Height * 96.0 / DeviceDpi);
        }
        // MemoPad 增强：关闭前把所有标签自动保存到云盘笔记目录（无论是否手动保存过）
        AutoSaveAllToNotes();
        SaveSessionSnapshot();
        SettingsService.Save(Settings);
    }

    /// <summary>启动时恢复上次会话：快照存在则逐标签重建（有文件路径的直接打开，未保存的读回内容），否则新建一个空标签。</summary>
    private void RestoreSession()
    {
        var manifest = SessionRecovery.Load();
        if (manifest is null || manifest.Tabs.Count == 0)
        {
            SessionRecovery.Clear();
            AddTab(new Document());
            return;
        }
        foreach (var info in manifest.Tabs)
        {
            var path = info.FilePath;
            if (path is not null && info.ContentFile is null)
            {
                // 已保存标签：优先按原路径打开；原路径失效（换机后用户名/盘符不同）时，
                // 用「坚果云相对路径」重定位到当前机的坚果云根
                if (File.Exists(path))
                {
                    OpenFile(path);
                    continue;
                }
                var relocated = RelocateCloudPath(path);
                if (relocated is not null)
                {
                    OpenFile(relocated);
                    continue;
                }
                // 文件真丢失（连重定位都找不到）：降级为未命名标签，不标 dirty，
                // 避免关闭时把空内容写回快照/云盘（2026-11-05 换机测试报告 P0-2/P0-3 修复）
            }

            // 未保存的标签（或文件已丢失）：读回正文内容
            var name = info.UntitledName;
            // 兼容早期版本存档里的「无标题 N」名字
            if (name.StartsWith("无标题", StringComparison.Ordinal))
            {
                name = "未命名" + name["无标题".Length..];
            }
            var doc = new Document(name)
            {
                // 换机容错：文件丢失时不留旧机绝对路径（FilePath=null），用户保存时走「另存为」落到当前机云盘
                FilePath = info.FilePath is not null && info.ContentFile is null ? null : info.FilePath,
                Encoding = info.Encoding,
                LineEnding = info.LineEnding,
            };
            var content = info.ContentFile is null ? "" : SessionRecovery.ReadContent(info.ContentFile) ?? "";
            var tab = AddTab(doc);
            tab.Editor.LoadText(content);
            // 会话恢复的标签也把标记画回来（FilePath 为空的无名标签跳过）
            if (doc.FilePath is not null) tab.Editor.Edit.ApplyMarks(MarkStore.Load(doc.FilePath));
            UpdatePreviewTitle(tab);
            // 只保留「原来就未保存」的脏标记；文件丢失恢复的空标签不再标 dirty（避免关闭时被当修改写回）
            doc.IsDirty = info.IsDirty;
            if (info.CaretPosition > 0 && info.CaretPosition <= content.Length)
            {
                tab.Editor.Select(info.CaretPosition, 0);
            }
        }
        // 上次选中的标签
        if (manifest.ActiveIndex >= 0 && manifest.ActiveIndex < _tabs.Count)
        {
            SelectTab(_tabs[manifest.ActiveIndex]);
        }
    }

    /// <summary>关闭时保存会话快照：先清空（会备份旧快照），再按标签顺序写入 manifest；未保存或已修改的标签把正文写入内容文件。</summary>
    private void SaveSessionSnapshot()
    {
        try
        {
            SessionRecovery.Clear();
            var list = new List<RecoveryTabInfo>(_tabs.Count);
            foreach (var tab in _tabs)
            {
                var doc = tab.Document;
                string? contentFile = null;
                if (doc.FilePath is null || doc.IsDirty)
                {
                    contentFile = SessionRecovery.WriteContent(tab.Editor.Text);
                }
                list.Add(new RecoveryTabInfo(doc.FilePath, doc.UntitledName, doc.Encoding, doc.LineEnding, doc.IsDirty, contentFile, tab.Editor.SelectionStart));
            }
            SessionRecovery.Save(new RecoveryManifest(_current is null ? 0 : _tabs.IndexOf(_current), list));
        }
        catch
        {
            // 快照保存失败不阻止退出
        }
    }

    /// <summary>MemoPad 增强：云盘笔记目录。设置值在本机真实存在且非旧版回退目录时直接使用；
    /// 否则（含换机后旧机绝对路径失效）自动探测：坚果云根存在 → 其下「笔记」，否则回退到数据目录\Notes。</summary>
    private string GetNotesDirectory()
    {
        // 换机容错：settings.json 里存的是旧机绝对路径（如 C:\Users\<用户名>\...），本机不存在时视为失效，重新探测
        if (!string.IsNullOrWhiteSpace(Settings.NotesDirectory) && !IsFallbackNotesDir(Settings.NotesDirectory) && Directory.Exists(Settings.NotesDirectory))
        {
            return Settings.NotesDirectory;
        }
        var cloudRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Nutstore", "1", "我的坚果云");
        if (Directory.Exists(cloudRoot))
        {
            var cloudNotes = Path.Combine(cloudRoot, "笔记");
            Directory.CreateDirectory(cloudNotes);
            Settings.NotesDirectory = cloudNotes;
            return cloudNotes;
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MemoPad", "Notes");
        Settings.NotesDirectory = fallback;
        Directory.CreateDirectory(Settings.NotesDirectory);
        return Settings.NotesDirectory;
    }

    /// <summary>判断目录是否为回退目录（数据目录\Notes）。</summary>
    private static bool IsFallbackNotesDir(string dir)
    {
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MemoPad", "Notes");
        return string.Equals(Path.GetFullPath(dir), Path.GetFullPath(fallback), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>MemoPad 增强（换机容错）：把旧机绝对路径按「坚果云相对路径」重定位到当前机。
    /// 例：C:\Users\<用户名>\Nutstore\1\我的坚果云\笔记\a.txt → C:\Users\<当前用户>\Nutstore\1\我的坚果云\笔记\a.txt。
    /// 目标文件存在才返回新路径，否则返回 null（非坚果云路径也返回 null）。</summary>
    private static string? RelocateCloudPath(string path)
    {
        try
        {
            const string marker = @"Nutstore\1\我的坚果云\";
            var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var relative = path[(idx + marker.Length)..];
            var newRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Nutstore", "1", "我的坚果云");
            var relocated = Path.Combine(newRoot, relative);
            return File.Exists(relocated) ? Path.GetFullPath(relocated) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>MemoPad 增强：文件若不在云盘笔记目录，先复制一份过去并返回副本路径（原文件保留）。
    /// 复制失败（只读/占用/网络）时按原路径返回，不打断打开操作。</summary>
    private string AdoptToNotes(string path)
    {
        try
        {
            var dir = Path.GetFullPath(GetNotesDirectory());
            var full = Path.GetFullPath(path);
            if (full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return full;
            var name = Path.GetFileName(full);
            var dest = Path.Combine(dir, name);
            var i = 1;
            while (File.Exists(dest))
            {
                dest = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({i++}){Path.GetExtension(name)}");
            }
            File.Copy(full, dest, false);
            return dest;
        }
        catch
        {
            return path;
        }
    }

    /// <summary>MemoPad 增强：关闭前把所有标签自动保存到云盘笔记目录（未命名标签自动按时间戳命名）。
    /// 保存成功后清脏（● 消失、下次启动按正式文件打开）；任一步失败不阻止退出，未保存内容仍由快照兜底。</summary>
    private void AutoSaveAllToNotes()
    {
        try
        {
            var dir = GetNotesDirectory();
            foreach (var tab in _tabs)
            {
                var doc = tab.Document;
                var path = doc.FilePath;
                // 已有路径且无未保存改动：内容已落盘（云盘副本），无需重复写
                if (path is not null && !doc.IsDirty) continue;
                // 换机容错（2026-11-05 换机测试报告 P0-3）：从未编辑过的空标签（如文件丢失恢复的空标签、
                // 新建后直接关闭的空标签）不落盘，避免往云盘写 0 字节文件
                if (path is null && !doc.IsDirty && tab.Editor.TextLength == 0) continue;
                // 数据保护：本标签内容为空而目标磁盘文件非空时，拒绝覆盖（防止把真实笔记清空）
                if (tab.Editor.TextLength == 0 && path is not null && File.Exists(path))
                {
                    var existingLen = new FileInfo(path).Length;
                    if (existingLen > 0) continue;
                }
                if (path is null)
                {
                    var baseName = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    path = Path.Combine(dir, baseName + ".txt");
                    var n = 2;
                    while (File.Exists(path))
                    {
                        path = Path.Combine(dir, $"{baseName}-{n++}.txt");
                    }
                }
                TextFileService.Write(path, tab.Editor.Text, doc.Encoding, doc.LineEnding);
                doc.FilePath = path;
                doc.IsDirty = false;
                Settings.PushRecentFile(path);
                // 自动保存到笔记目录时，标记也跟着正文一起落盘
                MarkStore.Save(path, tab.Editor.Edit.ReadMarks());
            }
        }
        catch
        {
            // 自动保存失败不阻止退出；快照仍会兜底未保存内容
        }
    }

    /// <summary>取正文开头一行的前 12 个字符作标签预览（去掉开头的制表符/空格/换行）。</summary>
    private static string? GetPreviewText(string? content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        var trimmed = content.TrimStart('\t', ' ', '\r', '\n');
        if (trimmed.Length == 0) return null;
        var firstLine = trimmed.Split('\r', '\n')[0];
        return firstLine.Length <= 12 ? firstLine : firstLine[..12] + "…";
    }

    // =====================================================================
    // キー操作（メニューのショートカット以外）
    // =====================================================================

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.Tab:
                SelectAdjacentTab(+1);
                return true;
            case Keys.Control | Keys.Shift | Keys.Tab:
                SelectAdjacentTab(-1);
                return true;
            case Keys.Escape when _findBar.Visible:
                HideFindBar();
                return true;
            case Keys.Control | Keys.Y:
                Current?.Editor.Redo();
                return true;
            case Keys.Control | Keys.Oemplus:
            case Keys.Control | Keys.Add:
                SetZoom(_zoomPercent + ZoomStep);
                return true;
            case Keys.Control | Keys.OemMinus:
            case Keys.Control | Keys.Subtract:
                SetZoom(_zoomPercent - ZoomStep);
                return true;
            case Keys.Control | Keys.D0:
            case Keys.Control | Keys.NumPad0:
                SetZoom(100);
                return true;
            // 折叠/展开（F7・F8）はメニュー項目の ShortcutKeys が受けるので、ここでは扱わない。
            // ここに書くと独自の固定キーになり、「快捷键设置」で変えても効かなくなる。
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // =====================================================================
    // ファイルのドロップ（タイトル行やメニューの上。本文の上は RichEdit が受ける）
    // =====================================================================

    protected override void OnDragEnter(DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        base.OnDragEnter(e);
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var f in files) if (File.Exists(f)) OpenFile(f);
        }
        base.OnDragDrop(e);
    }

    // =====================================================================
    // ファイル
    // =====================================================================

    // 文本类扩展名统一放一处：打开/另存为都认这几个，免得存成 .md 时被打回 .txt
    private const string TextPattern = "*.txt;*.md;*.markdown;*.mdown;*.mkd";

    private const string OpenFilter = "文本文件 (*.txt;*.md)|*.txt;*.md;*.markdown;*.mdown;*.mkd|Markdown (*.md;*.markdown)|*.md;*.markdown|所有文件 (*.*)|*.*";

    // 名前を付けて保存では「ファイルの種類」でエンコードを選ぶ（共通ダイアログにはメモ帳のようなエンコード欄を
    // 足せないため、種類の一覧で代用する）
    private static readonly (string Label, TextEncodingKind? Encoding)[] SaveFilters =
    {
        ("文本文件 - UTF-8 (*.txt;*.md)", TextEncodingKind.Utf8),
        ("文本文件 - UTF-8（带 BOM）(*.txt;*.md)", TextEncodingKind.Utf8Bom),
        ("文本文件 - ANSI (Shift_JIS) (*.txt;*.md)", TextEncodingKind.Ansi),
        ("文本文件 - UTF-16 LE (*.txt;*.md)", TextEncodingKind.Utf16LE),
        ("文本文件 - UTF-16 BE (*.txt;*.md)", TextEncodingKind.Utf16BE),
        ("所有文件（当前编码）(*.*)", null),
    };

    public void OpenFile(string path)
    {
        path = Path.GetFullPath(path);
        var original = path;
        // MemoPad 增强：云盘收编——文件不在云盘笔记目录时先复制一份过去，标签指向云盘副本（原文件保留不动）
        path = AdoptToNotes(path);

        // 既に開いているならそのタブへ（优先按副本路径找；其次按原路径找，找到则把标签升级指向副本）
        var existing = _tabs.FirstOrDefault(t => string.Equals(t.Document.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null && !string.Equals(original, path, StringComparison.OrdinalIgnoreCase))
        {
            existing = _tabs.FirstOrDefault(t => string.Equals(t.Document.FilePath, original, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Document.FilePath = path;
                UpdateTitle();
            }
        }
        if (existing is not null)
        {
            SelectTab(existing);
            return;
        }

        TextFileContent content;
        try
        {
            content = TextFileService.Read(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"{path}\n\n无法打开文件。\n{ex.Message}", "MemoPad", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // 空の「未命名」しか無いときはそのタブを使い回す（メモ帳と同じ挙動）
        var tab = Current is { } c && c.Document.FilePath is null && !c.Document.IsDirty && c.Editor.TextLength == 0
            ? c
            : AddTab(new Document());

        tab.Document.FilePath = path;
        tab.Document.Encoding = content.Encoding;
        tab.Document.LineEnding = content.LineEnding;
        tab.Editor.LoadText(content.Text);
        // 还原这篇笔记上次的标记（没有侧车文件就是空，不影响正文）
        tab.Editor.Edit.ApplyMarks(MarkStore.Load(path));
        UpdatePreviewTitle(tab);
        SelectTab(tab);
        Settings.PushRecentFile(path);
        UpdateTitle();
        UpdateContentStatus();
    }

    private void Open()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = OpenFilter,
            Multiselect = true,
            Title = "打开",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        foreach (var path in dialog.FileNames) OpenFile(path);
    }

    private void Save()
    {
        if (Current is { } tab) SaveTab(tab);
    }

    private void SaveAs()
    {
        if (Current is { } tab) SaveTabAs(tab);
    }

    private void SaveAll()
    {
        foreach (var tab in _tabs.ToList())
        {
            if (tab.Document.IsDirty || tab.Document.FilePath is null)
            {
                if (!SaveTab(tab)) return;
            }
        }
    }

    /// <summary>保存する。パスが無ければ「名前を付けて保存」。成功したら true。</summary>
    private bool SaveTab(DocumentTab tab)
    {
        if (tab.Document.FilePath is null) return SaveTabAs(tab);
        return WriteTab(tab, tab.Document.FilePath);
    }

    private bool SaveTabAs(DocumentTab tab)
    {
        SelectTab(tab);
        var currentIndex = Array.FindIndex(SaveFilters, f => f.Encoding == tab.Document.Encoding);
        // 存 .md 时别被默认的 txt 后綴改掉：手打文件名不带后缀时，按原文件的后缀补
        var keepExt = Path.GetExtension(tab.Document.FilePath ?? "").TrimStart('.');
        if (string.IsNullOrWhiteSpace(keepExt)) keepExt = "txt";
        using var dialog = new SaveFileDialog
        {
            Filter = string.Join("|", SaveFilters.Select(f => $"{f.Label}|{(f.Encoding is null ? "*.*" : TextPattern)}")),
            FilterIndex = currentIndex < 0 ? 1 : currentIndex + 1,
            FileName = tab.Document.FilePath is null ? $"{tab.Document.Title}.txt" : Path.GetFileName(tab.Document.FilePath),
            // MemoPad 增强：未命名标签的另存为默认落在云盘笔记目录
            InitialDirectory = tab.Document.FilePath is null ? GetNotesDirectory() : Path.GetDirectoryName(tab.Document.FilePath) ?? "",
            DefaultExt = keepExt,
            AddExtension = true,
            Title = "另存为",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;

        var chosen = SaveFilters[Math.Clamp(dialog.FilterIndex - 1, 0, SaveFilters.Length - 1)].Encoding;
        if (chosen is { } enc) tab.Document.Encoding = enc;
        return WriteTab(tab, dialog.FileName);
    }

    private bool WriteTab(DocumentTab tab, string path)
    {
        var oldPath = tab.Document.FilePath;
        try
        {
            TextFileService.Write(path, tab.Editor.Text, tab.Document.Encoding, tab.Document.LineEnding);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"{path}\n\n无法保存。\n{ex.Message}", "MemoPad", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        tab.Document.FilePath = path;
        // 保存した時点の内容をエディタにも覚えさせる（ここまで元に戻すと「編集なし」に戻る）
        tab.Editor.MarkClean();
        tab.Document.IsDirty = false;
        Settings.PushRecentFile(path);
        // 标记（荧光笔）的侧车文件跟着正文走；另存为换了路径时把旧路径的侧车删掉
        MarkStore.Save(path, tab.Editor.Edit.ReadMarks());
        if (oldPath is not null && !string.Equals(oldPath, path, StringComparison.OrdinalIgnoreCase))
        {
            MarkStore.Save(oldPath, new List<(int, int, int)>());
        }
        UpdateTitle();
        UpdateContentStatus();
        return true;
    }

    /// <summary>把当前标签的标记立即写进侧车文件（未保存过的标签没有路径，跳过）。</summary>
    private void PersistMarks(DocumentTab tab)
    {
        var path = tab.Document.FilePath;
        if (path is null) return;
        MarkStore.Save(path, tab.Editor.Edit.ReadMarks());
    }

    private void NewWindow()
    {
        var exe = Environment.ProcessPath;
        if (exe is not null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    private void PageSetup()
    {
        WpfHost.Ensure(Settings.Theme);
        Dialogs.PageSetupDialog.Show(Handle, Settings);
    }

    private void Print()
    {
        if (Current is not { } tab) return;
        try
        {
            WpfHost.Ensure(Settings.Theme);
            PrintService.Print(tab.Document.Title, tab.Editor.Text, Settings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打印。\n{ex.Message}", "MemoPad", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // =====================================================================
    // 表示：ズーム・ステータスバー・折り返し
    // =====================================================================

    private void SetZoom(int percent)
    {
        _zoomPercent = Math.Clamp(percent, ZoomMin, ZoomMax);
        ApplyAppearanceToAll();
        _statusBar.ZoomText = $"{_zoomPercent}%";
        // MemoPad 增强：缩放即时记忆
        Settings.ZoomPercent = _zoomPercent;
    }

    /// <summary>MemoPad 增强：把选中的多行折成一段（F7 / 编辑菜单）。</summary>
    private void FoldSelection()
    {
        if (Current is not { } tab) return;
        if (!tab.Editor.FoldSelectedLines())
        {
            MessageBox.Show(this, "请先选中多行文字（不能只选一行，也不能在已有折叠区域里再折）。", "折叠",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    /// <summary>MemoPad 增强：行间距设置（格式菜单）。</summary>
    private void ChooseLineSpacing()
    {
        var result = Dialogs.LineSpacingDialog.Ask(this, Settings.LineSpacing);
        if (result is not { } spacing) return;
        Settings.LineSpacing = spacing;
        ApplyAppearanceToAll();
    }

    private void ToggleStatusBar()
    {
        Settings.ShowStatusBar = !Settings.ShowStatusBar;
        UpdateViewMenu();
    }

    private void ToggleWordWrap()
    {
        Settings.WordWrap = !Settings.WordWrap;
        UpdateViewMenu();
        ApplyAppearanceToAll();
    }

    private void ToggleRainbow()
    {
        Settings.RainbowMode = !Settings.RainbowMode;
        UpdateViewMenu();
        ApplyAppearanceToAll();
    }

    private void ToggleRainbowFlow()
    {
        Settings.RainbowFlow = !Settings.RainbowFlow;
        UpdateViewMenu();
        ApplyAppearanceToAll();
    }

    private void ToggleRainbowByLine()
    {
        Settings.RainbowByLine = !Settings.RainbowByLine;
        UpdateViewMenu();
        ApplyAppearanceToAll();
    }

    /// <summary>切「每 N 字一色」。选分组字数即退出按行模式；改完让所有标签页按新密度重新着色。</summary>
    private void SetRainbowGroupChars(int chars)
    {
        Settings.RainbowGroupChars = chars;
        Settings.RainbowByLine = false;
        UpdateViewMenu();
        ApplyAppearanceToAll();
    }

    private void UpdateViewMenu()
    {
        _statusBarItem.Checked = Settings.ShowStatusBar;
        _wordWrapItem.Checked = Settings.WordWrap;
        _rainbowItem.Checked = Settings.RainbowMode;
        _rainbowFlowItem.Checked = Settings.RainbowFlow;
        // 彩虹关着的时候没有颜色可流，这一项置灰（设置值保留，彩虹再打开就照旧生效）
        _rainbowFlowItem.Enabled = Settings.RainbowMode;
        // 着色方式互斥：按行一色勾选时，四个「每 N 字一色」都不勾；彩虹关着时整组置灰
        _rainbowByLineItem.Checked = Settings.RainbowByLine;
        _rainbowByLineItem.Enabled = Settings.RainbowMode;
        foreach (var kv in _rainbowGroupItems)
        {
            kv.Value.Enabled = Settings.RainbowMode;
            kv.Value.Checked = !Settings.RainbowByLine && Settings.RainbowGroupChars == kv.Key;
        }
        _statusBar.Visible = Settings.ShowStatusBar;
    }

    // =====================================================================
    // 書式：フォント・色変更・配色パターン・テーマ
    // =====================================================================

    private void ChooseFont()
    {
        WpfHost.Ensure(Settings.Theme);
        if (Dialogs.FontDialog.Ask(Handle, Settings) is not { } choice) return;
        Settings.FontFamily = choice.Family;
        Settings.FontSize = choice.Size;
        Settings.FontBold = choice.Bold;
        Settings.FontItalic = choice.Italic;
        ApplyAppearanceToAll();
    }

    /// <summary>「色変更」ダイアログで背景色と文字色をまとめて変える（v0.4.0）。</summary>
    private void ChangeColors()
    {
        WpfHost.Ensure(Settings.Theme);
        if (ColorChangeDialog.Ask(Handle, Settings) is not { } result) return;
        Settings.BackgroundColor = result.Background;
        Settings.ForegroundColor = result.Foreground;
        ApplyAppearanceToAll();
    }

    /// <summary>「配色パターン」ダイアログで、目に優しい 16 パターンから背景色と文字色を一度に変える（v0.5.0）。</summary>
    private void ChooseColorScheme()
    {
        WpfHost.Ensure(Settings.Theme);
        if (ColorSchemeDialog.Ask(Handle, Settings) is not { } scheme) return;
        Settings.BackgroundColor = scheme.BackgroundHex;
        Settings.ForegroundColor = scheme.ForegroundHex;
        ApplyAppearanceToAll();
    }

    private void ResetColors()
    {
        Settings.BackgroundColor = "";
        Settings.ForegroundColor = "";
        ApplyAppearanceToAll();
    }

    private void ShowAbout()
    {
        WpfHost.Ensure(Settings.Theme);
        new AboutDialog(Handle).ShowDialog();
    }

    private void SetTheme(string theme)
    {
        Settings.Theme = theme;
        ApplyTheme();
    }

    /// <summary>テーマ（ライト／ダーク）を全体に反映する。</summary>
    private void ApplyTheme()
    {
        _palette = ThemeService.Palette(Settings.Theme);
        _renderer.Palette = _palette;
        BackColor = _palette.Window;
        _editorHost.BackColor = _palette.Window;
        _titleBar.Palette = _palette;
        _statusBar.Palette = _palette;
        _menu.BackColor = _palette.MenuBar;
        ApplyFindBarTheme(_findBar);
        ThemeService.ApplyWindowFrame(Handle, _palette.IsDark);
        if (WpfHost.IsInitialized) WpfHost.ApplyTheme(Settings.Theme);
        UpdateThemeMenu();
        ApplyAppearanceToAll();
        Invalidate(true);
    }

    private void UpdateThemeMenu()
    {
        _themeLightItem.Checked = Settings.Theme == "Light";
        _themeDarkItem.Checked = Settings.Theme == "Dark";
        _themeSystemItem.Checked = Settings.Theme is not ("Light" or "Dark");
    }

    /// <summary>全タブにフォント・色・折り返し・ズームを反映する。</summary>
    private void ApplyAppearanceToAll()
    {
        foreach (var tab in _tabs) tab.Editor.ApplyAppearance(Settings, _zoomPercent);
    }

    // =====================================================================
    // タイトルとステータスバー
    // =====================================================================

    private void UpdateTitle()
    {
        if (Current is { } tab)
        {
            Text = $"{(tab.Document.IsDirty ? "*" : "")}{tab.Document.Title} - MemoPad";
            _statusBar.LineEndingText = tab.Document.LineEnding.DisplayName();
            _statusBar.EncodingText = tab.Document.Encoding.DisplayName();
        }
        else
        {
            Text = "MemoPad";
        }
    }

    private void UpdateCaretStatus()
    {
        if (Current is { } tab)
        {
            var (line, column) = tab.Editor.GetCaretPosition();
            _statusBar.PositionText = $"第 {line} 行，第 {column} 列";
        }
    }

    private void UpdateContentStatus()
    {
        if (Current is { } tab)
        {
            _statusBar.CharCountText = $"{tab.Editor.GetCharacterCount():N0} 个字符";
            UpdateTitle();
        }
    }

    private void ShowLineEndingMenu(Point screenPoint)
    {
        if (Current is not { } tab) return;
        var menu = NewPopupMenu(checkable: true);
        foreach (var kind in Enum.GetValues<LineEndingKind>())
        {
            var item = new ToolStripMenuItem(kind.DisplayName()) { Checked = tab.Document.LineEnding == kind, Padding = ItemPadding };
            item.Click += (_, _) =>
            {
                tab.Document.LineEnding = kind;
                tab.Document.IsDirty = true;
                UpdateTitle();
            };
            menu.Items.Add(item);
        }
        ShowPopupAbove(menu, screenPoint);
    }

    private void ShowEncodingMenu(Point screenPoint)
    {
        if (Current is not { } tab) return;
        var menu = NewPopupMenu(checkable: true);
        foreach (var kind in Enum.GetValues<TextEncodingKind>())
        {
            var item = new ToolStripMenuItem(kind.DisplayName()) { Checked = tab.Document.Encoding == kind, Padding = ItemPadding };
            item.Click += (_, _) =>
            {
                tab.Document.Encoding = kind;
                tab.Document.IsDirty = true;
                UpdateTitle();
            };
            menu.Items.Add(item);
        }
        ShowPopupAbove(menu, screenPoint);
    }

    /// <summary>ステータスバーの項目の上に（右端を基準に左へ）ポップアップを出す。</summary>
    private static void ShowPopupAbove(ContextMenuStrip menu, Point screenPoint)
    {
        menu.Show(screenPoint, ToolStripDropDownDirection.AboveLeft);
    }
}
