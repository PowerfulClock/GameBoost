using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Collections.Generic;
using System.Windows.Forms;

namespace GameBoost
{
    class ModernSlider : AnimControl
    {
        public int Min, Max = 100, Step = 1, Recommended = int.MaxValue; int val; public string Unit = " MHz"; public bool Signed = true;
        float shown; bool drag; public event EventHandler ValueChanged;
        public ModernSlider() { Height = 46; }
        public int Value { get { return val; } set { int v = Math.Max(Min, Math.Min(Max, (int)Math.Round((double)value / Step) * Step)); if (v == val) return; val = v; Kick(); if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); } }
        public void SetInstant(int v) { val = Math.Max(Min, Math.Min(Max, v)); shown = val; Invalidate(); }
        protected override bool Tick() { shown += (val - shown) * .3f; return Math.Abs(val - shown) > .3f; }
        RectangleF Track { get { return new RectangleF(10, 26, Width - 20, 6); } }
        float X(float v) { var t = Track; return t.X + t.Width * (v - Min) / Math.Max(1, Max - Min); }
        void FromMouse(int x) { var t = Track; Value = Min + (int)Math.Round((x - t.X) / t.Width * (Max - Min)); }
        protected override void OnMouseDown(MouseEventArgs e) { drag = true; FromMouse(e.X); base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (drag) FromMouse(e.X); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) { drag = false; base.OnMouseUp(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(ParentBg); Theme.Hq(g); var t = Track;
            using (var p = Theme.RR(t, 3)) using (var b = new SolidBrush(Theme.Card2)) g.FillPath(b, p);
            if (Recommended < Max) using (var p = Theme.RR(new RectangleF(X(Recommended), t.Y, t.Right - X(Recommended), t.Height), 3)) using (var b = new SolidBrush(Color.FromArgb(60, Theme.Warn))) g.FillPath(b, p);
            float zero = X(Math.Max(Min, 0)), kx = X(shown);
            var fill = new RectangleF(Math.Min(zero, kx), t.Y, Math.Abs(kx - zero), t.Height);
            Color c = shown > Recommended ? Theme.Warn : Theme.Accent;
            if (fill.Width > .5f) using (var p = Theme.RR(fill, 3)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
            float r = 8 + hover * 2 + press * 2;
            using (var b = new SolidBrush(Color.FromArgb((int)(50 * hover), c))) g.FillEllipse(b, kx - r - 4, t.Y + 3 - r - 4, (r + 4) * 2, (r + 4) * 2);
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, kx - r, t.Y + 3 - r, r * 2, r * 2);
            string s = (Signed && val > 0 ? "+" : "") + val + Unit;
            using (var f = Theme.F(9.5f, true)) using (var b = new SolidBrush(c)) { var sz = g.MeasureString(s, f); float bx = Math.Max(0, Math.Min(Width - sz.Width, kx - sz.Width / 2)); g.DrawString(s, f, b, bx, 0); }
        }
    }

    class KeepDialog : Form
    {
        int left = 15; Label lbl; Timer t; public bool Keep;
        public KeepDialog(string what)
        {
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen; Size = new Size(460, 200); BackColor = Theme.Card; TopMost = true; ShowInTaskbar = false;
            using (var p = Theme.RR(new RectangleF(0, 0, Width, Height), 16)) Region = new Region(p);
            var t1 = new Label { Text = "Keep these GPU settings?", Font = Theme.F(14f, true), ForeColor = Theme.Fg, AutoSize = true, Location = new Point(24, 20) };
            var t2 = new Label { Text = what, Font = Theme.F(9.5f), ForeColor = Theme.Dim, AutoSize = true, Location = new Point(26, 56) };
            lbl = new Label { Font = Theme.F(9.5f, true), ForeColor = Theme.Warn, AutoSize = true, Location = new Point(26, 84) };
            var keep = new ModernButton("Keep", 130, true) { Location = new Point(24, 132) }; keep.Click += (s, e) => { Keep = true; Close(); };
            var rev = new ModernButton("Revert", 130) { Location = new Point(166, 132) }; rev.Click += (s, e) => { Keep = false; Close(); };
            Controls.AddRange(new Control[] { t1, t2, lbl, keep, rev });
            Upd(); t = new Timer { Interval = 1000 }; t.Tick += (s, e) => { left--; Upd(); if (left <= 0) { Keep = false; Close(); } }; t.Start();
            Opacity = 0; Shown += (s, e) => Anim.Tween(0, 1, 200, v => Opacity = v);
        }
        void Upd() { lbl.Text = "Reverting automatically in " + left + " s if you don't confirm (in case the screen glitches or freezes)."; }
        protected override void OnFormClosed(FormClosedEventArgs e) { t.Stop(); base.OnFormClosed(e); }
    }

    class PresetTile : AnimControl
    {
        public string Title, Offsets, Gain, Risk; public Color Col; public bool Selected; float sel;
        public PresetTile() { Height = 118; }
        public void Select(bool v) { Selected = v; Kick(); }
        protected override bool Tick() { float t = Selected ? 1 : 0; sel += (t - sel) * .2f; return Math.Abs(t - sel) > .004f; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(ParentBg); Theme.Hq(g);
            float lift = hover * 2; var r = new RectangleF(1, 1 + 2 - lift, Width - 3, Height - 5);
            using (var p = Theme.RR(r, 12))
            {
                using (var b = new SolidBrush(Theme.Mix(Theme.Card2, Theme.Mix(Theme.Card2, Col, .12f), Math.Max(sel, hover * .5f)))) g.FillPath(b, p);
                using (var pen = new Pen(Theme.Mix(Theme.Line, Col, Math.Max(sel, hover * .5f)), 1 + sel)) g.DrawPath(pen, p);
            }
            using (var f = Theme.F(12f, true)) using (var b = new SolidBrush(Col)) g.DrawString(Title, f, b, r.X + 14, r.Y + 10);
            using (var f = Theme.F(8f, true)) using (var b = new SolidBrush(Theme.Dim)) { var sz = g.MeasureString(Risk, f); g.DrawString(Risk, f, b, r.Right - sz.Width - 12, r.Y + 15); }
            using (var f = Theme.F(9f)) using (var b = new SolidBrush(Theme.Dim)) g.DrawString(Offsets, f, b, r.X + 14, r.Y + 40);
            using (var f = Theme.F(15f, true)) using (var b = new SolidBrush(Theme.Fg)) g.DrawString(Gain, f, b, r.X + 12, r.Y + 62);
            using (var f = Theme.F(8f)) using (var b = new SolidBrush(Theme.Dim)) g.DrawString("est. FPS in GPU-heavy games", f, b, r.X + 14, r.Y + 92);
            if (sel > .02f) using (var b = new SolidBrush(Color.FromArgb((int)(255 * sel), Col))) g.FillEllipse(b, r.Right - 22, r.Bottom - 22, 10, 10);
        }
    }

    partial class MainForm
    {
        // applied GPU state (survives page rebuilds)
        int gCore, gMem; double gPower = double.NaN; DateTime gAppliedAt; Timer crashWatch; bool ocConsent;
        int stableCore, stableMem; public bool powerChanged; public double powerDefault;
        static string OcLog { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameBoost", "gpu_oc_history.txt"); } }

        // Rough FPS estimate: games scale ~0.7x with core clock %; memory OC adds up to ~3%.
        static double[] EstGain(int core, int mem, int boostMHz, int memHard)
        {
            double c = core * 100.0 / Math.Max(1200, boostMHz), m = mem > 0 ? Math.Min(3.0, 3.0 * mem / Math.Max(1, memHard)) : 0;
            double mid = c * 0.7 + m * 0.8; return new[] { mid * 0.6, mid * 1.1 };
        }
        static string Pct(double[] g) { return g[1] < 0.3 ? "~0%" : "+" + g[0].ToString(g[0] < 10 ? "0.#" : "0") + "-" + g[1].ToString(g[1] < 10 ? "0.#" : "0") + "%"; }

        StackCard GpuTunerCard()
        {
            var c = new StackCard { Glow = Theme.Accent, Margin = new Padding(0, 0, 0, 12) };
            var head = new Label { Text = "", Font = Theme.Icons(15f), ForeColor = Theme.Accent, AutoSize = true, BackColor = Color.Transparent, Location = new Point(22, 20) };
            var title = Wrap("GPU Tuner  ·  " + hw.GpuName, 12.5f, Theme.Fg, true); title.Left = 56;
            c.Controls.Add(head); c.AddRow(title, 0);

            if (!NvOc.Init())
            {
                c.AddRow(Wrap("GPU tuning isn't available: " + NvOc.Error + ". Use the NVIDIA App's Automatic Tuning instead.", 9.5f, Theme.Warn), 12);
                return c;
            }
            var core = NvOc.Read(NvOc.DOM_CORE); var mem = NvOc.Read(NvOc.DOM_MEM);
            gCore = core.DeltaKHz / 1000; gMem = mem.Found ? mem.DeltaKHz / 1000 : 0;
            var cc = NvOc.CoreCap(hw); var mc = NvOc.MemCap(hw);
            int coreMax = Math.Min(cc[1], core.MaxKHz > 0 ? core.MaxKHz / 1000 : cc[1]);
            int memMax = Math.Min(mc[1], mem.MaxKHz > 0 ? mem.MaxKHz / 1000 : mc[1]);
            bool fixHeat = lastHealth.Exists(r => r.Level == 3 && r.Title.Contains("GPU")) || lastSession.Exists(r => r.Level == 3 && r.Title.StartsWith("GPU"));

            c.AddRow(Wrap("Adds clock speed through the NVIDIA driver, the same way MSI Afterburner does. No voltage is added. The overclock stays active while GameBoost is open (minimize it while you play) and resets to stock when you close the app or restart, so a bad setting can never stop Windows from booting.", 9.5f, Theme.Dim), 10);
            if (fixHeat) c.AddRow(Wrap("⚠   Your GPU ran too hot in a Health Check or gaming session. Fix cooling before overclocking. The tuner is locked to small steps.", 9.5f, Color.FromArgb(255, 95, 95)), 8);
            if (fixHeat) { coreMax = Math.Min(coreMax, 45); memMax = Math.Min(memMax, 200); c.AddRow(Wrap("Heat lock active: core limited to +45 MHz and memory to +200 MHz until a new Health Check or gaming session shows normal temperatures.", 9f, Theme.Warn), 4); }

            int selIdx = -1;
            int boost = core.BaseKHz > 0 ? core.BaseKHz / 1000 : 2500;
            c.AddRow(Wrap("CHOOSE A LEVEL   (or fine-tune with the sliders below)", 8f, Theme.Dim, true), 16);
            var prow = new FlowLayoutPanel { Height = 122, BackColor = Theme.Card, WrapContents = false };
            Func<double, int, int> snap = (v, st) => (int)Math.Round(v / st) * st;
            var defs = new[] {
                new { N = "Low", K = 0.40, R = "LOW RISK", C = Theme.Accent },
                new { N = "Medium", K = 0.75, R = "MODERATE", C = Color.FromArgb(240, 205, 70) },
                new { N = "Heavy", K = 1.00, R = "HIGHER RISK", C = Theme.Warn } };
            var tiles = new System.Collections.Generic.List<PresetTile>(); var vals = new System.Collections.Generic.List<int[]>();
            int baseCore = Math.Min(cc[0], coreMax), baseMem = Math.Min(mc[0], memMax); int prevC = -1, prevM = -1;
            foreach (var d in defs)
            {
                int pc = Math.Max(15, Math.Min(coreMax, snap(baseCore * d.K, 15)));
                int pm = mem.Found && mem.Editable ? Math.Max(50, Math.Min(memMax, snap(baseMem * d.K, 50))) : 0;
                if (pc <= prevC) pc = Math.Min(coreMax, prevC + 15);
                if (pm > 0 && pm <= prevM) pm = Math.Min(memMax, prevM + 50);
                prevC = pc; prevM = pm;
                var t = new PresetTile { Title = d.N, Risk = d.R, Col = d.C, Offsets = "Core +" + pc + " MHz" + (pm > 0 ? "  ·  Memory +" + pm + " MHz" : ""), Gain = Pct(EstGain(pc, pm, boost, mc[1])), Margin = new Padding(0, 0, 10, 0) };
                tiles.Add(t); vals.Add(new[] { pc, pm }); prow.Controls.Add(t);
            }
            prow.Resize += (o, e) => { int w = (prow.Width - 24) / 3; foreach (var t in tiles) t.Width = w; };
            c.AddRow(prow, 6);

            c.AddRow(Wrap("CORE CLOCK OFFSET   (stock boost " + (core.BaseKHz / 1000) + " MHz · steps of 15 · orange = beyond typical safe range)", 8f, Theme.Dim, true), 16);
            var sCore = new ModernSlider { Min = 0, Max = coreMax, Step = 15, Recommended = cc[0] }; sCore.SetInstant(gCore);
            c.AddRow(sCore, 4);
            ModernSlider sMem = null;
            if (mem.Found && mem.Editable)
            {
                c.AddRow(Wrap("MEMORY CLOCK OFFSET   (steps of 50 · typical safe for your card: up to +" + mc[0] + ")", 8f, Theme.Dim, true), 12);
                sMem = new ModernSlider { Min = 0, Max = memMax, Step = 50, Recommended = mc[0] }; sMem.SetInstant(gMem);
                c.AddRow(sMem, 4);
            }
            ModernSlider sPow = null; var pl = NvOc.PowerLimits();
            if (pl != null)
            {
                c.AddRow(Wrap("POWER LIMIT   (default " + pl[1].ToString("0") + " W · max allowed by your card " + pl[2].ToString("0") + " W)", 8f, Theme.Dim, true), 12);
                sPow = new ModernSlider { Min = (int)pl[1], Max = (int)pl[2], Step = 5, Unit = " W", Signed = false }; sPow.SetInstant((int)pl[0]);
                c.AddRow(sPow, 4);
            }

            var est = Wrap("", 10f, Theme.Accent, true); c.AddRow(est, 12);
            var estNote = Wrap("Estimates assume the game is limited by your GPU. CPU-limited games (Roblox, Valorant, CS2 at high FPS) gain little to nothing. Your actual result depends on your card's silicon; testing tells the truth.", 8.5f, Theme.Dim); c.AddRow(estNote, 4);
            Action updEst = () =>
            {
                int vc = sCore.Value, vm = sMem != null ? sMem.Value : 0;
                est.Text = "Estimated gain at these settings:  " + Pct(EstGain(vc, vm, boost, mc[1])) + " FPS" + (vc > cc[0] || (sMem != null && vm > mc[0]) ? "   ·   beyond typical safe range" : "");
                est.ForeColor = vc > cc[0] || (sMem != null && vm > mc[0]) ? Theme.Warn : Theme.Accent;
                if (selIdx >= 0 && (vals[selIdx][0] != vc || (sMem != null && vals[selIdx][1] != vm))) selIdx = -1;
                for (int i = 0; i < tiles.Count; i++) tiles[i].Select(i == selIdx);
            };
            sCore.ValueChanged += (o, e) => updEst(); if (sMem != null) sMem.ValueChanged += (o, e) => updEst();
            for (int i = 0; i < tiles.Count; i++) { int k = i; tiles[i].Click += (o, e) => { selIdx = k; sCore.Value = vals[k][0]; if (sMem != null) sMem.Value = vals[k][1]; updEst(); Notify(defs[k].N + " preset selected. Press Apply to use it", defs[k].C); }; }
            updEst();

            var status = Wrap(gCore != 0 || gMem != 0 ? "Active now: core +" + gCore + " MHz, memory +" + gMem + " MHz." : "Running at stock clocks.", 9.5f, gCore != 0 || gMem != 0 ? Theme.Accent : Theme.Dim);
            c.AddRow(status, 12);

            var live = Wrap("Live GPU clock: reading...", 9.5f, Theme.Fg); c.AddRow(live, 6);
            int stockBoost = boost - gCore;
            var lt = new Timer { Interval = 2000 }; bool busyQ = false;
            lt.Tick += (o, e) =>
            {
                if (live.IsDisposed) { lt.Stop(); lt.Dispose(); return; }
                if (busyQ || !live.Visible) return; busyQ = true;
                new System.Threading.Thread(() =>
                {
                    var d = Nv.Query("clocks.sm,utilization.gpu"); double clk = Nv.Num(d, "clocks.sm"), u = Nv.Num(d, "utilization.gpu");
                    try { live.BeginInvoke((Action)(() => {
                        busyQ = false; if (double.IsNaN(clk)) { live.Text = "Live GPU clock: unavailable"; return; }
                        live.Text = "Live GPU clock: " + clk.ToString("0") + " MHz at " + u.ToString("0") + "% load" + (u < 60 ? "   (idle clocks are low; check this while a game is running)" : clk > stockBoost + 10 ? "   \u2714 above stock boost (" + stockBoost + " MHz)" : "   (stock boost " + stockBoost + " MHz)");
                        live.ForeColor = u >= 60 && clk > stockBoost + 10 ? Theme.Accent : Theme.Fg; }));
                    } catch { busyQ = false; }
                }) { IsBackground = true }.Start();
            };
            lt.Start();

            var row = new FlowLayoutPanel { Height = 44, BackColor = Theme.Card, WrapContents = false };
            var bApply = new ModernButton("Apply", 140, true, "") { Margin = new Padding(0, 4, 10, 0) };
            var bReset = new ModernButton("Reset to stock", 160, false, "") { Margin = new Padding(0, 4, 10, 0) };
            var bTest = new ModernButton("Test while gaming", 180, false, "") { Margin = new Padding(0, 4, 10, 0) };
            row.Controls.AddRange(new Control[] { bApply, bReset, bTest });
            c.AddRow(row, 14);
            c.AddRow(Wrap("How to use: raise core by +15 to +30 at a time, Apply, then play 15-30 min with 'Test while gaming'. If a game crashes, flickers or shows colored dots, go back a step. Then do the same for memory. GameBoost watches for driver crashes and resets to stock automatically.", 9f, Theme.Dim), 12);

            bApply.Click += (s, e) =>
            {
                if (!ocConsent)
                {
                    var r = MessageBox.Show(this, "GPU overclocking runs your graphics card faster than its factory settings.\n\n• It can cause game crashes, artifacts or driver resets\n• Higher clocks mean more heat and power draw\n• Manual overclocks are at your own risk and may not be covered by warranty\n\nGameBoost limits the range, adds no voltage, auto-reverts if you don't confirm, and resets to stock if the driver crashes. Settings are cleared on restart.\n\nContinue?", "GameBoost: GPU overclocking", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r != DialogResult.Yes) return; ocConsent = true;
                }
                int nc = sCore.Value, nm = sMem != null ? sMem.Value : gMem; double np = sPow != null ? sPow.Value : double.NaN;
                int oc = gCore, om = gMem; double op = sPow != null ? pl[0] : double.NaN;
                bool okc = NvOc.SetDelta(NvOc.DOM_CORE, nc * 1000);
                bool okm = sMem == null || NvOc.SetDelta(NvOc.DOM_MEM, nm * 1000);
                string perr = null; if (sPow != null && Math.Abs(np - pl[0]) >= 1) perr = NvOc.SetPower(np);
                if (!okc || !okm) { Notify("The driver rejected that setting", Theme.Warn); return; }
                int rc = NvOc.Read(NvOc.DOM_CORE).DeltaKHz / 1000, rm = sMem != null ? NvOc.Read(NvOc.DOM_MEM).DeltaKHz / 1000 : nm;
                if (Math.Abs(rc - nc) > 1 || Math.Abs(rm - nm) > 1)
                {
                    NvOc.SetDelta(NvOc.DOM_CORE, oc * 1000); if (sMem != null) NvOc.SetDelta(NvOc.DOM_MEM, om * 1000);
                    MessageBox.Show(this, "The NVIDIA driver accepted the request but didn't apply it.\n\nAsked for: core +" + nc + ", memory +" + nm + "\nDriver reports: core +" + rc + ", memory +" + rm + "\n\nThis driver version may block offsets from apps. Use the NVIDIA App's Automatic Tuning or MSI Afterburner instead.", "GameBoost: overclock not applied", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (perr != null) Notify("Power limit not changed: this card/driver doesn't allow it", Theme.Warn);
                string what = "Core +" + nc + " MHz, memory +" + nm + " MHz" + (sPow != null && perr == null ? ", power " + np.ToString("0") + " W" : "");
                bool keep; using (var dlg = new KeepDialog(what)) { dlg.ShowDialog(this); keep = dlg.Keep; }
                if (!keep)
                {
                    NvOc.SetDelta(NvOc.DOM_CORE, oc * 1000); if (sMem != null) NvOc.SetDelta(NvOc.DOM_MEM, om * 1000);
                    if (sPow != null && perr == null && Math.Abs(np - op) >= 1) NvOc.SetPower(op);
                    sCore.Value = oc; if (sMem != null) sMem.Value = om;
                    Notify("Reverted to previous GPU settings", Theme.Dim); return;
                }
                if (sPow != null && perr == null && Math.Abs(np - op) >= 1) { powerChanged = Math.Abs(np - pl[1]) >= 1; powerDefault = pl[1]; }
                stableCore = oc; stableMem = om; gCore = nc; gMem = nm; gAppliedAt = DateTime.Now;
                status.Text = "\u2714 Driver confirms: core +" + gCore + " MHz, memory +" + gMem + " MHz are active. Test it while gaming."; status.ForeColor = Theme.Accent; c.Relayout();
                Log("GPU OC applied: " + what); SaveOcHistory("applied " + what);
                Notify("GPU overclock applied. Now test it in a game", Theme.Accent);
                StartCrashWatch();
            };
            bReset.Click += (s, e) =>
            {
                NvOc.SetDelta(NvOc.DOM_CORE, 0); if (sMem != null) NvOc.SetDelta(NvOc.DOM_MEM, 0);
                if (sPow != null) { NvOc.SetPower(pl[1]); powerChanged = false; }
                gCore = gMem = 0; sCore.Value = 0; if (sMem != null) sMem.Value = 0; if (sPow != null) sPow.Value = (int)pl[1];
                status.Text = "Running at stock clocks."; status.ForeColor = Theme.Dim; c.Relayout();
                Notify("GPU reset to stock", Theme.Dim); SaveOcHistory("reset to stock");
            };
            bTest.Click += (s, e) => { ShowPage(2); if (session == null || !session.Running) ToggleSession(); };
            return c;
        }

        void StartCrashWatch()
        {
            if (crashWatch == null)
            {
                crashWatch = new Timer { Interval = 15000 };
                crashWatch.Tick += (s, e) =>
                {
                    if (gCore == 0 && gMem == 0) { crashWatch.Stop(); return; }
                    if (NvOc.DriverCrashedSince(gAppliedAt))
                    {
                        string bad = "core +" + gCore + ", memory +" + gMem;
                        NvOc.SetDelta(NvOc.DOM_CORE, stableCore * 1000); NvOc.SetDelta(NvOc.DOM_MEM, stableMem * 1000);
                        gCore = stableCore; gMem = stableMem; gAppliedAt = DateTime.Now;
                        SaveOcHistory("UNSTABLE (driver crash) at " + bad + ", reverted");
                        Notify("Driver crash detected at " + bad + ". Reverted to the last stable settings", Color.FromArgb(255, 95, 95));
                        FillOc();
                    }
                };
            }
            crashWatch.Start();
        }

        void SaveOcHistory(string s)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(OcLog)); File.AppendAllText(OcLog, DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "  " + hw.GpuName + "  " + s + "\r\n"); } catch { }
        }
    }
}
