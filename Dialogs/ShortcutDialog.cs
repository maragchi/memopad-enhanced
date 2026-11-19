using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Memopad.Services;

namespace Memopad.Dialogs;

/// <summary>
/// 「快捷键设置」对话框：列出可以改绑的菜单命令，点一行后直接按新组合键即可替换。
/// 只负责收集结果，写入 settings.json 由调用方处理。
/// </summary>
public sealed class ShortcutDialog : Form
{
    /// <summary>编辑器内置或由固定分支处理、不能改绑的组合（避免改完反而失灵）。</summary>
    private static readonly (Keys Keys, string Who)[] Reserved =
    {
        (Keys.Control | Keys.Z, "撤销"),
        (Keys.Control | Keys.Y, "重做"),
        (Keys.Control | Keys.X, "剪切"),
        (Keys.Control | Keys.C, "复制"),
        (Keys.Control | Keys.V, "粘贴"),
        (Keys.Control | Keys.A, "全选"),
        (Keys.Delete, "删除"),
        (Keys.Control | Keys.Oemplus, "放大"),
        (Keys.Control | Keys.Add, "放大"),
        (Keys.Control | Keys.OemMinus, "缩小"),
        (Keys.Control | Keys.Subtract, "缩小"),
        (Keys.Control | Keys.D0, "恢复默认缩放"),
        (Keys.Control | Keys.NumPad0, "恢复默认缩放"),
        (Keys.Control | Keys.Tab, "切换标签"),
        (Keys.Control | Keys.Shift | Keys.Tab, "切换标签"),
        (Keys.Escape, "关闭查找栏"),
    };

    /// <summary>顶层菜单的加速键（Alt+F/E/V/O/H），单按 Alt+这些字母会被菜单栏抢走。</summary>
    private static readonly Keys[] MenuBarAccelerators =
    {
        Keys.F, Keys.E, Keys.V, Keys.O, Keys.H,
    };

    private readonly List<(string Id, string Label, Keys Default)> _rows = new();
    private readonly Dictionary<string, Keys> _values = new();
    private readonly ListView _list;
    private readonly TextBox _capture;
    private readonly Label _hint;

    private Dictionary<string, Keys>? _result;

    /// <summary>弹出对话框；取消时返回 null，确定时返回“命令 id → 组合键”的完整映射。</summary>
    public static Dictionary<string, Keys>? Ask(
        IWin32Window owner,
        IEnumerable<(string Id, string Label, Keys Default, Keys Current)> rows)
    {
        using var dialog = new ShortcutDialog(rows);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._result : null;
    }

    private ShortcutDialog(IEnumerable<(string Id, string Label, Keys Default, Keys Current)> rows)
    {
        Text = "快捷键设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(600, 560);
        Font = new Font("Microsoft YaHei UI", 9f);
        AutoScaleMode = AutoScaleMode.Font;

        var tip = new Label
        {
            Text = "点一行要改的功能，然后直接按下新组合键（例如 Ctrl+Shift+R）。\n"
                 + "按 Delete 清空可取消该快捷键。Tab / Enter / Esc 和上下方向键留给本对话框自己用。",
            Location = new Point(14, 12),
            Size = new Size(572, 40),
            ForeColor = Color.DimGray,
        };

        _list = new ListView
        {
            Location = new Point(14, 56),
            Size = new Size(572, 400),
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            GridLines = false,
        };
        _list.Columns.Add("功能", 400);
        _list.Columns.Add("当前快捷键", 150);
        _list.SelectedIndexChanged += OnSelected;

        var captureLabel = new Label
        {
            Text = "新快捷键：",
            Location = new Point(14, 468),
            AutoSize = true,
        };
        _capture = new TextBox
        {
            Location = new Point(96, 464),
            Width = 190,
            ReadOnly = true,
            TabStop = false,
        };
        _hint = new Label
        {
            Text = "先点一行功能",
            Location = new Point(296, 468),
            AutoSize = true,
            ForeColor = Color.DimGray,
        };

        var reset = new Button { Text = "恢复默认(&R)", Location = new Point(14, 508), Size = new Size(112, 30) };
        reset.Click += (_, _) => ResetAll();
        var clear = new Button { Text = "清除(&D)", Location = new Point(134, 508), Size = new Size(90, 30) };
        clear.Click += (_, _) => ClearCurrent();
        var ok = new Button { Text = "确定", Location = new Point(404, 508), Size = new Size(90, 30) };
        ok.Click += (_, _) => Confirm();
        var cancel = new Button
        {
            Text = "取消",
            Location = new Point(498, 508),
            Size = new Size(90, 30),
            DialogResult = DialogResult.Cancel,
        };
        AcceptButton = ok;
        CancelButton = cancel;

        foreach (var row in rows)
        {
            _rows.Add((row.Id, row.Label, row.Default));
            _values[row.Id] = row.Current;
            var item = new ListViewItem(new[] { row.Label, Display(row.Current) });
            item.Tag = row.Id;
            _list.Items.Add(item);
        }

        Controls.AddRange(new Control[] { tip, _list, captureLabel, _capture, _hint, reset, clear, ok, cancel });
    }

    private static string Display(Keys keys) => keys == Keys.None ? "（无）" : ShortcutText.Format(keys);

    private void OnSelected(object? sender, EventArgs e)
    {
        if (_list.SelectedItems.Count == 0)
        {
            _capture.Text = string.Empty;
            _hint.Text = "先点一行功能";
            _hint.ForeColor = Color.DimGray;
            return;
        }
        var id = (string)_list.SelectedItems[0].Tag!;
        _capture.Text = ShortcutText.Format(_values[id]);
        _hint.Text = "按下新组合键…";
        _hint.ForeColor = Color.DimGray;
        // 焦点留在列表里，这样方向键还能换行；组合键统一由 ProcessCmdKey 收
        _list.Focus();
    }

    /// <summary>
    /// 选中一行后，除对话框自己的导航键外，任何按键都当作新组合键记录下来。
    /// 放在 ProcessCmdKey 里做，才能拿到 Alt 组合和 F 键（否则会被菜单加速键先吃掉）。
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_list.SelectedItems.Count == 0) return base.ProcessCmdKey(ref msg, keyData);
        if (IsNavigationKey(keyData)) return base.ProcessCmdKey(ref msg, keyData);
        HandleKey(keyData);
        return true;
    }

    private static bool IsNavigationKey(Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Tab:
            case Keys.Shift | Keys.Tab:
            case Keys.Enter:
            case Keys.Escape:
            case Keys.Up:
            case Keys.Down:
            case Keys.PageUp:
            case Keys.PageDown:
            case Keys.Home:
            case Keys.End:
            case Keys.Left:
            case Keys.Right:
                return true;
        }
        return false;
    }

    private void HandleKey(Keys keyData)
    {
        var code = keyData & Keys.KeyCode;
        // 只按下了修饰键——继续等主键
        if (code == Keys.ControlKey || code == Keys.ShiftKey || code == Keys.Menu
            || code == Keys.LWin || code == Keys.RWin)
            return;

        if (code == Keys.Delete || code == Keys.Back)
        {
            SetCurrent(Keys.None);
            return;
        }

        if (!ShortcutText.IsUsable(keyData))
        {
            Warn("这个组合不行：需要 Ctrl / Alt / Shift，或单独用 F1–F12。");
            return;
        }

        // 单按 Alt+字母 很容易被顶层菜单的加速键抢走
        if ((keyData & Keys.Alt) == Keys.Alt
            && (keyData & (Keys.Control | Keys.Shift)) == Keys.None
            && Array.IndexOf(MenuBarAccelerators, code) >= 0)
        {
            Warn("Alt+" + ShortcutText.Format(code).Replace("Alt+", string.Empty)
                 + " 会被顶部菜单抢走，请再加 Ctrl 或 Shift。");
            return;
        }

        SetCurrent(keyData);
    }

    private void SetCurrent(Keys keys)
    {
        if (_list.SelectedItems.Count == 0) return;
        var item = _list.SelectedItems[0];
        var id = (string)item.Tag!;
        _values[id] = keys;
        item.SubItems[1].Text = Display(keys);
        _capture.Text = ShortcutText.Format(keys);
        _hint.Text = keys == Keys.None ? "已清空（不设快捷键）" : "已记录";
        _hint.ForeColor = Color.DimGray;
    }

    private void ClearCurrent()
    {
        if (_list.SelectedItems.Count == 0) return;
        SetCurrent(Keys.None);
    }

    private void ResetAll()
    {
        foreach (var row in _rows) _values[row.Id] = row.Default;
        foreach (ListViewItem item in _list.Items)
        {
            var id = (string)item.Tag!;
            item.SubItems[1].Text = Display(_values[id]);
        }
        if (_list.SelectedItems.Count > 0)
        {
            var id = (string)_list.SelectedItems[0].Tag!;
            _capture.Text = ShortcutText.Format(_values[id]);
        }
        _hint.Text = "已全部恢复默认";
        _hint.ForeColor = Color.DimGray;
    }

    private void Confirm()
    {
        // 1) 不能占用编辑器内置/固定分支的组合
        foreach (var row in _rows)
        {
            var keys = _values[row.Id];
            if (keys == Keys.None) continue;
            foreach (var (reserved, who) in Reserved)
            {
                if (keys != reserved) continue;
                Reject($"「{row.Label}」不能用 {ShortcutText.Format(keys)}，它已经归「{who}」用。");
                return;
            }
        }

        // 2) 同一组合不能绑两项
        var used = new Dictionary<Keys, string>();
        foreach (var row in _rows)
        {
            var keys = _values[row.Id];
            if (keys == Keys.None) continue;
            if (used.TryGetValue(keys, out var other))
            {
                Reject($"「{other}」和「{row.Label}」都用了 {ShortcutText.Format(keys)}，请改掉其中一个。");
                return;
            }
            used[keys] = row.Label;
        }

        _result = new Dictionary<string, Keys>(_values);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Reject(string message)
    {
        _hint.Text = message;
        _hint.ForeColor = Color.Firebrick;
        MessageBox.Show(this, message, "快捷键冲突", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void Warn(string message)
    {
        _hint.Text = message;
        _hint.ForeColor = Color.Firebrick;
    }
}
