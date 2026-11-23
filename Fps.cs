using System.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace GameBoost
{
    // Counts frames per process using Windows' DXGI / D3D9 "Present" ETW events (same source PresentMon uses).
    static class FpsTracker
    {
        const string SESSION = "GameBoostFPS";
        static readonly Guid DXGI = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
        static readonly Guid D3D9 = new Guid("783ACA0A-790E-4D7F-8451-AA850511C6B9");

        [StructLayout(LayoutKind.Sequential)] struct EVENT_TRACE_HEADER { public ushort Size, FieldTypeFlags; public uint Version, ThreadId, ProcessId; public long TimeStamp; public Guid Guid; public ulong ProcTime; }
        [StructLayout(LayoutKind.Sequential)] struct EVENT_TRACE { public EVENT_TRACE_HEADER Header; public uint InstanceId, ParentInstanceId; public Guid ParentGuid; public IntPtr MofData; public uint MofLength, ClientContext; }
        [StructLayout(LayoutKind.Sequential)] struct SYSTEMTIME { public ushort a, b, c, d, e, f, g, h; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct TZI { public int Bias; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string StdName; public SYSTEMTIME StdDate; public int StdBias; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DayName; public SYSTEMTIME DayDate; public int DayBias; }
        [StructLayout(LayoutKind.Sequential)] struct TRACE_LOGFILE_HEADER { public uint BufferSize, Version, ProviderVersion, NumberOfProcessors; public long EndTime; public uint TimerResolution, MaximumFileSize, LogFileMode, BuffersWritten; public Guid LogInstanceGuid; public IntPtr LoggerName, LogFileName; public TZI TimeZone; public long BootTime, PerfFreq, StartTime; public uint ReservedFlags, BuffersLost; }
        delegate void EventCb(IntPtr rec);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct EVENT_TRACE_LOGFILE { public string LogFileName; public string LoggerName; public long CurrentTime; public uint BuffersRead, ProcessTraceMode; public EVENT_TRACE CurrentEvent; public TRACE_LOGFILE_HEADER LogfileHeader; public IntPtr BufferCallback; public uint BufferSize, Filled, EventsLost; public EventCb Callback; public uint IsKernelTrace; public IntPtr Context; }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint StartTrace(out ulong h, string name, IntPtr props);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint ControlTrace(ulong h, string name, IntPtr props, uint code);
        [DllImport("advapi32.dll")] static extern uint EnableTraceEx2(ulong h, ref Guid g, uint code, byte level, ulong any, ulong all, uint timeout, IntPtr p);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern ulong OpenTrace(ref EVENT_TRACE_LOGFILE lf);
        [DllImport("advapi32.dll")] static extern uint ProcessTrace(ulong[] h, uint n, IntPtr s, IntPtr e);
        [DllImport("advapi32.dll")] static extern uint CloseTrace(ulong h);

        static EventCb cbKeep; static ulong session, consumer; static Thread thread;
        static readonly object lk = new object();
        // pid -> swapchain -> frame timestamps (QPC ticks, taken from the event itself, not arrival time)
        static Dictionary<int, Dictionary<long, Queue<long>>> frames = new Dictionary<int, Dictionary<long, Queue<long>>>();
        public static readonly long Freq = Stopwatch.Frequency;
        public static bool Running; public static string Error;

        static IntPtr Props()
        {
            int size = 120 + 1024; IntPtr p = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt32(p, 0, size); Marshal.WriteInt32(p, 40, 1); Marshal.WriteInt32(p, 44, 0x20000);
            Marshal.WriteInt32(p, 48, 64); Marshal.WriteInt32(p, 64, 0x100); Marshal.WriteInt32(p, 68, 1);
            Marshal.WriteInt32(p, 116, 120);
            return p;
        }

        public static bool Start()
        {
            if (Running) return true;
            Error = null;
            if (IntPtr.Size != 8) { Error = "Needs 64-bit Windows"; return false; }
            IntPtr p = Props();
            ControlTrace(0, SESSION, p, 1);
            Marshal.FreeHGlobal(p); p = Props();
            uint r = StartTrace(out session, SESSION, p);
            Marshal.FreeHGlobal(p);
            if (r != 0) { Error = "StartTrace failed (" + r + "). Run as administrator."; return false; }
            Guid g1 = DXGI, g2 = D3D9;
            EnableTraceEx2(session, ref g1, 1, 4, 0, 0, 0, IntPtr.Zero);
            EnableTraceEx2(session, ref g2, 1, 4, 0, 0, 0, IntPtr.Zero);
            cbKeep = OnEvent;
            var lf = new EVENT_TRACE_LOGFILE { LoggerName = SESSION, ProcessTraceMode = 0x100 | 0x10000000, Callback = cbKeep };
            consumer = OpenTrace(ref lf);
            if (consumer == ulong.MaxValue) { Error = "OpenTrace failed"; Stop(); return false; }
            thread = new Thread(() => { try { ProcessTrace(new[] { consumer }, 1, IntPtr.Zero, IntPtr.Zero); } catch { } }) { IsBackground = true };
            thread.Start();
            lock (lk) frames.Clear();
            Running = true; return true;
        }

        public static void Stop()
        {
            try { if (consumer != 0 && consumer != ulong.MaxValue) CloseTrace(consumer); } catch { }
            IntPtr p = Props(); ControlTrace(0, SESSION, p, 1); Marshal.FreeHGlobal(p);
            consumer = 0; Running = false;
        }

        static void OnEvent(IntPtr rec)
        {
            try
            {
                int pid = Marshal.ReadInt32(rec, 12);
                long ts = Marshal.ReadInt64(rec, 16);           // QPC timestamp of the actual Present call
                var prov = new Guid(ReadBytes(rec, 24, 16));
                int id = Marshal.ReadInt16(rec, 40);
                long swap = 0;
                if (prov == DXGI && id == 42)
                {
                    int len = Marshal.ReadInt16(rec, 86); IntPtr ud = Marshal.ReadIntPtr(rec, 96);
                    if (ud != IntPtr.Zero && len >= 12)
                    {
                        swap = Marshal.ReadInt64(ud);
                        int flags = Marshal.ReadInt32(ud, 8);
                        if ((flags & 0x1) != 0) return;        // DXGI_PRESENT_TEST: not a real frame
                    }
                }
                else if (!(prov == D3D9 && id == 1)) return;
                lock (lk)
                {
                    Dictionary<long, Queue<long>> chains;
                    if (!frames.TryGetValue(pid, out chains)) frames[pid] = chains = new Dictionary<long, Queue<long>>();
                    Queue<long> q; if (!chains.TryGetValue(swap, out q)) chains[swap] = q = new Queue<long>();
                    q.Enqueue(ts);
                    while (q.Count > 0 && ts - q.Peek() > Freq * 10) q.Dequeue();
                }
            }
            catch { }
        }
        static byte[] ReadBytes(IntPtr p, int off, int n) { var b = new byte[n]; Marshal.Copy(p + off, b, 0, n); return b; }

        // Average FPS over the last second of frames on the process's main swapchain.
        static double ChainFps(Queue<long> q, long now)
        {
            if (q.Count < 2) return 0;
            var arr = q.ToArray(); long last = arr[arr.Length - 1];
            if (now - last > Freq * 3) return 0;               // stopped rendering
            int first = arr.Length - 1;
            while (first > 0 && last - arr[first - 1] <= Freq) first--;
            int n = arr.Length - 1 - first; if (n < 1) return 0;
            double secs = (last - arr[first]) / (double)Freq; if (secs <= 0) return 0;
            return n / secs;
        }

        static double ProcFps(Dictionary<long, Queue<long>> chains, long now)
        {
            double best = 0; foreach (var q in chains.Values) { double f = ChainFps(q, now); if (f > best) best = f; }
            return best;
        }

        // Raw frame timestamps of the busiest swapchain of a process (last ~10 s)
        public static long[] Stamps(int pid)
        {
            lock (lk)
            {
                Dictionary<long, Queue<long>> chains; if (!frames.TryGetValue(pid, out chains)) return new long[0];
                Queue<long> best = null; long now = Stopwatch.GetTimestamp();
                foreach (var q in chains.Values) { int recent = q.Count(t => now - t < Freq); if (best == null || recent > best.Count(t => now - t < Freq)) best = q; }
                return best == null ? new long[0] : best.ToArray();
            }
        }

        // pid > 0: that process. Otherwise the busiest presenting process.
        public static int Fps(int pid, out int usedPid)
        {
            long now = Stopwatch.GetTimestamp(); usedPid = pid;
            lock (lk)
            {
                Dictionary<long, Queue<long>> chains;
                if (pid > 0) return frames.TryGetValue(pid, out chains) ? (int)Math.Round(ProcFps(chains, now)) : 0;
                double best = 0;
                foreach (var kv in frames) { double f = ProcFps(kv.Value, now); if (f > best) { best = f; usedPid = kv.Key; } }
                return (int)Math.Round(best);
            }
        }
    }

    // Click-through, see-through performance overlay in the top-right corner.
    class OverlayForm : Form
    {
        public Func<int> TargetPid = () => -1;
        System.Windows.Forms.Timer t, st; Font big = new Font("Segoe UI", 22f, FontStyle.Bold), mid = new Font("Segoe UI Semibold", 10.5f), small = new Font("Segoe UI Semibold", 9f);
        double shown; int fpsNow, low1; double ftNow; int pidUsed = -1;
        List<double> ft = new List<double>(); List<bool> spikeFlags = new List<bool>();
        List<DateTime> spikes = new List<DateTime>(); DateTime lastSpike = DateTime.MinValue; double lastSpikeMs; long lastStamp;
        string[] statLines = new string[0]; Color gpuCol = Color.White, cpuCol = Color.White; bool statBusy;
        PerformanceCounter cpuCtr; DateTime lastCpuT = DateTime.MinValue; double cpuT = double.NaN;
        static readonly Color Key = Color.FromArgb(1, 0, 1), Red = Color.FromArgb(255, 80, 80), Org = Color.FromArgb(255, 176, 64), Grn = Color.FromArgb(130, 230, 70), Gry = Color.FromArgb(200, 205, 212);

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            BackColor = Key; TransparencyKey = Key; StartPosition = FormStartPosition.Manual; Size = new Size(250, 230); DoubleBuffered = true;
            try { cpuCtr = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); cpuCtr.NextValue(); } catch { }
            Reposition();
            t = new System.Windows.Forms.Timer { Interval = 100 }; t.Tick += (s, e) => { Frame(); Invalidate(); TopMost = true; Reposition(); }; t.Start();
            st = new System.Windows.Forms.Timer { Interval = 1000 }; st.Tick += (s, e) => Stats(); st.Start(); Stats();
        }

        void Frame()
        {
            int used; int pid = TargetPid(); if (pid <= 0) pid = Native.ForegroundPid();
            int fps = FpsTracker.Fps(pid, out used); if (fps == 0) { fps = FpsTracker.Fps(-1, out used); } else used = pid;
            pidUsed = used;
            shown = shown <= 0 || Math.Abs(fps - shown) > 30 ? fps : shown + (fps - shown) * 0.35; fpsNow = (int)Math.Round(shown);
            var st = used > 0 ? FpsTracker.Stamps(used) : new long[0];
            if (st.Length < 3) { ft.Clear(); spikeFlags.Clear(); low1 = 0; ftNow = 0; return; }
            var d = new double[st.Length - 1]; for (int i = 1; i < st.Length; i++) d[i - 1] = (st[i] - st[i - 1]) * 1000.0 / FpsTracker.Freq;
            var sorted = d.OrderBy(x => x).ToArray(); double med = sorted[sorted.Length / 2];
            int n1 = Math.Max(1, d.Length / 100); low1 = (int)Math.Round(1000.0 / sorted.Skip(sorted.Length - n1).Average());
            ftNow = d.Skip(Math.Max(0, d.Length - 10)).Average();
            // spikes: new frames that took much longer than usual
            for (int i = 1; i < st.Length; i++)
            {
                if (st[i] <= lastStamp) continue;
                double x = d[i - 1]; if (x > med * 2.2 && x > med + 8) { spikes.Add(DateTime.Now); lastSpike = DateTime.Now; lastSpikeMs = x; }
            }
            lastStamp = st[st.Length - 1];
            spikes.RemoveAll(x => (DateTime.Now - x).TotalSeconds > 60);
            int take = Math.Min(100, d.Length); ft = d.Skip(d.Length - take).ToList(); spikeFlags = ft.Select(x => x > med * 2.2 && x > med + 8).ToList();
            tag = med;
        }
        double tag;

        void Stats()
        {
            if (statBusy) return; statBusy = true;
            new System.Threading.Thread(() =>
            {
                var lines = new List<string>(); Color gc = Gry, cc = Gry;
                var d = Nv.Available ? Nv.Query("temperature.gpu,utilization.gpu,clocks.sm,memory.used,memory.total,power.draw") : null;
                if (d != null)
                {
                    double gt = Nv.Num(d, "temperature.gpu");
                    lines.Add("GPU  " + F(gt, "°C") + "   " + F(Nv.Num(d, "utilization.gpu"), "%") + "   " + F(Nv.Num(d, "clocks.sm"), " MHz"));
                    lines.Add("VRAM " + (Nv.Num(d, "memory.used") / 1024).ToString("0.0") + "/" + (Nv.Num(d, "memory.total") / 1024).ToString("0") + " GB   " + F(Nv.Num(d, "power.draw"), " W"));
                    gc = gt >= 85 ? Red : gt >= 78 ? Org : Gry;
                }
                double cu = double.NaN; try { if (cpuCtr != null) cu = Math.Min(100, cpuCtr.NextValue()); } catch { }
                if ((DateTime.Now - lastCpuT).TotalSeconds > 5) { cpuT = ThermalZone.MaxC(); lastCpuT = DateTime.Now; }
                lines.Add("CPU  " + F(cu, "%") + (double.IsNaN(cpuT) ? "" : "   " + F(cpuT, "°C")));
                cc = !double.IsNaN(cpuT) && cpuT >= 90 ? Red : Gry;
                try
                {
                    var ci = new Microsoft.VisualBasic.Devices.ComputerInfo();
                    double tot = ci.TotalPhysicalMemory / 1073741824.0, av = ci.AvailablePhysicalMemory / 1073741824.0;
                    lines.Add("RAM  " + (tot - av).ToString("0.0") + "/" + tot.ToString("0") + " GB");
                }
                catch { }
                try { BeginInvoke((Action)(() => { statLines = lines.ToArray(); gpuCol = gc; cpuCol = cc; statBusy = false; })); } catch { statBusy = false; }
            }) { IsBackground = true }.Start();
        }
        static string F(double v, string u) { return double.IsNaN(v) ? "-" : v.ToString("0") + u; }

        void Reposition() { var wa = Screen.PrimaryScreen.Bounds; var loc = new Point(wa.Right - Width - 10, wa.Top + 8); if (Location != loc) Location = loc; }
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8; return cp; } }
        protected override bool ShowWithoutActivation { get { return true; } }

        void Txt(Graphics g, string s, Font f, Color c, float right, float y)
        {
            var sz = g.MeasureString(s, f); float x = right - sz.Width;
            using (var sh = new SolidBrush(Color.FromArgb(12, 12, 12)))
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) if (dx != 0 || dy != 0) g.DrawString(s, f, sh, x + dx, y + dy);
            using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, y);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit; g.SmoothingMode = SmoothingMode.None;
            float R = Width - 4, y = 0;
            bool active = FpsTracker.Running && fpsNow > 0;
            bool spikeNow = (DateTime.Now - lastSpike).TotalSeconds < 1.5;
            Txt(g, active ? "FPS " + fpsNow : "FPS --", big, spikeNow ? Red : Color.White, R, y); y += 38;
            if (active) { Txt(g, "1% low " + low1 + "   " + ftNow.ToString("0.0") + " ms", small, low1 < fpsNow * 0.6 ? Org : Gry, R, y); y += 18; }
            // frametime graph
            if (ft.Count > 5)
            {
                float gw = 170, gh = 26, gx = R - gw, gy = y + 2;
                double scale = Math.Max(tag * 3, ft.Max()); scale = Math.Max(scale, 8);
                using (var p = new Pen(Color.FromArgb(12, 12, 12), 1)) g.DrawRectangle(p, gx - 1, gy - 1, gw + 1, gh + 1);
                float bw = gw / 100f;
                for (int i = 0; i < ft.Count; i++)
                {
                    float h = (float)Math.Max(1, Math.Min(gh, ft[i] / scale * gh)); float x = gx + gw - (ft.Count - i) * bw;
                    using (var b = new SolidBrush(spikeFlags[i] ? Red : Color.FromArgb(150, 210, 110))) g.FillRectangle(b, x, gy + gh - h, Math.Max(1, bw), h);
                }
                y += gh + 8;
            }
            if (spikeNow) { Txt(g, "▼ STUTTER " + lastSpikeMs.ToString("0") + " ms", small, Red, R, y); y += 17; }
            else if (spikes.Count > 0) { Txt(g, spikes.Count + " stutter" + (spikes.Count == 1 ? "" : "s") + " in last min", small, Org, R, y); y += 17; }
            for (int i = 0; i < statLines.Length; i++)
            {
                Color c = statLines[i].StartsWith("GPU") ? gpuCol : statLines[i].StartsWith("CPU") ? cpuCol : Gry;
                Txt(g, statLines[i], small, c, R, y); y += 17;
            }
        }

        protected override void Dispose(bool d) { if (d) { t.Dispose(); st.Dispose(); big.Dispose(); mid.Dispose(); small.Dispose(); if (cpuCtr != null) cpuCtr.Dispose(); } base.Dispose(d); }
    }
}
