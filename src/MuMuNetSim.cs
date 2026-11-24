// MuMuNetSim - MuMu 模拟器弱网 / 断网模拟器（单文件 WinForms）
// 编译：powershell -File build.ps1   （或 csc /target:winexe /codepage:65001 ...）
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("MuMuNetSim")]
[assembly: System.Reflection.AssemblyProduct("MuMu 弱网 / 断网模拟器")]
[assembly: System.Reflection.AssemblyDescription("MuMu 模拟器实例 / 局域网手机的弱网、断网、延迟模拟")]
[assembly: System.Reflection.AssemblyVersion("1.5.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.5.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("v1.5")]

namespace MuMuNetSim
{
    public class InstanceInfo
    {
        public int Index;
        public string Name = "";
        public string AndroidVersion = "";
        public bool Running;
        public bool AndroidStarted;
        public int AdbPort = 5555;

        public override string ToString()
        {
            return "实例 " + Index + " · " + Name + " (Android " + AndroidVersion + ")  " + (Running ? (AndroidStarted ? "运行中" : "启动中") : "未运行");
        }
    }

    public static class App
    {
        public const string TAG = "mumu-net";
        public const string TITLE = "MuMu 弱网 / 断网模拟器  v1.5";

        public static string BaseDir = Path.GetDirectoryName(Application.ExecutablePath);
        public static string IniPath = Path.Combine(BaseDir, "MuMuNetSim.ini");
        public static string LogPath = Path.Combine(Path.GetTempPath(), "MuMuNetSim.log");

        public static string MuMuManagerPath;
        public static string ClumsyPath;
        public static string PhoneIp = "";
        public static string AssetDir = "";      // 单文件版解包出来的资产目录

        // ---------------------------------------------------------- 单文件版的资源解包

        static readonly string[][] Assets = new string[][] {
            new string[] { "asset.clumsy.exe",        @"clumsy\clumsy.exe" },
            new string[] { "asset.WinDivert.dll",     @"clumsy\WinDivert.dll" },
            new string[] { "asset.WinDivert64.sys",   @"clumsy\WinDivert64.sys" },
            new string[] { "asset.config.txt",        @"clumsy\config.txt" },
            new string[] { "asset.clumsy-license.txt",@"clumsy\License.txt" },
            new string[] { "asset.adb.exe",           @"adb\adb.exe" },
            new string[] { "asset.AdbWinApi.dll",     @"adb\AdbWinApi.dll" },
            new string[] { "asset.AdbWinUsbApi.dll",  @"adb\AdbWinUsbApi.dll" },
            new string[] { "asset.thirdparty.txt",    "第三方组件与许可.txt" },
            new string[] { "asset.readme.txt",        "使用说明.txt" }
        };

        public static bool DirWritable(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".wtest_" + Guid.NewGuid().ToString("N").Substring(0, 6));
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public static void EnsureAssetsDirForIni()
        {
            try
            {
                var dir = Path.GetDirectoryName(IniPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            catch { }
        }

        public static void EnsureAssets()
        {            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var names = asm.GetManifestResourceNames();
                if (!names.Any(n => n.EndsWith("asset.WinDivert.dll", StringComparison.OrdinalIgnoreCase))) return;  // 不是单文件版

                string home = BaseDir;
                if (!DirWritable(home))
                    home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MuMuNetSim");
                AssetDir = home;

                foreach (var pair in Assets)
                {
                    var resName = names.FirstOrDefault(n => n.EndsWith(pair[0], StringComparison.OrdinalIgnoreCase));
                    if (resName == null) continue;
                    var target = Path.Combine(home, pair[1]);
                    var targetDir = Path.GetDirectoryName(target);
                    if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                    using (var s = asm.GetManifestResourceStream(resName))
                    {
                        // 使用说明/许可：缺失时才写；二进制按大小一致就跳过（避免每次启动重写 ~10MB）
                        bool text = pair[1].EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
                        if (File.Exists(target))
                        {
                            if (text) continue;
                            if (new FileInfo(target).Length == s.Length) continue;
                        }
                        using (var f = File.Create(target)) s.CopyTo(f);
                    }
                }
                Log("单文件版：资产已解包到 " + home);
            }
            catch (Exception ex)
            {
                Log("资产解包失败：" + ex.Message);
            }
        }

        // 每实例配置：预设 / 延迟 ms / 上行 / 下行
        public static readonly Dictionary<string, string> Presets = new Dictionary<string, string>();
        public static readonly Dictionary<string, int> Delays = new Dictionary<string, int>();
        public static readonly Dictionary<string, int> Jitters = new Dictionary<string, int>();
        public static readonly Dictionary<string, int> Limits = new Dictionary<string, int>();
        public static readonly Dictionary<string, bool> Ups = new Dictionary<string, bool>();
        public static readonly Dictionary<string, bool> Downs = new Dictionary<string, bool>();

        public static void Log(string s)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }

        public static void LoadIni()
        {
            try
            {
                if (!File.Exists(IniPath)) return;
                foreach (var line in File.ReadAllLines(IniPath, Encoding.UTF8))
                {
                    var i = line.IndexOf('=');
                    if (i <= 0) continue;
                    var k = line.Substring(0, i).Trim();
                    var v = line.Substring(i + 1).Trim();
                    if (string.Equals(k, "clumsy", StringComparison.OrdinalIgnoreCase)) ClumsyPath = v;
                    else if (string.Equals(k, "mumumanager", StringComparison.OrdinalIgnoreCase)) MuMuManagerPath = v;
                    else if (string.Equals(k, "phone.ip", StringComparison.OrdinalIgnoreCase)) PhoneIp = v;
                    else if (k.StartsWith("preset.", StringComparison.OrdinalIgnoreCase)) Presets[k.Substring(7)] = v;
                    else if (k.StartsWith("delay.", StringComparison.OrdinalIgnoreCase)) { int ms; if (int.TryParse(v, out ms)) Delays[k.Substring(6)] = ms; }
                    else if (k.StartsWith("jitter.", StringComparison.OrdinalIgnoreCase)) { int j; if (int.TryParse(v, out j)) Jitters[k.Substring(7)] = j; }
                    else if (k.StartsWith("limit.", StringComparison.OrdinalIgnoreCase)) { int l; if (int.TryParse(v, out l)) Limits[k.Substring(6)] = l; }
                    else if (k.StartsWith("up.", StringComparison.OrdinalIgnoreCase)) Ups[k.Substring(3)] = (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));
                    else if (k.StartsWith("down.", StringComparison.OrdinalIgnoreCase)) Downs[k.Substring(5)] = (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));
                }
            }
            catch { }
        }

        public static void SaveIni()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("clumsy=" + (ClumsyPath ?? ""));
                sb.AppendLine("mumumanager=" + (MuMuManagerPath ?? ""));
                if (!string.IsNullOrEmpty(PhoneIp)) sb.AppendLine("phone.ip=" + PhoneIp);
                foreach (var kv in Presets) sb.AppendLine("preset." + kv.Key + "=" + kv.Value);
                foreach (var kv in Delays) sb.AppendLine("delay." + kv.Key + "=" + kv.Value);
                foreach (var kv in Jitters) sb.AppendLine("jitter." + kv.Key + "=" + kv.Value);
                foreach (var kv in Limits) sb.AppendLine("limit." + kv.Key + "=" + kv.Value);
                foreach (var kv in Ups) sb.AppendLine("up." + kv.Key + "=" + (kv.Value ? "1" : "0"));
                foreach (var kv in Downs) sb.AppendLine("down." + kv.Key + "=" + (kv.Value ? "1" : "0"));
                File.WriteAllText(IniPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        public static bool IsAdmin()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static string FindMuMuManager()
        {
            if (!string.IsNullOrEmpty(MuMuManagerPath) && File.Exists(MuMuManagerPath)) { MuMuManagerSource = "ini 配置"; return MuMuManagerPath; }
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            // 1) 常见安装路径
            var cands = new List<string>();
            cands.Add(Path.Combine(pf, @"Netease\MuMu\nx_main\MuMuManager.exe"));
            cands.Add(Path.Combine(pf86, @"Netease\MuMu\nx_main\MuMuManager.exe"));
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Network) continue;
                    cands.Add(Path.Combine(d.RootDirectory.FullName, @"Program Files\Netease\MuMu\nx_main\MuMuManager.exe"));
                    cands.Add(Path.Combine(d.RootDirectory.FullName, @"Netease\MuMu\nx_main\MuMuManager.exe"));
                }
                catch { }
            }
            foreach (var c in cands) if (File.Exists(c)) { MuMuManagerPath = c; MuMuManagerSource = "常见安装路径"; return c; }

            // 2) 从正在运行的 MuMu 进程反推安装目录（最通用）
            foreach (var pn in new[] { "MuMuNxMain", "MuMuNxService", "MuMuNxDevice" })
            {
                try
                {
                    foreach (var pr in Process.GetProcessesByName(pn))
                    {
                        try
                        {
                            var exe = pr.MainModule.FileName;
                            var dir = Path.GetDirectoryName(exe);
                            for (int up = 0; up < 3 && dir != null; up++)
                            {
                                var f = Path.Combine(dir, "MuMuManager.exe");
                                if (File.Exists(f)) { MuMuManagerPath = f; MuMuManagerSource = "从运行中的 " + pn + " 反推"; return f; }
                                dir = Path.GetDirectoryName(dir);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // 3) 注册表卸载项
            try
            {
                foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                {
                    foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
                    {
                        using (var k = hive.OpenSubKey(root))
                        {
                            if (k == null) continue;
                            foreach (var sn in k.GetSubKeyNames())
                            {
                                using (var sk = k.OpenSubKey(sn))
                                {
                                    if (sk == null) continue;
                                    var dn = Convert.ToString(sk.GetValue("DisplayName")) ?? "";
                                    if (dn.IndexOf("MuMu", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                    var loc = Convert.ToString(sk.GetValue("InstallLocation")) ?? "";
                                    if (loc.Length == 0) continue;
                                    var f = Path.Combine(loc, @"nx_main\MuMuManager.exe");
                                    if (!File.Exists(f)) f = Path.Combine(loc, @"MuMu\nx_main\MuMuManager.exe");
                                    if (File.Exists(f)) { MuMuManagerPath = f; MuMuManagerSource = "注册表卸载项"; return f; }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // 4) PATH
            try
            {
                foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(d)) continue;
                    var f = Path.Combine(d.Trim(), "MuMuManager.exe");
                    if (File.Exists(f)) { MuMuManagerPath = f; MuMuManagerSource = "PATH"; return f; }
                }
            }
            catch { }

            return null;
        }

        public static string MuMuManagerSource = "";

        public static string FindClumsy()
        {
            if (!string.IsNullOrEmpty(ClumsyPath) && File.Exists(ClumsyPath)) return ClumsyPath;
            var cands = new List<string>();
            if (!string.IsNullOrEmpty(AssetDir)) cands.Add(Path.Combine(AssetDir, @"clumsy\clumsy.exe"));
            cands.Add(Path.Combine(BaseDir, @"clumsy\clumsy.exe"));
            cands.Add(Path.Combine(BaseDir, "clumsy.exe"));
            cands.Add(@"D:\clumsy\clumsy.exe");
            cands.Add(@"C:\clumsy\clumsy.exe");
            foreach (var c in cands) if (File.Exists(c)) { ClumsyPath = c; return c; }
            return null;
        }

        public static string FindWinDivertDir()
        {
            var cands = new List<string>();
            if (!string.IsNullOrEmpty(AssetDir)) cands.Add(Path.Combine(AssetDir, "clumsy"));
            cands.Add(Path.Combine(BaseDir, "clumsy"));
            cands.Add(BaseDir);
            cands.Add(@"D:\clumsy");
            var cp = FindClumsy();
            if (cp != null) cands.Insert(0, Path.GetDirectoryName(cp));
            foreach (var d in cands)
                if (File.Exists(Path.Combine(d, "WinDivert.dll"))) return d;
            return null;
        }
    }

    public static class Proc
    {
        public static string Run(string exe, string args, int timeoutMs)
        {
            if (string.IsNullOrEmpty(exe)) return "<NOEXE>";
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                using (var p = new Process())
                {
                    p.StartInfo = psi;
                    var sb = new StringBuilder();
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) sb.AppendLine(e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return "<TIMEOUT>"; }
                    p.WaitForExit();
                    return sb.ToString();
                }
            }
            catch (Exception ex) { return "<ERROR: " + ex.Message + ">"; }
        }
    }

    // ---------------------------------------------------------------- WinDivert（自带 clumsy 里的 2.2）

    public static class WinDivertNative
    {
        const int LAYER_NETWORK = 0;
        const int SHUTDOWN_RECV = 1;
        static bool _loaded;
        public static string DllDir;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetDllDirectory(string lpPathName);

        [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        static extern IntPtr WinDivertOpen([MarshalAs(UnmanagedType.LPStr)] string filter, int layer, short priority, ulong flags);

        [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        static extern bool WinDivertRecv(IntPtr handle, byte[] pPacket, uint packetLen, out uint pRecvLen, byte[] pAddr);

        [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        static extern bool WinDivertSend(IntPtr handle, byte[] pPacket, uint packetLen, out uint pSendLen, byte[] pAddr);

        [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        static extern bool WinDivertShutdown(IntPtr handle, int how);

        [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        static extern bool WinDivertClose(IntPtr handle);

        public static bool EnsureLoaded(out string err)
        {
            err = "";
            if (_loaded) return true;
            DllDir = App.FindWinDivertDir();
            if (DllDir == null) { err = "找不到 WinDivert.dll（应随 clumsy 一起放在程序目录下）"; return false; }
            SetDllDirectory(DllDir);
            var h = LoadLibrary(Path.Combine(DllDir, "WinDivert.dll"));
            if (h == IntPtr.Zero)
            {
                err = "加载 WinDivert.dll 失败：" + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }
            _loaded = true;
            return true;
        }

        public static IntPtr Open(string filter, out string err)
        {
            err = "";
            if (!EnsureLoaded(out err)) return IntPtr.Zero;
            var h = WinDivertOpen(filter, LAYER_NETWORK, 0, 0);
            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                err = "WinDivertOpen 失败：" + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
                return IntPtr.Zero;
            }
            return h;
        }

        public static void Shutdown(IntPtr h) { try { WinDivertShutdown(h, SHUTDOWN_RECV); } catch { } }
        public static bool Recv(IntPtr h, byte[] p, uint len, out uint rlen, byte[] addr) { return WinDivertRecv(h, p, len, out rlen, addr); }
        public static bool Send(IntPtr h, byte[] p, uint len, out uint slen, byte[] addr) { return WinDivertSend(h, p, len, out slen, addr); }
        public static void Close(IntPtr h) { try { WinDivertClose(h); } catch { } }
    }

    // 单个实例的延迟引擎：一个（或两个）WinDivert 句柄，收包 -> 按到期时间重发
    public class DelayEngine
    {
        public int Instance;
        public int Ms;
        public bool Up;
        public bool Down;
        public volatile bool Stopping;
        public long Received;
        public long Sent;
        public long HoldSumMs;
        public long HoldCount;
        public long LastHoldMs;
        public int JitterMs;      // 抖动：每包延迟在 [Ms-J, Ms+J] 内随机
        public int LimitKBps;     // 限速：每方向令牌桶，KB/s
        public string Filter = "";
        public string LastError = "";
        public DateTime StartedAt = DateTime.Now;

        class Pkt { public byte[] Data; public byte[] Addr; public long Due; public long RecvAt; }
        class Ctx
        {
            public IntPtr H;
            public Thread RecvThread;
            public Thread SendThread;
            public readonly SortedDictionary<long, Queue<Pkt>> Q = new SortedDictionary<long, Queue<Pkt>>();
            public readonly object Lock = new object();
            public volatile bool RecvDone;
        }

        readonly List<Ctx> _ctxs = new List<Ctx>();
        readonly Random _rnd = new Random();
        static readonly Stopwatch Sw = Stopwatch.StartNew();
        static long NowMs() { return Sw.ElapsedMilliseconds; }

        public string DirText { get { return (Up ? "上行" : "") + (Down ? (Up ? "+" : "") + "下行" : ""); } }

        public bool Start(string filter, out string err)
        {
            err = "";
            Filter = filter;
            var list = new List<string>();
            if (Up) list.Add(filter + " and outbound");
            if (Down) list.Add(filter + " and inbound");
            foreach (var f in list)
            {
                string e;
                var h = WinDivertNative.Open(f, out e);
                if (h == IntPtr.Zero) { err = e; Stop(); return false; }
                var ctx = new Ctx();
                ctx.H = h;
                _ctxs.Add(ctx);
                ctx.RecvThread = new Thread(delegate() { RecvLoop(ctx); });
                ctx.RecvThread.IsBackground = true;
                ctx.SendThread = new Thread(delegate() { SendLoop(ctx); });
                ctx.SendThread.IsBackground = true;
                ctx.RecvThread.Start();
                ctx.SendThread.Start();
            }
            return true;
        }

        void RecvLoop(Ctx ctx)
        {
            var buf = new byte[65535];
            var addr = new byte[64];
            while (!Stopping)
            {
                uint len;
                if (!WinDivertNative.Recv(ctx.H, buf, 65535, out len, addr))
                {
                    if (Stopping) break;
                    Thread.Sleep(1);
                    continue;
                }
                Interlocked.Increment(ref Received);
                var pkt = new byte[len];
                Buffer.BlockCopy(buf, 0, pkt, 0, (int)len);
                var a = new byte[64];
                Buffer.BlockCopy(addr, 0, a, 0, 64);
                var p = new Pkt();
                long hold = Ms;
                if (JitterMs > 0) hold += _rnd.Next(-JitterMs, JitterMs + 1);
                if (hold < 0) hold = 0;
                p.Data = pkt; p.Addr = a; p.RecvAt = NowMs(); p.Due = p.RecvAt + hold;
                lock (ctx.Lock)
                {
                    Queue<Pkt> q;
                    if (!ctx.Q.TryGetValue(p.Due, out q)) { q = new Queue<Pkt>(); ctx.Q[p.Due] = q; }
                    q.Enqueue(p);
                }
            }
            ctx.RecvDone = true;
        }

        void SendLoop(Ctx ctx)
        {
            while (true)
            {
                Pkt p = null;
                lock (ctx.Lock)
                {
                    if (ctx.Q.Count > 0)
                    {
                        long key = ctx.Q.Keys.First();
                        if (key <= NowMs() + 1)
                        {
                            var q = ctx.Q[key];
                            p = q.Dequeue();
                            if (q.Count == 0) ctx.Q.Remove(key);
                        }
                    }
                }
                if (p == null)
                {
                    if (Stopping && ctx.RecvDone) break;
                    Thread.Sleep(1);
                    continue;
                }
                // 限速：按包大小反推应占用的时间（令牌桶的简化等价写法，单方向串行发送）
                if (LimitKBps > 0)
                {
                    double need = p.Data.Length * 1000.0 / (LimitKBps * 1024.0);
                    if (need >= 1) Thread.Sleep((int)Math.Min(need, 2000));
                }
                uint sl;
                long held = NowMs() - p.RecvAt;
                LastHoldMs = held;
                Interlocked.Add(ref HoldSumMs, held);
                Interlocked.Increment(ref HoldCount);
                if (WinDivertNative.Send(ctx.H, p.Data, (uint)p.Data.Length, out sl, p.Addr)) Interlocked.Increment(ref Sent);
            }
        }

        public void Stop()
        {
            Stopping = true;
            foreach (var c in _ctxs) WinDivertNative.Shutdown(c.H);
            foreach (var c in _ctxs) { try { c.RecvThread.Join(1500); } catch { } }
            // 把队列里剩下的按到期时间顺序立刻发出去，避免丢包
            foreach (var c in _ctxs)
            {
                while (true)
                {
                    Pkt p = null;
                    lock (c.Lock)
                    {
                        if (c.Q.Count > 0)
                        {
                            long key = c.Q.Keys.First();
                            var q = c.Q[key];
                            p = q.Dequeue();
                            if (q.Count == 0) c.Q.Remove(key);
                        }
                    }
                    if (p == null) break;
                    uint sl;
                    WinDivertNative.Send(c.H, p.Data, (uint)p.Data.Length, out sl, p.Addr);
                }
                WinDivertNative.Close(c.H);
            }
            _ctxs.Clear();
        }
    }

    // ---------------------------------------------------------------- 端口表（按 PID）

    public static class NetTable
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

        const int AF_INET = 2, TCP_TABLE_OWNER_PID_ALL = 5, UDP_TABLE_OWNER_PID = 1;

        static ushort Swap(ushort v) { return (ushort)(((v & 0xFF) << 8) | ((v >> 8) & 0xFF)); }

        public static List<int> TcpPorts(int pid)
        {
            var ports = new List<int>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (size <= 0) return ports;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return ports;
                int rows = Marshal.ReadInt32(buf);
                for (int i = 0; i < rows; i++)
                {
                    int off = 4 + i * 24;
                    uint localAddr = (uint)Marshal.ReadInt32(buf, off + 4);
                    ushort localPort = (ushort)Marshal.ReadInt32(buf, off + 8);
                    int owner = Marshal.ReadInt32(buf, off + 20);
                    if (owner != pid) continue;
                    if ((localAddr & 0xFF) == 127) continue;      // 回环不算
                    int p = Swap(localPort);
                    if (p >= 1024) ports.Add(p);
                }
            }
            catch { }
            finally { Marshal.FreeHGlobal(buf); }
            return ports;
        }

        public static List<int> UdpPorts(int pid)
        {
            var ports = new List<int>();
            int size = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref size, true, AF_INET, UDP_TABLE_OWNER_PID, 0);
            if (size <= 0) return ports;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedUdpTable(buf, ref size, true, AF_INET, UDP_TABLE_OWNER_PID, 0) != 0) return ports;
                int rows = Marshal.ReadInt32(buf);
                for (int i = 0; i < rows; i++)
                {
                    int off = 4 + i * 12;
                    uint localAddr = (uint)Marshal.ReadInt32(buf, off);
                    ushort localPort = (ushort)Marshal.ReadInt32(buf, off + 4);
                    int owner = Marshal.ReadInt32(buf, off + 8);
                    if (owner != pid) continue;
                    if ((localAddr & 0xFF) == 127) continue;
                    int p = Swap(localPort);
                    if (p >= 1024) ports.Add(p);
                }
            }
            catch { }
            finally { Marshal.FreeHGlobal(buf); }
            return ports;
        }
    }

    // ---------------------------------------------------------------- 业务引擎

    public class VmmInfo { public int Instance; public int Pid; }

    public static class Engine
    {
        static string Q(string s) { return "\"" + (s ?? "").Replace("\"", "\\\"") + "\""; }

        public static string Guest(int idx, string cmd, int timeoutMs)
        {
            return Proc.Run(App.MuMuManagerPath, "sh -v " + idx + " -c " + Q(cmd), timeoutMs);
        }

        public static List<InstanceInfo> ListInstances()
        {
            var list = new List<InstanceInfo>();
            var json = Proc.Run(App.MuMuManagerPath, "info -v all", 15000);
            if (json.StartsWith("<")) return list;
            foreach (Match m in Regex.Matches(json, "\"(\\d+)\"\\s*:\\s*\\{([^{}]*)\\}", RegexOptions.Singleline))
            {
                var body = m.Groups[2].Value;
                var inst = new InstanceInfo();
                inst.Index = int.Parse(m.Groups[1].Value);
                inst.Running = Regex.IsMatch(body, "\"is_process_started\"\\s*:\\s*true");
                inst.AndroidStarted = Regex.IsMatch(body, "\"is_android_started\"\\s*:\\s*true");
                var nm = Regex.Match(body, "\"name\"\\s*:\\s*\"([^\"]*)\"");
                inst.Name = nm.Success ? nm.Groups[1].Value : ("实例 " + inst.Index);
                var av = Regex.Match(body, "\"android_version\"\\s*:\\s*\"([^\"]*)\"");
                inst.AndroidVersion = av.Success ? av.Groups[1].Value : "";
                var ap = Regex.Match(body, "\"adb_port\"\\s*:\\s*(\\d+)");
                inst.AdbPort = ap.Success ? int.Parse(ap.Groups[1].Value) : 5555;
                list.Add(inst);
            }
            list.Sort(delegate(InstanceInfo a, InstanceInfo b) { return a.Index.CompareTo(b.Index); });
            return list;
        }

        // 实例号 -> MuMuVMMHeadless 进程（命令行里 --comment MuMuPlayer-12.0-<idx>）
        public static List<VmmInfo> ListVmms()
        {
            var list = new List<VmmInfo>();
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='MuMuVMMHeadless.exe'"))
                using (var res = q.Get())
                {
                    foreach (ManagementObject mo in res)
                    {
                        var cmd = Convert.ToString(mo["CommandLine"]) ?? "";
                        var m = Regex.Match(cmd, @"--comment\s+MuMuPlayer-[\d.]+-(\d+)");
                        if (!m.Success) continue;
                        var v = new VmmInfo();
                        v.Instance = int.Parse(m.Groups[1].Value);
                        v.Pid = Convert.ToInt32(mo["ProcessId"]);
                        list.Add(v);
                    }
                }
            }
            catch { }
            return list;
        }

        // 该实例在宿主机上的 WinDivert 过滤器（按 VMM 进程占用的 TCP/UDP 端口）
        public static string BuildPortFilter(int idx, out int portCount)
        {
            portCount = 0;
            var vmms = ListVmms().Where(v => v.Instance == idx).ToList();
            if (vmms.Count == 0) return null;
            var tcp = new List<int>();
            var udp = new List<int>();
            foreach (var v in vmms)
            {
                try { tcp.AddRange(NetTable.TcpPorts(v.Pid)); } catch { }
                try { udp.AddRange(NetTable.UdpPorts(v.Pid)); } catch { }
            }
            tcp = tcp.Distinct().OrderBy(x => x).Take(24).ToList();
            udp = udp.Distinct().OrderBy(x => x).Take(24).ToList();
            portCount = tcp.Count + udp.Count;
            if (portCount == 0) return null;
            var parts = new List<string>();
            if (tcp.Count > 0)
                parts.Add("(tcp and (" + string.Join(" or ", tcp.Select(p => "tcp.SrcPort == " + p + " or tcp.DstPort == " + p).ToArray()) + "))");
            if (udp.Count > 0)
                parts.Add("(udp and (" + string.Join(" or ", udp.Select(p => "udp.SrcPort == " + p + " or udp.DstPort == " + p).ToArray()) + "))");
            return "(" + string.Join(" or ", parts.ToArray()) + ")";
        }

        // 环境自检：用户点「刷新实例」或 /selfcheck 时输出，方便排查别人机器上的环境差异
        public static List<string> SelfCheck()
        {
            var L = new List<string>();
            L.Add("版本：" + App.TITLE + "　" + (Environment.Is64BitOperatingSystem ? "64 位系统 ✓" : "32 位系统 ✗（WinDivert 是 x64，延迟不可用）"));
            L.Add("管理员：" + (App.IsAdmin() ? "是 ✓（延迟引擎、代理防火墙放行都可用）" : "否 ✗（延迟会报『拒绝访问』，需要右键以管理员身份运行；丢包/断网不受影响）"));
            L.Add("程序目录：" + App.BaseDir + (string.IsNullOrEmpty(App.AssetDir) ? "" : "　单文件资产解包：" + App.AssetDir));
            L.Add("ini：" + App.IniPath + "　日志：" + App.LogPath);

            App.FindMuMuManager();
            L.Add("MuMuManager：" + (App.MuMuManagerPath ?? "未找到 ✗（模拟器功能不可用，可点『定位 MuMu』手动指定）")
                + (string.IsNullOrEmpty(App.MuMuManagerSource) ? "" : "　来源：" + App.MuMuManagerSource));

            string adb = "adb";
            try { adb = Phone.AdbPath(); } catch { }
            string adbVer = "";
            try
            {
                var o = Proc.Run(adb, "version", 8000);
                adbVer = (o.Split('\n').FirstOrDefault(x => x.IndexOf("Android Debug Bridge", StringComparison.OrdinalIgnoreCase) >= 0) ?? "").Trim();
            }
            catch { }
            L.Add("adb：" + adb + (adbVer.Length > 0 ? "　" + adbVer : "　（版本获取失败）"));

            var wd = App.FindWinDivertDir();
            if (wd == null) L.Add("WinDivert：未找到 ✗（延迟功能不可用）");
            else
                L.Add("WinDivert：" + wd + "　dll" + (File.Exists(Path.Combine(wd, "WinDivert.dll")) ? "✓" : "✗")
                    + " 驱动" + (File.Exists(Path.Combine(wd, "WinDivert64.sys")) ? "✓" : "✗")
                    + " 服务：" + DriverState());
            L.Add("clumsy：" + (App.FindClumsy() ?? "未找到（全局弱网不可用）"));
            L.Add("代理入站放行：MuMuNetSim Delay Proxy 8899 → " + (FirewallRuleExists("MuMuNetSim Delay Proxy 8899") ? "已存在 ✓" : "暂无（启动手机时延代理时会自动添加）"));

            try
            {
                var insts = ListInstances();
                var running = insts.Where(i => i.Running).ToList();
                L.Add("MuMu 实例：共 " + insts.Count + " 个，运行中 " + running.Count + " 个"
                    + (running.Count > 0 ? "（" + string.Join("、", running.Select(i => i.Index + "·" + i.Name).ToArray()) + "）" : ""));
                var vmms = ListVmms();
                L.Add("VMM 进程：" + (vmms.Count == 0 ? "无（实例没起来时属正常）" : string.Join("、", vmms.Select(v => "实例" + v.Instance + " pid=" + v.Pid).ToArray())));
            }
            catch (Exception ex) { L.Add("实例枚举失败：" + ex.Message); }
            return L;
        }

        static string DriverState()
        {
            try
            {
                var o = Proc.Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), "query WinDivert", 8000);
                if (o.Contains("RUNNING")) return "已运行";
                if (o.Contains("STOPPED")) return "已安装未启动";
                if (o.Contains("1060") || o.Contains("does not exist") || o.Contains("指定的服务")) return "未安装（首次开启延迟时会自动装）";
                return "未知";
            }
            catch { return "未知"; }
        }

        static bool FirewallRuleExists(string ruleName)
        {
            try
            {
                var o = Proc.Run(Path.Combine(Environment.SystemDirectory, "netsh.exe"),
                    "advfirewall firewall show rule name=\"" + ruleName + "\"", 8000);
                return o.Contains(ruleName);
            }
            catch { return false; }
        }

        public static int ClearRules(int idx)
        {
            int removed = 0;
            foreach (var chain in new[] { "INPUT", "OUTPUT" })
            {
                for (int pass = 0; pass < 6; pass++)
                {
                    var outp = Guest(idx, "iptables -L " + chain + " -n --line-numbers", 20000);
                    var nums = new List<int>();
                    foreach (var line in outp.Split('\n'))
                    {
                        var t = line.Trim();
                        var m = Regex.Match(t, @"^(\d+)\s+.*" + Regex.Escape(App.TAG));
                        if (m.Success) nums.Add(int.Parse(m.Groups[1].Value));
                    }
                    if (nums.Count == 0) break;
                    nums.Sort();
                    nums.Reverse();
                    foreach (var n in nums) { Guest(idx, "iptables -D " + chain + " " + n, 20000); removed++; }
                }
            }
            return removed;
        }

        static int[] AdbPorts(int adbHostPort)
        {
            var l = new List<int>();
            l.Add(5555);
            if (adbHostPort > 0 && adbHostPort != 5555) l.Add(adbHostPort);
            return l.ToArray();
        }

        public static void ApplyOffline(int idx, int adbPort, bool bothDirections)
        {
            var t = App.TAG;
            var cmds = new List<string>();
            int p = 1;
            cmds.Add("iptables -I OUTPUT " + (p++) + " -o lo -m comment --comment " + t + " -j ACCEPT");
            foreach (var port in AdbPorts(adbPort))
                cmds.Add("iptables -I OUTPUT " + (p++) + " -p tcp --sport " + port + " -m comment --comment " + t + " -j ACCEPT");
            cmds.Add("iptables -I OUTPUT " + (p++) + " -m comment --comment " + t + " -j DROP");
            if (bothDirections)
            {
                int q = 1;
                cmds.Add("iptables -I INPUT " + (q++) + " -i lo -m comment --comment " + t + " -j ACCEPT");
                foreach (var port in AdbPorts(adbPort))
                    cmds.Add("iptables -I INPUT " + (q++) + " -p tcp --dport " + port + " -m comment --comment " + t + " -j ACCEPT");
                cmds.Add("iptables -I INPUT " + (q++) + " -m comment --comment " + t + " -j DROP");
            }
            Guest(idx, string.Join("; ", cmds.ToArray()), 30000);
        }

        public static void ApplyLoss(int idx, int adbPort, double probability)
        {
            var t = App.TAG;
            var prob = probability.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
            var cmds = new List<string>();
            int p = 1;
            cmds.Add("iptables -I OUTPUT " + (p++) + " -o lo -m comment --comment " + t + " -j ACCEPT");
            foreach (var port in AdbPorts(adbPort))
                cmds.Add("iptables -I OUTPUT " + (p++) + " -p tcp --sport " + port + " -m comment --comment " + t + " -j ACCEPT");
            cmds.Add("iptables -I OUTPUT " + (p++) + " -m statistic --mode random --probability " + prob + " -m comment --comment " + t + " -j DROP");
            Guest(idx, string.Join("; ", cmds.ToArray()), 30000);
        }

        // 预设（丢包）-> 动作；返回显示文字
        public static string ApplyPreset(int idx, string preset, int adbPort)
        {
            string key = (preset ?? "normal").ToLowerInvariant();
            if (key == "offline") { ClearRules(idx); ApplyOffline(idx, adbPort, true); return "断网"; }
            if (key.StartsWith("loss"))
            {
                int pct;
                if (int.TryParse(key.Substring(4), out pct))
                {
                    ClearRules(idx);
                    ApplyLoss(idx, adbPort, pct / 100.0);
                    return "丢包 " + pct + "%";
                }
            }
            ClearRules(idx);
            return "正常";
        }

        public static bool LaunchInstance(int idx, Action<string> log)
        {
            var outp = Proc.Run(App.MuMuManagerPath, "control launch -v " + idx, 90000);
            var ok = !outp.StartsWith("<");
            log("启动实例 " + idx + (ok ? " 已下发（启动完成后会自动套用预设）" : " 失败：" + outp.Trim()));
            return ok;
        }

        public static string Ping(int idx, int count)
        {
            var outp = Guest(idx, "timeout 20 ping -c " + count + " -W 2 8.8.8.8 | tail -2", 30000);
            return Regex.Replace(outp.Trim(), @"\s+", " ");
        }

        public static int StopClumsy()
        {
            int n = 0;
            try { foreach (var p in Process.GetProcessesByName("clumsy")) { try { p.Kill(); n++; } catch { } } }
            catch { }
            for (int i = 0; i < 20; i++) { if (Process.GetProcessesByName("clumsy").Length == 0) break; Thread.Sleep(200); }
            return n;
        }

        public static bool StartClumsy(int lagMs, int dropPercent, int autoStopSec, int waitSec, Action<string> log)
        {
            var exe = App.FindClumsy();
            if (exe == null) { log("找不到 clumsy.exe"); return false; }
            StopClumsy();
            var sb = new StringBuilder();
            sb.Append("--filter \"outbound and ip\"");
            if (lagMs > 0) sb.Append(" --lag on --lag-time " + lagMs);
            if (dropPercent > 0) sb.Append(" --drop on --drop-chance " + dropPercent);
            if (autoStopSec > 0) sb.Append(" --timeout " + autoStopSec);
            log("启动 clumsy：" + sb);
            try
            {
                var psi = new ProcessStartInfo(exe, sb.ToString());
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                Process.Start(psi);
            }
            catch (Exception ex) { log("启动 clumsy 失败：" + ex.Message); return false; }
            for (int i = 0; i < waitSec * 4; i++)
            {
                Thread.Sleep(250);
                if (Process.GetProcessesByName("clumsy").Length > 0) { Thread.Sleep(1200); return true; }
            }
            log("clumsy 没起来");
            return false;
        }
    }

    public class PhoneInfo
    {
        public string Serial = "";
        public bool Connected;
        public string Model = "", Android = "", RootState = "未知";
        public bool Root;
        public bool NeedSu;        // true: 用 su -c 提权；false: adb shell 本身就是 root
        public int AdbPort = 5555;
        public string Applied = "";
    }

    public static class Phone
    {
        const string OUT_CHAIN = "MT_OUT";   // 手机端专用链，方便一键清空

        public static string AdbPath()
        {
            var cands = new List<string>();
            // 1) 程序自带（单文件版解包 / 目录版 adb\）——包体自包含，不依赖本机有没有 MuMu / Android SDK
            if (!string.IsNullOrEmpty(App.AssetDir)) cands.Add(Path.Combine(App.AssetDir, @"adb\adb.exe"));
            cands.Add(Path.Combine(App.BaseDir, @"adb\adb.exe"));
            // 2) 本机 Android SDK / 其它 adb
            var sdk = Environment.GetEnvironmentVariable("ANDROID_HOME");
            if (string.IsNullOrEmpty(sdk)) sdk = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
            if (!string.IsNullOrEmpty(sdk)) cands.Add(Path.Combine(sdk, @"platform-tools\adb.exe"));
            var lhd = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(lhd)) cands.Add(Path.Combine(lhd, @"Android\Sdk\platform-tools\adb.exe"));
            cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"AdbTools\adb\platform-tools\adb.exe"));
            // 3) MuMu 自带
            var mdir = Path.GetDirectoryName(App.MuMuManagerPath ?? "");
            if (!string.IsNullOrEmpty(mdir)) cands.Add(Path.Combine(mdir, "adb.exe"));
            foreach (var c in cands) if (File.Exists(c)) { AdbSource = c; return c; }
            AdbSource = "PATH 里的 adb";
            return "adb";
        }

        public static string AdbSource = "";

        static string Q(string s) { return "\"" + (s ?? "").Replace("\"", "\\\"") + "\""; }
        static string Run(string args, int timeoutMs) { return Proc.Run(AdbPath(), args, timeoutMs); }

        public static bool Connect(string serial)
        {
            var o = Run("connect " + serial, 20000);
            return !o.Contains("failed") && !o.Contains("cannot") && !o.StartsWith("<ERROR");
        }

        public static void Disconnect(string serial) { Run("disconnect " + serial, 8000); }

        public static List<string> Devices()
        {
            var list = new List<string>();
            var o = Run("devices", 8000);
            foreach (var line in o.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith("List of")) continue;
                if (t.Contains("\tdevice") || t.EndsWith(" device")) list.Add(t.Split('\t')[0].Trim());
            }
            return list;
        }

        public static string Shell(string serial, string cmd) { return Run("-s " + serial + " shell " + Q(cmd), 20000); }
        public static string ShellSu(string serial, string cmd) { return Run("-s " + serial + " shell " + Q("su -c '" + (cmd ?? "").Replace("'", "") + "'"), 25000); }

        // 按设备能力选择提权方式
        public static string ShellRun(PhoneInfo p, string cmd)
        {
            return p.NeedSu ? ShellSu(p.Serial, cmd) : Shell(p.Serial, cmd);
        }

        public static PhoneInfo Probe(string serial)
        {
            var info = new PhoneInfo();
            info.Serial = serial;
            var parts = serial.Split(':');
            if (parts.Length > 1) { int port; if (int.TryParse(parts[parts.Length - 1], out port)) info.AdbPort = port; }

            var devs = Devices();
            info.Connected = devs.Any(d => string.Equals(d, serial, StringComparison.OrdinalIgnoreCase));
            if (!info.Connected) return info;

            info.Model = Shell(serial, "getprop ro.product.model").Trim();
            info.Android = Shell(serial, "getprop ro.build.version.release").Trim();
            var id = Shell(serial, "id").Trim();
            if (id.Contains("uid=0"))
            {
                info.Root = true; info.NeedSu = false; info.RootState = "是（adb shell 直接 root）";
            }
            else
            {
                var suId = ShellSu(serial, "id").Trim();
                info.Root = suId.Contains("uid=0");
                info.NeedSu = true;
                info.RootState = info.Root ? "是（su）" : "否（不支持 su）";
            }
            return info;
        }

        // 给手机设/清全局 HTTP 代理（免 root，走 adb shell settings）
        public static string SetGlobalProxy(string serial, string hostPort)
        {
            return Shell(serial, "settings put global http_proxy " + hostPort);
        }

        public static string ClearGlobalProxy(string serial)
        {
            var a = Shell(serial, "settings put global http_proxy :0");
            var b = Shell(serial, "settings delete global http_proxy");
            return a + b;
        }

        public static string GetGlobalProxy(string serial)
        {
            return Shell(serial, "settings get global http_proxy").Trim();
        }

        // 设备内那部分（丢包 / 断网）：独立链 MT_OUT，始终放行 lo 与 adb 端口
        public static string ApplyDevicePreset(PhoneInfo p, string key, int lossPct)
        {
            key = (key ?? "normal").ToLowerInvariant();
            int adbPort = p.AdbPort > 0 ? p.AdbPort : 5555;
            if (key == "normal" || key == "delay") return Clear(p);

            var sb = new StringBuilder();
            sb.Append("iptables -N " + OUT_CHAIN + " 2>/dev/null; ");
            sb.Append("iptables -F " + OUT_CHAIN + " 2>/dev/null; ");
            sb.Append("iptables -A " + OUT_CHAIN + " -o lo -j ACCEPT; ");
            sb.Append("iptables -A " + OUT_CHAIN + " -p tcp --sport " + adbPort + " -j ACCEPT; ");

            string desc;
            if (key == "offline")
            {
                sb.Append("iptables -A " + OUT_CHAIN + " -j DROP; ");
                desc = "断网";
            }
            else
            {
                if (lossPct <= 0) return Clear(p);
                if (lossPct > 100) lossPct = 100;
                var prob = (lossPct / 100.0).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
                sb.Append("iptables -A " + OUT_CHAIN + " -m statistic --mode random --probability " + prob + " -j DROP; ");
                desc = "丢包 " + lossPct + "%";
            }
            sb.Append("iptables -C OUTPUT -j " + OUT_CHAIN + " 2>/dev/null || iptables -I OUTPUT 1 -j " + OUT_CHAIN);
            var outp = ShellRun(p, sb.ToString());
            if (outp.Contains("Permission denied") || outp.Contains("No chain/target") || outp.Contains("not found"))
                throw new Exception(outp.Trim());
            p.Applied = desc;
            return desc;
        }

        public static string Clear(PhoneInfo p)
        {
            string cmd = "iptables -F " + OUT_CHAIN + " 2>/dev/null; " +
                         "iptables -D OUTPUT -j " + OUT_CHAIN + " 2>/dev/null; " +
                         "iptables -D OUTPUT -j " + OUT_CHAIN + " 2>/dev/null; " +
                         "iptables -X " + OUT_CHAIN + " 2>/dev/null; true";
            try { ShellRun(p, cmd); } catch { }
            p.Applied = "正常";
            return "正常";
        }
    }

    // 本机延迟代理：给手机设全局 HTTP 代理，用「带时延/丢包的转发」实现手机侧弱网（免 root）
    public class DelayProxy
    {
        public int Port = 8899;
        public int DelayMs;
        public int JitterMs;      // 抖动
        public int LimitKBps;     // 限速
        public bool Up = true, Down = true;      // 上行 = 手机->服务器，下行 = 服务器->手机
        public int LossPercent;
        public long Connections, BytesUp, BytesDown;
        volatile bool _stop;
        TcpListener _listener;
        readonly Random _rnd = new Random();

        public bool Start(out string err)
        {
            err = "";
            for (int p = Port; p < Port + 20; p++)
            {
                try
                {
                    var l = new TcpListener(IPAddress.Any, p);
                    l.Start();
                    _listener = l;
                    Port = p;
                    var th = new Thread(AcceptLoop);
                    th.IsBackground = true;
                    th.Start();
                    return true;
                }
                catch (Exception ex) { err = ex.Message; }
            }
            return false;
        }

        public void Stop()
        {
            _stop = true;
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
        }

        void AcceptLoop()
        {
            while (!_stop)
            {
                TcpClient c = null;
                try { c = _listener.AcceptTcpClient(); }
                catch { break; }
                if (_stop) { try { c.Close(); } catch { } break; }
                Interlocked.Increment(ref Connections);
                var th = new Thread(delegate() { Handle(c); });
                th.IsBackground = true;
                th.Start();
            }
        }

        void Handle(TcpClient client)
        {
            TcpClient server = null;
            try
            {
                client.NoDelay = true;
                var cs = client.GetStream();
                var head = new List<byte>();
                var one = new byte[1];
                while (head.Count < 32768)
                {
                    int n = cs.Read(one, 0, 1);
                    if (n <= 0) return;
                    head.Add(one[0]);
                    int c = head.Count;
                    if (c >= 4 && head[c - 4] == 13 && head[c - 3] == 10 && head[c - 2] == 13 && head[c - 1] == 10) break;
                }
                string headerText = Encoding.ASCII.GetString(head.ToArray());
                var firstLine = headerText.Split('\n')[0].Trim();
                var parts = firstLine.Split(' ');
                if (parts.Length < 2) return;
                string method = parts[0], target = parts[1];
                bool isConnect = method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase);
                string host; int port;
                if (isConnect)
                {
                    ParseHostPort(target, out host, out port);
                    if (port == 0) port = 443;
                }
                else if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    var rest = target.Substring(7);
                    int slash = rest.IndexOf('/');
                    string hostport = slash < 0 ? rest : rest.Substring(0, slash);
                    ParseHostPort(hostport, out host, out port);
                    if (port == 0) port = 80;
                    string path = slash < 0 ? "/" : rest.Substring(slash);
                    headerText = headerText.Replace(firstLine, method + " " + path + " " + (parts.Length > 2 ? parts[2] : "HTTP/1.1"));
                }
                else
                {
                    ParseHostPort(target, out host, out port);
                    if (port == 0) port = 80;
                }
                if (string.IsNullOrEmpty(host)) return;

                server = new TcpClient();
                server.NoDelay = true;
                server.Connect(host, port);
                var ss = server.GetStream();

                if (isConnect)
                {
                    var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                    cs.Write(ok, 0, ok.Length);
                    cs.Flush();
                }
                else
                {
                    var b = Encoding.ASCII.GetBytes(headerText);
                    ss.Write(b, 0, b.Length);
                    ss.Flush();
                }

                var t1 = new Thread(delegate() { Pump(cs, ss, true); });
                var t2 = new Thread(delegate() { Pump(ss, cs, false); });
                t1.IsBackground = true; t2.IsBackground = true;
                t1.Start(); t2.Start();
                t1.Join(); t2.Join();
            }
            catch { }
            finally
            {
                try { client.Close(); } catch { }
                try { if (server != null) server.Close(); } catch { }
            }
        }

        static void ParseHostPort(string s, out string host, out int port)
        {
            host = ""; port = 0;
            if (string.IsNullOrEmpty(s)) return;
            int i = s.LastIndexOf(':');
            if (i > 0) { host = s.Substring(0, i); int.TryParse(s.Substring(i + 1), out port); }
            else host = s;
        }

        void Pump(NetworkStream from, NetworkStream to, bool uplink)
        {
            var buf = new byte[16384];
            try
            {
                while (!_stop)
                {
                    int n = from.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    if (LossPercent > 0 && _rnd.Next(100) < LossPercent) continue;
                    bool applyDelay = (uplink && Up) || (!uplink && Down);
                    int d = DelayMs;
                    if (JitterMs > 0) d += _rnd.Next(-JitterMs, JitterMs + 1);
                    if (d < 0) d = 0;
                    if (applyDelay && d > 0) Thread.Sleep(d);
                    if (LimitKBps > 0)
                    {
                        double need = n * 1000.0 / (LimitKBps * 1024.0);
                        if (need >= 1) Thread.Sleep((int)Math.Min(need, 2000));
                    }
                    to.Write(buf, 0, n);
                    to.Flush();
                    if (uplink) Interlocked.Add(ref BytesUp, n); else Interlocked.Add(ref BytesDown, n);
                }
            }
            catch { }
            finally { try { to.Close(); } catch { } }
        }

        // 手机要能连到本机代理，得放行入站端口（程序已提权，静默加规则）
        public static void EnsureFirewallRule(int port)
        {
            try
            {
                var netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
                var ruleName = "MuMuNetSim Delay Proxy " + port;
                var chk = Proc.Run(netsh, "advfirewall firewall show rule name=\"" + ruleName + "\"", 8000);
                if (chk.Contains(ruleName)) return;
                Proc.Run(netsh, "advfirewall firewall add rule name=\"" + ruleName + "\" dir=in action=allow protocol=TCP localport=" + port, 12000);
                App.Log("已添加入站放行规则：" + ruleName);
            }
            catch { }
        }

        // 选一个能到达对端的本机 IPv4（同网段优先，否则第一个非回环地址）
        public static string HostIpForPeer(string peerIp)
        {
            try
            {
                var peer = IPAddress.Parse(peerIp);
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (ua.IPv4Mask == null) continue;
                        if (IPAddress.IsLoopback(ua.Address)) continue;
                        if (SameSubnet(ua.Address, peer, ua.IPv4Mask)) return ua.Address.ToString();
                    }
                }
            }
            catch { }
            // 其次：优先选「有默认网关」的网卡（手机最可能和它在同一张网里）
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var props = ni.GetIPProperties();
                    bool hasGw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    if (!hasGw) continue;
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(ua.Address)) continue;
                        return ua.Address.ToString();
                    }
                }
            }
            catch { }
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork) return ua.Address.ToString();
                }
            }
            catch { }
            return "127.0.0.1";
        }

        static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
        {
            var ab = a.GetAddressBytes(); var bb = b.GetAddressBytes(); var mb = mask.GetAddressBytes();
            if (ab.Length != 4 || bb.Length != 4 || mb.Length != 4) return false;
            for (int i = 0; i < 4; i++) if ((ab[i] & mb[i]) != (bb[i] & mb[i])) return false;
            return true;
        }
    }

    public static class Presets
    {
        // 丢包档位：15~100（步长 10，末档 100% 即全丢）
        public static readonly int[] LossTiers = { 15, 25, 35, 45, 55, 65, 75, 80, 90, 100 };
        public static readonly string[] Names;
        public static readonly string[] Keys;

        static Presets()
        {
            var names = new List<string>();
            var keys = new List<string>();
            names.Add("正常"); keys.Add("normal");
            foreach (var t in LossTiers) { names.Add("丢包 " + t + "%"); keys.Add("loss" + t); }
            names.Add("断网"); keys.Add("offline");
            Names = names.ToArray();
            Keys = keys.ToArray();
        }

        public static int IndexOfKey(string key)
        {
            for (int i = 0; i < Keys.Length; i++) if (string.Equals(Keys[i], key, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        public static string KeyOfName(string name)
        {
            for (int i = 0; i < Names.Length; i++) if (Names[i] == name) return Keys[i];
            return "normal";
        }
    }

    public static class Cli
    {
        // /apply <preset|normal|offline|lossNN> [/instance N]
        // /delay /instance N /ms 300 [/dir both|up|down] [/seconds 15]
        public static int Run(string[] args)
        {
            try
            {
                var sb = new StringBuilder();
                if (args.Length > 0 && args[0].Equals("/where", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("管理员=" + App.IsAdmin());
                    sb.AppendLine("MuMuManager=" + (App.MuMuManagerPath ?? "未找到"));
                    sb.AppendLine("clumsy=" + (App.ClumsyPath ?? "未找到"));
                    sb.AppendLine("WinDivert=" + (WinDivertNative.DllDir ?? App.FindWinDivertDir() ?? "未找到"));
                    sb.AppendLine("日志=" + App.LogPath);
                    Console.WriteLine(sb.ToString());
                    File.AppendAllText(App.LogPath, sb.ToString(), Encoding.UTF8);
                    return 0;
                }
                if (args.Length > 0 && args[0].Equals("/phone", StringComparison.OrdinalIgnoreCase))
                {
                    string serial = "", preset = "normal", pDir = "both";
                    int lossPct = 0, pMs = 0, pSeconds = 0, pJitter = 0, pLimit = 0;
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (args[i].Equals("/ip", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) serial = args[i + 1];
                        if (args[i].Equals("/preset", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) preset = args[i + 1].ToLowerInvariant();
                        if (args[i].Equals("/loss", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) int.TryParse(args[i + 1], out lossPct);
                        if (args[i].Equals("/jitter", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) int.TryParse(args[i + 1], out pJitter);
                        if (args[i].Equals("/limit", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) int.TryParse(args[i + 1], out pLimit);
                        if (args[i].Equals("/ms", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) int.TryParse(args[i + 1], out pMs);
                        if (args[i].Equals("/seconds", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) int.TryParse(args[i + 1], out pSeconds);
                        if (args[i].Equals("/dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) pDir = args[i + 1].ToLowerInvariant();
                    }
                    if (serial.Length == 0)
                    {
                        sb.AppendLine("用法：/phone /ip 192.168.1.50:5555 /preset normal|loss|delay|delayloss|offline [/ms 300] [/jitter 50] [/limit 200] [/loss 30] [/dir both|up|down] [/seconds 20]");
                        Console.WriteLine(sb);
                        return 2;
                    }
                    if (!serial.Contains(":")) serial += ":5555";
                    if (preset.StartsWith("loss") && preset.Length > 4)
                    {
                        int p2;
                        if (int.TryParse(preset.Substring(4), out p2)) { lossPct = p2; preset = "loss"; }
                    }
                    Phone.Connect(serial);
                    var info = Phone.Probe(serial);
                    sb.AppendLine("设备 " + info.Serial + "  连接=" + info.Connected + "  型号=" + info.Model + "  Android=" + info.Android + "  root=" + info.RootState);
                    if (!info.Connected) { Console.WriteLine(sb); File.AppendAllText(App.LogPath, sb.ToString(), Encoding.UTF8); return 3; }
                    bool needRoot = preset == "offline" || ((preset == "loss" || preset == "delayloss") && lossPct > 0);
                    if (needRoot && !info.Root)
                    {
                        sb.AppendLine("没有 root：丢包/断网无法下发（只测时延可用 /preset delay）");
                        Console.WriteLine(sb);
                        File.AppendAllText(App.LogPath, sb.ToString(), Encoding.UTF8);
                        return 4;
                    }
                    DelayProxy px = null;
                    try
                    {
                        if (needRoot) sb.AppendLine("设备内：" + Phone.ApplyDevicePreset(info, preset == "offline" ? "offline" : "loss", lossPct));
                        if (preset == "delay" || preset == "delayloss")
                        {
                            string hostIp = DelayProxy.HostIpForPeer(serial.Split(':')[0]);
                            px = new DelayProxy();
                            px.DelayMs = pMs; px.JitterMs = pJitter; px.LimitKBps = pLimit;
                            px.Up = pDir != "down";
                            px.Down = pDir != "up";
                            px.LossPercent = (preset == "delayloss" && !info.Root) ? lossPct : 0;
                            string err;
                            if (!px.Start(out err)) { sb.AppendLine("代理启动失败：" + err); Console.WriteLine(sb); return 5; }
                            DelayProxy.EnsureFirewallRule(px.Port);
                            string spec = hostIp + ":" + px.Port;
                            Phone.SetGlobalProxy(serial, spec);
                            sb.AppendLine("本机代理 " + spec + "：时延 " + pMs + "ms" + (pJitter > 0 ? "±" + pJitter + "ms" : "") + " " + (px.Up && px.Down ? "上下行" : (px.Up ? "仅上行" : "仅下行")) + (pLimit > 0 ? "，限速 " + pLimit + "KB/s" : "") + (px.LossPercent > 0 ? "，丢包 " + px.LossPercent + "%" : ""));
                            sb.AppendLine("设备全局代理现在 = " + Phone.GetGlobalProxy(serial));
                        }
                        Console.WriteLine(sb);
                        File.AppendAllText(App.LogPath, sb.ToString(), Encoding.UTF8);
                        if (pSeconds > 0)
                        {
                            for (int i = 0; i < pSeconds; i++)
                            {
                                Thread.Sleep(1000);
                                if (px != null && i % 3 == 2)
                                    Console.WriteLine("  代理：连接 " + px.Connections + " 上行 " + (px.BytesUp / 1024) + "KB 下行 " + (px.BytesDown / 1024) + "KB");
                            }
                        }
                    }
                    finally
                    {
                        if (px != null)
                        {
                            px.Stop();
                            Phone.ClearGlobalProxy(serial);
                            Console.WriteLine("已清理：本机代理关闭 + 设备全局代理复位");
                        }
                    }
                    return 0;
                }
                if (args.Length > 0 && args[0].Equals("/selfcheck", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var line in Engine.SelfCheck()) sb.AppendLine("  " + line);
                    Console.WriteLine(sb.ToString());
                    File.AppendAllText(App.LogPath, "---- /selfcheck ----" + Environment.NewLine + sb.ToString(), Encoding.UTF8);
                    return 0;
                }
                if (args.Length > 0 && args[0].Equals("/ports", StringComparison.OrdinalIgnoreCase))
                {
                    // 只看端口过滤器（不需要管理员）：验证实例 -> VMM 进程 -> 端口 的映射
                    foreach (var i in Engine.ListInstances())
                    {
                        if (!i.Running) continue;
                        int n;
                        var f = Engine.BuildPortFilter(i.Index, out n);
                        sb.AppendLine("实例 " + i.Index + "  端口数=" + n);
                        sb.AppendLine("  " + (f ?? "(没找到 VMM 端口)"));
                    }
                    Console.WriteLine(sb.ToString());
                    File.AppendAllText(App.LogPath, sb.ToString(), Encoding.UTF8);
                    return 0;
                }
                if (args.Length > 0 && args[0].Equals("/status", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var i in Engine.ListInstances()) sb.AppendLine(i.ToString());
                    foreach (var v in Engine.ListVmms()) sb.AppendLine("VMM: 实例 " + v.Instance + " pid=" + v.Pid);
                    Console.WriteLine(sb.ToString());
                    File.AppendAllText(App.LogPath, sb.ToString(), Encoding.UTF8);
                    return 0;
                }

                string mode = "normal", dir = "both", customFilter = "";
                int idx = -1, ms = 0, seconds = 0, jitter = 0, limit = 0;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i].Equals("/apply", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) mode = args[i + 1].ToLowerInvariant();
                    if (args[i].Equals("/delay", StringComparison.OrdinalIgnoreCase)) mode = "delay";
                    if (args[i].Equals("/instance", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) int.TryParse(args[i + 1], out idx);
                    if (args[i].Equals("/ms", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) int.TryParse(args[i + 1], out ms);
                    if (args[i].Equals("/seconds", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) int.TryParse(args[i + 1], out seconds);
                    if (args[i].Equals("/dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) dir = args[i + 1].ToLowerInvariant();
                    if (args[i].Equals("/filter", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) customFilter = args[i + 1];
                    if (args[i].Equals("/jitter", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) int.TryParse(args[i + 1], out jitter);
                    if (args[i].Equals("/limit", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) int.TryParse(args[i + 1], out limit);
                }

                Action<string> log = delegate(string s) { Console.WriteLine(s); App.Log(s); };
                var insts = Engine.ListInstances().Where(x => x.Running).ToList();
                InstanceInfo inst = null;
                if (insts.Count > 0) inst = idx >= 0 ? insts.FirstOrDefault(x => x.Index == idx) : insts[0];

                if (mode == "delay")
                {
                    string filter = customFilter;
                    int portCount = 0;
                    if (string.IsNullOrEmpty(filter))
                    {
                        if (inst == null) { log("没有运行中的实例，无法按实例构造过滤器（可用 /filter 指定）"); return 2; }
                        filter = Engine.BuildPortFilter(inst.Index, out portCount);
                        if (filter == null) { log("没有找到实例 " + inst.Index + " 的 VMM 端口"); return 3; }
                    }
                    var eng = new DelayEngine();
                    eng.Instance = inst == null ? -1 : inst.Index;
                    eng.Ms = ms; eng.JitterMs = jitter; eng.LimitKBps = limit; eng.Up = dir != "down"; eng.Down = dir != "up";
                    string err;
                    if (!eng.Start(filter, out err)) { log("延迟引擎启动失败：" + err); return 4; }
                    log("延迟引擎已启动：" + (inst == null ? "(自定义过滤器)" : "实例 " + inst.Index) + " " + ms + "ms" + (jitter > 0 ? "±" + jitter + "ms" : "") + (limit > 0 ? " 限速" + limit + "KB/s" : "") + " " + eng.DirText + " 端口数=" + portCount);
                    log("过滤器：" + filter);
                    int total = seconds > 0 ? seconds : 15;
                    int lastCount = 0;
                    for (int i = 0; i < total; i++)
                    {
                        Thread.Sleep(1000);
                        // 与 GUI 相同：出现新端口就刷新过滤器，保证新连接也能被延迟
                        if (inst != null && i > 0 && i % 3 == 0)
                        {
                            int n2;
                            var f2 = Engine.BuildPortFilter(inst.Index, out n2);
                            if (f2 != null && f2 != eng.Filter)
                            {
                                long keepRecv = eng.Received, keepSent = eng.Sent;
                                eng.Stop();
                                var eng2 = new DelayEngine();
                                eng2.Instance = inst.Index; eng2.Ms = ms; eng2.Up = dir != "down"; eng2.Down = dir != "up";
                                eng2.Received = keepRecv; eng2.Sent = keepSent;
                                string e2;
                                if (eng2.Start(f2, out e2))
                                {
                                    eng = eng2;
                                    log("  过滤器已刷新，端口数=" + n2 + "（累计收包 " + eng.Received + "）");
                                }
                                else log("  刷新失败：" + e2);
                            }
                        }
                        if (i % 2 == 1 && (int)eng.Received != lastCount)
                        {
                            lastCount = (int)eng.Received;
                            log("  收包=" + eng.Received + " 重发=" + eng.Sent + " 实际保持=" + (eng.HoldCount > 0 ? (eng.HoldSumMs / eng.HoldCount) + "ms" : "-") + "（最后 " + eng.LastHoldMs + "ms，目标 " + ms + "ms）");
                        }
                    }
                    eng.Stop();
                    log("结束：收包=" + eng.Received + " 重发=" + eng.Sent);
                    return eng.Received > 0 ? 0 : 5;
                }

                if (inst == null) { Console.WriteLine("没有正在运行的 MuMu 实例"); return 2; }
                log("目标：" + inst + "  preset=" + mode);
                log("结果：" + Engine.ApplyPreset(inst.Index, mode, inst.AdbPort));
                Thread.Sleep(400);
                log("实例内网络： " + Engine.Ping(inst.Index, 3));
                return 0;
            }
            catch (Exception ex)
            {
                App.Log("CLI 异常：" + ex);
                return 1;
            }
        }
    }

    public class MainForm : Form
    {
        DataGridView grid;
        TextBox txtLog;
        Label lblStatus;
        Button btnRefresh, btnAllNormal;
        CheckBox chkClumsy;
        TextBox txtPhoneIp;
        Label lblPhoneStatus;
        ComboBox cmbPhonePreset;
        Button btnPhoneConnect, btnPhoneDisconnect, btnPhoneApply, btnPhoneNormal;
        TextBox txtPhoneLoss, txtPhoneMs, txtPhoneJitter, txtPhoneLimit;
        CheckBox chkPhoneUp, chkPhoneDown;
        DelayProxy _proxy;
        PhoneInfo _phone;
        DateTime _phoneProbeAt = DateTime.MinValue;

        static readonly string[] PhonePresets = { "正常", "丢包", "时延", "时延+丢包", "断网" };

        static string PhonePresetKey(string name)
        {
            switch (name)
            {
                case "丢包": return "loss";
                case "时延": return "delay";
                case "时延+丢包": return "delayloss";
                case "断网": return "offline";
                default: return "normal";
            }
        }
        System.Windows.Forms.Timer timer;

        readonly ConcurrentDictionary<int, string> _applied = new ConcurrentDictionary<int, string>();
        readonly ConcurrentDictionary<int, string> _actual = new ConcurrentDictionary<int, string>();
        readonly Dictionary<int, DelayEngine> _engines = new Dictionary<int, DelayEngine>();
        readonly Dictionary<int, string> _engineFilter = new Dictionary<int, string>();
        readonly Dictionary<int, DateTime> _engineRestartAt = new Dictionary<int, DateTime>();
        readonly object _engineLock = new object();
        volatile bool _pollBusy;
        volatile bool _closing;
        bool _suppress;
        string _lastShape = "";

        const int C_INDEX = 0, C_NAME = 1, C_AND = 2, C_STATE = 3, C_PRESET = 4, C_DELAY = 5, C_JITTER = 6, C_LIMIT = 7, C_UP = 8, C_DOWN = 9, C_ACTUAL = 10, C_OP = 11;

        public MainForm()
        {
            Text = App.TITLE;
            ClientSize = new Size(880, 620);
            MinimumSize = new Size(880, 520);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            try { var cp = App.FindClumsy(); if (cp != null) Icon = Icon.ExtractAssociatedIcon(cp); } catch { }

            var tabs = new TabControl();
            tabs.SetBounds(6, 6, 868, 446);
            tabs.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            var tab1 = new TabPage("MuMu 实例");
            var tab2 = new TabPage("手机 / 局域网设备");
            tabs.TabPages.Add(tab1);
            tabs.TabPages.Add(tab2);
            Controls.Add(tabs);

            var lblTop = new Label();
            lblTop.Text = "每实例单独预设：断网 / 丢包档位 / 延迟(ms，可分别只做上行或下行)；实例启动后自动套用";
            lblTop.SetBounds(14, 12, 700, 22);
            lblTop.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            Controls.Add(lblTop);

            btnRefresh = new Button();
            btnRefresh.Text = "刷新实例";
            btnRefresh.SetBounds(14, 40, 92, 26);
            btnRefresh.Click += delegate { SelfCheckAndRefresh(true); };
            Controls.Add(btnRefresh);

            btnAllNormal = new Button();
            btnAllNormal.Text = "全部恢复正常";
            btnAllNormal.SetBounds(112, 40, 110, 26);
            btnAllNormal.Click += delegate { SetAllNormal(); };
            Controls.Add(btnAllNormal);

            chkClumsy = new CheckBox();
            chkClumsy.Text = "全局弱网：延迟 200ms + 丢包 5%（clumsy，整机出站；与每实例延迟互斥）";
            chkClumsy.SetBounds(236, 43, 520, 22);
            chkClumsy.CheckedChanged += delegate { if (!_suppress) ToggleClumsy(chkClumsy.Checked); };
            Controls.Add(chkClumsy);

            lblStatus = new Label();
            lblStatus.SetBounds(760, 43, 110, 22);
            lblStatus.Text = "就绪";
            lblStatus.ForeColor = Color.Green;
            Controls.Add(lblStatus);

            grid = new DataGridView();
            grid.SetBounds(14, 76, 852, 350);
            grid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.BackgroundColor = Color.White;
            grid.EditMode = DataGridViewEditMode.EditOnEnter;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            grid.RowTemplate.Height = 26;

            var cIdx = Col("实例", 46); var cName = Col("名称", 130); var cAnd = Col("Android", 62);
            var cState = Col("状态", 66);
            var cPreset = new DataGridViewComboBoxColumn();
            cPreset.HeaderText = "预设环境"; cPreset.Width = 108; cPreset.FlatStyle = FlatStyle.Flat;
            cPreset.DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox;
            cPreset.Items.AddRange(Presets.Names);
            var cDelay = new DataGridViewTextBoxColumn();
            cDelay.HeaderText = "延迟ms"; cDelay.Width = 56;
            var cJitter = new DataGridViewTextBoxColumn();
            cJitter.HeaderText = "抖动ms"; cJitter.Width = 56;
            var cLimit = new DataGridViewTextBoxColumn();
            cLimit.HeaderText = "限速KB/s"; cLimit.Width = 64;
            var cUp = new DataGridViewCheckBoxColumn(); cUp.HeaderText = "上行"; cUp.Width = 44;
            var cDown = new DataGridViewCheckBoxColumn(); cDown.HeaderText = "下行"; cDown.Width = 44;
            var cActual = Col("实际状态", 190);
            var cOp = new DataGridViewButtonColumn();
            cOp.HeaderText = "操作"; cOp.Width = 74; cOp.ReadOnly = true; cOp.UseColumnTextForButtonValue = false;

            grid.Columns.AddRange(new DataGridViewColumn[] { cIdx, cName, cAnd, cState, cPreset, cDelay, cJitter, cLimit, cUp, cDown, cActual, cOp });
            grid.CellValueChanged += Grid_CellValueChanged;
            grid.CurrentCellDirtyStateChanged += delegate(object s, EventArgs e)
            {
                if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.CellContentClick += Grid_CellContentClick;
            grid.DataError += delegate(object s, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; };
            Controls.Add(grid);

            var lblLog = new Label();
            lblLog.Text = "日志：";
            lblLog.SetBounds(14, 434, 60, 20);
            lblLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(lblLog);

            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.SetBounds(14, 454, 852, 150);
            txtLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            txtLog.Font = new Font("Consolas", 9f);
            txtLog.BackColor = Color.FromArgb(250, 250, 250);
            Controls.Add(txtLog);

            // ---- 把上面的实例相关控件挪进「MuMu 实例」页，并把日志留在外面共用 ----
            foreach (var c in new Control[] { lblTop, btnRefresh, btnAllNormal, chkClumsy, lblStatus, grid })
            {
                Controls.Remove(c);
                tab1.Controls.Add(c);
            }
            lblTop.SetBounds(8, 6, 830, 20);
            btnRefresh.SetBounds(8, 30, 92, 26);
            btnAllNormal.SetBounds(106, 30, 110, 26);
            chkClumsy.SetBounds(226, 33, 520, 22);
            lblStatus.SetBounds(742, 33, 110, 22);
            grid.SetBounds(8, 62, 844, 344);
            lblLog.SetBounds(14, 458, 60, 20);
            txtLog.SetBounds(14, 478, 852, 124);

            // ---- 手机 / 局域网设备页 ----
            var lblP1 = new Label();
            lblP1.Text = "设备 IP:端口（需开无线调试）：";
            lblP1.SetBounds(12, 16, 200, 22);
            tab2.Controls.Add(lblP1);

            txtPhoneIp = new TextBox();
            txtPhoneIp.Text = GetPhoneIp();
            txtPhoneIp.SetBounds(214, 12, 190, 24);
            tab2.Controls.Add(txtPhoneIp);

            btnPhoneConnect = new Button();
            btnPhoneConnect.Text = "连接";
            btnPhoneConnect.SetBounds(414, 11, 78, 26);
            btnPhoneConnect.Click += delegate { PhoneConnect(); };
            tab2.Controls.Add(btnPhoneConnect);

            btnPhoneDisconnect = new Button();
            btnPhoneDisconnect.Text = "断开";
            btnPhoneDisconnect.SetBounds(498, 11, 78, 26);
            btnPhoneDisconnect.Click += delegate { PhoneDisconnect(); };
            tab2.Controls.Add(btnPhoneDisconnect);

            lblPhoneStatus = new Label();
            lblPhoneStatus.SetBounds(12, 46, 830, 56);
            lblPhoneStatus.Text = "未连接。手机先：设置 → 开发者选项 → 无线调试（或 USB 调试 + adb tcpip 5555），确保与本机同一局域网。";
            lblPhoneStatus.ForeColor = Color.DimGray;
            tab2.Controls.Add(lblPhoneStatus);

            var lblP2 = new Label();
            lblP2.Text = "预设环境：";
            lblP2.SetBounds(12, 108, 80, 22);
            lblP2.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            tab2.Controls.Add(lblP2);

            cmbPhonePreset = new ComboBox();
            cmbPhonePreset.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbPhonePreset.SetBounds(98, 104, 150, 26);
            cmbPhonePreset.Items.AddRange(PhonePresets);
            cmbPhonePreset.SelectedIndex = 0;
            cmbPhonePreset.SelectedIndexChanged += delegate { PhonePresetChanged(); };
            tab2.Controls.Add(cmbPhonePreset);

            var lblLoss = new Label();
            lblLoss.Text = "丢包 %：";
            lblLoss.SetBounds(258, 108, 56, 22);
            tab2.Controls.Add(lblLoss);

            txtPhoneLoss = new TextBox();
            txtPhoneLoss.Text = "30";
            txtPhoneLoss.SetBounds(314, 104, 54, 24);
            tab2.Controls.Add(txtPhoneLoss);

            var lblMs = new Label();
            lblMs.Text = "时延 ms：";
            lblMs.SetBounds(376, 108, 60, 22);
            tab2.Controls.Add(lblMs);

            txtPhoneMs = new TextBox();
            txtPhoneMs.Text = "300";
            txtPhoneMs.SetBounds(438, 104, 60, 24);
            tab2.Controls.Add(txtPhoneMs);

            chkPhoneUp = new CheckBox();
            chkPhoneUp.Text = "上行";
            chkPhoneUp.Checked = true;
            chkPhoneUp.SetBounds(508, 106, 52, 22);
            tab2.Controls.Add(chkPhoneUp);

            chkPhoneDown = new CheckBox();
            chkPhoneDown.Text = "下行";
            chkPhoneDown.Checked = true;
            chkPhoneDown.SetBounds(562, 106, 52, 22);
            tab2.Controls.Add(chkPhoneDown);

            var lblPJ = new Label();
            lblPJ.Text = "抖动ms：";
            lblPJ.SetBounds(12, 142, 58, 22);
            tab2.Controls.Add(lblPJ);

            txtPhoneJitter = new TextBox();
            txtPhoneJitter.Text = "0";
            txtPhoneJitter.SetBounds(72, 138, 54, 24);
            tab2.Controls.Add(txtPhoneJitter);

            var lblPL = new Label();
            lblPL.Text = "限速KB/s：";
            lblPL.SetBounds(134, 142, 68, 22);
            tab2.Controls.Add(lblPL);

            txtPhoneLimit = new TextBox();
            txtPhoneLimit.Text = "0";
            txtPhoneLimit.SetBounds(204, 138, 60, 24);
            tab2.Controls.Add(txtPhoneLimit);

            btnPhoneApply = new Button();
            btnPhoneApply.Text = "应用到手机";
            btnPhoneApply.SetBounds(282, 137, 100, 28);
            btnPhoneApply.Click += delegate { PhoneApply(); };
            tab2.Controls.Add(btnPhoneApply);

            btnPhoneNormal = new Button();
            btnPhoneNormal.Text = "恢复正常";
            btnPhoneNormal.SetBounds(388, 137, 90, 28);
            btnPhoneNormal.Click += delegate { PhoneNormal(); };
            tab2.Controls.Add(btnPhoneNormal);

            var lblPHint = new Label();
            lblPHint.SetBounds(12, 176, 830, 228);
            lblPHint.ForeColor = Color.DimGray;
            lblPHint.Text =
                "手机侧两种干预方式（可叠加）：\r\n" +
                "  · 丢包 / 断网 → 在手机内部下发 iptables（独立链 MT_OUT），精确、可逆，需要 root；\r\n" +
                "  · 时延（可加丢包）→ 本工具在本机起一个「延迟代理」，再用 adb 把手机的全局 HTTP 代理指过来，\r\n" +
                "    免 root。此时手机的上网流量走本机代理：上行=手机→服务器，下行=服务器→手机，各方向可单独设。\r\n" +
                "    注意：只有「遵守系统代理」的流量会受影响（浏览器、WebView、多数 App 的网络库）；\r\n" +
                "    游戏用原始 UDP、或自实现网络栈、或证书校验写死 IP 的 App 可能不受影响。\r\n" +
                "    这类要测全流量时延，请把手机连到本机开的移动热点，再用「MuMu 实例」页的全局引擎。\r\n" +
                "\r\n" +
                "连接步骤（手机与电脑同一 Wi-Fi）：\r\n" +
                "  Android 11+：开发者选项 → 无线调试 → 用配对码配对（首次）→ 页面能看到 IP:端口\r\n" +
                "  其它版本：USB 连线后执行  adb tcpip 5555  再拔线，用 手机IP:5555 连接\r\n" +
                "  手机弹出「允许 USB 调试」时点允许。\r\n" +
                "\r\n" +
                "恢复：点「恢复正常」会同时清掉手机上的 iptables 规则与全局代理设置；关窗口也会自动清。";
            tab2.Controls.Add(lblPHint);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 2000;
            timer.Tick += delegate { PollOnce(false); };

            Load += delegate
            {
                if (!App.IsAdmin())
                {
                    Text = App.TITLE + "  —— 未提权：延迟功能不可用";
                    Log("⚠ 当前不是管理员：每实例延迟需要 WinDivert 驱动，会报「拒绝访问」。请关掉后右键『以管理员身份运行』，或双击时点 UAC 的『是』。");
                    Log("  （丢包/断网 仍然可用，它们只动模拟器内部的 iptables）");
                }
                Log("启动。管理员=" + App.IsAdmin() + "  WinDivert=" + (App.FindWinDivertDir() ?? "未找到"));
                Log("预设/延迟配置：" + App.IniPath);
                PhonePresetChanged();
                var phoneCtl = new List<string>();
                foreach (Control c in tab2.Controls)
                {
                    var tx = (c.Text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                    if (tx.Length == 0) continue;
                    phoneCtl.Add(tx.Length > 14 ? tx.Substring(0, 14) : tx);
                }
                Log("手机页控件(" + tab2.Controls.Count + ")：" + string.Join(" | ", phoneCtl.ToArray()) + "　下拉项：" + string.Join(",", PhonePresets));
                if (!Environment.Is64BitOperatingSystem)
                    Log("⚠ 本机是 32 位 Windows：延迟功能（WinDivert x64）不可用；丢包/断网仍可用。");
                SelfCheckAndRefresh(false);
                timer.Start();
            };
        }

        static DataGridViewTextBoxColumn Col(string header, int width)
        {
            var c = new DataGridViewTextBoxColumn();
            c.HeaderText = header; c.Width = width; c.ReadOnly = true;
            return c;
        }

        // ------------------------------------------------------------ 环境自检 / 刷新

        // 「刷新实例」= 重新扫描本机环境（MuMu 路径、adb、WinDivert、权限、实例、端口、手机）并同步界面
        void SelfCheckAndRefresh(bool interactive)
        {
            var th = new Thread(delegate()
            {
                UiLog("---------------- 环境自检 / 刷新 ----------------");
                try { foreach (var l in Engine.SelfCheck()) UiLog("  " + l); }
                catch (Exception ex) { UiLog("  自检失败：" + ex.Message); }
                if (interactive && string.IsNullOrEmpty(App.MuMuManagerPath))
                    Ui(delegate() { OfferLocateMuMu(); });
                PollOnce(true);
            });
            th.IsBackground = true;
            th.Start();
        }

        void OfferLocateMuMu()
        {
            try
            {
                if (MessageBox.Show("没有找到 MuMuManager.exe（MuMu 模拟器的管理程序）。\r\n要现在手动定位它吗？",
                        App.TITLE, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Title = "选择 MuMuManager.exe";
                    dlg.Filter = "MuMuManager.exe|MuMuManager.exe|所有程序|*.exe";
                    dlg.InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Netease");
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        App.MuMuManagerPath = dlg.FileName;
                        App.MuMuManagerSource = "手动指定";
                        App.SaveIni();
                        UiLog("已指定 MuMuManager：" + dlg.FileName);
                    }
                }
            }
            catch (Exception ex) { UiLog("定位 MuMu 失败：" + ex.Message); }
        }

        // ------------------------------------------------------------ 轮询

        void PollOnce(bool manual)
        {
            if (_closing) return;
            if (_pollBusy) { if (manual) Log("上一次刷新还没结束…"); return; }
            _pollBusy = true;
            var th = new Thread(delegate()
            {
                List<InstanceInfo> insts = null;
                try { insts = Engine.ListInstances(); } catch (Exception ex) { UiLog("读取实例失败：" + ex.Message); }
                if (insts != null)
                {
                    var l = insts;
                    Ui(delegate() { UpdateGrid(l); });
                    SyncAll(l);
                    RefreshPhone();
                }
                _pollBusy = false;
            });
            th.IsBackground = true;
            th.Start();
        }

        void UpdateGrid(List<InstanceInfo> insts)
        {
            if (_closing) return;
            string shape = string.Join(",", insts.Select(x => x.Index).ToArray()) + "|" + insts.Count;
            if (shape != _lastShape) { RebuildRows(insts); _lastShape = shape; }
            for (int i = 0; i < insts.Count && i < grid.Rows.Count; i++)
            {
                var inst = insts[i];
                var row = grid.Rows[i];
                row.Tag = inst.Index;
                row.Cells[C_NAME].Value = inst.Name;
                row.Cells[C_AND].Value = inst.AndroidVersion;
                row.Cells[C_STATE].Value = inst.Running ? (inst.AndroidStarted ? "运行中" : "启动中") : "已停止";
                row.Cells[C_STATE].Style.ForeColor = _applied.ContainsKey(inst.Index) ? Color.DarkOrange : (inst.Running ? Color.Green : Color.Gray);
                string actual;
                if (!inst.Running) actual = "待启动（启动后自动套用）";
                else if (!_actual.TryGetValue(inst.Index, out actual)) actual = "未套用";
                row.Cells[C_ACTUAL].Value = actual;
                row.Cells[C_OP].Value = inst.Running ? "关闭实例" : "启动实例";
            }
        }

        void RebuildRows(List<InstanceInfo> insts)
        {
            _suppress = true;
            try
            {
                grid.Rows.Clear();
                foreach (var inst in insts)
                {
                    int ri = grid.Rows.Add();
                    var row = grid.Rows[ri];
                    row.Tag = inst.Index;
                    row.Cells[C_INDEX].Value = inst.Index;
                    row.Cells[C_NAME].Value = inst.Name;
                    row.Cells[C_AND].Value = inst.AndroidVersion;
                    row.Cells[C_STATE].Value = inst.Running ? "运行中" : "已停止";
                    row.Cells[C_PRESET].Value = Presets.Names[Presets.IndexOfKey(GetPreset(inst.Index))];
                    row.Cells[C_DELAY].Value = GetDelay(inst.Index).ToString();
                    row.Cells[C_JITTER].Value = GetJitter(inst.Index).ToString();
                    row.Cells[C_LIMIT].Value = GetLimit(inst.Index).ToString();
                    row.Cells[C_UP].Value = GetUp(inst.Index);
                    row.Cells[C_DOWN].Value = GetDown(inst.Index);
                    row.Cells[C_ACTUAL].Value = inst.Running ? "未套用" : "待启动（启动后自动套用）";
                    row.Cells[C_OP].Value = inst.Running ? "关闭实例" : "启动实例";
                }
            }
            finally { _suppress = false; }
        }

        string GetPreset(int idx) { string v; return App.Presets.TryGetValue(idx.ToString(), out v) ? v : "normal"; }
        int GetJitter(int idx) { int v; return App.Jitters.TryGetValue(idx.ToString(), out v) ? v : 0; }
        int GetLimit(int idx) { int v; return App.Limits.TryGetValue(idx.ToString(), out v) ? v : 0; }
        int GetDelay(int idx) { int v; return App.Delays.TryGetValue(idx.ToString(), out v) ? v : 0; }
        bool GetUp(int idx) { bool v; return App.Ups.TryGetValue(idx.ToString(), out v) ? v : true; }
        bool GetDown(int idx) { bool v; return App.Downs.TryGetValue(idx.ToString(), out v) ? v : true; }

        void Grid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_suppress || e.RowIndex < 0) return;
            var row = grid.Rows[e.RowIndex];
            if (row.Tag == null) return;
            int idx = (int)row.Tag;
            string key = idx.ToString();
            if (e.ColumnIndex == C_PRESET)
            {
                string name = Convert.ToString(row.Cells[C_PRESET].Value);
                App.Presets[key] = Presets.KeyOfName(name);
                Log("实例 " + idx + " 预设 -> " + name);
            }
            else if (e.ColumnIndex == C_DELAY)
            {
                int ms;
                int.TryParse(Convert.ToString(row.Cells[C_DELAY].Value), out ms);
                if (ms < 0) ms = 0;
                if (ms > 10000) ms = 10000;
                App.Delays[key] = ms;
                Log("实例 " + idx + " 延迟 -> " + ms + "ms");
            }
            else if (e.ColumnIndex == C_JITTER)
            {
                int j;
                int.TryParse(Convert.ToString(row.Cells[C_JITTER].Value), out j);
                if (j < 0) j = 0;
                if (j > 5000) j = 5000;
                App.Jitters[key] = j;
                Log("实例 " + idx + " 抖动 -> " + j + "ms");
            }
            else if (e.ColumnIndex == C_LIMIT)
            {
                int l;
                int.TryParse(Convert.ToString(row.Cells[C_LIMIT].Value), out l);
                if (l < 0) l = 0;
                if (l > 1000000) l = 1000000;
                App.Limits[key] = l;
                Log("实例 " + idx + " 限速 -> " + l + "KB/s");
            }
            else if (e.ColumnIndex == C_UP) { App.Ups[key] = Convert.ToBoolean(row.Cells[C_UP].Value ?? false); }
            else if (e.ColumnIndex == C_DOWN) { App.Downs[key] = Convert.ToBoolean(row.Cells[C_DOWN].Value ?? false); }
            else return;
            App.SaveIni();
            PollOnce(false);
        }

        void Grid_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != C_OP) return;
            var row = grid.Rows[e.RowIndex];
            if (row.Tag == null) return;
            int idx = (int)row.Tag;
            string state = Convert.ToString(row.Cells[C_STATE].Value);
            var th = new Thread(delegate()
            {
                if (state != null && state.StartsWith("运行"))
                {
                    UiLog("关闭实例 " + idx + " …");
                    var o = Proc.Run(App.MuMuManagerPath, "control shutdown -v " + idx, 60000);
                    UiLog("实例 " + idx + " 关闭指令已下发" + (o.StartsWith("<") ? "（" + o.Trim() + "）" : ""));
                }
                else
                {
                    UiLog("启动实例 " + idx + " …");
                    Engine.LaunchInstance(idx, UiLog);
                }
                Thread.Sleep(1500);
                PollOnce(false);
            });
            th.IsBackground = true;
            th.Start();
        }

        // ------------------------------------------------------------ 套用

        void SyncAll(List<InstanceInfo> insts)
        {
            foreach (var inst in insts)
            {
                if (_closing) return;
                int idx = inst.Index;
                string preset = GetPreset(idx);
                int ms = GetDelay(idx);
                int jitter = GetJitter(idx);
                int limit = GetLimit(idx);
                bool up = GetUp(idx), down = GetDown(idx);
                bool wantDelay = (ms > 0 && (up || down)) || limit > 0;
                string want = preset + "|" + (wantDelay ? ms + "|" + jitter + "|" + limit + "|" + (up ? "u" : "") + (down ? "d" : "") : "0");

                if (!inst.Running || !inst.AndroidStarted)
                {
                    _applied.TryRemove(idx, out _dummy);
                    continue;
                }

                string have;
                bool had = _applied.TryGetValue(idx, out have);
                if (preset == "normal" && !wantDelay)
                {
                    if (had)
                    {
                        try { Engine.ClearRules(idx); } catch { }
                        StopEngine(idx);
                        _applied.TryRemove(idx, out have);
                        SetActual(idx, "已恢复正常");
                        UiLog("实例 " + idx + " 已恢复正常");
                    }
                    continue;
                }
                if (had && have == want)
                {
                    RefreshEngineIfPortsChanged(idx, ms, jitter, limit, up, down);
                    ShowCounters(idx);
                    continue;
                }

                var desc = new List<string>();
                try
                {
                    if (preset != "normal") { Engine.ClearRules(idx); desc.Add(Engine.ApplyPreset(idx, preset, inst.AdbPort)); }
                    else { Engine.ClearRules(idx); }
                }
                catch (Exception ex) { desc.Add("预设失败：" + ex.Message); }

                if (wantDelay)
                {
                    string err;
                    if (StartEngine(idx, ms, jitter, limit, up, down, out err))
                    {
                        var d2 = new List<string>();
                        if (ms > 0) d2.Add("延迟 " + ms + "ms(" + DirText(up, down) + ")");
                        if (jitter > 0) d2.Add("抖动 ±" + jitter + "ms");
                        if (limit > 0) d2.Add("限速 " + limit + "KB/s");
                        desc.Add(string.Join("+", d2.ToArray()));
                    }
                    else desc.Add("延迟失败：" + err);
                }
                else StopEngine(idx);

                _applied[idx] = want;
                _desc[idx] = "已套用：" + string.Join(" + ", desc.ToArray());
                SetActual(idx, _desc[idx]);
                UiLog("实例 " + idx + "（" + inst.Name + "）已套用：" + string.Join(" + ", desc.ToArray()));
            }
        }
        string _dummy;
        readonly Dictionary<int, string> _desc = new Dictionary<int, string>();

        // 每轮刷新一下延迟引擎的计数，用户能看出“引擎确实在抓包”
        void ShowCounters(int idx)
        {
            DelayEngine e = null;
            lock (_engineLock) _engines.TryGetValue(idx, out e);
            if (e == null) return;
            string baseText;
            if (!_desc.TryGetValue(idx, out baseText)) baseText = "已套用";
            SetActual(idx, baseText + " · 收包 " + e.Received + "/重发 " + e.Sent);
        }

        static string DirText(bool up, bool down) { return up && down ? "上下行" : (up ? "仅上行" : "仅下行"); }

        bool StartEngine(int idx, int ms, int jitter, int limit, bool up, bool down, out string err)
        {
            err = "";
            int portCount;
            var filter = Engine.BuildPortFilter(idx, out portCount);
            if (filter == null) { err = "实例的 VMM 进程/端口未找到"; return false; }
            if (Process.GetProcessesByName("clumsy").Length > 0)
            {
                Engine.StopClumsy();
                Ui(delegate() { _suppress = true; chkClumsy.Checked = false; _suppress = false; });
                UiLog("已关闭全局 clumsy（与每实例延迟互斥）");
            }
            var eng = new DelayEngine();
            eng.Instance = idx; eng.Ms = ms; eng.JitterMs = jitter; eng.LimitKBps = limit; eng.Up = up; eng.Down = down;
            if (!eng.Start(filter, out err))
            {
                if (err.Contains("拒绝访问") || err.Contains("Access is denied") || err.Contains("5"))
                    err += "（延迟需要管理员权限：请以管理员身份运行本工具）";
                UiLog("实例 " + idx + " 延迟引擎启动失败：" + err);
                return false;
            }
            lock (_engineLock)
            {
                DelayEngine old;
                if (_engines.TryGetValue(idx, out old)) { eng.Received = old.Received; eng.Sent = old.Sent; try { old.Stop(); } catch { } }
                _engines[idx] = eng;
                _engineFilter[idx] = filter;
                _engineRestartAt[idx] = DateTime.Now;
            }
            return true;
        }

        void StopEngine(int idx)
        {
            lock (_engineLock)
            {
                DelayEngine e;
                if (_engines.TryGetValue(idx, out e)) { try { e.Stop(); } catch { } _engines.Remove(idx); }
                _engineFilter.Remove(idx);
                _engineRestartAt.Remove(idx);
            }
        }

        bool EngineRunning(int idx) { lock (_engineLock) { return _engines.ContainsKey(idx); } }

        void RefreshEngineIfPortsChanged(int idx, int ms, int jitter, int limit, bool up, bool down)
        {
            if (!EngineRunning(idx)) return;
            DateTime last;
            lock (_engineLock) { if (!_engineRestartAt.TryGetValue(idx, out last)) last = DateTime.MinValue; }
            if ((DateTime.Now - last).TotalSeconds < 4) return;
            int cnt;
            var filter = Engine.BuildPortFilter(idx, out cnt);
            if (filter == null) return;
            string old;
            lock (_engineLock) { _engineFilter.TryGetValue(idx, out old); }
            if (filter == old) return;
            string err;
            if (StartEngine(idx, ms, jitter, limit, up, down, out err)) UiLog("实例 " + idx + " 出现新端口，延迟引擎已刷新（覆盖 " + cnt + " 个端口）");
        }

        void SetActual(int idx, string text)
        {
            _actual[idx] = text;
            Ui(delegate()
            {
                foreach (DataGridViewRow row in grid.Rows)
                    if (row.Tag != null && (int)row.Tag == idx) { row.Cells[C_ACTUAL].Value = text; break; }
            });
        }

        void SetAllNormal()
        {
            foreach (var k in App.Presets.Keys.ToList()) App.Presets[k] = "normal";
            foreach (var k in App.Delays.Keys.ToList()) App.Delays[k] = 0;
            foreach (var k in App.Jitters.Keys.ToList()) App.Jitters[k] = 0;
            foreach (var k in App.Limits.Keys.ToList()) App.Limits[k] = 0;
            App.SaveIni();
            var th = new Thread(delegate()
            {
                foreach (var inst in Engine.ListInstances())
                {
                    if (!inst.Running) continue;
                    try { Engine.ClearRules(inst.Index); } catch { }
                    StopEngine(inst.Index);
                    _applied.TryRemove(inst.Index, out _dummy);
                    SetActual(inst.Index, "已恢复正常");
                }
                Engine.StopClumsy();
                Ui(delegate()
                {
                    _suppress = true;
                    foreach (DataGridViewRow row in grid.Rows)
                    {
                        row.Cells[C_PRESET].Value = Presets.Names[0];
                        row.Cells[C_DELAY].Value = "0";
                        row.Cells[C_JITTER].Value = "0";
                        row.Cells[C_LIMIT].Value = "0";
                        row.Cells[C_UP].Value = true;
                        row.Cells[C_DOWN].Value = true;
                    }
                    chkClumsy.Checked = false;
                    _suppress = false;
                });
                UiLog("已全部恢复正常（iptables 清空 + 延迟引擎关闭 + clumsy 关闭）");
            });
            th.IsBackground = true;
            th.Start();
        }

        void ToggleClumsy(bool on)
        {
            var th = new Thread(delegate()
            {
                if (on)
                {
                    foreach (var k in _engines.Keys.ToList()) StopEngine(k);
                    if (Engine.StartClumsy(200, 5, 600, 20, UiLog))
                        UiLog("全局弱网已开启（整机出站，10 分钟自动退出）；每实例延迟已暂停");
                    else
                        Ui(delegate() { _suppress = true; chkClumsy.Checked = false; _suppress = false; });
                }
                else
                {
                    int n = Engine.StopClumsy();
                    UiLog("全局弱网已关闭" + (n > 0 ? "（" + n + " 个 clumsy）" : ""));
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        // ------------------------------------------------------------ 手机 / 局域网设备

        string GetPhoneIp() { return App.PhoneIp ?? ""; }

        static string PhoneStatusText(PhoneInfo p)
        {
            if (p == null) return "未连接。";
            if (!p.Connected) return "未连接（" + p.Serial + "）。检查：手机与电脑同一 Wi-Fi、已开无线调试、手机上点了「允许调试」。";
            return "已连接：" + p.Serial + "　型号 " + p.Model + "　Android " + p.Android + "　root：" + p.RootState;
        }

        void PhoneConnect()
        {
            string ip = (txtPhoneIp.Text ?? "").Trim();
            if (ip.Length == 0) { Log("请填设备 IP:端口，例如 192.168.1.50:5555"); return; }
            if (!ip.Contains(":")) ip += ":5555";
            App.PhoneIp = ip; App.SaveIni();
            Log("连接手机 " + ip + " …");
            var th = new Thread(delegate()
            {
                try
                {
                    bool ok = Phone.Connect(ip);
                    var info = Phone.Probe(ip);
                    _phone = info;
                    _phoneProbeAt = DateTime.Now;
                    Ui(delegate()
                    {
                        lblPhoneStatus.Text = PhoneStatusText(info);
                        lblPhoneStatus.ForeColor = info.Connected ? Color.Green : Color.Firebrick;
                    });
                    UiLog(PhoneStatusText(info) + (ok ? "" : "（adb connect 返回异常，请检查端口/调试授权）"));
                }
                catch (Exception ex) { UiLog("连接失败：" + ex.Message); }
            });
            th.IsBackground = true;
            th.Start();
        }

        void PhoneDisconnect()
        {
            var p = _phone;
            if (p == null) { Log("还没连接"); return; }
            var th = new Thread(delegate()
            {
                try { StopProxyAndClearPhoneProxy(p, true); } catch { }
                Phone.Disconnect(p.Serial);
                _phone = null;
                _phoneProbeAt = DateTime.MinValue;
                Ui(delegate()
                {
                    lblPhoneStatus.Text = "已断开（已尝试清除手机上的规则）";
                    lblPhoneStatus.ForeColor = Color.DimGray;
                });
                UiLog("已断开 " + p.Serial);
            });
            th.IsBackground = true;
            th.Start();
        }

        void PhonePresetChanged()
        {
            string key = PhonePresetKey(Convert.ToString(cmbPhonePreset.SelectedItem));
            bool useLoss = (key == "loss" || key == "delayloss");
            bool useMs = (key == "delay" || key == "delayloss");
            txtPhoneLoss.Enabled = useLoss;
            txtPhoneMs.Enabled = useMs;
            txtPhoneJitter.Enabled = useMs;
            txtPhoneLimit.Enabled = useMs;
            chkPhoneUp.Enabled = useMs;
            chkPhoneDown.Enabled = useMs;
        }

        void StopProxyAndClearPhoneProxy(PhoneInfo p, bool clearDevice)
        {
            try { if (_proxy != null) { _proxy.Stop(); _proxy = null; } } catch { }
            try { if (p != null) Phone.ClearGlobalProxy(p.Serial); } catch { }
            if (clearDevice) { try { if (p != null) Phone.Clear(p); } catch { } }
        }

        void PhoneApply()
        {
            var p = _phone;
            if (p == null || !p.Connected) { Log("先在「手机 / 局域网设备」页点「连接」"); return; }
            string key = PhonePresetKey(Convert.ToString(cmbPhonePreset.SelectedItem));
            int lossPct = 0, ms = 0;
            int.TryParse((txtPhoneLoss.Text ?? "").Trim(), out lossPct);
            int.TryParse((txtPhoneMs.Text ?? "").Trim(), out ms);
            int jitter = 0, limit = 0;
            int.TryParse((txtPhoneJitter.Text ?? "").Trim(), out jitter);
            int.TryParse((txtPhoneLimit.Text ?? "").Trim(), out limit);
            bool up = chkPhoneUp.Checked, down = chkPhoneDown.Checked;
            bool deviceLoss = (key == "offline") || (key == "loss") || (key == "delayloss");
            if (deviceLoss && !p.Root)
            {
                if (key == "delayloss")
                    Log("该手机没有 root：丢包只能交给本机代理做（时延+代理丢包），已按此执行");
                else
                {
                    Log("该手机没有 root：丢包/断网需要在手机内部下发 iptables。只想测时延请选「时延」（走本机代理，免 root）");
                    return;
                }
            }

            var th = new Thread(delegate()
            {
                var desc = new List<string>();
                try
                {
                    StopProxyAndClearPhoneProxy(p, false);
                    if (deviceLoss && p.Root && key != "delay")
                    {
                        string d = Phone.ApplyDevicePreset(p, key == "offline" ? "offline" : "loss", lossPct);
                        if (d != "正常") desc.Add(d);
                    }
                    if (key == "delay" || key == "delayloss")
                    {
                        bool wantProxy = (ms > 0 && (up || down)) || jitter > 0 || limit > 0;
                        if (!wantProxy) UiLog("时延 / 抖动 / 限速 都填 0 的话不会有任何变化");
                        else
                        {
                            string peer = p.Serial.Split(':')[0];
                            string hostIp = DelayProxy.HostIpForPeer(peer);
                            var px = new DelayProxy();
                            px.DelayMs = ms;
                            px.JitterMs = jitter;
                            px.LimitKBps = limit;
                            px.Up = up;
                            px.Down = down;
                            px.LossPercent = (key == "delayloss" && !p.Root) ? Math.Max(0, Math.Min(100, lossPct)) : 0;
                            string err;
                            if (px.Start(out err))
                            {
                                _proxy = px;
                                DelayProxy.EnsureFirewallRule(px.Port);
                                string spec = hostIp + ":" + px.Port;
                                Phone.SetGlobalProxy(p.Serial, spec);
                                var d = new List<string>();
                                if (ms > 0) d.Add("时延 " + ms + "ms(" + DirText(up, down) + ")");
                                if (jitter > 0) d.Add("抖动 ±" + jitter + "ms");
                                if (limit > 0) d.Add("限速 " + limit + "KB/s");
                                desc.Add(string.Join("+", d.ToArray()) + "@本机代理 " + spec);
                            }
                            else { UiLog("代理启动失败：" + err); desc.Add("时延失败"); }
                        }
                    }
                    string text = desc.Count == 0 ? "正常" : string.Join(" + ", desc.ToArray());
                    UiLog("手机 " + p.Serial + " 已套用：" + text);
                    UI_PhoneApplied(text);
                }
                catch (Exception ex) { UiLog("手机套用失败：" + ex.Message); }
            });
            th.IsBackground = true;
            th.Start();
        }

        void PhoneNormal()
        {
            var p = _phone;
            if (p == null) { Log("还没连接"); return; }
            var th = new Thread(delegate()
            {
                try
                {
                    StopProxyAndClearPhoneProxy(p, true);
                    Ui(delegate() { cmbPhonePreset.SelectedIndex = 0; });
                    UiLog("手机 " + p.Serial + " 已恢复正常（iptables 规则 + 全局代理都已清）");
                    UI_PhoneApplied("正常");
                }
                catch (Exception ex) { UiLog("恢复正常失败：" + ex.Message); }
            });
            th.IsBackground = true;
            th.Start();
        }

        void UI_PhoneApplied(string desc)
        {
            Ui(delegate()
            {
                if (_phone != null && _phone.Connected)
                    lblPhoneStatus.Text = PhoneStatusText(_phone) + "　已套用：" + desc + ProxyInfo();
            });
        }

        string ProxyInfo()        {
            var px = _proxy;
            if (px == null) return "";
            return "　[代理 :" + px.Port + " 连接 " + px.Connections + " 上行 " + (px.BytesUp / 1024) + "KB 下行 " + (px.BytesDown / 1024) + "KB]";
        }

        void RefreshPhone()
        {
            var p = _phone;
            if (p == null) return;
            bool fresh = (DateTime.Now - _phoneProbeAt).TotalSeconds < 8;
            if (fresh) return;
            try
            {
                var info = Phone.Probe(p.Serial);
                _phone = info;
                _phoneProbeAt = DateTime.Now;
                Ui(delegate()
                {
                    lblPhoneStatus.Text = PhoneStatusText(info) + ProxyInfo();
                    lblPhoneStatus.ForeColor = info.Connected ? Color.Green : Color.Firebrick;
                });
            }
            catch { }
        }

        // ------------------------------------------------------------ UI

        void Ui(Action a)
        {
            if (_closing) return;
            try { if (InvokeRequired) BeginInvoke(a); else a(); } catch { }
        }

        void UiLog(string s)
        {
            App.Log(s);
            Ui(delegate() { txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s + Environment.NewLine); });
        }

        void Log(string s) { UiLog(s); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _closing = true;
            try
            {
                timer.Stop();
                lock (_engineLock) { foreach (var kv in _engines) { try { kv.Value.Stop(); } catch { } } _engines.Clear(); }
                foreach (var idx in _applied.Keys.ToList()) { try { Engine.ClearRules(idx); } catch { } }
                Engine.StopClumsy();
                // 手机：必须清掉全局代理，否则手机断网（代理没了）
                try { StopProxyAndClearPhoneProxy(_phone, false); } catch { }
            }
            catch { }
            base.OnFormClosing(e);
        }
    }

    static class Program
    {
        static System.Threading.Mutex _singleInstance;

        [STAThread]
        static int Main(string[] args)
        {
            App.EnsureAssets();                       // 单文件版：先把内置的 clumsy/WinDivert 解出来
            if (!App.DirWritable(App.BaseDir))
                App.IniPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MuMuNetSim", "MuMuNetSim.ini");
            App.EnsureAssetsDirForIni();
            App.LoadIni();
            App.FindMuMuManager();
            App.FindClumsy();

            bool noElevate = args.Any(a => a.Equals("/noelevate", StringComparison.OrdinalIgnoreCase));
            bool isStatus = args.Length > 0 && args[0].StartsWith("/status", StringComparison.OrdinalIgnoreCase);
            bool isWhere = args.Length > 0 && args[0].StartsWith("/where", StringComparison.OrdinalIgnoreCase);
            bool isPorts = args.Length > 0 && args[0].StartsWith("/ports", StringComparison.OrdinalIgnoreCase);
            bool isPhone = args.Length > 0 && args[0].StartsWith("/phone", StringComparison.OrdinalIgnoreCase);
            bool isCheck = args.Length > 0 && args[0].StartsWith("/selfcheck", StringComparison.OrdinalIgnoreCase);
            bool isCli = args.Length > 0 && (args[0].StartsWith("/apply", StringComparison.OrdinalIgnoreCase)
                || args[0].StartsWith("/delay", StringComparison.OrdinalIgnoreCase) || isStatus || isWhere || isPorts || isPhone || isCheck);

            // 延迟引擎要用 WinDivert 驱动，必须提权；GUI 也提权一次后 clumsy 才能静默
            if (!App.IsAdmin() && !noElevate && !isStatus && !isWhere && !isPorts && !isPhone && !isCheck)
            {
                try
                {
                    var psi = new ProcessStartInfo(Application.ExecutablePath);
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    psi.Arguments = string.Join(" ", args.Select(a => "\"" + a + "\"").ToArray());
                    Process.Start(psi);
                    return 0;
                }
                catch
                {
                    MessageBox.Show("未取得管理员权限：延迟引擎（WinDivert）无法工作。", App.TITLE, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            if (isCli) return Cli.Run(args);

            // 单实例保护：两个窗口同时跑会互相顶（都在轮询、都套预设）
            string mutexName = "Global\\MuMuNetSim_SingleInstance";
            var altMutex = Environment.GetEnvironmentVariable("MUMUNETSIM_MUTEX");
            if (!string.IsNullOrEmpty(altMutex)) mutexName = "Global\\MuMuNetSim_" + altMutex;
            bool createdNew;
            _singleInstance = new System.Threading.Mutex(true, mutexName, out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("已经有一个 MuMuNetSim 在运行。\r\n两个窗口同时开会互相顶（都轮询、都套预设），请先关掉另一个。",
                    App.TITLE, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }
}
