using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Memopad.Services;
using Memopad.Views;

namespace Memopad;

/// <summary>
/// アプリケーションの入口（v0.8.0 で WPF の App から Windows Forms に置き換え）。
/// 起動を速くするため、ここから MainForm が表示されるまでの間は WPF のアセンブリを一切読み込まない。
/// WPF はダイアログ（フォント・色変更・配色パターン・行へ移動・ページ設定・バージョン情報・保存確認）を
/// 初めて開くときに WpfHost が遅延初期化する。
///
/// MemoPad 增强（单实例 v3）：
/// v1 用 Global 互斥锁 + Process.MainWindowHandle 激活窗口。若锁被另一个 Windows 会话里的实例
/// （或一个已经没有可见窗口的残留进程）占着，第二次双击就会「静默退出」——「点了没反应 / 打不开」。
/// v2 改为会话内（Local）单实例 + 命名事件握手 + 激活失败则照常自行启动（永不静默失败）。
/// v3（2026-11-05 换机测试报告 P2）：改回 Global 锁，但保留「请求激活 + 2.5 秒超时 + 无响应则自行启动」
/// 的完整兜底——跨会话也能唤醒已有实例；唤醒失败才双开（比 Local 下常态双开更少见），
/// 快照另有 recovery-backup 10 份备份兜底，风险可控。诊断信息写入 %APPDATA%\MemoPad\startup.log。
/// </summary>
internal static class Program
{
    /// <summary>アプリ全体で共有する設定。</summary>
    public static AppSettings Settings { get; private set; } = new();

    /// <summary>当前正在运行的窗口（供激活监听线程回调）。</summary>
    public static MainForm? Form { get; private set; }

    // 单实例锁用 Global（跨会话共享）。拿锁失败时先尝试唤醒已有实例（事件握手 + 2.5 秒超时），
    // 唤醒不了就照常自行启动——有兜底，所以 Global 锁不会造成「点了没反应」。
    private const string MutexName = @"Global\MemoPad_SingleInstance_v2";
    private const string ActivateEventName = @"Global\MemoPad_Activate_v2";
    private const string AckEventName = @"Global\MemoPad_ActivateAck_v2";

    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _activateRequest;
    private static EventWaitHandle? _activateAck;
    private static Thread? _activateListener;
    private static bool _startedWithoutMutex;

    [STAThread]
    private static void Main(string[] args)
    {
        PerfLog.Mark("主程序启动（运行时就绪）");

        // MemoPad 增强（单文件便携版）：Scintilla 原生库不在 exe 旁时，从内嵌资源释放到
        // runtimes\win-x64\native\（必须在任何 Scintilla 类型加载之前执行）
        EnsureNativeLibraries();

        // MemoPad 增强：单实例。拿不到锁说明已有实例在跑，先请它把窗口亮出来。
        if (!TryClaimInstance())
        {
            if (RequestActivation(args))
            {
                Log("已有实例已激活，本实例退出");
                return;
            }
            // 老实例没有响应（卡死 / 无窗口 / 属于别的会话）：照常自行启动，绝不让用户「点了没反应」
            Log("已有实例无响应，作为新实例启动");
            _startedWithoutMutex = true;
        }

        StartActivationListener();
        if (_startedWithoutMutex) StartMutexRetry();

        // 後で使う DLL を別スレッドで先に読み込む（起動時間の短縮）
        StartupWarmup.Start();
        // ANSI（Shift_JIS）を扱えるようにする
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // MemoPad 增强（换机一键配置）：设置加载前先确保目录联接，
        // 使 settings.json / recovery 直接读坚果云同步盘（新机双击即自动配置）
        EnsureCloudJunction();

        Settings = SettingsService.Load();
        PerfLog.Mark("设置加载完成");

        // csproj の ApplicationHighDpiMode／ApplicationVisualStyles／ApplicationUseCompatibleTextRendering を適用する
        ApplicationConfiguration.Initialize();
        PerfLog.Mark("Windows Forms を初期化");

        var form = new MainForm(args);
        Form = form;
        PerfLog.Mark("主窗体生成完成");
        try
        {
            Application.Run(form);
        }
        finally
        {
            Form = null;
        }
        SettingsService.Save(Settings);
    }

    // =====================================================================
    // 换机一键配置：自动创建数据目录联接（%APPDATA%\MemoPad → 坚果云盘\MemoPad）
    // =====================================================================

    /// <summary>
    /// MemoPad 增强（单文件便携版）：确保 Scintilla 原生库可用。
    /// Scintilla.NET 硬性要求 exe 同级 runtimes\win-x64\native\ 下存在 Scintilla.dll/Lexilla.dll，
    /// 单文件打包无法满足；因此把两个原生库内嵌为程序集资源，发现缺失时自动释放到该路径。
    /// 幂等：已存在（正常部署/用户手动补齐）则不动；释放失败（程序目录只读等）时给出可读指引，
    /// 而不是让 Scintilla 崩溃成裸异常（2026-11-05 换机测试报告 v2 新增风险 1）。
    /// </summary>
    private static void EnsureNativeLibraries()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
            var scintilla = Path.Combine(dir, "Scintilla.dll");
            var lexilla = Path.Combine(dir, "Lexilla.dll");
            if (File.Exists(scintilla) && File.Exists(lexilla)) return;
            Directory.CreateDirectory(dir);
            ExtractResource("Memopad.Native.Scintilla.dll", scintilla);
            ExtractResource("Memopad.Native.Lexilla.dll", lexilla);
        }
        catch (Exception ex)
        {
            try
            {
                MessageBox.Show(
                    "MemoPad 无法在程序目录释放 Scintilla 组件（Scintilla.dll / Lexilla.dll）。\n\n" +
                    "可能原因：程序放在了只读位置（如 Program Files、只读 U 盘或网络共享）。\n" +
                    "请把 MemoPad.exe 移动到可写目录（如桌面或 D 盘），然后重新启动。\n\n" +
                    $"详细信息：{ex.Message}",
                    "MemoPad", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch
            {
                // 连提示框都弹不出时静默——保持原有行为
            }
        }
    }

    private static void ExtractResource(string resourceName, string targetPath)
    {
        using var source = typeof(Program).Assembly.GetManifestResourceStream(resourceName);
        if (source is null) throw new InvalidOperationException($"资源缺失: {resourceName}");
        using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
        source.CopyTo(target);
    }

    // =====================================================================
    // 换机一键配置：自动创建数据目录联接（%APPDATA%\MemoPad → 坚果云盘\MemoPad）
    // =====================================================================

    /// <summary>
    /// MemoPad 增强（换机一键配置）：新机首次启动时，若坚果云同步盘存在且 %APPDATA%\MemoPad
    /// 不是目录联接，自动创建联接指向坚果云盘\MemoPad，使 settings/recovery 直接读云盘数据。
    /// 幂等：已是联接 → 跳过；存在非空真实目录 → 不动（保护数据）；无坚果云 → 跳过（用本机目录）。
    /// 空目录残留（上次建联失败产生）→ 删除后重建，实现自愈。
    /// </summary>
    private static void EnsureCloudJunction()
    {
        try
        {
            var cloudRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Nutstore", "1", "我的坚果云");
            if (!Directory.Exists(cloudRoot)) return; // 无坚果云：数据留在本机

            var link = SettingsService.SettingsDirectory; // %APPDATA%\MemoPad
            var di = new DirectoryInfo(link);
            if (di.Exists)
            {
                if ((di.Attributes & FileAttributes.ReparsePoint) != 0) return; // 已是联接
                // 真实目录：非空不动（保护数据），空则视为残留，删除后重建联接
                if (di.EnumerateFileSystemInfos().Any()) return;
                try { di.Delete(); }
                catch { return; }
            }

            var target = Path.Combine(cloudRoot, "MemoPad");
            Directory.CreateDirectory(target);
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using (var p = Process.Start(psi))
            {
                p?.WaitForExit(10000);
            }
            if (Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is not null)
            {
                Log($"已自动创建目录联接：{link} → {target}");
            }
            else
            {
                Log($"目录联接创建失败：{link} → {target}（将继续使用本机目录）");
            }
        }
        catch (Exception ex)
        {
            try { Log($"目录联接自动配置异常：{ex.Message}"); } catch { }
        }
    }

    // =====================================================================
    // 单实例：互斥锁（会话内）+ 命名事件握手
    // =====================================================================

    /// <summary>取得单实例锁。成功（或无法创建锁）返回 true；已有实例在运行返回 false。</summary>
    private static bool TryClaimInstance()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (createdNew)
            {
                _instanceMutex = mutex;
                return true;
            }
            mutex.Dispose();
            return false;
        }
        catch
        {
            // 拿不到锁（权限等）也不阻止启动
            return true;
        }
    }

    /// <summary>请已在运行的实例唤醒窗口，并等它的确认（确认由对方 UI 线程发出）。成功返回 true。</summary>
    private static bool RequestActivation(string[] args)
    {
        try
        {
            QueuePendingOpen(args);

            if (!EventWaitHandle.TryOpenExisting(ActivateEventName, out var request)) return false;
            using (request)
            {
                if (!EventWaitHandle.TryOpenExisting(AckEventName, out var ack)) return false;
                using (ack)
                {
                    ack.Reset();
                    request.Set();
                    // 2.5 秒内没收到确认：认为对方不可用（无窗口 / UI 线程卡住），返回 false 走自行启动
                    return ack.WaitOne(TimeSpan.FromMilliseconds(2500));
                }
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把启动参数里的文件路径写进待打开清单，交给已运行的实例处理。</summary>
    private static void QueuePendingOpen(string[] args)
    {
        try
        {
            var paths = new List<string>();
            foreach (var path in args)
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) paths.Add(path);
            }
            if (paths.Count == 0) return;
            File.AppendAllLines(PendingOpenPath, paths, new UTF8Encoding(false));
        }
        catch
        {
            // 转发失败不影响激活
        }
    }

    private static string PendingOpenPath =>
        Path.Combine(SettingsService.SettingsDirectory, "pending-open.txt");

    /// <summary>在后台线程等「另一个实例请求激活」的事件，转到 UI 线程还原窗口并回确认。</summary>
    private static void StartActivationListener()
    {
        try
        {
            _activateRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName, out _);
            _activateAck = new EventWaitHandle(false, EventResetMode.AutoReset, AckEventName, out _);
        }
        catch
        {
            return;
        }

        _activateListener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!_activateRequest.WaitOne()) continue;
                    var form = Form;
                    if (form is null || form.IsDisposed)
                    {
                        _activateAck.Set();
                        continue;
                    }
                    form.BeginInvoke(() =>
                    {
                        try
                        {
                            form.ActivateFromOtherInstance();
                            form.OpenPathsFromOtherInstance(TakePendingOpen());
                        }
                        catch
                        {
                            // 激活失败也要回确认，避免对方误判为僵尸进程
                        }
                        finally
                        {
                            _activateAck.Set();
                        }
                    });
                }
                catch
                {
                    // 事件对象被释放等异常：继续等待
                }
            }
        })
        {
            IsBackground = true,
            Name = "MemoPad-Activate",
        };
        _activateListener.Start();
    }

    /// <summary>
    /// 兜底启动的实例：后台定期再尝试拿锁。拿到后它就变成唯一的可激活实例，
    /// 之后双击快捷方式会重新走「激活已有窗口」，不会再叠出第二个窗口。
    /// </summary>
    private static void StartMutexRetry()
    {
        var thread = new Thread(() =>
        {
            var deadline = DateTime.UtcNow.AddMinutes(10);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(3000);
                if (TryClaimInstance())
                {
                    Log("兜底实例已接管单实例锁");
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "MemoPad-ClaimMutex",
        };
        thread.Start();
    }

    /// <summary>取出并清空待打开清单。</summary>
    private static string[] TakePendingOpen()
    {
        try
        {
            if (!File.Exists(PendingOpenPath)) return Array.Empty<string>();
            var lines = File.ReadAllLines(PendingOpenPath);
            File.Delete(PendingOpenPath);
            return lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>启动诊断日志（只在单实例相关路径写，正常使用几乎不产生内容）。</summary>
    private static void Log(string message)
    {
        try
        {
            var path = Path.Combine(SettingsService.SettingsDirectory, "startup.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 200_000) info.Delete();
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [pid {Environment.ProcessId}] {message}\n", new UTF8Encoding(false));
        }
        catch
        {
            // 日志写不进去不影响运行
        }
    }
}
