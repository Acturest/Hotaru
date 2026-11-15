using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;

namespace FloatingTranslator
{
    public partial class App : Application
    {
        public static AppConfig Config;
        public static HistoryStore History;
        public static WordBook Words;
        public static string BaseDir;

        private Mutex _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            BaseDir = AppDomain.CurrentDomain.BaseDirectory;

            // --demo <模块>：开发/验证钩子，启动即摆出某个模块，便于自动截图检查
            string demo = null;
            string[] a = e.Args;
            for (int i = 0; i < a.Length; i++)
            {
                if (string.Equals(a[i], "--demo", StringComparison.OrdinalIgnoreCase) && i + 1 < a.Length)
                    demo = a[i + 1];
            }

            // 带 --demo 时跳过单实例检查：否则已经开机常驻的那一个实例会把自动截图挡在门外
            if (demo == null)
            {
                bool createdNew;
                _mutex = new Mutex(true, "FloatingTranslator_SingleInstance_v1", out createdNew);
                if (!createdNew)
                {
                    MessageBox.Show("悬浮翻译器已经在运行了。", "悬浮翻译器",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    Shutdown();
                    return;
                }
            }

            Log("启动 悬浮翻译器 " + Version);
            Log("运行环境: 完整性级别=" + IntegrityLevel() + " 系统=" + Environment.OSVersion.Version +
                " .NET=" + Environment.Version);
            Config = AppConfig.Load();
            History = new HistoryStore();
            Words = new WordBook();

            base.OnStartup(e);

            DispatcherUnhandledException += (s, args) =>
            {
                Log("未处理异常: " + args.Exception);
                args.Handled = true;
            };

            var win = new MainWindow();

            if (demo != null)
            {
                string d = demo;
                win.Loaded += (s2, e2) => win.Dispatcher.InvokeAsync(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(400);
                    win.RunDemo(d);
                });
            }

            win.Show();
        }

        public static string Version
        {
            get
            {
                try
                {
                    var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                    return v.Major + "." + v.Minor + "." + v.Build;
                }
                catch { return "1.0.0"; }
            }
        }

        public static string LogPath
        {
            get { return Path.Combine(BaseDir, "悬浮翻译器.log"); }
        }

        /// <summary>进程完整性级别（低/中/高）。语音合成（SAPI）在「低」权限下会被系统拒绝，
        /// 把它写进日志是为了区分「沙箱里跑」和「用户正常双击跑」两种环境。</summary>
        public static string IntegrityLevel()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    IntPtr token = id.Token;
                    int len;
                    GetTokenInformation(token, 25 /* TokenIntegrityLevel */, IntPtr.Zero, 0, out len);
                    if (len <= 0) return "未知";
                    IntPtr buf = Marshal.AllocHGlobal(len);
                    try
                    {
                        if (!GetTokenInformation(token, 25, buf, len, out len)) return "未知";
                        IntPtr sid = Marshal.ReadIntPtr(buf);       // TOKEN_MANDATORY_LABEL.Label.Sid
                        byte subs = Marshal.ReadByte(sid, 1);       // SID SubAuthorityCount
                        int last = Marshal.ReadInt32(sid, 8 + (subs - 1) * 4);
                        if (last >= 0x4000) return "系统";
                        if (last >= 0x3000) return "高";
                        if (last >= 0x2000) return "中";
                        if (last >= 0x1000) return "低";
                        return "未知(0x" + last.ToString("X") + ")";
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            catch (Exception ex) { return "读取失败: " + ex.Message; }
        }

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int len, out int retLen);

        // 日志会被多个线程同时写：UI 线程（各段合成/播放）与音频线程（WaveOutStream 的 Starved/Error 回调）。
        // File.AppendAllText 并发写同一文件会抛「文件正被另一进程使用」，虽然被下面 catch 掉了，但那一行就丢了 —— 加个锁。
        private static readonly object _logLock = new object();

        /// <summary>单个日志文件的上限；到了就归档，免得长年累月无限长。</summary>
        private const long LogMaxBytes = 2L * 1024 * 1024;
        /// <summary>保留几份历史归档（.1 最新 → .2 更旧）。</summary>
        private const int LogKeepArchives = 2;

        // 从 0x3F 起步：进程启动后第一次写日志就会做一次大小检查（上次退出时可能已经写超了）；
        // 之后每 64 行才查一次 FileInfo.Length —— 朗读/字幕高频打点时不必每次都发一次系统调用。
        private static int _logWrites = 0x3F;

        public static void Log(string msg)
        {
            lock (_logLock)
            {
                try
                {
                    if ((++_logWrites & 0x3F) == 0) RotateLogIfNeeded();
                    File.AppendAllText(LogPath,
                        "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg + Environment.NewLine,
                        Encoding.UTF8);
                }
                catch { }
            }
        }

        /// <summary>超过上限就把当前日志改名成 .1，旧的 .1 顺延成 .2，最老的直接删。</summary>
        private static void RotateLogIfNeeded()
        {
            try
            {
                var fi = new FileInfo(LogPath);
                if (!fi.Exists || fi.Length < LogMaxBytes) return;
                for (int i = LogKeepArchives - 1; i >= 1; i--)
                {
                    string from = LogPath + "." + i;
                    string to = LogPath + "." + (i + 1);
                    if (!File.Exists(from)) continue;
                    if (File.Exists(to)) File.Delete(to);
                    File.Move(from, to);
                }
                string first = LogPath + ".1";
                if (File.Exists(first)) File.Delete(first);
                File.Move(LogPath, first);
            }
            catch { }
        }
    }
}
