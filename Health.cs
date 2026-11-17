using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace GameBoost
{
    class Result { public int Level; public string Title, Value, Detail; } // 0 good, 1 info, 2 warning, 3 problem

    // nvidia-smi ships with the NVIDIA driver
    static class Nv
    {
        static string path;
        public static bool Available { get { return Path != null; } }
        static string Path
        {
            get
            {
                if (path != null) return path.Length == 0 ? null : path;
                foreach (var p in new[] { Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\nvidia-smi.exe"), @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe" })
                    if (File.Exists(p)) { path = p; return p; }
                path = ""; return null;
            }
        }
        public static Dictionary<string, string> Query(string fields)
        {
            if (Path == null) return null;
            try
            {
                var psi = new ProcessStartInfo(Path, "--query-gpu=" + fields + " --format=csv,noheader,nounits") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(4000);
                    if (p.ExitCode != 0) return null;
                    var line = o.Split('\n')[0]; var vals = line.Split(',').Select(v => v.Trim()).ToArray(); var keys = fields.Split(',');
                    if (vals.Length != keys.Length) return null;
                    var d = new Dictionary<string, string>(); for (int i = 0; i < keys.Length; i++) d[keys[i]] = vals[i]; return d;
                }
            }
            catch { return null; }
        }
        public static double Num(Dictionary<string, string> d, string k) { double v; return d != null && d.ContainsKey(k) && double.TryParse(d[k], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : double.NaN; }
        static string throttlePrefix;
        public static Dictionary<string, string> Throttle()
        {
            foreach (var pre in throttlePrefix != null ? new[] { throttlePrefix } : new[] { "clocks_event_reasons", "clocks_throttle_reasons" })
            {
                var d = Query(pre + ".hw_thermal_slowdown," + pre + ".sw_thermal_slowdown," + pre + ".hw_slowdown," + pre + ".sw_power_cap");
                if (d != null) { throttlePrefix = pre; return d.ToDictionary(kv => kv.Key.Substring(pre.Length + 1), kv => kv.Value); }
            }
            return null;
        }
        public const string Live = "temperature.gpu,utilization.gpu,clocks.sm,clocks.max.sm,power.draw,power.limit,fan.speed";
    }

    // Real CPU clock from Windows counters: % Processor Performance x nominal frequency
    class CpuClocks : IDisposable
    {
        List<PerformanceCounter> cores = new List<PerformanceCounter>();
        PerformanceCounter freq, limit, util; public double Base; public bool Ok;
        public CpuClocks(int fallbackBase)
        {
            Base = fallbackBase;
            try
            {
                var cat = new PerformanceCounterCategory("Processor Information");
                foreach (var inst in cat.GetInstanceNames().Where(i => !i.Contains("_Total")))
                    cores.Add(new PerformanceCounter("Processor Information", "% Processor Performance", inst));
                freq = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total");
                try { limit = new PerformanceCounter("Processor Information", "% Performance Limit", "_Total"); } catch { }
                try { util = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); } catch { }
                foreach (var c in cores) c.NextValue(); if (limit != null) limit.NextValue(); if (util != null) util.NextValue();
                double f = freq.NextValue(); if (f > 100) Base = f;
                Ok = cores.Count > 0;
            }
            catch { Ok = false; }
        }
        // returns max core MHz, average core MHz, performance limit %, cpu usage %
        public double[] Sample()
        {
            if (!Ok) return new[] { double.NaN, double.NaN, double.NaN, double.NaN };
            var v = cores.Select(c => { try { return (double)c.NextValue(); } catch { return 0.0; } }).Where(x => x > 0).ToList();
            double lim = double.NaN, u = double.NaN; try { if (limit != null) lim = limit.NextValue(); } catch { } try { if (util != null) u = Math.Min(100, util.NextValue()); } catch { }
            if (v.Count == 0) return new[] { double.NaN, double.NaN, lim, u };
            return new[] { v.Max() / 100 * Base, v.Average() / 100 * Base, lim, u };
        }
        public void Dispose() { foreach (var c in cores) c.Dispose(); }
    }

    static class ThermalZone
    {
        // ACPI thermal zone (motherboard-reported). Not exact core temp, but often tracks CPU heat.
        public static double MaxC()
        {
            try
            {
                var cat = new PerformanceCounterCategory("Thermal Zone Information");
                double best = double.NaN;
                foreach (var i in cat.GetInstanceNames())
                    using (var c = new PerformanceCounter("Thermal Zone Information", "Temperature", i))
                    {
                        double k = c.NextValue(); if (k < 200 || k > 420) continue; double cdeg = k - 273.15;
                        if (double.IsNaN(best) || cdeg > best) best = cdeg;
                    }
                return best;
            }
            catch { return double.NaN; }
        }
    }

    class HealthTest
    {
        public event Action<string, float> Progress; public event Action<Result> Found;
        public double PeakMHz;
        volatile bool stop;
        HwInfo hw;
        public HealthTest(HwInfo h) { hw = h; }
        void P(string s, float f) { if (Progress != null) Progress(s, f); }
        void R(int lvl, string t, string v, string d) { if (Found != null) Found(new Result { Level = lvl, Title = t, Value = v, Detail = d }); }

        static double Work(Func<bool> keepGoing)
        {
            // mixed integer/float workload; returns iterations done
            double ops = 0; uint x = 2463534242; double f = 1.0001;
            while (keepGoing())
            {
                for (int i = 0; i < 200000; i++) { x ^= x << 13; x ^= x >> 17; x ^= x << 5; f = f * 1.0000001 + (x & 7) * 1e-9; if (f > 2) f = 1.0001; }
                ops += 200000;
            }
            if (f < 0) ops = -1; // keep f alive
            return ops;
        }

        public void Run()
        {
            using (var clk = new CpuClocks(hw.BaseMHz))
            {
                // ---- 1. Single core ----
                P("Testing single-core speed...", .02f);
                double single = 0; var sw = Stopwatch.StartNew(); bool run = true;
                var t1 = new Thread(() => single = Work(() => run)) { Priority = ThreadPriority.AboveNormal, IsBackground = true }; t1.Start();
                double peak = 0;
                while (sw.ElapsedMilliseconds < 6000) { Thread.Sleep(250); var s = clk.Sample(); if (!double.IsNaN(s[0])) peak = Math.Max(peak, s[0]); P("Testing single-core speed...", .02f + .13f * sw.ElapsedMilliseconds / 6000f); }
                run = false; t1.Join(); double singleRate = single / sw.Elapsed.TotalSeconds; PeakMHz = peak;
                int singlePts = (int)(singleRate / 1e5);
                if (clk.Ok && peak > 0)
                {
                    if (peak < clk.Base * 0.95) R(3, "CPU boost", "Peak " + (peak / 1000).ToString("0.00") + " GHz (base " + (clk.Base / 1000).ToString("0.00") + ")", "Your CPU isn't even reaching its base clock. Usually a power-saver plan, a laptop on battery, or heavy throttling. Try the Ultimate Performance toggle.");
                    else R(0, "CPU boost", "Peak " + (peak / 1000).ToString("0.00") + " GHz (base " + (clk.Base / 1000).ToString("0.00") + ")", "Boosting above its base clock as designed.");
                }
                R(1, "Single-core score", singlePts.ToString("N0") + " pts", "Single-thread speed matters most for high FPS in games like Roblox, Valorant and Fortnite.");

                // ---- 2. Multi core sustained ----
                P("Stress testing all cores (20 s)...", .15f);
                int n = Environment.ProcessorCount; var counts = new double[n]; run = true; sw.Restart();
                var ts = Enumerable.Range(0, n).Select(i => new Thread(() => counts[i] = Work(() => run)) { IsBackground = true }).ToList();
                ts.ForEach(t => t.Start());
                var avgs = new List<double>(); var lims = new List<double>(); var temps = new List<double>();
                double tz0 = ThermalZone.MaxC();
                while (sw.ElapsedMilliseconds < 20000)
                {
                    Thread.Sleep(500); var s = clk.Sample();
                    if (!double.IsNaN(s[1])) avgs.Add(s[1]); if (!double.IsNaN(s[2])) lims.Add(s[2]);
                    double tz = ThermalZone.MaxC(); if (!double.IsNaN(tz)) temps.Add(tz);
                    P("Stress testing all cores (" + (20 - sw.ElapsedMilliseconds / 1000) + " s)...", .15f + .35f * sw.ElapsedMilliseconds / 20000f);
                }
                run = false; ts.ForEach(t => t.Join());
                double multiRate = counts.Sum() / sw.Elapsed.TotalSeconds; int multiPts = (int)(multiRate / 1e5);
                double scale = multiRate / Math.Max(1, singleRate);
                R(1, "Multi-core score", multiPts.ToString("N0") + " pts", hw.Cores + " cores / " + hw.Threads + " threads working together.");
                if (scale < hw.Cores * 0.55) R(2, "Multi-core scaling", scale.ToString("0.0") + "x on " + hw.Cores + " cores", "Lower than expected for this core count. Something in the background was busy during the test, or the CPU is power/heat limited.");
                else R(0, "Multi-core scaling", scale.ToString("0.0") + "x on " + hw.Cores + " cores", "All cores are contributing normally.");

                if (avgs.Count >= 16)
                {
                    double first = avgs.Skip(1).Take(5).Average(), last = avgs.Skip(avgs.Count - 10).Average(); double ratio = last / first;
                    string v = (first / 1000).ToString("0.00") + " → " + (last / 1000).ToString("0.00") + " GHz";
                    if (ratio < 0.85) R(3, "Clock stability under load", v, "Clocks dropped " + ((1 - ratio) * 100).ToString("0") + "% during 20 s of load. That's throttling: check CPU cooler mounting, fans, dust, and case airflow.");
                    else if (ratio < 0.93) R(2, "Clock stability under load", v, "Clocks sagged " + ((1 - ratio) * 100).ToString("0") + "% under sustained load. Running warm or hitting a power limit; better cooling would help.");
                    else R(0, "Clock stability under load", v, "Clocks held steady for the whole stress test. Cooling is keeping up.");
                    if (peak > 0) R(1, "All-core vs boost clock", (last / peak * 100).ToString("0") + "% of single-core boost", hw.IsLaptop ? "Laptops typically sit at 60-85% under all-core load." : "Desktops typically hold 80-95% of their boost on all cores.");
                }
                if (lims.Count > 4)
                {
                    double lim = lims.Skip(lims.Count / 2).Average();
                    if (lim < 90) R(2, "Windows performance limit", lim.ToString("0") + "% of max", "Windows reports the CPU was being held back (power or thermal limits) during the stress test.");
                    else R(0, "Windows performance limit", lim.ToString("0") + "%", "No power or thermal limit reported by Windows.");
                }
                double tMax = temps.Count > 0 ? temps.Max() : double.NaN;
                if (!double.IsNaN(tMax) && tMax - (double.IsNaN(tz0) ? tMax : tz0) >= 3 && tMax > 35)
                {
                    int lim = hw.CpuTempLimit;
                    if (tMax >= lim - 3) R(3, "CPU temperature (thermal zone)", tMax.ToString("0") + " °C", "At the CPU's limit (" + lim + " °C). Check the cooler, thermal paste and fans.");
                    else if (tMax >= lim - 12) R(2, "CPU temperature (thermal zone)", tMax.ToString("0") + " °C", "Hot but within spec (limit " + lim + " °C)." + (hw.IsRyzen ? " Ryzen 7000/9000 chips are designed to run warm." : ""));
                    else R(0, "CPU temperature (thermal zone)", tMax.ToString("0") + " °C", "Comfortable under full load (limit " + lim + " °C). Motherboard sensor, so roughly ±5 °C.");
                }
                else R(1, "CPU temperature", "Not exposed by Windows", "This PC doesn't report a usable CPU temperature to Windows without a kernel driver. The clock-stability test above is the throttling check; HWiNFO shows exact temps.");
            }

            // ---- 3. Memory ----
            P("Checking memory...", .52f);
            double gb = hw.RamBytes / 1073741824.0;
            if (hw.RamCfg > 0)
            {
                if (hw.XmpLikelyOff && !hw.IsLaptop) R(2, "RAM speed", hw.RamType + "-" + hw.RamCfg + (hw.RamRated > hw.RamCfg ? " (rated " + hw.RamRated + ")" : ""), "Your RAM is probably running at its default speed. Enable XMP (Intel) or EXPO/DOCP (AMD) in the BIOS. It's often a big FPS gain on its own.");
                else if (hw.XmpLikelyOff) R(1, "RAM speed", hw.RamType + "-" + hw.RamCfg, "Running at standard speed. Most laptops can't change this.");
                else R(0, "RAM speed", hw.RamType + "-" + hw.RamCfg, "Running at a performance speed (XMP/EXPO looks enabled).");
            }
            if (hw.Sticks == 1 && !hw.IsLaptop) R(2, "Memory channels", "1 stick (single channel)", "A single RAM stick halves memory bandwidth. Two matching sticks (dual channel) noticeably help FPS in CPU-heavy games.");
            else if (hw.Sticks >= 2) R(0, "Memory channels", hw.Sticks + " sticks", "Dual channel (or better). Good.");
            if (gb < 15) R(2, "Memory amount", gb.ToString("0") + " GB", "16 GB is the comfortable minimum for modern games; 32 GB for heavy multitasking.");
            else R(0, "Memory amount", gb.ToString("0") + " GB", gb >= 31 ? "Plenty for gaming and streaming." : "Enough for most games.");

            P("Measuring memory bandwidth...", .58f);
            try
            {
                int th = Math.Max(2, Math.Min(8, Environment.ProcessorCount / 2)); var bytes = new double[th]; bool run2 = true; var sw2 = Stopwatch.StartNew();
                var tl = Enumerable.Range(0, th).Select(i => new Thread(() =>
                {
                    var a = new byte[32 << 20]; var b = new byte[32 << 20];
                    while (run2) { Buffer.BlockCopy(a, 0, b, 0, a.Length); bytes[i] += a.Length * 2.0; }
                }) { IsBackground = true }).ToList();
                tl.ForEach(t => t.Start()); Thread.Sleep(3000); run2 = false; tl.ForEach(t => t.Join());
                double gbs = bytes.Sum() / sw2.Elapsed.TotalSeconds / 1e9;
                if (hw.RamCfg > 0)
                {
                    double theo = hw.RamCfg * 8.0 * Math.Max(1, Math.Min(2, hw.Sticks)) / 1000.0;
                    if (gbs < theo * 0.3) R(2, "Memory bandwidth", gbs.ToString("0.0") + " GB/s", "Low for " + hw.RamType + "-" + hw.RamCfg + " (theoretical " + theo.ToString("0") + " GB/s). Background load during the test, or RAM in the wrong slots.");
                    else R(0, "Memory bandwidth", gbs.ToString("0.0") + " GB/s", "Normal for " + hw.RamType + "-" + hw.RamCfg + " (theoretical max " + theo.ToString("0") + " GB/s).");
                }
                else R(1, "Memory bandwidth", gbs.ToString("0.0") + " GB/s", "Measured copy speed.");
            }
            catch { }
            GC.Collect();

            // ---- 4. Storage ----
            P("Testing drive speed (1 GB)...", .64f);
            var sd = hw.SysDisk;
            try
            {
                var drv = new DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
                double free = drv.AvailableFreeSpace / (double)drv.TotalSize;
                if (free < 0.12) R(2, "Free space on " + drv.Name.TrimEnd('\\'), (free * 100).ToString("0") + "% free", "Under ~15% free, SSDs slow down and Windows updates/shader caches can fail. Clear some space.");
                else R(0, "Free space on " + drv.Name.TrimEnd('\\'), (free * 100).ToString("0") + "% free (" + (drv.AvailableFreeSpace / 1073741824.0).ToString("0") + " GB)", "Plenty of room.");
            }
            catch { }
            double[] disk = DiskTest.Run(f => P("Testing drive speed (1 GB)...", .64f + .16f * f));
            if (disk != null)
            {
                string type = sd != null ? sd.Type : "Drive"; double floor = type.StartsWith("NVMe") ? 1500 : type.Contains("SSD") ? 350 : type.StartsWith("Hard") ? 80 : 0;
                string v = "Read " + disk[0].ToString("N0") + " MB/s · Write " + disk[1].ToString("N0") + " MB/s";
                if (floor > 0 && disk[0] < floor * 0.5) R(3, "System drive (" + type + ")", v, "Much slower than a healthy " + type + " (usually " + floor.ToString("N0") + "+ MB/s). Check the drive's health (CrystalDiskInfo), free space, and that it's in a full-speed slot.");
                else if (floor > 0 && disk[0] < floor) R(2, "System drive (" + type + ")", v, "A bit slow for a " + type + ". Can happen when it's nearly full or busy.");
                else R(0, "System drive (" + type + ")", v, type.StartsWith("Hard") ? "Normal for a hard drive, but games load far faster from an SSD." : "Healthy speed for this kind of drive.");
            }

            // ---- 5. GPU ----
            P("Checking graphics card...", .82f);
            if (hw.IsNvidia && Nv.Available)
            {
                var d = Nv.Query("temperature.gpu,utilization.gpu,pcie.link.gen.current,pcie.link.gen.max,pcie.link.width.current,pcie.link.width.max,driver_version,power.limit,fan.speed");
                if (d != null)
                {
                    double t = Nv.Num(d, "temperature.gpu"), u = Nv.Num(d, "utilization.gpu");
                    if (u < 25)
                    {
                        if (t > 60) R(3, "GPU idle temperature", t + " °C", "Very warm while idle. Check for dust, case airflow, or something running on the GPU in the background.");
                        else if (t > 52) R(2, "GPU idle temperature", t + " °C", "A little warm for idle. Normal if the fans are stopped (zero-RPM mode), otherwise check airflow.");
                        else R(0, "GPU idle temperature", t + " °C", "Normal idle temperature (30-50 °C).");
                    }
                    else R(1, "GPU temperature", t + " °C at " + u + "% load", "Use the Gaming session monitor below for a proper under-load heat check.");
                    double wc = Nv.Num(d, "pcie.link.width.current"), wm = Nv.Num(d, "pcie.link.width.max");
                    if (!double.IsNaN(wc) && !double.IsNaN(wm))
                    {
                        if (wc < wm && !hw.IsLaptop) R(2, "PCIe slot", "x" + wc + " of x" + wm, "The GPU is running with fewer lanes than it supports. Make sure it's in the top full-length slot and fully seated.");
                        else R(0, "PCIe slot", "x" + wc + " lanes, Gen " + d["pcie.link.gen.max"] + " max", "Connected at full width. (The PCIe generation drops at idle to save power; that's normal.)");
                    }
                    R(1, "NVIDIA driver", d["driver_version"], "Keep this updated through the NVIDIA App for the best game performance.");
                }
                var th = Nv.Throttle();
                if (th != null && th.Any(kv => kv.Key.Contains("thermal") && kv.Value.StartsWith("Active")))
                {
                    double tnow = d != null ? Nv.Num(d, "temperature.gpu") : double.NaN;
                    if (!double.IsNaN(tnow) && tnow >= hw.GpuTempLimit - 12) R(3, "GPU thermal throttling", "Active at " + tnow + " °C", "The GPU is slowing itself down because it's too hot right now.");
                    else R(1, "Brief thermal flag", "core " + (double.IsNaN(tnow) ? "?" : tnow + " °C"), "The driver reported a momentary heat flag while temperatures were normal. Not a concern on its own.");
                }
            }
            else if (!hw.IsNvidia) R(1, "Graphics card", hw.GpuName, "Detailed GPU temperature checks are available for NVIDIA cards.");
            else R(1, "Graphics card", hw.GpuName, "nvidia-smi wasn't found; reinstall the NVIDIA driver to enable GPU health checks.");

            // ---- 6. Power plan ----
            try
            {
                var psi = new ProcessStartInfo("powercfg", "/getactivescheme") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                string o; using (var p = Process.Start(psi)) { o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
                int a = o.IndexOf('('), b = o.LastIndexOf(')'); string name = a > 0 && b > a ? o.Substring(a + 1, b - a - 1) : "Unknown";
                if (name.IndexOf("saver", StringComparison.OrdinalIgnoreCase) >= 0) R(3, "Power plan", name, "Power saver caps your CPU. Switch it in Performance > Ultimate / High Performance.");
                else if (name.IndexOf("Balanced", StringComparison.OrdinalIgnoreCase) >= 0) R(1, "Power plan", name, "Fine for everyday use. For gaming, the Ultimate Performance toggle reduces clock drops between loads.");
                else R(0, "Power plan", name, "Set up for performance.");
            }
            catch { }
            P("Done", 1f);
        }
    }

    static class DiskTest
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateFile(string n, uint acc, uint share, IntPtr sec, uint disp, uint flags, IntPtr t);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(IntPtr h, IntPtr buf, uint n, out uint done, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(IntPtr h, IntPtr buf, uint n, out uint done, IntPtr ov);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern IntPtr VirtualAlloc(IntPtr a, UIntPtr size, uint type, uint prot);
        [DllImport("kernel32.dll")] static extern bool VirtualFree(IntPtr a, UIntPtr size, uint type);
        const uint NO_BUFFERING = 0x20000000, WRITE_THROUGH = 0x80000000, SEQ = 0x08000000;

        // Unbuffered 1 GB sequential write + read on the system drive. Returns {readMBs, writeMBs}.
        public static double[] Run(Action<float> prog)
        {
            string file = Path.Combine(Path.GetTempPath(), "gameboost_disktest.tmp");
            const int block = 8 << 20; const int blocks = 128; // 1 GB
            IntPtr buf = VirtualAlloc(IntPtr.Zero, (UIntPtr)block, 0x3000, 0x04);
            if (buf == IntPtr.Zero) return null;
            try
            {
                var rnd = new byte[block]; new Random(1).NextBytes(rnd); Marshal.Copy(rnd, 0, buf, block);
                IntPtr h = CreateFile(file, 0x40000000, 0, IntPtr.Zero, 2, NO_BUFFERING | WRITE_THROUGH | SEQ, IntPtr.Zero);
                if (h == new IntPtr(-1)) return null;
                var sw = Stopwatch.StartNew(); uint done;
                for (int i = 0; i < blocks; i++) { if (!WriteFile(h, buf, block, out done, IntPtr.Zero)) { CloseHandle(h); return null; } prog(i / (float)blocks * .5f); }
                double w = 1024.0 / sw.Elapsed.TotalSeconds; CloseHandle(h);
                h = CreateFile(file, 0x80000000, 0, IntPtr.Zero, 3, NO_BUFFERING | SEQ, IntPtr.Zero);
                if (h == new IntPtr(-1)) return null;
                sw.Restart();
                for (int i = 0; i < blocks; i++) { if (!ReadFile(h, buf, block, out done, IntPtr.Zero) || done == 0) break; prog(.5f + i / (float)blocks * .5f); }
                double r = 1024.0 / sw.Elapsed.TotalSeconds; CloseHandle(h);
                return new[] { r, w };
            }
            catch { return null; }
            finally { VirtualFree(buf, UIntPtr.Zero, 0x8000); try { File.Delete(file); } catch { } }
        }
    }

    // Records GPU/CPU behaviour while you actually play
    class SessionMonitor
    {
        public class Sample { public double GpuT = double.NaN, GpuU = double.NaN, GpuClk = double.NaN, GpuMax = double.NaN, GpuW = double.NaN, GpuLim = double.NaN, Fan = double.NaN, CpuU = double.NaN, CpuMHz = double.NaN, CpuLim = double.NaN, CpuT = double.NaN; public bool GpuThermal; }
        public event Action<Sample> OnSample;
        public List<Sample> Samples = new List<Sample>();
        volatile bool running; Thread th; HwInfo hw;
        public bool Running { get { return running; } }
        public SessionMonitor(HwInfo h) { hw = h; }
        public void Start()
        {
            if (running) return; running = true; Samples.Clear();
            th = new Thread(() =>
            {
                using (var clk = new CpuClocks(hw.BaseMHz))
                {
                    int i = 0;
                    while (running)
                    {
                        var sw = Stopwatch.StartNew(); var s = new Sample();
                        if (hw.IsNvidia)
                        {
                            var d = Nv.Query(Nv.Live);
                            if (d != null) { s.GpuT = Nv.Num(d, "temperature.gpu"); s.GpuU = Nv.Num(d, "utilization.gpu"); s.GpuClk = Nv.Num(d, "clocks.sm"); s.GpuMax = Nv.Num(d, "clocks.max.sm"); s.GpuW = Nv.Num(d, "power.draw"); s.GpuLim = Nv.Num(d, "power.limit"); s.Fan = Nv.Num(d, "fan.speed"); }
                            if (i++ % 3 == 0) { var t = Nv.Throttle(); if (t != null) s.GpuThermal = t.Any(kv => kv.Key.Contains("thermal") && kv.Value.StartsWith("Active")); }
                        }
                        var c = clk.Sample(); s.CpuMHz = c[1]; s.CpuLim = c[2]; s.CpuU = c[3]; s.CpuT = ThermalZone.MaxC();
                        lock (Samples) Samples.Add(s);
                        if (OnSample != null) OnSample(s);
                        int wait = 1000 - (int)sw.ElapsedMilliseconds; if (wait > 0) Thread.Sleep(wait);
                    }
                }
            }) { IsBackground = true };
            th.Start();
        }
        public void Stop() { running = false; }

        public List<Result> Verdict(double cpuPeakMHz)
        {
            var res = new List<Result>(); List<Sample> s; lock (Samples) s = Samples.ToList();
            Action<int, string, string, string> R = (l, t, v, d) => res.Add(new Result { Level = l, Title = t, Value = v, Detail = d });
            if (s.Count < 20) { R(1, "Session too short", s.Count + " s recorded", "Record at least a minute or two of real gameplay for a reliable result."); return res; }
            var g = s.Where(x => !double.IsNaN(x.GpuT)).ToList();
            if (g.Count > 0)
            {
                double max = g.Max(x => x.GpuT), avg = g.Average(x => x.GpuT); int lim = hw.GpuTempLimit; double maxU = g.Max(x => double.IsNaN(x.GpuU) ? 0 : x.GpuU);
                string v = "max " + max.ToString("0") + " °C · avg " + avg.ToString("0") + " °C";
                if (max >= lim - 2) R(3, "GPU heat while gaming", v, "Hitting the card's limit (" + lim + " °C), so it will throttle. Clean dust, improve case airflow, or set a more aggressive fan curve.");
                else if (max > 85) R(2, "GPU heat while gaming", v, "Running warm (normal gaming is 65-85 °C, limit " + lim + " °C). Safe, but more airflow would keep boost clocks higher.");
                else if (max > 80) R(0, "GPU heat while gaming", v, "Normal gaming temperature (65-85 °C).");
                else R(0, "GPU heat while gaming", v, "Excellent: cool under load.");
                int thr = g.Count(x => x.GpuThermal); double thrPct = thr * 100.0 / g.Count;
                if (thr >= 3 && max >= lim - 12) R(3, "GPU thermal throttling", thr + " checks (" + thrPct.ToString("0") + "% of session)", "The GPU repeatedly slowed itself down from heat while near its limit. Improve airflow or clean dust.");
                else if (thr >= 3) R(2, "GPU thermal slowdown flag", thr + " checks at " + max.ToString("0") + " °C", "The driver flagged heat slowdown several times while the core was cool. Usually a hot spot on the video memory or power circuitry; worth checking case airflow over the card.");
                else if (thr > 0) R(1, "Brief thermal flag", "seen " + thr + "x, core " + max.ToString("0") + " °C", "The driver raised a momentary heat flag, but temperatures stayed normal. This is common and not a concern.");
                var loaded = g.Where(x => x.GpuU >= 85 && !double.IsNaN(x.GpuClk)).ToList();
                if (loaded.Count >= 10)
                {
                    double clk = loaded.Average(x => x.GpuClk), mx = loaded.Max(x => x.GpuMax);
                    double pct = clk / mx;
                    if (!double.IsNaN(pct) && pct < 0.75) R(2, "GPU clocks under load", clk.ToString("N0") + " of " + mx.ToString("N0") + " MHz", "Lower than expected under heavy load. Usually heat or a power limit.");
                    else R(0, "GPU clocks under load", clk.ToString("N0") + " MHz avg", "Boosting normally while gaming.");
                }
                if (maxU < 60) R(1, "GPU load", "peaked at " + maxU.ToString("0") + "%", "The GPU wasn't working hard: the game is CPU-limited, light, or FPS-capped. Totally fine, but the heat check is less conclusive.");
                var fans = g.Where(x => !double.IsNaN(x.Fan)).ToList();
                if (fans.Count > 0 && max > 75 && fans.Max(x => x.Fan) < 5) R(3, "GPU fans", "0% at " + max.ToString("0") + " °C", "The fans never spun up while the GPU was hot. Check that they work.");
            }
            var c = s.Where(x => !double.IsNaN(x.CpuMHz)).ToList();
            if (c.Count > 0)
            {
                double busy = c.Where(x => x.CpuU > 30).Select(x => x.CpuMHz).DefaultIfEmpty(c.Average(x => x.CpuMHz)).Average();
                R(1, "CPU clock while gaming", (busy / 1000).ToString("0.00") + " GHz avg" + (cpuPeakMHz > 0 ? " (boost " + (cpuPeakMHz / 1000).ToString("0.00") + ")" : ""), "Average effective clock while the CPU was busy.");
                var lims = c.Where(x => !double.IsNaN(x.CpuLim)).ToList();
                if (lims.Count > 0 && lims.Average(x => x.CpuLim) < 90) R(2, "CPU limited while gaming", lims.Average(x => x.CpuLim).ToString("0") + "%", "Windows reported the CPU being held back by power or heat during your session.");
            }
            var ct = s.Where(x => !double.IsNaN(x.CpuT)).ToList();
            if (ct.Count > 0 && ct.Max(x => x.CpuT) - ct.Min(x => x.CpuT) >= 3)
            {
                double mx = ct.Max(x => x.CpuT); int lim = hw.CpuTempLimit;
                if (mx >= lim - 3) R(3, "CPU heat while gaming", mx.ToString("0") + " °C (thermal zone)", "At the CPU's limit while gaming. Check the cooler and airflow.");
                else if (mx > 85) R(2, "CPU heat while gaming", mx.ToString("0") + " °C (thermal zone)", "Warm for gaming (60-85 °C is typical).");
                else R(0, "CPU heat while gaming", mx.ToString("0") + " °C (thermal zone)", "Normal gaming temperature.");
            }
            return res;
        }
    }
}
