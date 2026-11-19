using System;
using System.Drawing;
using System.Windows.Forms;

namespace Memopad.Dialogs;

public sealed class LineSpacingDialog : Form
{
	private readonly NumericUpDown _spacing;

	public int LineSpacing { get; private set; }

	public static int? Ask(IWin32Window owner, int current)
	{
		using LineSpacingDialog lineSpacingDialog = new LineSpacingDialog(current);
		return (lineSpacingDialog.ShowDialog(owner) == DialogResult.OK) ? new int?(lineSpacingDialog.LineSpacing) : ((int?)null);
	}

	private LineSpacingDialog(int current)
	{
		Text = "行间距";
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		StartPosition = FormStartPosition.CenterParent;
		ClientSize = new Size(320, 128);
		Font = new Font("Microsoft YaHei UI", 9f);
		Label label = new Label
		{
			Text = "行间距（像素）：",
			Location = new Point(16, 22),
			AutoSize = true
		};
		_spacing = new NumericUpDown
		{
			Minimum = 0m,
			Maximum = 40m,
			Increment = 1m,
			Value = Math.Clamp(current, 0, 40),
			Location = new Point(122, 19),
			Width = 70
		};
		Label label2 = new Label
		{
			Text = "0 = 默认行距，数值越大行与行之间越宽",
			Location = new Point(16, 52),
			AutoSize = true,
			ForeColor = Color.Gray
		};
		Button button = new Button
		{
			Text = "确定",
			DialogResult = DialogResult.OK,
			Location = new Point(120, 86),
			Size = new Size(82, 28)
		};
		button.Click += (object? _, EventArgs _) =>
		{
			LineSpacing = (int)_spacing.Value;
		};
		Button button2 = new Button
		{
			Text = "取消",
			DialogResult = DialogResult.Cancel,
			Location = new Point(212, 86),
			Size = new Size(82, 28)
		};
		AcceptButton = button;
		CancelButton = button2;
		Controls.AddRange(label, _spacing, label2, button, button2);
	}
}
