using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Memopad.Services;

/// <summary>标记（荧光笔）的持久化：MarkStore
/// 纯 txt 装不下「哪几个字被标了颜色」，所以在正文旁边挂一个同名的 .marks.json 侧车文件。
/// 位置按字节记（与 Scintilla 的 position 口径一致），加载时按当前位置还原；
/// 若正文被外部程序改过，偏移可能对不上，加载时会把越界部分夹到文档长度内。</summary>
public static class MarkStore
{
    public sealed class MarkFile
    {
        public int Version { get; set; } = 1;
        public List<MarkEntry> Marks { get; set; } = new();
    }

    public sealed class MarkEntry
    {
        /// <summary>起始字节偏移</summary>
        public int S { get; set; }
        /// <summary>长度（字节）</summary>
        public int L { get; set; }
        /// <summary>颜色序号 0-4</summary>
        public int C { get; set; }
    }

    public static string SidecarPath(string textPath) => textPath + ".marks.json";

    /// <summary>读取标记；文件不存在或损坏都返回空列表，绝不影响正文打开。</summary>
    public static List<(int Start, int Length, int Color)> Load(string textPath)
    {
        var result = new List<(int, int, int)>();
        try
        {
            var path = SidecarPath(textPath);
            if (!File.Exists(path)) return result;
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize(json, MarkJsonContext.Default.MarkFile);
            if (file?.Marks is null) return result;
            foreach (var m in file.Marks)
            {
                if (m.L > 0) result.Add((m.S, m.L, m.C));
            }
        }
        catch
        {
            // 侧车文件坏了就当没标记，不打扰用户
        }
        return result;
    }

    /// <summary>写入标记；没有标记时删除侧车文件，避免留下空壳。</summary>
    public static void Save(string textPath, IReadOnlyList<(int Start, int Length, int Color)> marks)
    {
        try
        {
            var path = SidecarPath(textPath);
            if (marks.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            var file = new MarkFile();
            foreach (var m in marks)
            {
                file.Marks.Add(new MarkEntry { S = m.Start, L = m.Length, C = m.Color });
            }
            var json = JsonSerializer.Serialize(file, MarkJsonContext.Default.MarkFile);
            File.WriteAllText(path, json);
        }
        catch
        {
            // 写失败不能影响正文保存
        }
    }
}

/// <summary>MarkStore 的 JSON 转换代码（编译时生成）。</summary>
[JsonSerializable(typeof(MarkStore.MarkFile))]
internal sealed partial class MarkJsonContext : JsonSerializerContext
{
}
