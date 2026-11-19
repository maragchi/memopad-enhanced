using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Memopad.Models;
using Memopad.Services;

namespace Memopad.Views;

/// <summary>メイン ウィンドウ：メニュー（項目とショートカットはメモ帳に合わせる）と右クリック メニュー。</summary>
public sealed partial class MainForm
{
    private ToolStripMenuItem _editMenu = null!;
    private ToolStripMenuItem _recentMenu = null!;
    private ToolStripMenuItem _undoItem = null!;
    private ToolStripMenuItem _redoItem = null!;
    private ToolStripMenuItem _cutItem = null!;
    private ToolStripMenuItem _copyItem = null!;
    private ToolStripMenuItem _pasteItem = null!;
    private ToolStripMenuItem _deleteItem = null!;
    private ToolStripMenuItem _statusBarItem = null!;
    private ToolStripMenuItem _wordWrapItem = null!;
    private ToolStripMenuItem _rainbowItem = null!;
    private ToolStripMenuItem _rainbowFlowItem = null!;
    private ToolStripMenuItem _rainbowByLineItem = null!;
    /// <summary>「彩虹分组」子菜单里的 4 个选项（每 N 字一色），按字数存，用于互斥勾选。</summary>
    private readonly Dictionary<int, ToolStripMenuItem> _rainbowGroupItems = new();
    private ToolStripMenuItem _themeLightItem = null!;
    private ToolStripMenuItem _themeDarkItem = null!;
    private ToolStripMenuItem _themeSystemItem = null!;

    /// <summary>MemoPad 增强：可自定义快捷键的命令。id → 菜单项，以及对话框里按注册顺序显示的名称与默认键。</summary>
    private readonly Dictionary<string, ToolStripMenuItem> _shortcutItems = new(StringComparer.Ordinal);
    private readonly List<(string Id, string Label, Keys Default)> _shortcutRows = new();

    /// <summary>
    /// ポップアップ内の項目の余白（Fluent のメニュー項目の高さに合わせる）。
    /// 左右はメニューの内部レイアウトが決めるので、効くのは上下（＝項目の高さ）だけ。
    /// </summary>
    private Padding ItemPadding => new(S(4), S(5), S(4), S(5));

    /// <summary>チェックが付き得る項目の目印。左のチェック欄を出すかの判定に使う。</summary>
    private static readonly object CheckableTag = new();

    private MenuStrip BuildMenu()
    {
        var menu = new ChromeMenuStrip
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = S(MenuHeight),
            Padding = new Padding(S(6), 0, 0, 0),
            BackColor = _palette.MenuBar,
            Renderer = _renderer,
            Font = Font,
            ImageScalingSize = new Size(S(16), S(16)),
            ShowItemToolTips = false,
            GripStyle = ToolStripGripStyle.Hidden,
        };

        // 文件
        _recentMenu = SubMenu("最近使用的文件(&R)", new ToolStripMenuItem("(无)") { Enabled = false });
        _recentMenu.DropDownOpening += (_, _) => RebuildRecentMenu();
        menu.Items.Add(TopMenu("文件(&F)",
            Item("新标签(&N)", () => AddTab(new Document()), Keys.Control | Keys.N, id: "new-tab"),
            Item("新窗口(&W)", NewWindow, Keys.Control | Keys.Shift | Keys.N, id: "new-window"),
            Item("打开(&O)...", Open, Keys.Control | Keys.O, id: "open"),
            _recentMenu,
            Item("保存(&S)", Save, Keys.Control | Keys.S, id: "save"),
            Item("另存为(&A)...", SaveAs, Keys.Control | Keys.Shift | Keys.S, id: "save-as"),
            Item("全部保存(&L)", SaveAll, Keys.Control | Keys.Alt | Keys.S, id: "save-all"),
            new ToolStripSeparator(),
            Item("页面设置(&U)...", PageSetup, Keys.Control | Keys.Alt | Keys.U, id: "page-setup"),
            Item("打印(&P)...", Print, Keys.Control | Keys.P, id: "print"),
            new ToolStripSeparator(),
            Item("关闭标签页(&C)", () => { if (Current is { } t) CloseTab(t); }, Keys.Control | Keys.W, id: "close-tab"),
            Item("关闭窗口(&E)", Close, Keys.Control | Keys.Shift | Keys.W, id: "close-window"),
            Item("退出(&X)", Close)));

        // 编辑：撤销〜删除由编辑器自行处理，这里只做显示。启用/禁用状态在打开菜单时更新
        _undoItem = Item("撤销(&U)", () => Current?.Editor.Undo(), display: "Ctrl+Z");
        _redoItem = Item("重做(&R)", () => Current?.Editor.Redo(), display: "Ctrl+Y");
        _cutItem = Item("剪切(&T)", () => Current?.Editor.Cut(), display: "Ctrl+X");
        _copyItem = Item("复制(&C)", () => Current?.Editor.Copy(), display: "Ctrl+C");
        _pasteItem = Item("粘贴(&P)", () => Current?.Editor.Paste(), display: "Ctrl+V");
        _deleteItem = Item("删除(&L)", () => Current?.Editor.Delete(), display: "Del");
        _editMenu = TopMenu("编辑(&E)",
            _undoItem, _redoItem,
            new ToolStripSeparator(),
            _cutItem, _copyItem, _pasteItem, _deleteItem,
            new ToolStripSeparator(),
            Item("查找(&F)...", () => ShowFindBar(replace: false), Keys.Control | Keys.F, id: "find"),
            Item("查找下一个(&N)", () => FindNext(backward: false), Keys.F3, id: "find-next"),
            Item("查找上一个(&V)", () => FindNext(backward: true), Keys.Shift | Keys.F3, id: "find-prev"),
            Item("替换(&R)...", () => ShowFindBar(replace: true), Keys.Control | Keys.H, id: "replace"),
            Item("转到行(&G)...", GoToLine, Keys.Control | Keys.G, id: "goto"),
            new ToolStripSeparator(),
            Item("全选(&A)", () => Current?.Editor.SelectAll(), display: "Ctrl+A"),
            Item("日期和时间(&D)", InsertDateTime, Keys.F5, id: "datetime"),
            new ToolStripSeparator(),
            // MemoPad 增强：文字折叠与展开
            Item("折叠选定文本(&Z)", FoldSelection, Keys.F7, id: "fold"),
            Item("展开光标处折叠(&X)", () => Current?.Editor.UnfoldAtCaret(), Keys.F8, id: "unfold-caret"),
            Item("展开全部折叠", () => Current?.Editor.UnfoldAllFolds(), Keys.Control | Keys.Alt | Keys.X, id: "unfold-all"));
        _editMenu.DropDownOpening += (_, _) => UpdateEditMenu();
        menu.Items.Add(_editMenu);

        // 查看
        _statusBarItem = Item("状态栏(&S)", ToggleStatusBar, Keys.Control | Keys.Shift | Keys.B, checkable: true, id: "status-bar");
        _wordWrapItem = Item("自动换行(&W)", ToggleWordWrap, Keys.Alt | Keys.Z, checkable: true, id: "word-wrap");
        _rainbowItem = Item("彩虹模式(&R)", ToggleRainbow, Keys.Control | Keys.Shift | Keys.R, checkable: true, id: "rainbow");
        _rainbowFlowItem = Item("彩虹流动(&L)", ToggleRainbowFlow, Keys.Control | Keys.Alt | Keys.F, checkable: true, id: "rainbow-flow");

        // 彩虹分组：每 N 个字符换一色。这四项只用于对比效果，不登记成可自定义快捷键（否则对话框里太挤）
        _rainbowGroupItems.Clear();
        var groupItems = new List<ToolStripItem>();
        foreach (var n in new[] { 4, 7, 10, 20 })
        {
            var chars = n;
            var label = chars == 4 ? $"每 {chars} 字一色（默认）" : $"每 {chars} 字一色";
            var groupItem = Item(label, () => SetRainbowGroupChars(chars), checkable: true);
            _rainbowGroupItems[chars] = groupItem;
            groupItems.Add(groupItem);
        }
        // 按行一色：整行同色，行号循环七色；与上面的「每 N 字一色」互斥
        _rainbowByLineItem = Item("每行一色", ToggleRainbowByLine, checkable: true);
        groupItems.Add(_rainbowByLineItem);
        menu.Items.Add(TopMenu("查看(&V)",
            SubMenu("缩放(&Z)",
                Item("放大(&I)", () => SetZoom(_zoomPercent + ZoomStep), display: "Ctrl+加号 (+)"),
                Item("缩小(&O)", () => SetZoom(_zoomPercent - ZoomStep), display: "Ctrl+减号 (-)"),
                Item("恢复默认缩放(&R)", () => SetZoom(100), display: "Ctrl+0")),
            _statusBarItem,
            _wordWrapItem,
            _rainbowItem,
            _rainbowFlowItem,
            SubMenu("着色方式(&G)", groupItems.ToArray())));

        // 格式：MemoPad 的附加功能（颜色）与字体、行间距、主题
        _themeLightItem = Item("浅色(&L)", () => SetTheme("Light"), checkable: true);
        _themeDarkItem = Item("深色(&D)", () => SetTheme("Dark"), checkable: true);
        _themeSystemItem = Item("使用系统设置(&S)", () => SetTheme("System"), checkable: true);
        menu.Items.Add(TopMenu("格式(&O)",
            Item("字体(&F)...", ChooseFont, Keys.Control | Keys.Shift | Keys.F, id: "font"),
            Item("行间距(&S)...", ChooseLineSpacing, Keys.Control | Keys.Shift | Keys.L, id: "line-spacing"),
            new ToolStripSeparator(),
            Item("颜色(&C)...", ChangeColors, Keys.Control | Keys.Shift | Keys.K, id: "colors"),
            Item("配色方案(&P)...", ChooseColorScheme, Keys.Control | Keys.Shift | Keys.G, id: "scheme"),
            Item("恢复默认颜色(&D)", ResetColors, Keys.Control | Keys.Alt | Keys.R, id: "reset-colors"),
            new ToolStripSeparator(),
            SubMenu("主题(&M)", _themeLightItem, _themeDarkItem, _themeSystemItem)));

        // 帮助
        menu.Items.Add(TopMenu("帮助(&H)",
            Item("快捷键设置(&K)...", OpenShortcutSettings),
            new ToolStripSeparator(),
            Item("关于 MemoPad(&A)", ShowAbout)));

        ApplyCustomShortcuts();
        return menu;
    }

    private ToolStripMenuItem TopMenu(string text, params ToolStripItem[] items)
    {
        var item = new ToolStripMenuItem(text) { Padding = new Padding(S(10), 0, S(10), 0) };
        item.DropDownItems.AddRange(items);
        PrepareDropDown(item);
        return item;
    }

    private ToolStripMenuItem SubMenu(string text, params ToolStripItem[] items)
    {
        var item = new ToolStripMenuItem(text) { Padding = ItemPadding };
        item.DropDownItems.AddRange(items);
        PrepareDropDown(item);
        return item;
    }

    /// <summary>
    /// 菜单项工厂。<paramref name="id"/> 非空时把该项登记为「可自定义快捷键」，
    /// 名称与 <paramref name="shortcut"/>（默认组合）会出现在「快捷键设置」对话框里。
    /// </summary>
    private ToolStripMenuItem Item(string text, Action action, Keys shortcut = Keys.None, string? display = null, bool checkable = false, string? id = null)
    {
        var item = new ToolStripMenuItem(text) { Padding = ItemPadding };
        if (checkable) item.Tag = CheckableTag;
        if (shortcut != Keys.None) item.ShortcutKeys = shortcut;
        if (display is not null) item.ShortcutKeyDisplayString = display;
        item.Click += (_, _) => action();
        if (id is not null)
        {
            _shortcutItems[id] = item;
            _shortcutRows.Add((id, ShortcutLabel(text), shortcut));
        }
        return item;
    }

    /// <summary>把「彩虹模式(&R)」「另存为(&A)...」变成对话框里好读的「彩虹模式」「另存为」。</summary>
    private static string ShortcutLabel(string text)
    {
        var label = Regex.Replace(text, @"\(&[A-Za-z0-9]\)", string.Empty).Trim();
        return label.TrimEnd('.', '。').Trim();
    }

    /// <summary>
    /// 按 settings.json 里的自定义值重设每个可自定义菜单项的 ShortcutKeys。
    /// 菜单只建一次，所以这里直接改现有项，改完菜单右侧的组合键文字会跟着变。
    /// </summary>
    private void ApplyCustomShortcuts()
    {
        foreach (var row in _shortcutRows)
        {
            var item = _shortcutItems[row.Id];
            if (item is null) continue;

            var keys = row.Default;
            if (Settings.Shortcuts.TryGetValue(row.Id, out var text))
                keys = ShortcutText.Parse(text);

            item.ShortcutKeys = keys;
            item.ShortcutKeyDisplayString = null;
        }
    }

    /// <summary>打开「快捷键设置」对话框；确认后立刻生效并立即写盘（不必等到关窗）。</summary>
    private void OpenShortcutSettings()
    {
        var rows = new List<(string Id, string Label, Keys Default, Keys Current)>(_shortcutRows.Count);
        foreach (var row in _shortcutRows)
        {
            var keys = Settings.Shortcuts.TryGetValue(row.Id, out var text)
                ? ShortcutText.Parse(text)
                : row.Default;
            rows.Add((row.Id, row.Label, row.Default, keys));
        }

        var result = Dialogs.ShortcutDialog.Ask(this, rows);
        if (result is null) return;

        // 只保存和默认不同的项，settings.json 里就不会堆一长串本来就一样的默认值
        Settings.Shortcuts.Clear();
        foreach (var row in _shortcutRows)
        {
            var keys = result.TryGetValue(row.Id, out var edited) ? edited : row.Default;
            if (keys == row.Default) continue;
            Settings.Shortcuts[row.Id] = ShortcutText.Format(keys);
        }

        ApplyCustomShortcuts();
        SettingsService.Save(Settings);
    }

    /// <summary>ポップアップの見た目（レンダラー・余白・角丸・チェック欄）を整える。</summary>
    private void PrepareDropDown(ToolStripMenuItem item)
    {
        var dropDown = item.DropDown;
        dropDown.Renderer = _renderer;
        dropDown.Padding = new Padding(S(4));
        dropDown.Font = Font;
        // メモ帳と同じで、チェックが付く項目のある menu だけ左にチェック欄を空ける。
        // 無い menu（ファイル・編集など）で欄を空けると、字下げが全角 2 文字分ほどになって右へ寄りすぎる
        if (dropDown is ToolStripDropDownMenu menu)
        {
            menu.ShowImageMargin = item.DropDownItems.OfType<ToolStripMenuItem>().Any(i => ReferenceEquals(i.Tag, CheckableTag));
        }
        item.DropDownOpening += (_, _) => ThemeService.ApplySmallRoundedCorners(dropDown.Handle);
    }

    /// <summary>
    /// 右键菜单里那一条「标记 + 五色块」。ToolStrip 的菜单项只能竖着排，五色各占一行太占地方，
    /// 所以用 ToolStripControlHost 塞一个自绘控件进来，一行把文字和五个色块排开，点色块就上色。
    /// </summary>
    private sealed class MarkColorRow : Control
    {
        private readonly Color[] _colors;
        private readonly ThemePalette _palette;
        private readonly int _box;
        private readonly int _gap;
        private readonly int _padH;
        private readonly string _label = "标记";
        private int _hover = -1;

        public event Action<int>? ColorClicked;

        public MarkColorRow(ThemePalette palette, Color[] colors, Font font, int box, int gap, int padH, int padV)
        {
            _palette = palette;
            _colors = colors;
            _box = box;
            _gap = gap;
            _padH = padH;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = font;
            BackColor = palette.Popup;
            Height = box + padV * 2;
            Width = padH * 2 + LabelWidth + gap * 2 + colors.Length * box + (colors.Length - 1) * gap;
            Cursor = Cursors.Hand;
        }

        private int LabelWidth => TextRenderer.MeasureText(_label, Font, new Size(int.MaxValue, Height), TextFormatFlags.NoPadding).Width;

        private int BoxX(int index) => _padH + LabelWidth + _gap * 2 + index * (_box + _gap);

        private int HitTest(Point p)
        {
            for (var i = 0; i < _colors.Length; i++)
            {
                var r = new Rectangle(BoxX(i), (Height - _box) / 2, _box, _box);
                if (r.Contains(p)) return i;
            }
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            // 没有选中文字时整条变灰，提示点下去也不会有反应
            var textColor = Enabled ? _palette.Text : _palette.MutedText;
            var labelWidth = LabelWidth;
            TextRenderer.DrawText(e.Graphics, _label, Font, new Rectangle(_padH, 0, labelWidth, Height), textColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            var y = (Height - _box) / 2;
            for (var i = 0; i < _colors.Length; i++)
            {
                var r = new Rectangle(BoxX(i), y, _box, _box);
                var c = Enabled ? _colors[i] : Color.FromArgb(150, _colors[i].R, _colors[i].G, _colors[i].B);
                using var path = UiStyle.RoundedRect(r, _box / 4);
                using var fill = new SolidBrush(c);
                e.Graphics.FillPath(fill, path);
                // 描一道淡边，否则浅黄、浅粉贴在浅色菜单底上会糊；鼠标悬停的那块边框加深
                var edge = Enabled && _hover == i ? _palette.Text : Color.FromArgb(90, 0, 0, 0);
                using var pen = new Pen(edge);
                e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hit = HitTest(e.Location);
            if (hit != _hover) { _hover = hit; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1) { _hover = -1; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var hit = HitTest(e.Location);
            if (hit >= 0 && Enabled) ColorClicked?.Invoke(hit);
        }
    }

    /// <summary>
    /// 右键菜单里那一排编辑图标（剪切／复制／粘贴／删除）。原来这四个各占一行文字，
    /// 换成 Windows 11 记事本那样的图标条，一行就够了，也更好认。
    /// </summary>
    private sealed class EditIconRow : Control
    {
        public const string GlyphUndo = "\uE7A7";
        public const string GlyphRedo = "\uE7A6";
        public const string GlyphCut = "\uE8C6";
        public const string GlyphCopy = "\uE8C8";
        public const string GlyphPaste = "\uE77F";
        public const string GlyphDelete = "\uE74D";

        private readonly ThemePalette _palette;
        private readonly string[] _glyphs;
        private readonly bool[] _enabled;
        private readonly Action[] _actions;
        private readonly Font _iconFont;
        private readonly int _box;
        private readonly int _gap;
        private readonly int _padH;
        private int _hover = -1;

        public EditIconRow(ThemePalette palette, string[] glyphs, bool[] enabled, Action[] actions,
            int box, int gap, int padH, int padV)
        {
            _palette = palette;
            _glyphs = glyphs;
            _enabled = enabled;
            _actions = actions;
            _box = box;
            _gap = gap;
            _padH = padH;
            _iconFont = new Font(IconFontFamily(), box, GraphicsUnit.Pixel);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = palette.Popup;
            Height = box + padV * 2;
            Width = padH * 2 + glyphs.Length * box + (glyphs.Length - 1) * gap;
            Cursor = Cursors.Hand;
        }

        /// <summary>Win11 用 Segoe Fluent Icons；换到没有它的机器上退回 MDL2，码位一样能画。</summary>
        private static string IconFontFamily()
        {
            foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                try
                {
                    using var probe = new FontFamily(name);
                    return name;
                }
                catch (ArgumentException)
                {
                }
            }
            return "Segoe UI Symbol";
        }

        private Rectangle BoxOf(int index) =>
            new(_padH + index * (_box + _gap), (Height - _box) / 2, _box, _box);

        private int HitTest(Point p)
        {
            for (var i = 0; i < _glyphs.Length; i++)
            {
                if (BoxOf(i).Contains(p)) return i;
            }
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            for (var i = 0; i < _glyphs.Length; i++)
            {
                var box = BoxOf(i);
                if (_enabled[i] && _hover == i)
                {
                    var inflate = Math.Max(2, _box / 8);
                    var back = new Rectangle(box.X - inflate, box.Y - inflate, box.Width + inflate * 2, box.Height + inflate * 2);
                    using var path = UiStyle.RoundedRect(back, back.Height / 6);
                    using var brush = new SolidBrush(_palette.TabHover);
                    e.Graphics.FillPath(brush, path);
                }
                // 颜色：可用＝正文色，不可用＝淡化的次要色，一眼能看出点了有没有反应
                var color = _enabled[i] ? _palette.Text : Color.FromArgb(120, _palette.MutedText);
                TextRenderer.DrawText(e.Graphics, _glyphs[i], _iconFont, box, color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hit = HitTest(e.Location);
            if (hit >= 0 && !_enabled[hit]) hit = -1;
            if (hit != _hover) { _hover = hit; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1) { _hover = -1; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var hit = HitTest(e.Location);
            if (hit >= 0 && _enabled[hit]) _actions[hit]();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _iconFont.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary><paramref name="checkable"/> が true なら左にチェック欄を空ける。</summary>
    private ContextMenuStrip NewPopupMenu(bool checkable = false)
    {
        var menu = new ContextMenuStrip { Renderer = _renderer, Font = Font, Padding = new Padding(S(4)), ImageScalingSize = new Size(S(16), S(16)), ShowImageMargin = checkable };
        menu.Opening += (_, _) => ThemeService.ApplySmallRoundedCorners(menu.Handle);
        // Closed の中で破棄すると、その後の後始末が破棄済みオブジェクトに触れて例外になる。
        // メッセージを処理し終えてから捨てる
        menu.Closed += (_, _) => BeginInvoke(() => menu.Dispose());
        return menu;
    }

    private void RebuildRecentMenu()
    {
        _recentMenu.DropDownItems.Clear();
        // MemoPad 增强（换机容错）：先修正失效路径——旧机绝对路径重定位到当前机坚果云根；
        // 重定位失败的原样保留（点击时提示并移除），避免「最近使用」整列变成死路径
        if (Settings.RecentFiles.Count > 0)
        {
            var fixedList = new List<string>(Settings.RecentFiles.Count);
            foreach (var path in Settings.RecentFiles)
            {
                if (File.Exists(path)) { fixedList.Add(path); continue; }
                var relocated = RelocateCloudPath(path);
                fixedList.Add(relocated ?? path);
            }
            Settings.RecentFiles.Clear();
            Settings.RecentFiles.AddRange(fixedList);
        }
        if (Settings.RecentFiles.Count == 0)
        {
            _recentMenu.DropDownItems.Add(new ToolStripMenuItem("(无)") { Enabled = false, Padding = ItemPadding });
            return;
        }
        var n = 1;
        foreach (var path in Settings.RecentFiles.ToList())
        {
            var item = new ToolStripMenuItem($"&{n++} {Path.GetFileName(path)}") { ToolTipText = path, Padding = ItemPadding };
            item.Click += (_, _) =>
            {
                if (File.Exists(path)) OpenFile(path);
                else
                {
                    MessageBox.Show(this, $"{path}\n\n找不到文件，已从列表移除。", "MemoPad", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    Settings.RecentFiles.Remove(path);
                }
            };
            _recentMenu.DropDownItems.Add(item);
        }
        _recentMenu.DropDownItems.Add(new ToolStripSeparator());
        var clear = new ToolStripMenuItem("清除列表(&C)") { Padding = ItemPadding };
        clear.Click += (_, _) => Settings.RecentFiles.Clear();
        _recentMenu.DropDownItems.Add(clear);
    }

    private void UpdateEditMenu()
    {
        var editor = Current?.Editor;
        _undoItem.Enabled = editor?.CanUndo == true;
        _redoItem.Enabled = editor?.CanRedo == true;
        _cutItem.Enabled = editor?.HasSelection == true;
        _copyItem.Enabled = editor?.HasSelection == true;
        _deleteItem.Enabled = editor?.HasSelection == true;
        _pasteItem.Enabled = editor?.CanPaste == true;
    }

    /// <summary>テキスト領域の右クリック メニュー（メモ帳の編集項目。Copilot 系は対象外）。</summary>
    private void ShowEditorContextMenu(DocumentTab tab)
    {
        var editor = tab.Editor;
        var menu = NewPopupMenu();
        void Add(string text, string gesture, bool enabled, Action action)
        {
            var item = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = gesture, Enabled = enabled, Padding = ItemPadding };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        // MemoPad 增强：撤销／重做／剪切／复制／粘贴／删除 六项文字全换成一行图标条，跟记事本一个样子。
        // 用 ToolStripControlHost 塞自绘控件，ToolStrip 菜单项自己只能竖着排
        var iconRow = new EditIconRow(
            _palette,
            new[]
            {
                EditIconRow.GlyphUndo, EditIconRow.GlyphRedo,
                EditIconRow.GlyphCut, EditIconRow.GlyphCopy, EditIconRow.GlyphPaste, EditIconRow.GlyphDelete,
            },
            new[]
            {
                editor.CanUndo, editor.CanRedo,
                editor.HasSelection, editor.HasSelection, editor.CanPaste, editor.HasSelection,
            },
            new Action[] { editor.Undo, editor.Redo, editor.Cut, editor.Copy, editor.Paste, editor.Delete },
            S(18), S(8), S(8), S(5));
        menu.Items.Add(new ToolStripControlHost(iconRow) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = iconRow.Size });
        menu.Items.Add(new ToolStripSeparator());
        Add("全选(&A)", "Ctrl+A", true, editor.SelectAll);
        Add("查找(&F)...", "Ctrl+F", true, () => ShowFindBar(replace: false));
        menu.Items.Add(new ToolStripSeparator());
        Add("折叠选定文本(&Z)", "F7", true, () => FoldSelection());
        Add("展开光标处折叠(&X)", "", true, () => editor.UnfoldAtCaret());
        Add("展开全部折叠", "", true, () => editor.UnfoldAllFolds());
        menu.Items.Add(new ToolStripSeparator());
        // MemoPad 增强：手动标记（荧光笔）。只作用于选中的文字，颜色可选，结果存到同名 .marks.json
        // 右键一按下 Scintilla 就挪插入点，所以这里用弹出菜单那一刻定格的选区，而不是实时读
        var markRange = editor.Edit.ContextMarkRange;
        // 五种颜色压成一条：左边「标记」两个字，右边五个色块并排，点哪个上哪个色。
        // ToolStrip 菜单项只能竖着排，所以用 ControlHost 把一个自绘控件塞进菜单里占一行
        var marks = new Color[PlainTextEdit.MarkColorCount];
        for (var i = 0; i < marks.Length; i++) marks[i] = PlainTextEdit.GetMarkColor(i);
        var row = new MarkColorRow(_palette, marks, Font, S(16), S(7), S(10), S(6)) { Enabled = markRange.Length > 0 };
        row.ColorClicked += index =>
        {
            if (editor.Edit.MarkSelection(index, markRange.Start, markRange.Length)) PersistMarks(tab);
            menu.Close();
        };
        menu.Items.Add(new ToolStripControlHost(row) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = row.Size });
        Add("清除标记(&K)", "", markRange.Length > 0, () =>
        {
            if (editor.Edit.ClearMarkInSelection(markRange.Start, markRange.Length)) PersistMarks(tab);
        });
        Add("清除全部标记", "", true, () =>
        {
            editor.Edit.ClearAllMarks();
            PersistMarks(tab);
        });
        menu.Items.Add(new ToolStripSeparator());
        Add("插入日期与时间(&D)", "F5", true, InsertDateTime);
        menu.Closed += (_, _) => editor.FocusEditor();
        menu.Show(Cursor.Position);
    }

    // =====================================================================
    // 編集：行へ移動・日付と時刻
    // =====================================================================

    private void GoToLine()
    {
        if (Current is not { } tab) return;
        var (line, _) = tab.Editor.GetCaretPosition();
        // 全文を読まずにエディタから行数をもらう（大きなファイルでも一瞬で済む）
        var lineCount = tab.Editor.GetLineCount();
        var result = AskGoToLine(line, lineCount);
        if (result is { } target) tab.Editor.GoToLine(target);
    }

    private int? AskGoToLine(int line, int lineCount)
    {
        WpfHost.Ensure(Settings.Theme);
        return Dialogs.GoToLineDialog.Ask(Handle, line, lineCount);
    }

    private void InsertDateTime()
    {
        // メモ帳の F5 と同じ形式（例：16:25 2026/09/06）
        if (Current is { } tab)
        {
            var editor = tab.Editor;
            editor.SelectedText = DateTime.Now.ToString("H:mm yyyy/MM/dd");
            editor.Select(editor.SelectionStart + editor.SelectionLength, 0);
            editor.FocusEditor();
        }
    }
}
