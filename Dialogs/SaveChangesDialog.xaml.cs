using System.Windows;

namespace Memopad.Dialogs;

public enum SaveChangesResult
{
    Save,
    DontSave,
    Cancel,
}

/// <summary>关闭有未保存更改的标签时的确认。返回「保存／不保存／取消」。</summary>
public partial class SaveChangesDialog : Window
{
    private SaveChangesResult _result = SaveChangesResult.Cancel;

    public SaveChangesDialog(IntPtr owner, string documentTitle)
    {
        InitializeComponent();
        new System.Windows.Interop.WindowInteropHelper(this).Owner = owner;
        MessageText.Text = $"要保存对 {documentTitle} 的更改吗？";
    }

    public static SaveChangesResult Ask(IntPtr owner, string documentTitle)
    {
        var dialog = new SaveChangesDialog(owner, documentTitle);
        dialog.ShowDialog();
        return dialog._result;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _result = SaveChangesResult.Save;
        DialogResult = true;
    }

    private void DontSaveButton_Click(object sender, RoutedEventArgs e)
    {
        _result = SaveChangesResult.DontSave;
        DialogResult = true;
    }
}
