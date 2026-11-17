// GameBoost - background monitor + reversible gaming mode for Windows 10/11
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("GameBoost")]
[assembly: System.Reflection.AssemblyProduct("GameBoost")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace GameBoost
{
    static class Native
    {
        [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr h);
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint p);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint p);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint a, uint b, int[] c, uint d);
        [DllImport("ntdll.dll")] public static extern int NtSetSystemInformation(int cls, ref int info, int len);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr p, uint acc, out IntPtr tok);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool LookupPrivilegeValue(string s, string n, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool AdjustTokenPrivileges(IntPtr t, bool d, ref TOKEN_PRIV n, int l, IntPtr p, IntPtr r);
        [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct TOKEN_PRIV { public int Count; public long Luid; public int Attr; }

        public static bool EnablePrivilege(string name)
        {
            IntPtr tok;
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x28, out tok)) return false;
            var tp = new TOKEN_PRIV { Count = 1, Attr = 2 };
            if (!LookupPrivilegeValue(null, name, out tp.Luid)) return false;
            return AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }

        // Clears the "standby" memory cache (same as RAMMap > Empty Standby List). Safe; Windows refills it as needed.
        public static bool PurgeStandbyList()
        {
            EnablePrivilege("SeProfileSingleProcessPrivilege");
            int cmd = 4; // MemoryPurgeStandbyList
            return NtSetSystemInformation(80, ref cmd, 4) == 0;
        }

        public static int ForegroundPid()
        {
            uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return (int)pid;
        }
    }

    class ProcRow
    {
        public int Pid; public string Name; public double Cpu; public double Gpu; public long RamMB;
        public bool HasWindow; public string Title;
    }

    class Monitor
    {
        Dictionary<int, TimeSpan> lastCpu = new Dictionary<int, TimeSpan>();
        DateTime lastTime = DateTime.UtcNow;
        Dictionary<string, CounterSample> lastGpu = new Dictionary<string, CounterSample>();
        PerformanceCounterCategory gpuCat;
        public double TotalGpu;

        public Monitor()
        {
            try { if (PerformanceCounterCategory.Exists("GPU Engine")) gpuCat = new PerformanceCounterCategory("GPU Engine"); } catch { }
        }

        Dictionary<int, double> ReadGpu()
        {
            var res = new Dictionary<int, double>();
            var engMax = new Dictionary<string, double>();
            if (gpuCat == null) return res;
            try
            {
                var data = gpuCat.ReadCategory();
                var util = data["Utilization Percentage"];
                if (util == null) return res;
                var now = new Dictionary<string, CounterSample>();
                foreach (InstanceData d in util.Values)
                {
                    string inst = d.InstanceName;
                    now[inst] = d.Sample;
                    CounterSample prev;
                    if (!lastGpu.TryGetValue(inst, out prev)) continue;
                    double v = CounterSample.Calculate(prev, d.Sample);
                    if (double.IsNaN(v) || v <= 0) continue;
                    int a = inst.IndexOf("pid_"); if (a < 0) continue;
                    int b = inst.IndexOf('_', a + 4); if (b < 0) continue;
                    int pid; if (!int.TryParse(inst.Substring(a + 4, b - a - 4), out pid)) continue;
                    double cur; res.TryGetValue(pid, out cur); res[pid] = cur + v;
                    // engine key (everything after the pid part) for total GPU = busiest engine
                    int e = inst.IndexOf("_phys"); string eng = e > 0 ? inst.Substring(e) : inst;
                    double em; engMax.TryGetValue(eng, out em); engMax[eng] = em + v;
                }
                lastGpu = now;
            }
            catch { }
            TotalGpu = engMax.Count == 0 ? 0 : Math.Min(100, engMax.Values.Max());
            return res;
        }

        public List<ProcRow> Sample()
        {
            var now = DateTime.UtcNow;
            double ms = Math.Max(1, (now - lastTime).TotalMilliseconds) * Environment.ProcessorCount;
            lastTime = now;
            var gpu = ReadGpu();
            var list = new List<ProcRow>();
            var seen = new Dictionary<int, TimeSpan>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == 0) continue;
                    var r = new ProcRow { Pid = p.Id, Name = p.ProcessName, RamMB = p.WorkingSet64 / (1024 * 1024) };
                    try
                    {
                        var t = p.TotalProcessorTime; seen[p.Id] = t;
                        TimeSpan old; if (lastCpu.TryGetValue(p.Id, out old)) r.Cpu = Math.Max(0, (t - old).TotalMilliseconds / ms * 100);
                    }
                    catch { }
                    try { r.HasWindow = p.MainWindowHandle != IntPtr.Zero; r.Title = p.MainWindowTitle; } catch { }
                    double g; gpu.TryGetValue(p.Id, out g); r.Gpu = Math.Min(100, g);
                    list.Add(r);
                }
                catch { }
                finally { p.Dispose(); }
            }
            lastCpu = seen;
            return list;
        }
    }

    // ---------------- Gaming Mode (manual, fully reversible) ----------------
    static class GamingMode
    {
        // Never touch these
        static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "System","Idle","Registry","smss","csrss","wininit","winlogon","services","lsass","lsaiso","svchost","dwm",
            "audiodg","fontdrvhost","Memory Compression","MsMpEng","NisSrv","SecurityHealthService","spoolsv","explorer",
            "ShellExperienceHost","StartMenuExperienceHost","sihost","ctfmon","TextInputHost","conhost","dllhost",
            "nvcontainer","NVDisplay.Container","nvsphelper64","WmiPrvSE","SearchHost","RuntimeBroker","taskhostw",
            "GameBoost","vgc","vgk","EasyAntiCheat","EasyAntiCheat_EOS","BEService","FACEIT","faceitservice"};

        static readonly string[] PauseServices = { "wuauserv", "UsoSvc", "BITS", "DoSvc", "WSearch" };

        public static bool Active;
        static Dictionary<int, ProcessPriorityClass> origPriority = new Dictionary<int, ProcessPriorityClass>();
        static List<string> stoppedServices = new List<string>();
        static bool timerSet;
        static string StateFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameBoost", "gamingmode.state"); } }

        public static string Enable(int gamePid, List<ProcRow> rows, Action<string> log)
        {
            if (Active) return "Already on";
            Active = true;
            int fg = Native.ForegroundPid();
            int boosted = 0, lowered = 0, trimmed = 0;

            // 1. Game priority -> High
            if (gamePid > 0)
            {
                try
                {
                    using (var g = Process.GetProcessById(gamePid))
                    {
                        origPriority[gamePid] = g.PriorityClass;
                        g.PriorityClass = ProcessPriorityClass.High;
                        boosted++;
                        log("Game boosted to High priority: " + g.ProcessName);
                    }
                }
                catch (Exception ex) { log("Could not boost game: " + ex.Message); }
            }

            // 2. Idle background apps -> lower priority + trim RAM (nothing is closed)
            int self = Process.GetCurrentProcess().Id;
            foreach (var r in rows)
            {
                if (r.Pid == gamePid || r.Pid == self || r.Pid == fg || r.Pid <= 4) continue;
                if (Protected.Contains(r.Name)) continue;
                if (r.Cpu > 2.0) continue; // in active use -> leave alone
                try
                {
                    using (var p = Process.GetProcessById(r.Pid))
                    {
                        if (p.SessionId == 0) continue; // system services
                        var pc = p.PriorityClass;
                        if (pc == ProcessPriorityClass.Normal || pc == ProcessPriorityClass.AboveNormal)
                        {
                            origPriority[r.Pid] = pc;
                            p.PriorityClass = ProcessPriorityClass.BelowNormal;
                            lowered++;
                        }
                        if (Native.EmptyWorkingSet(p.Handle)) trimmed++;
                    }
                }
                catch { }
            }
            log("Lowered priority of " + lowered + " idle background apps, trimmed RAM of " + trimmed + " (none closed).");

            // 3. Pause Windows Update / delivery / search indexing
            foreach (var s in PauseServices)
            {
                try
                {
                    using (var sc = new ServiceController(s))
                    {
                        if (sc.Status == ServiceControllerStatus.Running && sc.CanStop)
                        {
                            sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8));
                            stoppedServices.Add(s); log("Paused service: " + sc.DisplayName);
                        }
                    }
                }
                catch (Exception ex) { log("Could not pause " + s + ": " + ex.Message); }
            }

            // 4. 1 ms system timer resolution (smoother frame pacing / input)
            timerSet = Native.timeBeginPeriod(1) == 0;
            if (timerSet) log("System timer set to 1 ms.");

            // 5. Clear standby memory cache
            if (Native.PurgeStandbyList()) log("Standby memory cache cleared.");

            SaveState();
            return string.Format("Gaming Mode ON  -  game boosted: {0}, apps eased off: {1}, services paused: {2}", boosted, lowered, stoppedServices.Count);
        }

        public static string Disable(Action<string> log)
        {
            int restored = 0;
            foreach (var kv in origPriority)
            {
                try { using (var p = Process.GetProcessById(kv.Key)) { p.PriorityClass = kv.Value; restored++; } } catch { }
            }
            origPriority.Clear();
            int svc = RestartServices(stoppedServices, log);
            stoppedServices.Clear();
            if (timerSet) { Native.timeEndPeriod(1); timerSet = false; }
            Active = false;
            try { File.Delete(StateFile); } catch { }
            log("Restored priority of " + restored + " processes, resumed " + svc + " services.");
            return "Gaming Mode OFF  -  everything restored";
        }

        static int RestartServices(IEnumerable<string> names, Action<string> log)
        {
            int n = 0;
            foreach (var s in names)
            {
                try { using (var sc = new ServiceController(s)) { if (sc.Status == ServiceControllerStatus.Stopped) { sc.Start(); n++; log("Resumed service: " + sc.DisplayName); } } }
                catch (Exception ex) { log("Could not resume " + s + ": " + ex.Message); }
            }
            return n;
        }

        static void SaveState()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile));
                File.WriteAllLines(StateFile, stoppedServices);
            }
            catch { }
        }

        // If the app was killed while Gaming Mode was on, put services back on next launch.
        public static void RecoverFromCrash(Action<string> log)
        {
            try
            {
                if (!File.Exists(StateFile)) return;
                var svcs = File.ReadAllLines(StateFile).Where(x => x.Trim().Length > 0).ToList();
                if (svcs.Count > 0) { log("Recovering from previous session..."); RestartServices(svcs, log); }
                File.Delete(StateFile);
            }
            catch { }
        }
    }

    // ---------------- Always-on/off toggles (each independent & reversible) ----------------
    abstract class Tweak
    {
        public string Name, Description; public bool NeedsReboot;
        public abstract bool IsOn();
        public abstract void Set(bool on);
    }

    class RegTweak : Tweak
    {
        public RegistryKey Root; public string Path; public List<Tuple<string, object, object, RegistryValueKind>> Values = new List<Tuple<string, object, object, RegistryValueKind>>();
        public Action After;
        public RegTweak Val(string name, object onVal, object offVal, RegistryValueKind k = RegistryValueKind.DWord) { Values.Add(Tuple.Create(name, onVal, offVal, k)); return this; }
        public override bool IsOn()
        {
            using (var k = Root.OpenSubKey(Path))
            {
                if (k == null) return false;
                foreach (var v in Values)
                {
                    var cur = k.GetValue(v.Item1);
                    if (cur == null || Convert.ToString(cur) != Convert.ToString(v.Item2 is uint ? (object)unchecked((int)(uint)v.Item2) : v.Item2)) return false;
                }
                return true;
            }
        }
        public override void Set(bool on)
        {
            using (var k = Root.CreateSubKey(Path))
                foreach (var v in Values)
                {
                    object val = on ? v.Item2 : v.Item3;
                    if (val is uint) val = unchecked((int)(uint)val);
                    k.SetValue(v.Item1, val, v.Item4);
                }
            if (After != null) After();
        }
    }

    class PowerPlanTweak : Tweak
    {
        const string ULTIMATE = "e9a42b02-d5df-448d-aa00-03f14749eb61";
        const string HIGH = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        const string BALANCED = "381b4222-f694-41f0-9685-ff5bb260df2e";
        static string SaveFile { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameBoost", "powerplan.txt"); } }

        static string Run(string args)
        {
            var psi = new ProcessStartInfo("powercfg", args) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using (var p = Process.Start(psi)) { string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); return o; }
        }
        static string ActiveGuid()
        {
            var m = System.Text.RegularExpressions.Regex.Match(Run("/getactivescheme"), "[0-9a-fA-F-]{36}");
            return m.Success ? m.Value.ToLower() : "";
        }
        static string FindPlan(string nameOrGuid)
        {
            foreach (var line in Run("/list").Split('\n'))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, "[0-9a-fA-F-]{36}");
                if (m.Success && (line.IndexOf(nameOrGuid, StringComparison.OrdinalIgnoreCase) >= 0)) return m.Value.ToLower();
            }
            return null;
        }
        public override bool IsOn()
        {
            var a = ActiveGuid();
            var line = Run("/getactivescheme");
            return a == ULTIMATE || a == HIGH || line.IndexOf("Ultimate", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("High performance", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        public override void Set(bool on)
        {
            if (on)
            {
                try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SaveFile)); File.WriteAllText(SaveFile, ActiveGuid()); } catch { }
                string g = FindPlan("Ultimate");
                if (g == null) { Run("-duplicatescheme " + ULTIMATE); g = FindPlan("Ultimate"); }
                if (g == null) g = FindPlan(HIGH) ?? HIGH;
                Run("/setactive " + g);
            }
            else
            {
                string prev = BALANCED;
                try { if (File.Exists(SaveFile)) prev = File.ReadAllText(SaveFile).Trim(); } catch { }
                if (prev.Length != 36 || prev == ULTIMATE || prev == HIGH) prev = BALANCED;
                Run("/setactive " + prev);
            }
        }
    }

    class WindowedOptTweak : Tweak
    {
        const string P = @"Software\Microsoft\DirectX\UserGpuPreferences";
        public override bool IsOn()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(P))
                return k != null && Convert.ToString(k.GetValue("DirectXUserGlobalSettings", "")).Contains("SwapEffectUpgradeEnable=1");
        }
        public override void Set(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(P))
            {
                var parts = Convert.ToString(k.GetValue("DirectXUserGlobalSettings", "")).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(x => !x.StartsWith("SwapEffectUpgradeEnable")).ToList();
                parts.Add("SwapEffectUpgradeEnable=" + (on ? "1" : "0"));
                k.SetValue("DirectXUserGlobalSettings", string.Join(";", parts) + ";", RegistryValueKind.String);
            }
        }
    }

    static class Tweaks
    {
        public static List<Tweak> All()
        {
            var list = new List<Tweak>();
            list.Add(new PowerPlanTweak { Name = "Ultimate / High Performance power plan", Description = "Stops the CPU from down-clocking between loads (reduces micro-stutter). Turning off restores the plan you had before. Laptops: use while plugged in." });
            list.Add(new RegTweak { Name = "Windows Game Mode", Description = "Windows' built-in game prioritization; also blocks Windows Update driver installs/restart prompts while you play.", Root = Registry.CurrentUser, Path = @"Software\Microsoft\GameBar" }
                .Val("AutoGameModeEnabled", 1, 0).Val("AllowAutoGameMode", 1, 0));
            list.Add(new RegTweak { Name = "Hardware-accelerated GPU scheduling (HAGS)", Description = "Lets your NVIDIA GPU manage its own memory scheduling; lowers CPU overhead/latency and is required for DLSS Frame Generation. Needs a restart.", NeedsReboot = true, Root = Registry.LocalMachine, Path = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers" }
                .Val("HwSchMode", 2, 1));
            list.Add(new WindowedOptTweak { Name = "Optimizations for windowed games", Description = "Gives borderless/windowed games near-fullscreen latency and enables Auto HDR/VRR handling. Takes effect next game launch." });
            list.Add(new RegTweak { Name = "Disable Xbox background recording (Game DVR)", Description = "Stops Game Bar from constantly recording gameplay in the background, which costs GPU encode time and some FPS.", Root = Registry.CurrentUser, Path = @"System\GameConfigStore" }
                .Val("GameDVR_Enabled", 0, 1));
            var capture = new RegTweak { Name = "Disable Game Bar captures", Description = "Turns off the Game Bar capture hooks (screenshots/clips via Win+Alt+R). Use NVIDIA ShadowPlay instead if you want clips.", Root = Registry.CurrentUser, Path = @"Software\Microsoft\Windows\CurrentVersion\GameDVR" }
                .Val("AppCaptureEnabled", 0, 1);
            list.Add(capture);
            list.Add(new RegTweak { Name = "Disable network throttling", Description = "Removes Windows' multimedia network packet throttle; can help online games with ping spikes while music/video plays.", Root = Registry.LocalMachine, Path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile" }
                .Val("NetworkThrottlingIndex", 0xFFFFFFFFu, 10));
            var mouse = new RegTweak { Name = "Disable mouse acceleration", Description = "Turns off 'Enhance pointer precision' so aim is 1:1 with hand movement. Applied instantly.", Root = Registry.CurrentUser, Path = @"Control Panel\Mouse" }
                .Val("MouseSpeed", "0", "1", RegistryValueKind.String).Val("MouseThreshold1", "0", "6", RegistryValueKind.String).Val("MouseThreshold2", "0", "10", RegistryValueKind.String);
            mouse.After = () =>
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse"))
                {
                    int[] p = { int.Parse((string)k.GetValue("MouseThreshold1")), int.Parse((string)k.GetValue("MouseThreshold2")), int.Parse((string)k.GetValue("MouseSpeed")) };
                    Native.SystemParametersInfo(0x0004, 0, p, 0x03); // SPI_SETMOUSE
                }
            };
            list.Add(mouse);
            return list;
        }
    }

}
