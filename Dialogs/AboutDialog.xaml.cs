using System.Reflection;
using System.Windows;
using Memopad.Services;

namespace Memopad.Dialogs;

/// <summary>「关于 MemoPad」。显示版本号与设置文件位置。</summary>
public partial class AboutDialog : Window
{
    public AboutDialog(IntPtr owner)
    {
        InitializeComponent();
        new System.Windows.Interop.WindowInteropHelper(this).Owner = owner;
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"版本 {version?.ToString(3) ?? "?"}";
        SettingsPathText.Text = SettingsService.SettingsPath;
    }
}
