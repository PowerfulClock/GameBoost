using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace GameBoost
{
    partial class MainForm
    {
        StatCard tGpu, tCpu, tDisk, tFan; Label tempHdr; System.Windows.Forms.Timer tempTimer; bool tempBusy; DateTime lastDisk = DateTime.MinValue;
        List<double[]> diskTemps = new List<double[]>(); List<string> diskNames = new List<string>();

        void BuildTemps()
        {
            tempHdr = L("TEMPERATURES", 8f, Theme.Dim, true);
            tGpu = new StatCard("GPU core", Theme.Accent) { suffix = " °C" };
            tCpu = new StatCard("CPU", Theme.Blue) { suffix = " °C" };
            tDisk = new StatCard("Hottest drive", Theme.Pink) { suffix = " °C" };
            tFan = new StatCard("GPU fan", Theme.Blue) { suffix = "%" };
            foreach (var c in new[] { tGpu, tCpu, tDisk, tFan }) { c.Override = "--"; dash.Controls.Add(c); }
            dash.Controls.Add(tempHdr);
            tempTimer = new System.Windows.Forms.Timer { Interval = 3000 }; tempTimer.Tick += (s, e) => PollTemps(); tempTimer.Start();
            PollTemps();
        }

        void LayoutTemps(int w, int y)
        {
            tempHdr.Location = new Point(2, y - 2);
            int gap = 16, cw = (w - gap * 3) / 4, top = y + 16;
            tGpu.Bounds = new Rectangle(0, top, cw, 120); tCpu.Bounds = new Rectangle(cw + gap, top, cw, 120);
            tDisk.Bounds = new Rectangle((cw + gap) * 2, top, cw, 120); tFan.Bounds = new Rectangle((cw + gap) * 3, top, w - (cw + gap) * 3, 120);
        }

        static Color TempCol(double t, double warn, double bad) { return t >= bad ? Color.FromArgb(255, 95, 95) : t >= warn ? Theme.Warn : Theme.Accent; }

        void PollTemps()
        {
            if (tempBusy || current != 0 && !WindowState.Equals(FormWindowState.Normal)) { if (tempBusy) return; }
            tempBusy = true;
            new Thread(() =>
            {
                var d = Nv.Available ? Nv.Query("temperature.gpu,fan.speed,power.draw,utilization.gpu") : null;
                double cpu = ThermalZone.MaxC();
                if ((DateTime.Now - lastDisk).TotalSeconds > 60) { ReadDiskTemps(); lastDisk = DateTime.Now; }
                try { BeginInvoke((Action)(() => { tempBusy = false; ShowTemps(d, cpu); })); } catch { tempBusy = false; }
            }) { IsBackground = true }.Start();
        }

        void ShowTemps(Dictionary<string, string> d, double cpu)
        {
            int glim = hw != null ? hw.GpuTempLimit : 90, clim = hw != null ? hw.CpuTempLimit : 95;
            double gt = Nv.Num(d, "temperature.gpu"), fan = Nv.Num(d, "fan.speed"), pw = Nv.Num(d, "power.draw"), gu = Nv.Num(d, "utilization.gpu");
            if (double.IsNaN(gt)) { tGpu.Override = "N/A"; tGpu.Set(0, Nv.Available ? "Driver didn't report it" : "NVIDIA GPUs only"); }
            else
            {
                tGpu.Override = null; tGpu.Col = TempCol(gt, glim - 10, glim - 3);
                tGpu.Set((float)gt, (gt >= glim - 3 ? "Near limit (" + glim + " °C)!" : gt >= glim - 10 ? "Warm · limit " + glim + " °C" : gu >= 50 ? "Normal under load · limit " + glim + " °C" : "Normal · limit " + glim + " °C") + (double.IsNaN(pw) ? "" : "  ·  " + pw.ToString("0") + " W"));
            }
            if (double.IsNaN(cpu)) { tCpu.Override = "N/A"; tCpu.Set(0, "Not reported by Windows"); }
            else { tCpu.Override = null; tCpu.Col = TempCol(cpu, clim - 12, clim - 3); tCpu.Set((float)cpu, "Board sensor · limit " + clim + " °C"); }
            if (diskTemps.Count == 0) { tDisk.Override = "N/A"; tDisk.Set(0, "Drive didn't report a temperature"); }
            else
            {
                int i = Enumerable.Range(0, diskTemps.Count).OrderByDescending(k => diskTemps[k][0]).First(); double t = diskTemps[i][0];
                tDisk.Override = null; tDisk.Col = TempCol(t, 60, 70);
                string nm = diskNames[i].Length > 22 ? diskNames[i].Substring(0, 22) + "…" : diskNames[i];
                tDisk.Set((float)t, nm + (t >= 70 ? "  ·  HOT, may throttle" : t >= 60 ? "  ·  warm" : "  ·  normal") + (diskTemps.Count > 1 ? "  (+" + (diskTemps.Count - 1) + " more)" : ""));
            }
            if (double.IsNaN(fan)) { tFan.Override = "N/A"; tFan.Set(0, Nv.Available ? "Fan speed not reported" : "NVIDIA GPUs only"); }
            else { tFan.Override = null; tFan.Col = Theme.Blue; tFan.Set((float)fan, fan < 1 ? (gt < 55 ? "Stopped (zero-RPM mode, normal when cool)" : "Not spinning while warm: check fans!") : "Spinning normally"); if (fan < 1 && gt >= 70) tFan.Col = Color.FromArgb(255, 95, 95); }
        }

        void ReadDiskTemps()
        {
            try
            {
                string cmd = "Get-PhysicalDisk | ForEach-Object { $r = $_ | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue; if ($r -and $r.Temperature -gt 0) { '{0}|{1}|{2}' -f $_.FriendlyName, $r.Temperature, $r.TemperatureMax } }";
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(cmd))) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    var o = p.StandardOutput.ReadToEndAsync(); p.StandardError.ReadToEndAsync(); if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return; }
                    var t = new List<double[]>(); var n = new List<string>();
                    foreach (var line in o.Result.Split('\n').Select(x => x.Trim()).Where(x => x.Count(c => c == '|') == 2))
                    {
                        var a = line.Split('|'); double v; if (!double.TryParse(a[1], out v) || v <= 0 || v > 120) continue;
                        double mx; double.TryParse(a[2], out mx); t.Add(new[] { v, mx }); n.Add(a[0]);
                    }
                    diskTemps = t; diskNames = n;
                }
            }
            catch { }
        }
    }
}
