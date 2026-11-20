// MuMuNetSim - MuMu 模拟器弱网 / 断网模拟器（单文件 WinForms）
// 编译：csc.exe /target:winexe /out:MuMuNetSim.exe MuMuNetSim.cs /r:System.Windows.Forms.dll /r:System.Drawing.dll
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace MuMuNetSim
{
    public class InstanceInfo
    {
        public int Index;
        public string Name = "";
        public string AndroidVersion = "";
        public bool Running;
        public int AdbPort = 5555;

        public override string ToString()
        {
            return "实例 " + Index + " · " + Name + " (Android " + AndroidVersion + ")" + (Running ? "" : "  [未运行]");
        }
    }

    public static class App
    {
        public const string TAG = "mumu-net";
        public const string TITLE = "MuMu 弱网 / 断网模拟器  v1.0";

        public static string BaseDir = Path.GetDirectoryName(Application.ExecutablePath);
        public static string IniPath = Path.Combine(BaseDir, "MuMuNetSim.ini");
        public static string LogPath = Path.Combine(Path.GetTempPath(), "MuMuNetSim.log");

        public static string MuMuManagerPath;
        public static string ClumsyPath;

        public static void Log(string s)
        {
            try
            {
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s + Environment.NewLine, Encoding.UTF8);
            }
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
                    if (string.Equals(k, "mumumanager", StringComparison.OrdinalIgnoreCase)) MuMuManagerPath = v;
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
                File.WriteAllText(IniPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        public static bool IsAdmin()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        public static string FindMuMuManager()
        {
            if (!string.IsNullOrEmpty(MuMuManagerPath) && File.Exists(MuMuManagerPath)) return MuMuManagerPath;
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var cands = new List<string>();
            cands.Add(Path.Combine(pf, @"Netease\MuMu\nx_main\MuMuManager.exe"));
            cands.Add(Path.Combine(pf86, @"Netease\MuMu\nx_main\MuMuManager.exe"));
            cands.Add(@"D:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe");
            cands.Add(@"D:\Netease\MuMu\nx_main\MuMuManager.exe");
            foreach (var c in cands) if (File.Exists(c)) { MuMuManagerPath = c; return c; }
            foreach (var root in new[] { pf + @"\Netease\MuMu", @"D:\Program Files\Netease\MuMu", pf86 + @"\Netease\MuMu" })
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    var f = Directory.GetFiles(root, "MuMuManager.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (f != null) { MuMuManagerPath = f; return f; }
                }
                catch { }
            }
            return null;
        }

        public static string FindClumsy()
        {
            if (!string.IsNullOrEmpty(ClumsyPath) && File.Exists(ClumsyPath)) return ClumsyPath;
            var cands = new List<string>();
            cands.Add(Path.Combine(BaseDir, @"clumsy\clumsy.exe"));
            cands.Add(Path.Combine(BaseDir, "clumsy.exe"));
            cands.Add(@"D:\clumsy\clumsy.exe");
            cands.Add(@"C:\clumsy\clumsy.exe");
            cands.Add(@"D:\tools\clumsy\clumsy.exe");
            foreach (var c in cands) if (File.Exists(c)) { ClumsyPath = c; return c; }
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
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return "<TIMEOUT>";
                    }
                    p.WaitForExit();
                    return sb.ToString();
                }
            }
            catch (Exception ex)
            {
                return "<ERROR: " + ex.Message + ">";
            }
        }
    }

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
                    foreach (var n in nums)
                    {
                        Guest(idx, "iptables -D " + chain + " " + n, 20000);
                        removed++;
                    }
                }
            }
            return removed;
        }

        // 保护 adb 控制通道：实例内 adbd 监听 5555，另外把 MuMuManager 报的宿主侧端口也一并放行（不同版本可能不同）
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

        public static string ListRules(int idx)
        {
            var outp = Guest(idx, "iptables -S INPUT; echo ---; iptables -S OUTPUT", 20000);
            var hits = new List<string>();
            foreach (var line in outp.Split('\n'))
            {
                var t = line.Trim();
                if (t.Contains(App.TAG)) hits.Add("    " + t);
            }
            return hits.Count == 0 ? "    （无 mumu-net 规则）" : string.Join(Environment.NewLine, hits.ToArray());
        }

        public static string Ping(int idx, int count)
        {
            var outp = Guest(idx, "timeout 20 ping -c " + count + " -W 2 8.8.8.8 | tail -2", 30000);
            return Regex.Replace(outp.Trim(), @"\s+", " ");
        }

        public static int StopClumsy()
        {
            int n = 0;
            try
            {
                foreach (var p in Process.GetProcessesByName("clumsy"))
                {
                    try { p.Kill(); n++; } catch { }
                }
            }
            catch { }
            if (n == 0)
            {
                // 兜底：taskkill 能杀掉提权实例（本进程若已提权）
                try
                {
                    Proc.Run(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), "/IM clumsy.exe /F", 8000);
                }
                catch { }
            }
            for (int i = 0; i < 20; i++)
            {
                if (Process.GetProcessesByName("clumsy").Length == 0) break;
                Thread.Sleep(200);
            }
            return n;
        }

        public static bool StartClumsy(int lagMs, int dropPercent, int autoStopSec, int waitSec, Action<string> log)
        {
            var exe = App.FindClumsy();
            if (exe == null) { log("找不到 clumsy.exe，请把 clumsy 放在程序目录下的 clumsy 子文件夹里"); return false; }
            StopClumsy();
            var sb = new StringBuilder();
            sb.Append("--filter \"outbound and ip\"");
            if (lagMs > 0) { sb.Append(" --lag on --lag-time " + lagMs); }
            if (dropPercent > 0) { sb.Append(" --drop on --drop-chance " + dropPercent); }
            if (autoStopSec > 0) { sb.Append(" --timeout " + autoStopSec); }
            var args = sb.ToString();
            log("启动 clumsy：" + args);
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                log("启动 clumsy 失败：" + ex.Message);
                return false;
            }
            for (int i = 0; i < waitSec * 4; i++)
            {
                Thread.Sleep(250);
                if (Process.GetProcessesByName("clumsy").Length > 0)
                {
                    Thread.Sleep(1500);
                    return true;
                }
            }
            log("clumsy 没有启动成功（若未提权，请点 UAC 的「是」）");
            return false;
        }
    }

    public static class Cli
    {
        // 供脚本/自测用：MuMuNetSim.exe /apply normal|offline|weak|weak-guest [/instance N] [/noelevate]
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
                    sb.AppendLine("日志=" + App.LogPath);
                    sb.AppendLine("程序目录=" + App.BaseDir);
                    Console.WriteLine(sb.ToString());
                    File.AppendAllText(App.LogPath, sb.ToString(), Encoding.UTF8);
                    return 0;
                }
                if (args.Length > 0 && args[0].Equals("/status", StringComparison.OrdinalIgnoreCase))
                {
                    var all = Engine.ListInstances();
                    foreach (var i in all) sb.AppendLine(i.ToString() + (i.Running ? "  adb=" + i.AdbPort : ""));
                    Console.WriteLine(sb.ToString());
                    File.AppendAllText(App.LogPath, sb.ToString(), Encoding.UTF8);
                    return 0;
                }
                string mode = "normal";
                int idx = -1;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i].Equals("/apply", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) mode = args[i + 1].ToLowerInvariant();
                    if (args[i].Equals("/instance", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) idx = int.Parse(args[i + 1]);
                }
                var insts = Engine.ListInstances().Where(x => x.Running).ToList();
                if (insts.Count == 0) { Console.WriteLine("没有正在运行的 MuMu 实例"); return 2; }
                var inst = idx >= 0 ? insts.FirstOrDefault(x => x.Index == idx) : insts[0];
                if (inst == null) { Console.WriteLine("没有找到实例 " + idx); return 2; }
                Action<string> log = delegate(string s) { Console.WriteLine(s); App.Log(s); };
                log("目标：" + inst + "  mode=" + mode);
                log("清理旧规则：" + Engine.ClearRules(inst.Index) + " 条");
                Engine.StopClumsy();
                switch (mode)
                {
                    case "offline":
                        Engine.ApplyOffline(inst.Index, inst.AdbPort, true);
                        log("已断网（双向）");
                        break;
                    case "weak-guest":
                        Engine.ApplyLoss(inst.Index, inst.AdbPort, 0.15);
                        log("已设置实例内丢包 15%");
                        break;
                    case "weak":
                        if (!Engine.StartClumsy(200, 5, 600, 20, log)) return 3;
                        log("clumsy 已启动：延迟 200ms + 丢包 5%");
                        break;
                    default:
                        log("已恢复正常");
                        break;
                }
                Thread.Sleep(500);
                log("实例内网络： " + Engine.Ping(inst.Index, 3));
                log("当前规则：\r\n" + Engine.ListRules(inst.Index));
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
        ComboBox cmbMode;
        ComboBox cmbInstance;
        Label lblHint;
        Label lblStatus;
        TextBox txtLog;
        Button btnRefresh;
        Button btnNormal;
        bool _busy;
        bool _ready;
        readonly List<InstanceInfo> _instances = new List<InstanceInfo>();
        int _appliedIndex = -1;
        string _appliedMode = "normal";

        static readonly string[] Modes = {
            "正常（不干预网络）",
            "弱网 · 延迟 200ms + 丢包 5%（clumsy，作用于整机出站）",
            "弱网 · 丢包 15%（仅该实例，走实例内 iptables）",
            "断网 · 完全不通（仅该实例）"
        };
        static readonly string[] Hints = {
            "恢复：清掉实例内的 mumu-net 规则并关闭 clumsy。",
            "clumsy 需要管理员权限；程序启动时已提权，所以这里一般不会再弹 UAC。整机出站流量会变慢，本机回环 127.0.0.1 不受影响。",
            "不需要 clumsy：直接在模拟器内部随机丢 15% 出方向包，只影响这一个实例，秒级生效。",
            "在模拟器内部双向 DROP，仅该实例断网；adb 控制通道会保留，所以随时能切回「正常」。"
        };

        public MainForm()
        {
            Text = App.TITLE;
            ClientSize = new Size(560, 452);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            try
            {
                var cp = App.FindClumsy();
                if (cp != null) Icon = Icon.ExtractAssociatedIcon(cp);
            }
            catch { }

            var lblMode = new Label();
            lblMode.Text = "模拟环境：";
            lblMode.SetBounds(14, 16, 80, 22);
            lblMode.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            Controls.Add(lblMode);

            cmbMode = new ComboBox();
            cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbMode.SetBounds(96, 12, 448, 26);
            cmbMode.Items.AddRange(Modes);
            cmbMode.SelectedIndex = 0;
            cmbMode.SelectedIndexChanged += CmbMode_Changed;
            Controls.Add(cmbMode);

            lblHint = new Label();
            lblHint.SetBounds(96, 42, 448, 34);
            lblHint.ForeColor = Color.DimGray;
            lblHint.Text = Hints[0];
            Controls.Add(lblHint);

            var lblInst = new Label();
            lblInst.Text = "目标实例：";
            lblInst.SetBounds(14, 88, 80, 22);
            Controls.Add(lblInst);

            cmbInstance = new ComboBox();
            cmbInstance.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbInstance.SetBounds(96, 84, 356, 26);
            Controls.Add(cmbInstance);

            btnRefresh = new Button();
            btnRefresh.Text = "刷新";
            btnRefresh.SetBounds(458, 83, 86, 28);
            btnRefresh.Click += delegate { LoadInstances(true); };
            Controls.Add(btnRefresh);

            btnNormal = new Button();
            btnNormal.Text = "立即恢复正常";
            btnNormal.SetBounds(96, 120, 140, 30);
            btnNormal.Click += delegate { cmbMode.SelectedIndex = 0; };
            Controls.Add(btnNormal);

            lblStatus = new Label();
            lblStatus.SetBounds(250, 126, 300, 20);
            lblStatus.Text = "就绪";
            lblStatus.ForeColor = Color.Green;
            Controls.Add(lblStatus);

            var lblLog = new Label();
            lblLog.Text = "日志：";
            lblLog.SetBounds(14, 162, 60, 20);
            Controls.Add(lblLog);

            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.SetBounds(14, 184, 532, 236);
            txtLog.Font = new Font("Consolas", 9f);
            txtLog.BackColor = Color.FromArgb(250, 250, 250);
            Controls.Add(txtLog);

            var lblTip = new Label();
            lblTip.SetBounds(14, 424, 532, 20);
            lblTip.ForeColor = Color.DimGray;
            lblTip.Text = "提示：关闭窗口会自动恢复正常网络。clumsy 源文件：" + (App.FindClumsy() ?? "未找到");
            Controls.Add(lblTip);

            Load += delegate { _ready = true; LoadInstances(false); Log("启动。管理员=" + App.IsAdmin() + "，clumsy=" + (App.ClumsyPath ?? "未找到")); };
        }

        void LoadInstances(bool manual)
        {
            _instances.Clear();
            cmbInstance.Items.Clear();
            try
            {
                var all = Engine.ListInstances();
                foreach (var i in all.Where(x => x.Running)) _instances.Add(i);
            }
            catch (Exception ex) { Log("读取实例失败：" + ex.Message); }
            foreach (var i in _instances) cmbInstance.Items.Add(i.ToString());
            if (_instances.Count > 0) { cmbInstance.SelectedIndex = 0; Log("发现 " + _instances.Count + " 个运行中的实例"); }
            else if (manual) Log("没有运行中的实例，请先启动 MuMu 实例");
        }

        void CmbMode_Changed(object sender, EventArgs e)
        {
            if (!_ready) return;
            lblHint.Text = Hints[cmbMode.SelectedIndex];
            if (_busy) { lblStatus.Text = "上一步还在执行…"; return; }
            ApplyAsync(cmbMode.SelectedIndex);
        }

        void ApplyAsync(int modeIndex)
        {
            if (_instances.Count == 0) { Log("没有运行中的实例"); cmbMode.SelectedIndex = 0; return; }
            var inst = _instances[cmbInstance.SelectedIndex < 0 ? 0 : cmbInstance.SelectedIndex];
            _busy = true;
            cmbMode.Enabled = false; cmbInstance.Enabled = false; btnRefresh.Enabled = false;
            string modeName = ModeName(modeIndex);
            lblStatus.Text = "执行中：" + modeName;
            lblStatus.ForeColor = Color.DarkOrange;
            Log("---- " + modeName + " / " + inst);

            var th = new Thread(delegate()
            {
                string result = "";
                Color color = Color.Green;
                try
                {
                    int removed = Engine.ClearRules(inst.Index);
                    Engine.StopClumsy();
                    if (removed > 0) UiLog("已清理旧规则 " + removed + " 条");

                    if (modeIndex == 3)
                    {
                        Engine.ApplyOffline(inst.Index, inst.AdbPort, true);
                        UiLog("已断网（双向 DROP，保留 lo + adb/" + inst.AdbPort + "）");
                    }
                    else if (modeIndex == 2)
                    {
                        Engine.ApplyLoss(inst.Index, inst.AdbPort, 0.15);
                        UiLog("已设置实例内丢包 15%（出方向）");
                    }
                    else if (modeIndex == 1)
                    {
                        if (!Engine.StartClumsy(200, 5, 600, 20, UiLog))
                        {
                            UiLog("clumsy 启动失败，回退为实例内丢包 15%");
                            Engine.ApplyLoss(inst.Index, inst.AdbPort, 0.15);
                            result = "clumsy 失败，已回退丢包模式";
                            color = Color.DarkOrange;
                        }
                        else
                        {
                            UiLog("clumsy 已启动：延迟 200ms + 丢包 5%，10 分钟后自动退出");
                        }
                    }
                    UiLog("实例内网络： " + Engine.Ping(inst.Index, 3));
                    UiLog("当前规则：");
                    foreach (var l in Engine.ListRules(inst.Index).Split('\n')) UiLog("  " + l.Trim());
                    if (result == "") result = (modeIndex == 0 ? "正常" : "已生效：" + modeName);
                    _appliedIndex = modeIndex == 0 ? -1 : inst.Index;
                    _appliedMode = modeIndex == 0 ? "normal" : modeName;
                }
                catch (Exception ex)
                {
                    result = "出错：" + ex.Message;
                    color = Color.Firebrick;
                    UiLog("错误：" + ex);
                }
                finally
                {
                    var r = result; var c = color;
                    Ui(delegate()
                    {
                        _busy = false;
                        cmbMode.Enabled = true; cmbInstance.Enabled = true; btnRefresh.Enabled = true;
                        lblStatus.Text = r;
                        lblStatus.ForeColor = c;
                    });
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        static string ModeName(int i)
        {
            return Modes[Math.Max(0, Math.Min(Modes.Length - 1, i))];
        }

        void Ui(Action a)
        {
            try
            {
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch { }
        }

        void UiLog(string s)
        {
            App.Log(s);
            Ui(delegate()
            {
                txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s + Environment.NewLine);
            });
        }

        void Log(string s) { UiLog(s); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                if (_appliedIndex >= 0)
                {
                    Engine.ClearRules(_appliedIndex);
                    Engine.StopClumsy();
                }
                else
                {
                    Engine.StopClumsy();
                }
            }
            catch { }
            base.OnFormClosing(e);
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            App.LoadIni();
            App.FindMuMuManager();
            App.FindClumsy();

            bool noElevate = args.Any(a => a.Equals("/noelevate", StringComparison.OrdinalIgnoreCase));
            bool isStatus = args.Length > 0 && args[0].StartsWith("/status", StringComparison.OrdinalIgnoreCase);
            bool isWhere = args.Length > 0 && args[0].StartsWith("/where", StringComparison.OrdinalIgnoreCase);
            bool isCli = args.Length > 0 && (args[0].StartsWith("/apply", StringComparison.OrdinalIgnoreCase)
                || isStatus || isWhere);

            // 提权放在 CLI 之前：这样 /apply weak 也能以管理员身份静默启动 clumsy（clumsy 已提权时不会再弹 UAC）
            if (!App.IsAdmin() && !noElevate && !isStatus && !isWhere)
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
                    MessageBox.Show("未取得管理员权限：弱网（clumsy）可能弹 UAC，关闭 clumsy 也可能失败。",
                        App.TITLE, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            if (isCli) return Cli.Run(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }
}
