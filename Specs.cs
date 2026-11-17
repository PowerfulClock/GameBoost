using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameBoost
{
    class DiskInfo { public string Model, Type; public long Size; public int Number; public bool System; }

    class HwInfo
    {
        public string CpuName = "Unknown CPU"; public int Cores, Threads, BaseMHz;
        public string GpuName = "Unknown GPU", Driver = ""; public long VramBytes; public int RefreshHz; public string Resolution = "";
        public long RamBytes; public string RamType = "RAM"; public int RamCfg, RamRated, Sticks; public string RamParts = "";
        public bool XmpLikelyOff, IsLaptop;
        public List<DiskInfo> Disks = new List<DiskInfo>();
        public string Board = "", Bios = "", Os = "";
        public double CpuScore, GpuScore, RamScore, Overall;
        public string Tier, CpuTier, GpuTier, RamTier, Outlook;
        public bool IsNvidia { get { return GpuName.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0; } }
        public bool IsRyzen { get { return CpuName.IndexOf("Ryzen", StringComparison.OrdinalIgnoreCase) >= 0; } }
        public DiskInfo SysDisk { get { return Disks.FirstOrDefault(d => d.System) ?? Disks.FirstOrDefault(); } }

        public int CpuTempLimit
        {
            get
            {
                if (CpuName.Contains("X3D")) return 89;
                if (IsRyzen) return 95;
                if (CpuName.IndexOf("Ultra", StringComparison.OrdinalIgnoreCase) >= 0) return 105;
                return 100;
            }
        }
        public int GpuTempLimit
        {
            get
            {
                var m = Regex.Match(GpuName, @"RTX\s*(\d)0(\d)0\s*(Ti)?", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    int gen = int.Parse(m.Groups[1].Value), cls = int.Parse(m.Groups[2].Value); bool ti = m.Groups[3].Success;
                    if (gen == 2) return 88; if (gen == 3) return 93; if (gen == 4) return 90;
                    if (gen >= 5) { if (cls == 9) return 90; if (cls == 8 || (cls == 7 && ti)) return 88; if (cls == 7) return 85; if (cls == 6 && ti) return 87; return 89; }
                }
                if (Regex.IsMatch(GpuName, @"GTX\s*10", RegexOptions.IgnoreCase)) return 94;
                return 90;
            }
        }

        public static string TierName(double s) { return s >= 4.3 ? "High-end" : s >= 3.6 ? "Upper mid-range" : s >= 2.8 ? "Mid-range" : s >= 2.0 ? "Entry-level" : "Low-end"; }
    }

    static class Specs
    {
        static IEnumerable<ManagementBaseObject> Q(string scope, string q)
        {
            var list = new List<ManagementBaseObject>();
            try { using (var s = new ManagementObjectSearcher(scope, q)) foreach (var o in s.Get()) list.Add(o); } catch { }
            return list;
        }
        static string S(ManagementBaseObject o, string p) { try { var v = o[p]; return v == null ? "" : v.ToString().Trim(); } catch { return ""; } }
        static long L(ManagementBaseObject o, string p) { long v; return long.TryParse(S(o, p), out v) ? v : 0; }

        public static HwInfo Detect()
        {
            var h = new HwInfo();
            var cpu = Q(@"root\cimv2", "SELECT Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed FROM Win32_Processor").FirstOrDefault();
            if (cpu != null) { h.CpuName = Regex.Replace(S(cpu, "Name"), @"\s+", " "); h.Cores = (int)L(cpu, "NumberOfCores"); h.Threads = (int)L(cpu, "NumberOfLogicalProcessors"); h.BaseMHz = (int)L(cpu, "MaxClockSpeed"); }
            if (h.Threads == 0) h.Threads = Environment.ProcessorCount; if (h.Cores == 0) h.Cores = Math.Max(1, h.Threads / 2);

            var gpus = Q(@"root\cimv2", "SELECT Name,DriverVersion,CurrentRefreshRate,CurrentHorizontalResolution,CurrentVerticalResolution,AdapterRAM FROM Win32_VideoController").ToList();
            var g = gpus.OrderByDescending(x => Regex.IsMatch(S(x, "Name"), "NVIDIA|Radeon RX|Arc", RegexOptions.IgnoreCase) ? 1 : 0).FirstOrDefault();
            if (g != null) { h.GpuName = S(g, "Name"); h.Driver = S(g, "DriverVersion"); h.VramBytes = L(g, "AdapterRAM"); }
            var disp = gpus.FirstOrDefault(x => L(x, "CurrentHorizontalResolution") > 0);
            if (disp != null) { h.Resolution = L(disp, "CurrentHorizontalResolution") + " x " + L(disp, "CurrentVerticalResolution"); h.RefreshHz = (int)L(disp, "CurrentRefreshRate"); }
            try
            {
                using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                    foreach (var sub in cls.GetSubKeyNames())
                        using (var k = cls.OpenSubKey(sub))
                        {
                            if (k == null || Convert.ToString(k.GetValue("DriverDesc")) != h.GpuName) continue;
                            var v = k.GetValue("HardwareInformation.qwMemorySize");
                            if (v is long) h.VramBytes = (long)v; else if (v is byte[] && ((byte[])v).Length >= 8) h.VramBytes = BitConverter.ToInt64((byte[])v, 0);
                        }
            }
            catch { }

            var mods = Q(@"root\cimv2", "SELECT Capacity,Speed,ConfiguredClockSpeed,SMBIOSMemoryType,Manufacturer,PartNumber FROM Win32_PhysicalMemory").ToList();
            h.Sticks = mods.Count; h.RamBytes = mods.Sum(m => L(m, "Capacity"));
            if (mods.Count > 0)
            {
                var m0 = mods[0]; long t = L(m0, "SMBIOSMemoryType");
                h.RamType = t == 34 ? "DDR5" : t == 26 ? "DDR4" : t == 24 ? "DDR3" : t == 35 ? "LPDDR5" : t == 30 ? "LPDDR4" : "RAM";
                h.RamCfg = (int)mods.Min(m => L(m, "ConfiguredClockSpeed")); h.RamRated = (int)mods.Max(m => L(m, "Speed"));
                h.RamParts = string.Join(", ", mods.Select(m => (S(m, "Manufacturer") + " " + S(m, "PartNumber")).Trim()).Distinct().Where(x => x.Length > 0));
            }
            if (h.RamBytes == 0) { try { h.RamBytes = (long)new Microsoft.VisualBasic.Devices.ComputerInfo().TotalPhysicalMemory; } catch { } }

            var chassis = Q(@"root\cimv2", "SELECT ChassisTypes FROM Win32_SystemEnclosure").FirstOrDefault();
            try { var ct = chassis != null ? chassis["ChassisTypes"] as ushort[] : null; if (ct != null && ct.Any(c => new ushort[] { 8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32 }.Contains(c))) h.IsLaptop = true; } catch { }
            if (Q(@"root\cimv2", "SELECT Name FROM Win32_Battery").Any()) h.IsLaptop = true;

            if (h.RamCfg > 0)
            {
                bool ddr4 = h.RamType == "DDR4", ddr5 = h.RamType == "DDR5";
                h.XmpLikelyOff = (h.RamRated > h.RamCfg + 100) || (ddr4 && h.RamCfg <= 2666) || (ddr5 && h.RamCfg <= 4800);
            }

            // Storage
            int sysDisk = -1;
            foreach (var p in Q(@"root\Microsoft\Windows\Storage", "SELECT DriveLetter,DiskNumber FROM MSFT_Partition"))
            {
                try { if (Convert.ToChar(p["DriveLetter"]) == Environment.GetFolderPath(Environment.SpecialFolder.Windows)[0]) sysDisk = Convert.ToInt32(p["DiskNumber"]); } catch { }
            }
            foreach (var d in Q(@"root\Microsoft\Windows\Storage", "SELECT DeviceId,FriendlyName,Size,MediaType,BusType FROM MSFT_PhysicalDisk"))
            {
                var di = new DiskInfo { Model = S(d, "FriendlyName"), Size = L(d, "Size") };
                int.TryParse(S(d, "DeviceId"), out di.Number);
                long bus = L(d, "BusType"), media = L(d, "MediaType");
                di.Type = bus == 17 ? "NVMe SSD" : media == 4 ? "SATA SSD" : media == 3 ? "Hard drive" : di.Model.IndexOf("SSD", StringComparison.OrdinalIgnoreCase) >= 0 ? "SSD" : bus == 7 ? "USB drive" : "Drive";
                di.System = di.Number == sysDisk;
                h.Disks.Add(di);
            }
            h.Disks = h.Disks.OrderByDescending(x => x.System).ThenBy(x => x.Number).ToList();

            var b = Q(@"root\cimv2", "SELECT Manufacturer,Product FROM Win32_BaseBoard").FirstOrDefault();
            if (b != null) h.Board = (S(b, "Manufacturer") + " " + S(b, "Product")).Trim();
            var bios = Q(@"root\cimv2", "SELECT SMBIOSBIOSVersion,ReleaseDate FROM Win32_BIOS").FirstOrDefault();
            if (bios != null) { string rd = S(bios, "ReleaseDate"); h.Bios = S(bios, "SMBIOSBIOSVersion") + (rd.Length >= 8 ? "  (" + rd.Substring(0, 4) + "-" + rd.Substring(4, 2) + "-" + rd.Substring(6, 2) + ")" : ""); }
            var os = Q(@"root\cimv2", "SELECT Caption,BuildNumber FROM Win32_OperatingSystem").FirstOrDefault();
            if (os != null) h.Os = S(os, "Caption").Replace("Microsoft ", "") + "  (build " + S(os, "BuildNumber") + ")";

            Score(h);
            return h;
        }

        static double CpuScore(HwInfo h)
        {
            string n = h.CpuName; Match m;
            if ((m = Regex.Match(n, @"Ultra\s*(\d)\s*(\d{3})([A-Z]*)", RegexOptions.IgnoreCase)).Success)
            {
                double s = m.Groups[1].Value == "9" ? 4.8 : m.Groups[1].Value == "7" ? 4.4 : 3.8;
                string suf = m.Groups[3].Value.ToUpper(); if (suf.Contains("U") || suf.Contains("V")) s -= 0.7; else if (suf.Contains("H")) s -= 0.2;
                return s;
            }
            if ((m = Regex.Match(n, @"i([3579])-(\d{4,5})([A-Z]*)", RegexOptions.IgnoreCase)).Success)
            {
                int tier = int.Parse(m.Groups[1].Value); string num = m.Groups[2].Value; int gen = num.Length == 5 ? int.Parse(num.Substring(0, 2)) : int.Parse(num.Substring(0, 1));
                double s = tier == 9 ? 4.2 : tier == 7 ? 3.6 : tier == 5 ? 3.0 : 2.0;
                s += gen >= 13 ? 0.4 : gen == 12 ? 0.2 : gen == 11 ? -0.3 : gen == 10 ? -0.5 : -0.9;
                string suf = m.Groups[3].Value.ToUpper(); if (suf.Contains("K")) s += 0.1; if (suf.Contains("U")) s -= 0.8; else if (suf.Contains("H")) s -= 0.2;
                return s;
            }
            if ((m = Regex.Match(n, @"Ryzen\s*(?:AI\s*)?(\d)\s*(?:PRO\s*)?(\d{3,4})?\s*([A-Z0-9]*)", RegexOptions.IgnoreCase)).Success)
            {
                int tier = int.Parse(m.Groups[1].Value);
                double s = tier == 9 ? 4.4 : tier == 7 ? 4.0 : tier == 5 ? 3.4 : 2.4;
                string num = m.Groups[2].Value; int gen = num.Length == 4 ? num[0] - '0' : (n.Contains(" AI ") ? 9 : 5);
                s += gen == 9 ? 0.4 : gen == 7 ? 0.3 : gen == 8 ? 0.1 : gen == 6 ? -0.1 : gen == 5 ? 0 : gen == 4 ? -0.2 : gen == 3 ? -0.5 : -1.0;
                string suf = (m.Groups[3].Value + " " + n).ToUpper(); if (suf.Contains("X3D")) s += 0.5; if (Regex.IsMatch(n, @"\d{4}U\b")) s -= 0.6;
                return s;
            }
            // Fallback by thread count
            return h.Threads >= 24 ? 4.2 : h.Threads >= 16 ? 3.6 : h.Threads >= 12 ? 3.1 : h.Threads >= 8 ? 2.5 : 1.6;
        }

        static double GpuScore(HwInfo h)
        {
            string n = h.GpuName; Match m; double s;
            bool laptop = n.IndexOf("Laptop", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Mobile", StringComparison.OrdinalIgnoreCase) >= 0 || n.Contains("Max-Q");
            if ((m = Regex.Match(n, @"RTX\s*(\d)0(\d)0\s*(Ti|SUPER)?\s*(SUPER)?", RegexOptions.IgnoreCase)).Success)
            {
                int gen = int.Parse(m.Groups[1].Value), cls = int.Parse(m.Groups[2].Value); bool plus = m.Groups[3].Success;
                s = cls == 9 ? 5.0 : cls == 8 ? 4.6 : cls == 7 ? (plus ? 4.2 : 3.9) : cls == 6 ? (plus ? 3.4 : 3.1) : cls == 5 ? 2.4 : 2.0;
                s += gen == 2 ? -1.0 : gen == 3 ? -0.5 : gen == 4 ? 0 : 0.3;
            }
            else if ((m = Regex.Match(n, @"GTX\s*(\d{3,4})", RegexOptions.IgnoreCase)).Success)
            {
                string v = m.Groups[1].Value; s = v.StartsWith("16") ? 2.0 : v == "1080" ? 2.2 : v == "1070" ? 1.9 : v == "1060" ? 1.6 : 1.2;
            }
            else if ((m = Regex.Match(n, @"RX\s*(\d{4})\s*(XTX|XT|GRE)?", RegexOptions.IgnoreCase)).Success)
            {
                int v = int.Parse(m.Groups[1].Value); bool xt = m.Groups[2].Success;
                var map = new Dictionary<int, double> { { 9070, 3.9 }, { 9060, 3.0 }, { 7900, 4.4 }, { 7800, 3.9 }, { 7700, 3.6 }, { 7600, 2.9 }, { 6950, 3.9 }, { 6900, 3.9 }, { 6800, 3.6 }, { 6750, 3.3 }, { 6700, 3.2 }, { 6650, 2.8 }, { 6600, 2.7 }, { 6500, 1.8 }, { 5700, 2.3 }, { 5600, 2.0 } };
                s = map.ContainsKey(v) ? map[v] : v >= 5000 ? 2.0 : 1.4; if (xt) s += 0.3;
            }
            else if ((m = Regex.Match(n, @"Arc\s*([AB])(\d{3})", RegexOptions.IgnoreCase)).Success) s = m.Groups[1].Value.ToUpper() == "B" ? 3.0 : (m.Groups[2].Value == "770" ? 2.7 : m.Groups[2].Value == "750" ? 2.5 : 1.8);
            else if (Regex.IsMatch(n, @"Radeon\s*8[0-9]0M", RegexOptions.IgnoreCase)) s = 1.7;
            else if (Regex.IsMatch(n, @"Radeon\s*7[0-9]0M", RegexOptions.IgnoreCase)) s = 1.5;
            else s = 0.8; // integrated / unknown
            if (laptop) s -= 0.4;
            return s;
        }

        static void Score(HwInfo h)
        {
            h.CpuScore = Math.Max(0.5, Math.Min(5, CpuScore(h)));
            h.GpuScore = Math.Max(0.5, Math.Min(5, GpuScore(h)));
            double gb = h.RamBytes / 1073741824.0;
            h.RamScore = (gb >= 31 ? 4.5 : gb >= 15 ? 3.5 : gb >= 7 ? 2.0 : 1.0) + (h.RamType.Contains("DDR5") ? 0.3 : 0);
            h.Overall = 0.55 * h.GpuScore + 0.30 * h.CpuScore + 0.15 * h.RamScore;
            h.Tier = HwInfo.TierName(h.Overall); h.CpuTier = HwInfo.TierName(h.CpuScore); h.GpuTier = HwInfo.TierName(h.GpuScore); h.RamTier = HwInfo.TierName(h.RamScore);
            h.Outlook = h.Overall >= 4.3 ? "Built for 1440p/4K at high settings and high refresh rates." :
                        h.Overall >= 3.6 ? "Great for 1440p high settings and high-refresh 1080p." :
                        h.Overall >= 2.8 ? "Solid 1080p gaming at high settings; lighter 1440p." :
                        h.Overall >= 2.0 ? "1080p at medium settings; esports titles run well." :
                        "Best for lighter and esports games at low-medium settings.";
        }
    }
}
