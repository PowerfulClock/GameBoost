using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;

namespace GameBoost
{
    class QItem { public string Threat, Type, Resource, When, Severity = ""; public int SevId; }

    class AvProduct { public string Name; public bool Enabled, UpToDate, IsDefender; }

    class DefSetting
    {
        public string Key, Name, Desc, OnArg, OffArg; public bool On, Recommended, RiskyOff, Known;
    }

    class DefenderState
    {
        public List<AvProduct> Products = new List<AvProduct>();
        public bool Available, ServiceOn, AvOn, RealTime, Tamper; public string Mode = "";
        public TimeSpan LastQuick = TimeSpan.Zero, LastFull = TimeSpan.Zero;
        public int SigAgeDays = -1, QuickScanAgeDays = -1; public DateTime SigUpdated; public string SigVersion = "";
        public List<DefSetting> Settings = new List<DefSetting>();
        public List<QItem> Quarantine = new List<QItem>(); public int PurgeDays = -1; public bool QuarantineRead;
        public List<string> ExPaths = new List<string>(), ExProcs = new List<string>(), ExExts = new List<string>();
        public bool DefenderActive { get { return Available && AvOn && Mode.IndexOf("Passive", StringComparison.OrdinalIgnoreCase) < 0; } }
        public AvProduct ThirdParty { get { return Products.FirstOrDefault(p => !p.IsDefender && p.Enabled) ?? Products.FirstOrDefault(p => !p.IsDefender); } }
    }

    static class Defender
    {
        const string NS = @"root\Microsoft\Windows\Defender";
        static ManagementBaseObject One(string ns, string q) { try { using (var s = new ManagementObjectSearcher(ns, q)) foreach (var o in s.Get()) return o; } catch { } return null; }
        static object V(ManagementBaseObject o, string p) { try { return o == null ? null : o[p]; } catch { return null; } }
        static bool B(ManagementBaseObject o, string p) { var v = V(o, p); return v is bool && (bool)v; }
        static int I(ManagementBaseObject o, string p, int d = -1) { var v = V(o, p); try { return v == null ? d : Convert.ToInt32(v); } catch { return d; } }
        static List<string> L(ManagementBaseObject o, string p) { var v = V(o, p) as string[]; return v == null ? new List<string>() : v.Where(x => !string.IsNullOrWhiteSpace(x) && x != "N/A: Must be an administrator to view exclusions").ToList(); }

        public static DefenderState Read()
        {
            var s = new DefenderState();
            try
            {
                using (var q = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct"))
                    foreach (var o in q.Get())
                    {
                        int st = Convert.ToInt32(o["productState"]); string n = Convert.ToString(o["displayName"]);
                        s.Products.Add(new AvProduct { Name = n, Enabled = ((st >> 12) & 0xF) == 1, UpToDate = ((st >> 4) & 0xF) == 0, IsDefender = n.IndexOf("Defender", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0 });
                    }
            }
            catch { }

            var st2 = One(NS, "SELECT * FROM MSFT_MpComputerStatus");
            if (st2 != null)
            {
                s.Available = true;
                s.ServiceOn = B(st2, "AMServiceEnabled"); s.AvOn = B(st2, "AntivirusEnabled"); s.RealTime = B(st2, "RealTimeProtectionEnabled");
                s.Tamper = B(st2, "IsTamperProtected"); s.Mode = Convert.ToString(V(st2, "AMRunningMode") ?? "");
                s.SigAgeDays = I(st2, "AntivirusSignatureAge"); s.QuickScanAgeDays = I(st2, "QuickScanAge"); s.SigVersion = Convert.ToString(V(st2, "AntivirusSignatureVersion") ?? "");
                try { s.SigUpdated = ManagementDateTimeConverter.ToDateTime(Convert.ToString(V(st2, "AntivirusSignatureLastUpdated"))); } catch { }
                Func<string, string, TimeSpan> dur = (a1, b1) => { try { var x = ManagementDateTimeConverter.ToDateTime(Convert.ToString(V(st2, a1))); var y = ManagementDateTimeConverter.ToDateTime(Convert.ToString(V(st2, b1))); var d = y - x; return d.TotalSeconds > 5 && d.TotalHours < 24 ? d : TimeSpan.Zero; } catch { return TimeSpan.Zero; } };
                s.LastQuick = dur("QuickScanStartTime", "QuickScanEndTime"); s.LastFull = dur("FullScanStartTime", "FullScanEndTime");
            }
            var p = One(NS, "SELECT * FROM MSFT_MpPreference");
            if (p != null)
            {
                Func<string, bool> notDisabled = k => V(p, k) == null ? true : !B(p, k);
                Action<string, string, string, bool, string, string, bool, bool> add = (key, name, desc, on, onArg, offArg, rec, risky) =>
                    s.Settings.Add(new DefSetting { Key = key, Name = name, Desc = desc, On = on, OnArg = onArg, OffArg = offArg, Recommended = rec, RiskyOff = risky, Known = V(p, key) != null });
                add("DisableRealtimeMonitoring", "Real-time protection", "Scans files the moment they're opened, downloaded or run. Your main line of defense.", notDisabled("DisableRealtimeMonitoring"), "-DisableRealtimeMonitoring $false", "-DisableRealtimeMonitoring $true", true, true);
                add("MAPSReporting", "Cloud-delivered protection", "Checks suspicious files against Microsoft's cloud for brand-new threats.", I(p, "MAPSReporting", 0) > 0, "-MAPSReporting Advanced", "-MAPSReporting Disabled", true, true);
                add("DisableBlockAtFirstSeen", "Block at first sight", "Holds never-seen-before files for a few seconds while the cloud checks them. Needs cloud protection.", notDisabled("DisableBlockAtFirstSeen"), "-DisableBlockAtFirstSeen $false", "-DisableBlockAtFirstSeen $true", true, true);
                int sc = I(p, "SubmitSamplesConsent", 1);
                add("SubmitSamplesConsent", "Automatic sample submission", "Sends suspicious (non-personal) files to Microsoft for analysis. A privacy choice; cloud protection works better with it on.", sc == 1 || sc == 3, "-SubmitSamplesConsent SendSafeSamples", "-SubmitSamplesConsent NeverSend", true, false);
                add("DisableBehaviorMonitoring", "Behavior monitoring", "Watches running programs for ransomware-like or malicious behavior.", notDisabled("DisableBehaviorMonitoring"), "-DisableBehaviorMonitoring $false", "-DisableBehaviorMonitoring $true", true, true);
                add("DisableIOAVProtection", "Scan downloads & attachments", "Scans files from the internet and email before you open them.", notDisabled("DisableIOAVProtection"), "-DisableIOAVProtection $false", "-DisableIOAVProtection $true", true, true);
                add("DisableScriptScanning", "Script scanning", "Scans PowerShell/JavaScript/VBScript as it runs; common malware delivery method.", notDisabled("DisableScriptScanning"), "-DisableScriptScanning $false", "-DisableScriptScanning $true", true, true);
                add("PUAProtection", "Block potentially unwanted apps", "Blocks adware, bundled toolbars, crypto-miners and shady installers.", I(p, "PUAProtection", 0) == 1, "-PUAProtection Enabled", "-PUAProtection Disabled", true, false);
                add("EnableNetworkProtection", "Network protection", "Blocks connections to known malicious and scam websites from any app.", I(p, "EnableNetworkProtection", 0) == 1, "-EnableNetworkProtection Enabled", "-EnableNetworkProtection Disabled", false, false);
                add("EnableControlledFolderAccess", "Ransomware protection (Controlled folder access)", "Stops untrusted apps from changing Documents, Pictures, etc. Can block some games from saving; you'd need to allow them in Windows Security.", I(p, "EnableControlledFolderAccess", 0) == 1, "-EnableControlledFolderAccess Enabled", "-EnableControlledFolderAccess Disabled", false, false);
                add("DisableRemovableDriveScanning", "Scan USB drives in full scans", "Includes USB sticks and external drives when running a full scan.", notDisabled("DisableRemovableDriveScanning"), "-DisableRemovableDriveScanning $false", "-DisableRemovableDriveScanning $true", true, false);
                s.PurgeDays = I(p, "QuarantinePurgeItemsAfterDelay", -1);
                s.ExPaths = L(p, "ExclusionPath"); s.ExProcs = L(p, "ExclusionProcess"); s.ExExts = L(p, "ExclusionExtension");
            }
            if (s.Available) { try { s.Quarantine = ListQuarantine(); s.QuarantineRead = true; } catch { } }
            return s;
        }

        public static int RunMp(string args, out string output, int timeoutMs = 120000)
        {
            output = ""; string mp = MpCmdRun(); if (mp == null) { output = "MpCmdRun.exe not found"; return -1; }
            try
            {
                var psi = new ProcessStartInfo(mp, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } output = "Timed out"; return -1; }
                    output = o.Result + e.Result; return p.ExitCode;
                }
            }
            catch (Exception ex) { output = ex.Message; return -1; }
        }

        public static List<QItem> ListQuarantine()
        {
            var list = new List<QItem>(); string o; RunMp("-Restore -ListAll", out o);
            var sev = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try { using (var q = new ManagementObjectSearcher(NS, "SELECT ThreatName, SeverityID FROM MSFT_MpThreat")) foreach (var t in q.Get()) { try { sev[Convert.ToString(t["ThreatName"])] = Convert.ToInt32(t["SeverityID"]); } catch { } } } catch { }
            string threat = null;
            foreach (var raw in o.Split('\n'))
            {
                string line = raw.Trim(); if (line.Length == 0) continue;
                if (line.StartsWith("ThreatName", StringComparison.OrdinalIgnoreCase) && line.Contains("=")) { threat = line.Substring(line.IndexOf('=') + 1).Trim(); continue; }
                if (threat == null) continue;
                int qa = line.IndexOf(" quarantined at ", StringComparison.OrdinalIgnoreCase); int colon = line.IndexOf(':');
                if (qa < 0 || colon < 0 || colon > qa) continue;
                var it = new QItem { Threat = threat, Type = line.Substring(0, colon), Resource = line.Substring(colon + 1, qa - colon - 1).Trim() };
                string when = line.Substring(qa + 16).Replace("(UTC)", "").Trim(); DateTime dt;
                it.When = DateTime.TryParse(when, System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out dt) || DateTime.TryParse(when, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out dt) ? dt.ToLocalTime().ToString("MMM d, yyyy  h:mm tt") : when;
                int sid; it.SevId = sev.TryGetValue(threat, out sid) ? sid : 0;
                it.Severity = it.SevId == 5 ? "Severe" : it.SevId == 4 ? "High" : it.SevId == 2 ? "Moderate" : it.SevId == 1 ? "Low" : "Unknown";
                list.Add(it);
            }
            return list;
        }

        // Runs a Defender PowerShell command. Returns null on success, error text otherwise.
        public static string PS(string cmd, int timeoutMs = 60000)
        {
            try
            {
                string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; try { " + cmd + " | Out-Null; exit 0 } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }"));
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + enc) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
                using (var pr = Process.Start(psi))
                {
                    var errT = pr.StandardError.ReadToEndAsync(); var outT = pr.StandardOutput.ReadToEndAsync();
                    if (!pr.WaitForExit(timeoutMs)) { try { pr.Kill(); } catch { } return "Timed out"; }
                    string err = errT.Result;
                    if (pr.ExitCode != 0)
                    {
                        // ignore progress/CLIXML noise; keep the real message
                        var line = err.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0 && !x.StartsWith("#< CLIXML") && !x.StartsWith("<Objs"));
                        return string.IsNullOrEmpty(line) ? "Failed (exit code " + pr.ExitCode + ")" : line;
                    }
                    return null;
                }
            }
            catch (Exception ex) { return ex.Message; }
        }
        public static string MpCmdRun()
        {
            try
            {
                var plat = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows Defender\Platform");
                if (Directory.Exists(plat))
                {
                    var best = Directory.GetDirectories(plat).Select(d => Path.Combine(d, "MpCmdRun.exe")).Where(File.Exists).OrderByDescending(f => f).FirstOrDefault();
                    if (best != null) return best;
                }
            }
            catch { }
            var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe");
            return File.Exists(pf) ? pf : null;
        }

        // Threat names detected since a time
        public static List<string> ThreatsSince(DateTime since)
        {
            var names = new List<string>();
            try
            {
                var ids = new HashSet<long>();
                using (var q = new ManagementObjectSearcher(NS, "SELECT ThreatID, InitialDetectionTime FROM MSFT_MpThreatDetection"))
                    foreach (var o in q.Get())
                    {
                        try { var t = ManagementDateTimeConverter.ToDateTime(Convert.ToString(o["InitialDetectionTime"])); if (t >= since.AddMinutes(-1)) ids.Add(Convert.ToInt64(o["ThreatID"])); } catch { }
                    }
                if (ids.Count == 0) return names;
                using (var q = new ManagementObjectSearcher(NS, "SELECT ThreatID, ThreatName FROM MSFT_MpThreat"))
                    foreach (var o in q.Get()) { try { if (ids.Contains(Convert.ToInt64(o["ThreatID"]))) names.Add(Convert.ToString(o["ThreatName"])); } catch { } }
                if (names.Count == 0) names.AddRange(ids.Select(i => "Threat ID " + i));
            }
            catch { }
            return names;
        }

        public static string Q(string s) { return "'" + s.Replace("'", "''") + "'"; }

        // Exclusion safety: returns null if OK, "BLOCK:reason" or "WARN:reason"
        public static string CheckExclusion(string path)
        {
            try
            {
                string full = Path.GetFullPath(path).TrimEnd('\\');
                Func<string, string> norm = x => string.IsNullOrEmpty(x) ? "\u0000" : Path.GetFullPath(x).TrimEnd('\\');
                string win = norm(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), user = norm(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                string pf = norm(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)), pf86 = norm(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
                string temp = norm(Path.GetTempPath()), dl = norm(Path.Combine(user, "Downloads")), desk = norm(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)), appd = norm(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
                Func<string, bool> eq = x => string.Equals(full, x, StringComparison.OrdinalIgnoreCase);
                Func<string, bool> under = x => full.StartsWith(x + "\\", StringComparison.OrdinalIgnoreCase) || eq(x);
                if (full.Length <= 3) return "BLOCK:Excluding an entire drive would turn off virus scanning for everything on it.";
                if (under(win)) return "BLOCK:Windows system folders must never be excluded; malware loves hiding there.";
                if (eq(user) || eq(pf) || eq(pf86) || eq(appd) || eq(norm(Path.Combine(user, "AppData")))) return "BLOCK:That folder is too broad. Exclude the specific game folder inside it instead.";
                if (under(temp) || under(dl) || eq(desk)) return "WARN:Downloads, Desktop and Temp are where new malware usually lands. Excluding them is risky.";
                return null;
            }
            catch { return "BLOCK:That path isn't valid."; }
        }
    }
}
