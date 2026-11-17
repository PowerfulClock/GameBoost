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
        static readonly long Freq = Stopwatch.Frequency;
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
                    while (q.Count > 0 && ts - q.Peek() > Freq * 2) q.Dequeue();
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

    // Click-through, see-through FPS counter in the top-right corner.
    class OverlayForm : Form
    {
        public Func<int> TargetPid = () => -1;
        string text = "FPS --"; double shown;
        System.Windows.Forms.Timer t;
        Font font = new Font("Segoe UI Semibold", 20f);

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            BackColor = Color.Magenta; TransparencyKey = Color.Magenta;
            StartPosition = FormStartPosition.Manual; Size = new Size(200, 44);
            DoubleBuffered = true;
            Reposition();
            t = new System.Windows.Forms.Timer { Interval = 100 };
            t.Tick += (s, e) =>
            {
                int used; int pid = TargetPid();
                if (pid <= 0) pid = Native.ForegroundPid();
                int fps = FpsTracker.Fps(pid, out used);
                if (fps == 0) fps = FpsTracker.Fps(-1, out used); // nothing from target: use whatever is rendering
                shown = shown <= 0 || Math.Abs(fps - shown) > 30 ? fps : shown + (fps - shown) * 0.35;
                string nt = FpsTracker.Running ? "FPS " + (int)Math.Round(shown) : "FPS n/a";
                if (nt != text) { text = nt; Invalidate(); }
                TopMost = true; Reposition();
            };
            t.Start();
        }

        void Reposition() { var wa = Screen.PrimaryScreen.Bounds; var loc = new Point(wa.Right - Width - 12, wa.Top + 8); if (Location != loc) Location = loc; }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8; return cp; } // layered, click-through, toolwindow, noactivate, topmost
        }
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit; // crisp edges with color-key transparency
            var sz = g.MeasureString(text, font);
            float x = Width - sz.Width - 2, y = 2;
            using (var shadow = new SolidBrush(Color.FromArgb(20, 20, 20)))
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) if (dx != 0 || dy != 0) g.DrawString(text, font, shadow, x + dx, y + dy);
            g.DrawString(text, font, Brushes.White, x, y);
        }

        protected override void Dispose(bool d) { if (d) { t.Dispose(); font.Dispose(); } base.Dispose(d); }
    }
}
