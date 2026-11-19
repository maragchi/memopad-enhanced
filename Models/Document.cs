using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Memopad.Services;

namespace Memopad.Models;

/// <summary>
/// タブ 1 つ分の文書。本文そのものはエディタ（TextBox）が持ち、ここではファイルの属性と変更状態を持つ。
/// </summary>
public sealed class Document : INotifyPropertyChanged
{
    private static int _untitledCounter;

    private string? _filePath;
    private bool _isDirty;
    private string? _previewText;
    private TextEncodingKind _encoding = TextEncodingKind.Utf8;
    private LineEndingKind _lineEnding = LineEndingKind.Crlf;

    public Document()
        : this(null)
    {
    }

    public Document(string? untitledName)
    {
        // 「未命名」「未命名 2」… とメモ帳風に採番する（会话恢复时可用存档里的旧名字）
        var n = Interlocked.Increment(ref _untitledCounter);
        UntitledName = untitledName ?? (n == 1 ? "未命名" : $"未命名 {n}");
    }

    public string UntitledName { get; }

    /// <summary>保存先。未保存の新規文書なら null。</summary>
    public string? FilePath
    {
        get => _filePath;
        set { if (Set(ref _filePath, value)) { Raise(nameof(Title)); Raise(nameof(TabLabel)); Raise(nameof(TabTitle)); } }
    }

    /// <summary>未保存の変更があるか。タブの●印とタイトルバーの * に使う。</summary>
    public bool IsDirty
    {
        get => _isDirty;
        set { if (Set(ref _isDirty, value)) Raise(nameof(TabTitle)); }
    }

    public TextEncodingKind Encoding
    {
        get => _encoding;
        set => Set(ref _encoding, value);
    }

    public LineEndingKind LineEnding
    {
        get => _lineEnding;
        set => Set(ref _lineEnding, value);
    }

    /// <summary>未保存标签的正文预览（取开头一行，截断到 12 字符）。标签名优先用它；窗口标题栏仍用文件名。</summary>
    public string? PreviewText
    {
        get => _previewText;
        set { if (Set(ref _previewText, value)) { Raise(nameof(Title)); Raise(nameof(TabLabel)); Raise(nameof(TabTitle)); } }
    }

    /// <summary>タブとタイトルバーに出す名前（拡張子付きのファイル名、または「未命名」、未保存時は正文预览）。</summary>
    public string Title => FilePath is not null
        ? Path.GetFileName(FilePath)
        : string.IsNullOrEmpty(PreviewText) ? UntitledName : PreviewText!;

    /// <summary>タブに描く表示名。正文预览优先，预览为空（空文档/首行空白）时回退 Title。</summary>
    public string TabLabel => string.IsNullOrEmpty(PreviewText) ? Title : PreviewText!;

    /// <summary>タブに出す名前。未保存なら●を付ける。</summary>
    public string TabTitle => IsDirty ? $"● {TabLabel}" : TabLabel;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
