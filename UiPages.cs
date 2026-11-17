using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

namespace GameBoost
{
    static class Tiers
    {
        public static Color Of(string tier)
        {
            switch (tier)
            {
                case "High-end": return Theme.Accent;
                case "Upper mid-range": return Theme.Blue;
                case "Mid-range": return Color.FromArgb(240, 205, 70);
                case "Entry-level": return Theme.Warn;
                default: return Color.FromArgb(255, 95, 95);
            }
        }
        public static Color Level(int l) { return l == 0 ? Theme.Accent : l == 1 ? Theme.Blue : l == 2 ? Theme.Warn : Color.FromArgb(255, 95, 95); }
        public static string LevelName(int l) { return l == 0 ? "GOOD" : l == 1 ? "INFO" : l == 2 ? "WARNING" : "PROBLEM"; }
    }

    class Pill : Control
    {
        public Color Col = Theme.Accent; float k = 1;
        public Pill(string t, Color c) { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); Font = Theme.F(8f, true); Set(t, c); }
        public void Set(string t, Color c)
        {
            Text = t; Col = c;
            using (var g = CreateGraphics()) Size = new Size((int)g.MeasureString(t.ToUpper(), Font).Width + 22, 24);
            k = 0; Anim.Tween(0, 1, 380, v => { k = v; Invalidate(); });
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent != null ? Parent.BackColor : Theme.Card); Theme.Hq(g);
            float s = .7f + .3f * k; float w = Width * s, h = Height * s;
            var r = new RectangleF((Width - w) / 2, (Height - h) / 2, w - 1, h - 1);
            using (var p = Theme.RR(r, r.Height / 2))
            {
                using (var b = new SolidBrush(Color.FromArgb((int)(45 * k), Col))) g.FillPath(b, p);
                using (var pen = new Pen(Color.FromArgb((int)(140 * k), Col))) g.DrawPath(pen, p);
            }
            if (k > .5f) using (var b = new SolidBrush(Theme.Mix(Parent.BackColor, Col, (k - .5f) * 2))) g.DrawString(Text.ToUpper(), Font, b, new RectangleF(0, 0, Width, Height), new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        }
    }

    class TierMeter : Control
    {
        float v;
        public TierMeter() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); Size = new Size(440, 64); }
        public void Set(double score) { Anim.Tween(0, (float)Math.Max(0, Math.Min(5, score)), 1400, x => { v = x; Invalidate(); }); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent.BackColor); Theme.Hq(g);
            var track = new RectangleF(2, 8, Width - 4, 12);
            using (var p = Theme.RR(track, 6)) using (var b = new SolidBrush(Theme.Card2)) g.FillPath(b, p);
            if (v > .05f)
            {
                var fr = new RectangleF(track.X, track.Y, track.Width * v / 5f, track.Height);
                using (var p = Theme.RR(fr, 6))
                using (var b = new LinearGradientBrush(track, Color.Red, Color.Green, 0f))
                {
                    var cb = new ColorBlend { Colors = new[] { Color.FromArgb(255, 95, 95), Theme.Warn, Color.FromArgb(240, 205, 70), Theme.Blue, Theme.Accent }, Positions = new[] { 0f, .3f, .55f, .75f, 1f } };
                    b.InterpolationColors = cb; g.FillPath(b, p);
                }
                float x = fr.Right; using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x - 8, track.Y - 3, 18, 18);
            }
            string[] labels = { "Low", "Entry", "Mid", "Upper-mid", "High-end" };
            using (var f = Theme.F(8f)) using (var b = new SolidBrush(Theme.Dim))
                for (int i = 0; i < 5; i++) g.DrawString(labels[i], f, b, new RectangleF(track.X + track.Width * i / 5f, 30, track.Width / 5f, 20), new StringFormat { Alignment = StringAlignment.Center });
        }
    }

    class ProgressLine : Control
    {
        float v, target, shimmer; public bool Busy;
        public ProgressLine() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); Height = 8; }
        bool running;
        public void Set(float t)
        {
            target = t; if (running) return; running = true;
            Anim.Run(() => { if (IsDisposed) return false; v += (target - v) * .12f; shimmer = (shimmer + .02f) % 1.4f; Invalidate(); bool more = Busy || Math.Abs(target - v) > .002f; running = more; return more; });
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent.BackColor); Theme.Hq(g);
            var r = new RectangleF(0, 1, Width - 1, Height - 2);
            using (var p = Theme.RR(r, r.Height / 2)) using (var b = new SolidBrush(Theme.Card2)) g.FillPath(b, p);
            if (v > .005f)
            {
                var f = new RectangleF(0, 1, r.Width * v, r.Height);
                using (var p = Theme.RR(f, r.Height / 2))
                {
                    using (var b = new LinearGradientBrush(r, Theme.Accent, Theme.Accent2, 0f)) g.FillPath(b, p);
                    if (Busy)
                    {
                        float sx = (shimmer - .2f) * r.Width; var sr = new RectangleF(sx, 0, r.Width * .2f + 1, Height);
                        using (var b = new LinearGradientBrush(sr, Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 0f))
                        {
                            b.InterpolationColors = new ColorBlend { Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(110, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) }, Positions = new[] { 0f, .5f, 1f } };
                            g.SetClip(p); g.FillRectangle(b, sr); g.ResetClip();
                        }
                    }
                }
            }
        }
    }

    class ResultRow : Control
    {
        Result r; float k;
        public ResultRow(Result res) { r = res; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); Height = 78; Margin = new Padding(0, 0, 0, 8); }
        public void Appear(int delay = 0) { Anim.Delay(delay, () => Anim.Tween(0, 1, 420, v => { k = v; Invalidate(); })); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; var bg = Parent.BackColor; g.Clear(bg); Theme.Hq(g);
            float off = (1 - k) * 28; var col = Tiers.Level(r.Level);
            g.TranslateTransform(off, 0);
            var rr = new RectangleF(0, 0, Width - 1 - off, Height - 1);
            using (var p = Theme.RR(rr, 12))
            {
                using (var b = new SolidBrush(Theme.Mix(bg, Theme.Card, k))) g.FillPath(b, p);
                using (var pen = new Pen(Theme.Mix(bg, Theme.Line, k))) g.DrawPath(pen, p);
            }
            using (var p = Theme.RR(new RectangleF(0, 12, 4, Height - 25), 2)) using (var b = new SolidBrush(Theme.Mix(bg, col, k))) g.FillPath(b, p);
            string lvl = Tiers.LevelName(r.Level);
            using (var f = Theme.F(7.5f, true))
            {
                var sz = g.MeasureString(lvl, f); var pr = new RectangleF(20, 14, sz.Width + 14, 20);
                using (var p = Theme.RR(pr, 10)) using (var b = new SolidBrush(Theme.Mix(bg, Color.FromArgb(50, col), k))) g.FillPath(b, p);
                using (var b = new SolidBrush(Theme.Mix(bg, col, k))) g.DrawString(lvl, f, b, pr, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                using (var ft = Theme.F(10.5f, true)) using (var b = new SolidBrush(Theme.Mix(bg, Theme.Fg, k))) g.DrawString(r.Title, ft, b, pr.Right + 10, 14);
            }
            using (var f = Theme.F(10.5f, true)) using (var b = new SolidBrush(Theme.Mix(bg, Theme.Fg, k)))
                g.DrawString(r.Value ?? "", f, b, new RectangleF(rr.Width * .5f, 12, rr.Width * .5f - 18, 24), new StringFormat { Alignment = StringAlignment.Far, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            using (var f = Theme.F(9f)) using (var b = new SolidBrush(Theme.Mix(bg, Theme.Dim, k)))
                g.DrawString(r.Detail ?? "", f, b, new RectangleF(20, 40, rr.Width - 40, Height - 42), new StringFormat { Trimming = StringTrimming.EllipsisWord });
        }
    }

    class LiveChart : Control
    {
        public List<float> A = new List<float>(), B = new List<float>(); public Color CA = Theme.Warn, CB = Theme.Blue; public string LA = "GPU °C", LB = "CPU %";
        public LiveChart() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        public void Add(float a, float b) { A.Add(a); B.Add(b); if (A.Count > 120) { A.RemoveAt(0); B.RemoveAt(0); } Invalidate(); }
        public void Clear() { A.Clear(); B.Clear(); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent.BackColor); Theme.Hq(g);
            var r = new RectangleF(34, 8, Width - 44, Height - 30);
            using (var pen = new Pen(Theme.Line) { DashStyle = DashStyle.Dot }) using (var f = Theme.F(7.5f)) using (var b = new SolidBrush(Theme.Dim))
                for (int i = 0; i <= 4; i++) { float y = r.Bottom - r.Height * i / 4f; g.DrawLine(pen, r.X, y, r.Right, y); g.DrawString((i * 25).ToString(), f, b, 4, y - 7); }
            Action<List<float>, Color> draw = (list, c) =>
            {
                var pts = list.Select((v, i) => new PointF(r.X + r.Width * (i + 120 - list.Count) / 119f, r.Bottom - r.Height * Math.Max(0, Math.Min(100, float.IsNaN(v) ? 0 : v)) / 100f)).ToArray();
                if (pts.Length < 2) return;
                var fill = pts.Concat(new[] { new PointF(pts.Last().X, r.Bottom), new PointF(pts[0].X, r.Bottom) }).ToArray();
                using (var b = new LinearGradientBrush(r, Color.FromArgb(60, c), Color.FromArgb(0, c), 90f)) g.FillPolygon(b, fill);
                using (var pen = new Pen(c, 2f)) g.DrawLines(pen, pts);
            };
            draw(B, CB); draw(A, CA);
            using (var f = Theme.F(8f, true))
            {
                using (var b = new SolidBrush(CA)) g.DrawString("● " + LA, f, b, r.X, r.Bottom + 6);
                using (var b = new SolidBrush(CB)) g.DrawString("● " + LB, f, b, r.X + 90, r.Bottom + 6);
            }
        }
    }

    partial class MainForm
    {
        HwInfo hw; FlowLayoutPanel specFlow, healthFlow; ProgressLine detectBar;
        ModernButton bRun, bSession; ProgressLine hProg; Label hStatus, hVerdict, hCounts, sLive; LiveChart chart; Card runCard, monCard;
        List<Control> healthRows = new List<Control>(), sessionRows = new List<Control>();
        SessionMonitor session; double cpuPeak; bool testing;

        void StartDetect()
        {
            new Thread(() =>
            {
                HwInfo h; try { h = Specs.Detect(); } catch { h = new HwInfo { Tier = "Unknown", CpuTier = "?", GpuTier = "?", RamTier = "?", Outlook = "" }; }
                try { BeginInvoke((Action)(() => { hw = h; FillSpecs(); })); } catch { }
            }) { IsBackground = true }.Start();
        }

        FlowLayoutPanel VFlow()
        {
            var f = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, 24) };
            f.Resize += (s, e) => { foreach (Control c in f.Controls) c.Width = f.ClientSize.Width - 22; };
            f.ControlAdded += (s, e) => e.Control.Width = f.ClientSize.Width - 22;
            return f;
        }

        // ---------- My PC ----------
        Panel BuildSpecs()
        {
            var page = NewPage(); specFlow = VFlow();
            var wait = new Card { Height = 90 };
            var wl = L("Detecting your hardware...", 11f, Theme.Fg, true); wl.Location = new Point(22, 20);
            detectBar = new ProgressLine { Location = new Point(22, 54), Width = 400, Busy = true }; detectBar.Set(.6f);
            wait.Controls.Add(wl); wait.Controls.Add(detectBar);
            specFlow.Controls.Add(wait);
            page.Controls.Add(specFlow);
            return page;
        }

        Card SpecCard(string glyph, Color col, string title, string main, string[] lines, string tier)
        {
            var c = new Card { Height = 52 + 30 + lines.Length * 22 + 6, Margin = new Padding(0, 0, 0, 12), Glow = col };
            var ic = new Label { Text = glyph, Font = Theme.Icons(16f), ForeColor = col, AutoSize = true, Location = new Point(20, 20), BackColor = Color.Transparent };
            var t = L(title.ToUpper(), 8f, Theme.Dim, true); t.Location = new Point(58, 16);
            var m = new Label { Text = main, Font = Theme.F(12.5f, true), ForeColor = Theme.Fg, Location = new Point(56, 34), AutoSize = false, Height = 28, AutoEllipsis = true, BackColor = Color.Transparent };
            c.Controls.Add(ic); c.Controls.Add(t); c.Controls.Add(m);
            for (int i = 0; i < lines.Length; i++) { var l = L(lines[i], 9.5f, Theme.Dim); l.Location = new Point(58, 70 + i * 22); c.Controls.Add(l); }
            Pill pill = null;
            if (tier != null) { pill = new Pill(tier, Tiers.Of(tier)); c.Controls.Add(pill); }
            c.Resize += (s, e) => { m.Width = c.Width - 80 - (pill != null ? pill.Width + 10 : 0); if (pill != null) pill.Location = new Point(c.Width - pill.Width - 18, 18); };
            return c;
        }

        void FillSpecs()
        {
            specFlow.SuspendLayout();
            foreach (Control c in specFlow.Controls.Cast<Control>().ToList()) { specFlow.Controls.Remove(c); c.Dispose(); }

            var hero = new Card { Height = 178, Margin = new Padding(0, 0, 0, 14), Glow = Tiers.Of(hw.Tier) };
            var o1 = L("OVERALL RATING", 8f, Theme.Dim, true); o1.Location = new Point(24, 20);
            var o2 = L(hw.Tier, 26f, Tiers.Of(hw.Tier), true); o2.Location = new Point(20, 38);
            var o3 = L(hw.Outlook, 10f, Theme.Fg); o3.Location = new Point(24, 92);
            var meter = new TierMeter(); hero.Controls.AddRange(new Control[] { o1, o2, o3, meter });
            int px = 24; foreach (var kv in new[] { new[] { "CPU", hw.CpuTier }, new[] { "GPU", hw.GpuTier }, new[] { "RAM", hw.RamTier } })
            { var p = new Pill(kv[0] + ": " + kv[1], Tiers.Of(kv[1])); p.Location = new Point(px, 130); hero.Controls.Add(p); px += p.Width + 8; }
            hero.Resize += (s, e) => meter.Location = new Point(Math.Max(px + 20, hero.Width - meter.Width - 28), 74);
            specFlow.Controls.Add(hero); meter.Set(hw.Overall);

            double vram = hw.VramBytes / 1073741824.0;
            specFlow.Controls.Add(SpecCard("", Theme.Blue, "Processor", hw.CpuName, new[] { hw.Cores + " cores / " + hw.Threads + " threads", "Base clock " + (hw.BaseMHz / 1000.0).ToString("0.00") + " GHz", hw.IsRyzen ? "AMD · temperature limit " + hw.CpuTempLimit + " °C" : "Temperature limit " + hw.CpuTempLimit + " °C" }, hw.CpuTier));
            specFlow.Controls.Add(SpecCard("", Theme.Accent, "Graphics", hw.GpuName, new[] { (vram >= 1 ? vram.ToString("0") + " GB VRAM" : "Shared memory"), "Driver " + hw.Driver, "Max safe temperature ~" + hw.GpuTempLimit + " °C" }, hw.GpuTier));
            string ramSpeed = hw.RamCfg > 0 ? "Running at " + hw.RamCfg + " MT/s" + (hw.XmpLikelyOff && !hw.IsLaptop ? "  ·  XMP/EXPO looks OFF" : "") : "Speed unknown";
            specFlow.Controls.Add(SpecCard("", Theme.Pink, "Memory", (hw.RamBytes / 1073741824.0).ToString("0") + " GB " + hw.RamType, new[] { ramSpeed, hw.Sticks + (hw.Sticks == 1 ? " stick (single channel)" : " sticks"), hw.RamParts.Length > 0 ? hw.RamParts : "-" }, hw.RamTier));
            var disks = hw.Disks.Take(4).Select(d => (d.System ? "★ " : "") + d.Model + "  ·  " + (d.Size / 1e9).ToString("N0") + " GB  ·  " + d.Type).ToArray();
            specFlow.Controls.Add(SpecCard("", Theme.Warn, "Storage", hw.Disks.Count + (hw.Disks.Count == 1 ? " drive" : " drives"), disks.Length > 0 ? disks : new[] { "No drives detected" }, null));
            specFlow.Controls.Add(SpecCard("", Color.FromArgb(240, 205, 70), "Display", hw.Resolution.Length > 0 ? hw.Resolution + " @ " + hw.RefreshHz + " Hz" : "Unknown",
                new[] { hw.RefreshHz > 0 && hw.RefreshHz <= 60 ? "Running at 60 Hz. If your monitor supports more, set it in Settings > Display > Advanced display." : "High refresh rate active." }, null));
            specFlow.Controls.Add(SpecCard("", Theme.Dim, "System", hw.Os, new[] { (hw.IsLaptop ? "Laptop" : "Desktop") + (hw.Board.Length > 0 ? "  ·  " + hw.Board : ""), "BIOS " + hw.Bios }, null));
            specFlow.ResumeLayout();
            foreach (Control c in specFlow.Controls) c.Width = specFlow.ClientSize.Width - 22;
            FillOc();
            bRun.Enabled = true; bSession.Enabled = true; hStatus.Text = "Ready. Close games and heavy apps first for accurate results.";
        }

        // ---------- Health ----------
        Panel BuildHealth()
        {
            var page = NewPage(); healthFlow = VFlow();

            runCard = new Card { Height = 168, Margin = new Padding(0, 0, 0, 14) };
            var t = L("Full health check", 13f, Theme.Fg, true); t.Location = new Point(22, 18);
            var d = L("~50 seconds: CPU boost + 20 s all-core stress, memory speed and bandwidth, a 1 GB drive test, GPU and power checks.", 9.5f, Theme.Dim); d.Location = new Point(24, 48);
            bRun = new ModernButton("Run health check", 190, true, "") { Location = new Point(22, 82), Enabled = false };
            bRun.Click += (s, e) => RunHealth();
            hProg = new ProgressLine { Location = new Point(24, 136) };
            hStatus = L("Detecting hardware...", 9f, Theme.Dim); hStatus.Location = new Point(230, 92);
            hVerdict = L("", 18f, Theme.Fg, true); hCounts = L("", 9f, Theme.Dim);
            runCard.Controls.AddRange(new Control[] { t, d, bRun, hProg, hStatus, hVerdict, hCounts });
            runCard.Resize += (s, e) => { hProg.Width = runCard.Width - 48; hVerdict.Location = new Point(runCard.Width - hVerdict.Width - 26, 78); hCounts.Location = new Point(runCard.Width - hCounts.Width - 26, 110); };
            healthFlow.Controls.Add(runCard);

            monCard = new Card { Height = 330, Margin = new Padding(0, 0, 0, 14), Glow = Theme.Warn };
            var mt = L("Gaming session monitor", 13f, Theme.Fg, true); mt.Location = new Point(22, 18);
            var md = L("Press Start, play your game for a few minutes, then press Stop to check heat and throttling under real load.", 9.5f, Theme.Dim); md.Location = new Point(24, 48);
            bSession = new ModernButton("Start monitoring", 190, false, "") { Location = new Point(22, 80), Enabled = false };
            bSession.Click += (s, e) => ToggleSession();
            sLive = L("", 9.5f, Theme.Fg); sLive.Location = new Point(230, 90);
            chart = new LiveChart { Location = new Point(14, 132), Height = 186 };
            monCard.Controls.AddRange(new Control[] { mt, md, bSession, sLive, chart });
            monCard.Resize += (s, e) => chart.Width = monCard.Width - 28;
            healthFlow.Controls.Add(monCard);

            page.Controls.Add(healthFlow);
            return page;
        }

        Label SectionLabel(string s) { var l = L(s, 12f, Theme.Fg, true); l.Margin = new Padding(2, 6, 0, 10); return l; }

        void AddRow(List<Control> group, Result r, ref int delay)
        {
            var row = new ResultRow(r); healthFlow.Controls.Add(row); group.Add(row); row.Appear(delay); delay += 70;
        }

        void ClearGroup(List<Control> g) { foreach (var c in g) { healthFlow.Controls.Remove(c); c.Dispose(); } g.Clear(); }

        void RunHealth()
        {
            if (testing || hw == null) return;
            if (session != null && session.Running) { Notify("Stop the gaming session monitor first", Theme.Warn); return; }
            testing = true; bRun.Enabled = false; bRun.Text = "Testing...";
            ClearGroup(healthRows);
            hVerdict.Text = ""; hCounts.Text = "";
            var head = SectionLabel("Health check results"); healthFlow.Controls.Add(head); healthRows.Add(head);
            // keep session results below health results
            foreach (var c in sessionRows) healthFlow.Controls.SetChildIndex(c, healthFlow.Controls.Count - 1);
            hProg.Busy = true; hProg.Set(0);
            var results = new List<Result>();
            var test = new HealthTest(hw);
            test.Progress += (s, f) => { try { BeginInvoke((Action)(() => { hStatus.Text = s; hProg.Set(f); })); } catch { } };
            test.Found += r => { lock (results) results.Add(r); try { BeginInvoke((Action)(() => { int d = 0; var row = new ResultRow(r); healthFlow.Controls.Add(row); healthFlow.Controls.SetChildIndex(row, healthFlow.Controls.IndexOf(head) + healthRows.Count); healthRows.Add(row); row.Appear(d); })); } catch { } };
            Notify("Health check started. Keep the PC idle for best accuracy.", Theme.Blue);
            new Thread(() =>
            {
                try { test.Run(); } catch (Exception ex) { results.Add(new Result { Level = 2, Title = "Test error", Value = "", Detail = ex.Message }); }
                cpuPeak = test.PeakMHz;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        testing = false; bRun.Enabled = true; bRun.Text = "Run again"; hProg.Busy = false; hProg.Set(1);
                        int p = results.Count(r => r.Level == 3), w = results.Count(r => r.Level == 2), g = results.Count(r => r.Level == 0);
                        hVerdict.Text = p > 0 ? "Needs attention" : w > 0 ? "Mostly healthy" : "Healthy";
                        hVerdict.ForeColor = p > 0 ? Tiers.Level(3) : w > 0 ? Theme.Warn : Theme.Accent;
                        hCounts.Text = g + " passed  ·  " + w + " warnings  ·  " + p + " problems";
                        hStatus.Text = "Finished " + DateTime.Now.ToString("h:mm tt");
                        runCard.Width += 0; runCard.PerformLayout(); hVerdict.Location = new Point(runCard.Width - hVerdict.Width - 26, 78); hCounts.Location = new Point(runCard.Width - hCounts.Width - 26, 110);
                        lock (results) lastHealth = results.ToList(); FillOc();
                        Notify("Health check: " + hVerdict.Text, hVerdict.ForeColor);
                    }));
                }
                catch { }
            }) { IsBackground = true, Priority = ThreadPriority.Highest }.Start();
        }

        void ToggleSession()
        {
            if (hw == null) return;
            if (testing) { Notify("Wait for the health check to finish", Theme.Warn); return; }
            if (session == null)
            {
                session = new SessionMonitor(hw);
                session.OnSample += s => { try { BeginInvoke((Action)(() => OnLive(s))); } catch { } };
            }
            if (!session.Running)
            {
                chart.Clear(); session.Start(); bSession.Text = "Stop & analyze"; bSession.Primary = true; bSession.Invalidate();
                chart.LA = hw.IsNvidia ? "GPU °C" : "CPU MHz/50"; sLive.Text = "Recording... start your game.";
                Notify("Session monitor recording. Go play!", Theme.Warn);
            }
            else
            {
                session.Stop(); bSession.Text = "Start monitoring"; bSession.Primary = false; bSession.Invalidate();
                ClearGroup(sessionRows);
                var head = SectionLabel("Gaming session results  (" + session.Samples.Count + " s recorded)"); healthFlow.Controls.Add(head); sessionRows.Add(head);
                int d = 0; lastSession = session.Verdict(cpuPeak); foreach (var r in lastSession) AddRow(sessionRows, r, ref d); FillOc();
                Notify("Session analyzed: see results below", Theme.Accent);
                Anim.Delay(150, () => { try { healthFlow.ScrollControlIntoView(head); } catch { } });
            }
        }

        void OnLive(SessionMonitor.Sample s)
        {
            Func<double, string, string> f = (v, u) => double.IsNaN(v) ? "-" : v.ToString("0") + u;
            sLive.Text = (hw.IsNvidia ? "GPU " + f(s.GpuT, " °C") + "   load " + f(s.GpuU, "%") + "   " + f(s.GpuClk, " MHz") + "   " + f(s.GpuW, " W") + "      " : "") +
                         "CPU " + f(s.CpuU, "%") + "   " + (double.IsNaN(s.CpuMHz) ? "-" : (s.CpuMHz / 1000).ToString("0.00") + " GHz") + "      " + (session.Samples.Count / 60) + ":" + (session.Samples.Count % 60).ToString("00");
            float a = hw.IsNvidia ? (float)s.GpuT : (float)(s.CpuMHz / 50);
            chart.Add(a, (float)s.CpuU);
        }
    }
}
