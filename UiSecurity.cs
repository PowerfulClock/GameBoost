using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace GameBoost
{
    partial class MainForm
    {
        FlowLayoutPanel secFlow; DefenderState def; bool secBusy;
        static readonly Color Red = Color.FromArgb(255, 95, 95);

        // ---- scan state (survives page rebuilds) ----
        int scanType; DateTime scanStart; TimeSpan scanEst; Process scanProc; System.Windows.Forms.Timer scanTimer;
        ProgressLine scanBar; Label scanLbl; string scanResult; Color scanResultCol;

        void StartScan(int type)
        {
            if (scanType != 0) return;
            string mp = Defender.MpCmdRun();
            if (mp == null) { Notify("Couldn't find Defender's scanner (MpCmdRun.exe)", Theme.Warn); return; }
            try
            {
                var psi = new ProcessStartInfo(mp, "-Scan -ScanType " + type) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                var proc = Process.Start(psi); scanProc = proc;
                var outLines = new List<string>();
                proc.OutputDataReceived += (o, e) => { if (e.Data != null) lock (outLines) outLines.Add(e.Data); };
                proc.ErrorDataReceived += (o, e) => { };
                proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
                scanType = type; scanStart = DateTime.Now; scanResult = null;
                scanEst = def != null ? (type == 1 ? def.LastQuick : def.LastFull) : TimeSpan.Zero;
                var started = scanStart;
                Notify((type == 1 ? "Quick" : "Full") + " scan started", Theme.Blue);
                FillSecurity();
                new Thread(() =>
                {
                    proc.WaitForExit(); int code = -1; try { code = proc.ExitCode; } catch { }
                    var threats = Defender.ThreatsSince(started);
                    string last; lock (outLines) last = outLines.LastOrDefault(x => x.Trim().Length > 0) ?? "";
                    try { BeginInvoke((Action)(() => ScanDone(code, threats, last))); } catch { }
                }) { IsBackground = true }.Start();
                if (scanTimer == null) { scanTimer = new System.Windows.Forms.Timer { Interval = 500 }; scanTimer.Tick += (o, e) => UpdScan(); }
                scanTimer.Start();
            }
            catch (Exception ex) { Notify("Couldn't start the scan: " + ex.Message, Theme.Warn); scanType = 0; }
        }

        void UpdScan()
        {
            if (scanType == 0 || scanBar == null || scanBar.IsDisposed) return;
            var el = DateTime.Now - scanStart; string els = (int)el.TotalMinutes + ":" + el.Seconds.ToString("00");
            float f; string est;
            if (scanEst.TotalSeconds > 5)
            {
                f = (float)Math.Min(0.97, el.TotalSeconds / scanEst.TotalSeconds);
                est = el > scanEst ? "taking longer than last time, almost done" : "~" + (int)(f * 100) + "%  ·  about " + Math.Max(1, (int)Math.Ceiling((scanEst - el).TotalMinutes)) + " min left (your last scan took " + (int)scanEst.TotalMinutes + ":" + scanEst.Seconds.ToString("00") + ")";
            }
            else { f = (float)(0.95 * (1 - Math.Exp(-el.TotalSeconds / (scanType == 1 ? 150.0 : 2400.0)))); est = "first scan, so no time estimate yet"; }
            scanBar.Busy = true; scanBar.Set(f);
            scanLbl.Text = (scanType == 1 ? "Quick" : "Full") + " scan running  ·  " + els + " elapsed  ·  " + est;
        }

        void ScanDone(int code, List<string> threats, string lastLine)
        {
            if (scanTimer != null) scanTimer.Stop();
            string kind = scanType == 1 ? "Quick" : "Full"; var el = DateTime.Now - scanStart; scanType = 0; scanProc = null;
            string took = (int)el.TotalMinutes + ":" + el.Seconds.ToString("00");
            if (threats.Count > 0 || code == 2)
            {
                scanResult = "\u26A0  " + kind + " scan found " + (threats.Count > 0 ? threats.Count + " threat(s): " + string.Join(", ", threats.Take(5)) : "threats") + ". Open Windows Security > Protection history to review and remove them.";
                scanResultCol = Red; Notify(kind + " scan found threats!", Red);
            }
            else if (code == 0) { scanResult = "\u2714  " + kind + " scan finished in " + took + ". No threats found."; scanResultCol = Theme.Accent; Notify(kind + " scan complete: no threats found", Theme.Accent); }
            else if (cancelled || lastLine.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0) { scanResult = kind + " scan was cancelled."; scanResultCol = Theme.Dim; Notify("Scan cancelled", Theme.Dim); }
            else { scanResult = kind + " scan didn't complete (code " + code + "). " + lastLine; scanResultCol = Theme.Warn; Notify("Scan didn't complete", Theme.Warn); }
            cancelled = false;
            LoadSecurity();
        }

        bool cancelled;
        void CancelScan()
        {
            cancelled = true;
            new Thread(() =>
            {
                try { string mp = Defender.MpCmdRun(); if (mp != null) using (var p = Process.Start(new ProcessStartInfo(mp, "-Scan -Cancel") { UseShellExecute = false, CreateNoWindow = true })) p.WaitForExit(10000); } catch { }
                try { if (scanProc != null && !scanProc.HasExited) scanProc.Kill(); } catch { }
            }) { IsBackground = true }.Start();
            Notify("Cancelling scan...", Theme.Dim);
        }

        Panel BuildSecurity()
        {
            var page = NewPage(); secFlow = VFlow();
            var wait = new Card { Height = 70 }; var wl = L("Checking your protection...", 11f, Theme.Fg, true); wl.Location = new Point(22, 22); wait.Controls.Add(wl);
            secFlow.Controls.Add(wait); page.Controls.Add(secFlow);
            return page;
        }

        void LoadSecurity()
        {
            if (secBusy) return; secBusy = true;
            new Thread(() =>
            {
                DefenderState s; try { s = Defender.Read(); } catch { s = new DefenderState(); }
                try { BeginInvoke((Action)(() => { def = s; secBusy = false; FillSecurity(); })); } catch { secBusy = false; }
            }) { IsBackground = true }.Start();
        }

        // Runs a Defender command off the UI thread, then reloads
        void DefRun(string cmd, string doneMsg, Func<DefenderState, bool> verify = null, int timeoutMs = 60000, string startMsg = "Applying...")
        {
            Cursor = Cursors.AppStarting; Notify(startMsg, Theme.Blue);
            new Thread(() =>
            {
                string err = Defender.PS(cmd, timeoutMs); var s = Defender.Read();
                bool okv = verify == null || verify(s);
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        Cursor = Cursors.Default; def = s; FillSecurity();
                        if (err == null && okv) Notify(doneMsg, Theme.Accent);
                        else if (verify != null && s.Tamper && (err == null || !okv)) Notify("Blocked by Tamper Protection. Change this in Windows Security instead.", Theme.Warn);
                        else if (verify != null) Notify("Couldn't change it: " + (err ?? "Windows didn't apply the setting (policy or Tamper Protection)"), Theme.Warn);
                        else Notify("Didn't finish: " + err, Theme.Warn);
                    }));
                }
                catch { }
            }) { IsBackground = true }.Start();
        }

        void FillSecurity()
        {
            if (secFlow == null || def == null) return;
            int scroll = secFlow.VerticalScroll.Value;
            secFlow.SuspendLayout();
            foreach (Control c in secFlow.Controls.Cast<Control>().ToList()) { secFlow.Controls.Remove(c); c.Dispose(); }

            var s = def; var third = s.ThirdParty; bool thirdOn = third != null && third.Enabled;
            var defSettings = s.Settings; int offRec = s.DefenderActive ? defSettings.Count(x => x.Known && x.Recommended && !x.On) : 0;
            bool protectedNow = thirdOn || (s.DefenderActive && s.RealTime);
            string verdict; Color vc;
            if (!protectedNow) { verdict = "Not protected"; vc = Red; }
            else if (thirdOn) { verdict = "Protected by " + third.Name; vc = third.UpToDate ? Theme.Accent : Theme.Warn; }
            else if (offRec > 0 || s.SigAgeDays > 7) { verdict = "Protected, with gaps"; vc = Theme.Warn; }
            else { verdict = "Fully protected"; vc = Theme.Accent; }

            // ---- Status hero ----
            var hero = new StackCard { Glow = vc, Margin = new Padding(0, 0, 0, 12) };
            var shield = new Label { Text = "", Font = Theme.Icons(30f), ForeColor = vc, AutoSize = true, BackColor = Color.Transparent, Location = new Point(24, 24) };
            hero.Controls.Add(shield);
            var t1 = Wrap("ANTIVIRUS STATUS", 8f, Theme.Dim, true); t1.Left = 84; hero.AddRow(t1, 0);
            var t2 = Wrap(verdict, 20f, vc, true); t2.Left = 82; hero.AddRow(t2, 2);
            foreach (var p in s.Products)
            {
                string st = (p.Enabled ? "On" : "Off") + (p.Enabled ? (p.UpToDate ? "  ·  up to date" : "  ·  definitions OUT OF DATE") : "");
                var l = Wrap((p.Enabled ? "✔   " : "✖   ") + p.Name + "   (" + st + ")", 10f, p.Enabled ? (p.UpToDate ? Theme.Fg : Theme.Warn) : Theme.Dim); l.Left = 84; hero.AddRow(l, 8);
            }
            if (s.Products.Count == 0) { var l = Wrap("Windows Security Center didn't report any antivirus.", 10f, Theme.Warn); l.Left = 84; hero.AddRow(l, 8); }
            if (thirdOn && s.Available) { var l = Wrap("Microsoft Defender is " + (s.DefenderActive ? "also active" : "standing by (passive/off)") + " because " + third.Name + " is your main antivirus. That's normal.", 9f, Theme.Dim); l.Left = 84; hero.AddRow(l, 8); }
            if (!protectedNow) { var l = Wrap("⚠   Nothing is actively scanning for viruses. Turn Real-time protection on below, or open Windows Security.", 10f, Red, true); l.Left = 84; hero.AddRow(l, 10); }
            var hb = new FlowLayoutPanel { Height = 44, BackColor = Theme.Card, WrapContents = false };
            var bOpen = new ModernButton("Open Windows Security", 210, false, "") { Margin = new Padding(62, 4, 10, 0) };
            bOpen.Click += (o, e) => { try { Process.Start("windowsdefender:"); } catch { } };
            var bRef = new ModernButton("Refresh", 120, false, "") { Margin = new Padding(0, 4, 10, 0) };
            bRef.Click += (o, e) => LoadSecurity();
            hb.Controls.Add(bOpen); hb.Controls.Add(bRef); hero.AddRow(hb, 14);
            secFlow.Controls.Add(hero);

            if (thirdOn && !s.DefenderActive)
            {
                var tp = new StackCard { Glow = Theme.Blue, Margin = new Padding(0, 0, 0, 12) };
                tp.AddRow(Wrap("Managed by " + third.Name, 12.5f, Theme.Fg, true), 0);
                tp.AddRow(Wrap("GameBoost only manages Microsoft Defender. To change settings or exclusions, use " + third.Name + "'s own app. Your PC is " + (third.UpToDate ? "protected and up to date." : "protected, but its virus definitions look out of date. Open " + third.Name + " and update it."), 9.5f, Theme.Dim), 8);
                secFlow.Controls.Add(tp);
            }
            else if (s.Available)
            {
                // ---- Defender details & toggles ----
                var dc = new StackCard { Glow = Theme.Accent, Margin = new Padding(0, 0, 0, 12) };
                dc.AddRow(Wrap("Microsoft Defender", 12.5f, Theme.Fg, true), 0);
                string sig = s.SigAgeDays < 0 ? "unknown" : s.SigAgeDays == 0 ? "today" : s.SigAgeDays + (s.SigAgeDays == 1 ? " day ago" : " days ago");
                string scan = s.QuickScanAgeDays < 0 || s.QuickScanAgeDays > 3650 ? "never" : s.QuickScanAgeDays == 0 ? "today" : s.QuickScanAgeDays + " days ago";
                dc.AddRow(Wrap("Definitions updated " + sig + (s.SigVersion.Length > 0 ? "  (" + s.SigVersion + ")" : "") + "   ·   Last quick scan " + scan + "   ·   Tamper Protection " + (s.Tamper ? "ON" : "off"), 9.5f, s.SigAgeDays > 7 ? Theme.Warn : Theme.Dim), 6);
                if (s.Tamper) dc.AddRow(Wrap("Tamper Protection is on (recommended). It stops apps, including GameBoost and malware, from turning protections OFF. To turn something off you'll need to switch Tamper Protection off in Windows Security first. Turning things ON still works.", 9f, Theme.Blue), 8);
                var ab = new FlowLayoutPanel { Height = 44, BackColor = Theme.Card, WrapContents = false };
                var bUpd = new ModernButton("Update definitions", 190, s.SigAgeDays > 3, "") { Margin = new Padding(0, 4, 10, 0) };
                bUpd.Click += (o, e) => DefRun("Update-MpSignature", "Virus definitions are up to date", null, 10 * 60000, "Downloading the latest virus definitions...");
                var bScan = new ModernButton("Quick scan", 140, false, "\uE721") { Margin = new Padding(0, 4, 10, 0), Enabled = scanType == 0 };
                bScan.Click += (o, e) => StartScan(1);
                var bFull = new ModernButton("Full scan", 130, false, "\uE721") { Margin = new Padding(0, 4, 10, 0), Enabled = scanType == 0 };
                bFull.Click += (o, e) =>
                {
                    if (MessageBox.Show(this, "A full scan checks every file and can take an hour or more, and it uses a lot of disk and CPU. Best to run it when you're not gaming.\n\nStart a full scan?", "GameBoost", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) StartScan(2);
                };
                ab.Controls.Add(bUpd); ab.Controls.Add(bScan); ab.Controls.Add(bFull); dc.AddRow(ab, 12);
                if (scanType != 0)
                {
                    scanLbl = Wrap("Starting scan...", 9.5f, Theme.Blue, true); dc.AddRow(scanLbl, 12);
                    var barRow = new Panel { Height = 40, BackColor = Theme.Card };
                    scanBar = new ProgressLine { Location = new Point(0, 12), Height = 10 };
                    var bCancel = new ModernButton("Cancel", 100) { Height = 34 };
                    bCancel.Click += (o, e) => CancelScan();
                    barRow.Controls.Add(scanBar); barRow.Controls.Add(bCancel);
                    barRow.Resize += (o, e) => { scanBar.Width = barRow.Width - 120; bCancel.Location = new Point(barRow.Width - 104, 2); };
                    dc.AddRow(barRow, 6);
                    dc.AddRow(Wrap("This is Defender's own scan, the same as starting it in Windows Security. You can keep using the PC or switch pages.", 8.5f, Theme.Dim), 4);
                    UpdScan();
                }
                else if (scanResult != null) dc.AddRow(Wrap(scanResult, 10f, scanResultCol, true), 12);

                dc.AddRow(Wrap("PROTECTION SETTINGS", 8f, Theme.Dim, true), 16);
                foreach (var st in defSettings.Where(x => x.Known))
                {
                    var row = new Panel { Height = 62, BackColor = Theme.Card };
                    var n = new Label { Text = st.Name + (st.Recommended ? "" : "   (optional)"), Font = Theme.F(10.5f, true), ForeColor = Theme.Fg, AutoSize = true, Location = new Point(0, 6), BackColor = Color.Transparent };
                    var d = new Label { Text = st.Desc, Font = Theme.F(8.5f), ForeColor = Theme.Dim, AutoSize = false, Location = new Point(0, 30), Height = 30, BackColor = Color.Transparent, AutoEllipsis = true };
                    var sw = new ToggleSwitch(); sw.SetInstant(st.On);
                    if (st.Recommended && !st.On) { n.ForeColor = Theme.Warn; n.Text += "   • OFF (recommended ON)"; }
                    row.Controls.Add(n); row.Controls.Add(d); row.Controls.Add(sw);
                    row.Resize += (o, e) => { sw.Location = new Point(row.Width - 56, 14); d.Width = row.Width - 80; };
                    var cur = st;
                    sw.Toggled += (o, e) =>
                    {
                        bool want = sw.Checked;
                        if (!want && cur.RiskyOff)
                        {
                            var r = MessageBox.Show(this, "Turning off \"" + cur.Name + "\" makes your PC easier to infect.\n\nIt does NOT improve gaming performance in any meaningful way. Defender already lowers its activity while you game.\n\nTurn it off anyway?", "GameBoost: lower protection?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                            if (r != DialogResult.Yes) { sw.Checked = true; return; }
                        }
                        DefRun("Set-MpPreference " + (want ? cur.OnArg : cur.OffArg), cur.Name + (want ? " turned on" : " turned off"),
                            ns => { var x = ns.Settings.FirstOrDefault(y => y.Key == cur.Key); return x == null || x.On == want; });
                    };
                    dc.AddRow(row, 4);
                }
                dc.Resize += (o, e) => { foreach (var r in dc.Rows.OfType<Panel>()) r.Width = dc.Width - 44; };
                secFlow.Controls.Add(dc);

                // ---- Quarantine ----
                secFlow.Controls.Add(QuarantineCard(s));

                // ---- Exclusions ----
                var ex = new StackCard { Glow = Theme.Warn, Margin = new Padding(0, 0, 0, 12) };
                ex.AddRow(Wrap("Exclusions", 12.5f, Theme.Fg, true), 0);
                ex.AddRow(Wrap("Excluded files and folders are NOT scanned by Defender. Only exclude things you trust, like a game folder that Defender keeps slowing down or falsely flagging. Exclusions are added through Windows Defender and show up in Windows Security too.", 9.5f, Theme.Dim), 8);
                var eb = new FlowLayoutPanel { Height = 44, BackColor = Theme.Card, WrapContents = false };
                var bFolder = new ModernButton("Exclude folder", 160, false, "") { Margin = new Padding(0, 4, 10, 0) };
                var bFile = new ModernButton("Exclude file", 150, false, "") { Margin = new Padding(0, 4, 10, 0) };
                var bProc = new ModernButton("Exclude process", 165, false, "") { Margin = new Padding(0, 4, 10, 0) };
                var bExt = new ModernButton("Exclude file type", 170, false, "") { Margin = new Padding(0, 4, 10, 0) };
                eb.Controls.AddRange(new Control[] { bFolder, bFile, bProc, bExt }); ex.AddRow(eb, 12);
                bFolder.Click += (o, e) => { using (var f = new FolderBrowserDialog { Description = "Choose a folder to exclude from Defender scans" }) if (f.ShowDialog(this) == DialogResult.OK) AddExclusion("Path", f.SelectedPath); };
                bFile.Click += (o, e) => { using (var f = new OpenFileDialog { Title = "Choose a file to exclude from Defender scans" }) if (f.ShowDialog(this) == DialogResult.OK) AddExclusion("Path", f.FileName); };
                bProc.Click += (o, e) => { using (var f = new OpenFileDialog { Title = "Choose a program (.exe). Files it opens won't be scanned", Filter = "Programs (*.exe)|*.exe" }) if (f.ShowDialog(this) == DialogResult.OK) AddExclusion("Process", f.FileName); };
                bExt.Click += (o, e) =>
                {
                    string x = Microsoft.VisualBasic.Interaction.InputBox("File extension to exclude (for example: pak)", "GameBoost: exclude file type", "").Trim().TrimStart('.', '*');
                    if (x.Length == 0) return;
                    var bad = new[] { "exe", "dll", "bat", "cmd", "ps1", "vbs", "js", "msi", "scr", "com", "zip", "rar", "7z", "docm", "xlsm", "lnk", "jar", "hta" };
                    if (bad.Contains(x.ToLower())) { MessageBox.Show(this, "." + x + " files can contain malware. GameBoost won't exclude this file type.", "GameBoost", MessageBoxButtons.OK, MessageBoxIcon.Stop); return; }
                    AddExclusion("Extension", x);
                };

                Action<string, List<string>, string> list = (title, items, kind) =>
                {
                    if (items.Count == 0) return;
                    ex.AddRow(Wrap(title, 8f, Theme.Dim, true), 14);
                    foreach (var it in items)
                    {
                        var row = new Panel { Height = 40, BackColor = Theme.Card2 };
                        var lbl = new Label { Text = kind == "Extension" ? "." + it : it, Font = Theme.F(9.5f), ForeColor = Theme.Fg, AutoSize = false, AutoEllipsis = true, Location = new Point(12, 11), Height = 20, BackColor = Theme.Card2 };
                        var rm = new ModernButton("Remove", 100) { Height = 30 };
                        row.Controls.Add(lbl); row.Controls.Add(rm);
                        row.Resize += (o, e) => { rm.Location = new Point(row.Width - 108, 5); lbl.Width = row.Width - 130; };
                        string val = it;
                        rm.Click += (o, e) => DefRun("Remove-MpPreference -Exclusion" + kind + " " + Defender.Q(val), "Exclusion removed: " + val);
                        ex.AddRow(row, 6);
                    }
                };
                list("EXCLUDED FILES & FOLDERS", s.ExPaths, "Path");
                list("EXCLUDED PROCESSES", s.ExProcs, "Process");
                list("EXCLUDED FILE TYPES", s.ExExts, "Extension");
                if (s.ExPaths.Count + s.ExProcs.Count + s.ExExts.Count == 0) ex.AddRow(Wrap("No exclusions. Everything is being scanned.", 9.5f, Theme.Accent), 14);
                ex.Resize += (o, e) => { foreach (var r in ex.Rows.OfType<Panel>()) r.Width = ex.Width - 44; };
                secFlow.Controls.Add(ex);
            }
            else
            {
                var na = new StackCard { Glow = Theme.Warn, Margin = new Padding(0, 0, 0, 12) };
                na.AddRow(Wrap("Microsoft Defender isn't available on this PC (it may be removed or disabled by policy), so there are no settings to manage here.", 10f, Theme.Warn), 0);
                secFlow.Controls.Add(na);
            }

            secFlow.ResumeLayout();
            foreach (Control c in secFlow.Controls) { c.Width = secFlow.ClientSize.Width - 22; if (c is StackCard) ((StackCard)c).Relayout(); }
            try { secFlow.VerticalScroll.Value = Math.Min(scroll, secFlow.VerticalScroll.Maximum); } catch { }
        }

        StackCard QuarantineCard(DefenderState s)
        {
            var q = new StackCard { Glow = Red, Margin = new Padding(0, 0, 0, 12) };
            var title = Wrap("Quarantine  (" + s.Quarantine.Count + (s.Quarantine.Count == 1 ? " item)" : " items)"), 12.5f, Theme.Fg, true); q.AddRow(title, 0);
            q.AddRow(Wrap("Files Defender caught and locked away. Quarantined files are encrypted and can't run or harm your PC. Only restore something if you're sure it's a false alarm (for example a game mod or tool you trust).", 9.5f, Theme.Dim), 8);

            var bar = new FlowLayoutPanel { Height = 44, BackColor = Theme.Card, WrapContents = false };
            var bHist = new ModernButton("Delete items in Windows Security", 280, false, "\uE74D") { Margin = new Padding(0, 4, 10, 0) };
            bHist.Click += (o, e) => { try { Process.Start("windowsdefender://history"); } catch { try { Process.Start("windowsdefender:"); } catch { } } };
            bar.Controls.Add(bHist);
            if (s.PurgeDays >= 0)
            {
                var lbl = new Label { Text = "Auto-delete after:", Font = Theme.F(9.5f), ForeColor = Theme.Dim, AutoSize = true, Margin = new Padding(6, 13, 6, 0), BackColor = Theme.Card };
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Theme.Card2, ForeColor = Theme.Fg, Font = Theme.F(9.5f), Width = 130, Margin = new Padding(0, 9, 0, 0) };
                var opts = new[] { 7, 15, 30, 90, 0 }; foreach (var d in opts) cb.Items.Add(d == 0 ? "Never" : d + " days");
                int idx = Array.IndexOf(opts, s.PurgeDays); if (idx < 0) { cb.Items.Add(s.PurgeDays + " days"); idx = cb.Items.Count - 1; }
                cb.SelectedIndex = idx;
                cb.SelectedIndexChanged += (o, e) => { if (cb.SelectedIndex < opts.Length && opts[cb.SelectedIndex] != s.PurgeDays) DefRun("Set-MpPreference -QuarantinePurgeItemsAfterDelay " + opts[cb.SelectedIndex], "Quarantined items will be deleted " + (opts[cb.SelectedIndex] == 0 ? "never (kept until you remove them)" : "after " + opts[cb.SelectedIndex] + " days"), ns => ns.PurgeDays == opts[cb.SelectedIndex]); };
                bar.Controls.Add(lbl); bar.Controls.Add(cb);
            }
            q.AddRow(bar, 12);

            if (!s.QuarantineRead) q.AddRow(Wrap("Couldn't read the quarantine list.", 9.5f, Theme.Warn), 12);
            else if (s.Quarantine.Count == 0) q.AddRow(Wrap("\u2714   Quarantine is empty.", 10f, Theme.Accent), 12);
            foreach (var it in s.Quarantine)
            {
                Color sc = it.SevId >= 4 ? Red : it.SevId == 2 ? Theme.Warn : it.SevId == 1 ? Color.FromArgb(240, 205, 70) : Theme.Dim;
                var row = new Panel { Height = 84, BackColor = Theme.Card2 };
                var name = new Label { Text = it.Threat, Font = Theme.F(10.5f, true), ForeColor = Theme.Fg, AutoSize = true, Location = new Point(14, 10), BackColor = Theme.Card2 };
                var pill = new Pill(it.Severity, sc); pill.BackColor = Theme.Card2;
                var path = new Label { Text = it.Type + ": " + it.Resource, Font = Theme.F(9f), ForeColor = Theme.Dim, AutoSize = false, AutoEllipsis = true, Location = new Point(14, 36), Height = 20, BackColor = Theme.Card2 };
                var when = new Label { Text = "Quarantined " + it.When, Font = Theme.F(8.5f), ForeColor = Theme.Dim, AutoSize = true, Location = new Point(14, 58), BackColor = Theme.Card2 };
                var bRestore = new ModernButton("Restore", 110) { Height = 34 };
                var bAllow = new ModernButton("Restore + allow", 150) { Height = 34 };
                row.Controls.AddRange(new Control[] { name, pill, path, when, bRestore, bAllow });
                row.Resize += (o, e) =>
                {
                    bAllow.Location = new Point(row.Width - 162, 24); bRestore.Location = new Point(row.Width - 282, 24);
                    pill.Location = new Point(name.Right + 10, 8); path.Width = row.Width - 310;
                };
                var item = it;
                bool isFile = it.Type.Equals("file", StringComparison.OrdinalIgnoreCase);
                bAllow.Visible = isFile;
                bRestore.Click += (o, e) => RestoreItem(item, false);
                bAllow.Click += (o, e) => RestoreItem(item, true);
                q.AddRow(row, 8);
            }
            q.Resize += (o, e) => { foreach (var r in q.Rows.OfType<Panel>()) r.Width = q.Width - 44; };
            return q;
        }

        void RestoreItem(QItem it, bool allow)
        {
            string sevWarn = it.SevId >= 4 ? "\n\n\u26A0 Defender rated this threat " + it.Severity.ToUpper() + ". Restoring it could infect your PC. Only continue if you are CERTAIN it's a false alarm." : "";
            string msg = "Restore this item from quarantine?\n\nThreat: " + it.Threat + "\nSeverity: " + it.Severity + "\nFile: " + it.Resource + sevWarn +
                (allow ? "\n\nThe file will also be EXCLUDED from Defender scans so it isn't caught again." : "\n\nDefender may quarantine it again on the next scan unless you also allow it.");
            if (MessageBox.Show(this, msg, "GameBoost: restore from quarantine", MessageBoxButtons.YesNo, it.SevId >= 4 ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            if (it.SevId >= 4 && MessageBox.Show(this, "Last check: restore a " + it.Severity + " threat?", "GameBoost", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            if (allow)
            {
                string chk = Defender.CheckExclusion(it.Resource);
                if (chk != null && chk.StartsWith("BLOCK:")) { MessageBox.Show(this, "Can't exclude this location: " + chk.Substring(6) + "\nUse plain Restore instead.", "GameBoost", MessageBoxButtons.OK, MessageBoxIcon.Stop); return; }
            }
            Cursor = Cursors.AppStarting; Notify("Restoring " + System.IO.Path.GetFileName(it.Resource) + "...", Theme.Blue);
            new Thread(() =>
            {
                string excludeErr = null;
                if (allow) excludeErr = Defender.PS("Add-MpPreference -ExclusionPath " + Defender.Q(it.Resource));
                string o; int code = Defender.RunMp("-Restore -Name \"" + it.Threat + "\" -FilePath \"" + it.Resource + "\"", out o);
                var ns = Defender.Read();
                bool gone = !ns.Quarantine.Any(x => x.Threat == it.Threat && string.Equals(x.Resource, it.Resource, StringComparison.OrdinalIgnoreCase));
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        Cursor = Cursors.Default; def = ns; FillSecurity();
                        if (gone) Notify("Restored" + (allow && excludeErr == null ? " and allowed: " : ": ") + System.IO.Path.GetFileName(it.Resource), Theme.Accent);
                        else
                        {
                            string last = o.Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0) ?? "";
                            Notify("Couldn't restore it" + (last.Length > 0 ? ": " + last : "") + ". Try Windows Security > Protection history", Theme.Warn);
                        }
                    }));
                }
                catch { }
            }) { IsBackground = true }.Start();
        }

        void AddExclusion(string kind, string value)
        {
            if (kind != "Extension")
            {
                string chk = Defender.CheckExclusion(value);
                if (chk != null && chk.StartsWith("BLOCK:")) { MessageBox.Show(this, chk.Substring(6) + "\n\nGameBoost won't add this exclusion.", "GameBoost: unsafe exclusion", MessageBoxButtons.OK, MessageBoxIcon.Stop); return; }
                string msg = (chk != null ? "⚠ " + chk.Substring(5) + "\n\n" : "") + "Defender will stop scanning:\n" + value + (kind == "Process" ? "\n(and every file this program opens)" : "") + "\n\nOnly do this for software you trust. Add the exclusion?";
                if (MessageBox.Show(this, msg, "GameBoost: add exclusion", MessageBoxButtons.YesNo, chk != null ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            }
            else if (MessageBox.Show(this, "Defender will stop scanning every ." + value + " file on this PC. Continue?", "GameBoost: add exclusion", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            DefRun("Add-MpPreference -Exclusion" + kind + " " + Defender.Q(value), "Excluded from Defender: " + value,
                ns => (kind == "Path" ? ns.ExPaths : kind == "Process" ? ns.ExProcs : ns.ExExts).Any(x => string.Equals(x.TrimEnd('\\'), value.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)));
        }
    }
}
