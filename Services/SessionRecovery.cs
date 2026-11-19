using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Memopad.Services;

/// <summary>
/// 会话快照（recovery）的读写。
/// 关闭时把所有标签（含未保存内容）写入 %APPDATA%\MemoPad\recovery\，
/// 下次启动无条件恢复；写入新快照前先把旧快照备份到 recovery-backup（保留最近 10 份）。
/// </summary>
public static class SessionRecovery
{
    private static readonly RecoveryJsonContext Context = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static string RecoveryDirectory => Path.Combine(SettingsService.SettingsDirectory, "recovery");

    public static string ManifestPath => Path.Combine(RecoveryDirectory, "manifest.json");

    public static RecoveryManifest? Load()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(ManifestPath), Context.RecoveryManifest);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(RecoveryManifest manifest)
    {
        try
        {
            Directory.CreateDirectory(RecoveryDirectory);
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, Context.RecoveryManifest));
        }
        catch
        {
            // 快照保存失败也不阻止退出
        }
    }

    public static string WriteContent(string text)
    {
        var name = Guid.NewGuid().ToString("N") + ".txt";
        Directory.CreateDirectory(RecoveryDirectory);
        File.WriteAllText(Path.Combine(RecoveryDirectory, name), text, new UTF8Encoding(false));
        return name;
    }

    public static string? ReadContent(string contentFile)
    {
        try
        {
            var path = Path.Combine(RecoveryDirectory, contentFile);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>清空当前快照。先把旧快照备份到 recovery-backup，防止覆盖后无法找回。</summary>
    public static void Clear()
    {
        try
        {
            if (Directory.Exists(RecoveryDirectory))
            {
                Backup();
                Directory.Delete(RecoveryDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }

    /// <summary>把旧快照备份到 recovery-backup（时间戳目录，只保留最近 10 份）。</summary>
    private static void Backup()
    {
        try
        {
            var backupRoot = Path.Combine(SettingsService.SettingsDirectory, "recovery-backup");
            Directory.CreateDirectory(backupRoot);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var dest = Path.Combine(backupRoot, stamp);
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.GetFiles(RecoveryDirectory))
            {
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
            }
            foreach (var dir in Directory.GetDirectories(backupRoot).OrderByDescending(d => d).Skip(10))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
        }
    }
}
