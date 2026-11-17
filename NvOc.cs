using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GameBoost
{
    // NVIDIA GPU clock offsets through the driver's NVAPI (same mechanism MSI Afterburner uses).
    // Offsets are applied by the driver within its own allowed ranges and reset on every reboot.
    static class NvOc
    {
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr QI(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_Init();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_Enum([Out] IntPtr[] h, out int n);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_P20(IntPtr gpu, IntPtr info);

        static D_P20 getP, setP; static IntPtr gpu; static bool tried, ok;
        public static string Error = "";
        const int CLOCK = 44, PSTATE = 456, HDR = 20, V1 = HDR + 16 * PSTATE, V2 = V1 + 4 + 4 * 24;
        public const int DOM_CORE = 0, DOM_MEM = 4;

        public class Clk { public bool Found, Editable; public int DeltaKHz, MinKHz, MaxKHz, BaseKHz; }

        static T F<T>(uint id) where T : class { var p = QI(id); return p == IntPtr.Zero ? null : (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T)); }

        public static bool Init()
        {
            if (tried) return ok; tried = true;
            try
            {
                var init = F<D_Init>(0x0150E828); var en = F<D_Enum>(0xE5AC921F);
                getP = F<D_P20>(0x6FF81213); setP = F<D_P20>(0x0F4DAE6B);
                if (init == null || en == null || getP == null || setP == null) { Error = "NVAPI functions unavailable"; return false; }
                if (init() != 0) { Error = "NVAPI init failed"; return false; }
                var hs = new IntPtr[64]; int n; if (en(hs, out n) != 0 || n < 1) { Error = "No NVIDIA GPU found"; return false; }
                gpu = hs[0]; ok = Read(DOM_CORE).Found; if (!ok) Error = "Driver didn't report editable clocks";
                return ok;
            }
            catch (DllNotFoundException) { Error = "NVIDIA driver (nvapi64.dll) not found"; return false; }
            catch (Exception ex) { Error = ex.Message; return false; }
        }

        public static Clk Read(int domain)
        {
            var c = new Clk(); IntPtr b = Marshal.AllocHGlobal(V2);
            try
            {
                for (int i = 0; i < V2; i++) Marshal.WriteByte(b, i, 0);
                Marshal.WriteInt32(b, 0, V2 | (2 << 16));
                if (getP(gpu, b) != 0) { for (int i = 0; i < V2; i++) Marshal.WriteByte(b, i, 0); Marshal.WriteInt32(b, 0, V1 | (1 << 16)); if (getP(gpu, b) != 0) return c; }
                int np = Marshal.ReadInt32(b, 8), nc = Marshal.ReadInt32(b, 12);
                for (int p = 0; p < Math.Min(np, 16); p++)
                {
                    int po = HDR + p * PSTATE; if (Marshal.ReadInt32(b, po) != 0) continue; // P0 only
                    for (int k = 0; k < Math.Min(nc, 8); k++)
                    {
                        int co = po + 8 + k * CLOCK;
                        if (Marshal.ReadInt32(b, co) != domain) continue;
                        c.Found = true; c.Editable = (Marshal.ReadInt32(b, co + 8) & 1) == 1;
                        c.DeltaKHz = Marshal.ReadInt32(b, co + 12); c.MinKHz = Marshal.ReadInt32(b, co + 16); c.MaxKHz = Marshal.ReadInt32(b, co + 20);
                        int type = Marshal.ReadInt32(b, co + 4); c.BaseKHz = type == 0 ? Marshal.ReadInt32(b, co + 24) : Marshal.ReadInt32(b, co + 28);
                    }
                }
                return c;
            }
            finally { Marshal.FreeHGlobal(b); }
        }

        public static bool SetDelta(int domain, int deltaKHz)
        {
            if (!ok) return false;
            IntPtr b = Marshal.AllocHGlobal(V1);
            try
            {
                for (int i = 0; i < V1; i++) Marshal.WriteByte(b, i, 0);
                Marshal.WriteInt32(b, 0, V1 | (1 << 16));
                Marshal.WriteInt32(b, 8, 1); Marshal.WriteInt32(b, 12, 1); Marshal.WriteInt32(b, 16, 0);
                int po = HDR; Marshal.WriteInt32(b, po, 0);              // P0
                int co = po + 8; Marshal.WriteInt32(b, co, domain);       // clock domain
                Marshal.WriteInt32(b, co + 12, deltaKHz);                 // offset
                return setP(gpu, b) == 0;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(b); }
        }

        // Safety caps by card class, from typical stable results (MHz). {recommended, hard max}
        public static int[] CoreCap(HwInfo h) { return h.IsLaptop ? new[] { 75, 100 } : new[] { 150, 200 }; }
        public static int[] MemCap(HwInfo h)
        {
            if (h.IsLaptop) return new[] { 200, 300 };
            var m = Regex.Match(h.GpuName, @"RTX\s*(\d)0(\d)0"); if (!m.Success) return new[] { 400, 600 };
            int gen = int.Parse(m.Groups[1].Value), cls = int.Parse(m.Groups[2].Value);
            if (gen < 4) return new[] { 500, 800 };
            return cls >= 9 ? new[] { 600, 1000 } : cls == 8 ? new[] { 700, 1100 } : cls == 7 ? new[] { 800, 1200 } : new[] { 1000, 1300 };
        }

        // Power limit through nvidia-smi (where the card allows it)
        public static double[] PowerLimits() // {current, default, max}
        {
            var d = Nv.Query("power.limit,power.default_limit,power.max_limit");
            if (d == null) return null;
            double a = Nv.Num(d, "power.limit"), b = Nv.Num(d, "power.default_limit"), c = Nv.Num(d, "power.max_limit");
            if (double.IsNaN(a) || double.IsNaN(b) || double.IsNaN(c) || c <= b + 1) return null;
            return new[] { a, b, c };
        }
        public static string SetPower(double w)
        {
            try
            {
                string exe = System.IO.File.Exists(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\nvidia-smi.exe")) ? Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\nvidia-smi.exe") : @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe";
                var psi = new ProcessStartInfo(exe, "-pl " + w.ToString("0", CultureInfo.InvariantCulture)) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using (var p = Process.Start(psi)) { string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(5000); return p.ExitCode == 0 ? null : o.Trim(); }
            }
            catch (Exception ex) { return ex.Message; }
        }

        // Did the NVIDIA driver crash/reset (TDR) since a given time?
        public static bool DriverCrashedSince(DateTime since)
        {
            try
            {
                using (var log = new EventLog("System"))
                {
                    var e = log.Entries; int n = e.Count;
                    for (int i = n - 1; i >= 0 && i > n - 400; i--)
                    {
                        var en = e[i]; if (en.TimeGenerated < since) break;
                        if ((en.Source == "nvlddmkm" && en.EntryType == EventLogEntryType.Error) || (en.Source == "Display" && en.InstanceId == 4101)) return true;
                    }
                }
            }
            catch { }
            return false;
        }
    }
}
