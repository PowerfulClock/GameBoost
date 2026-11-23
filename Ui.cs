using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GameBoost
{
    // ================= Theme & helpers =================
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(15, 17, 21), Side = Color.FromArgb(20, 23, 28), Card = Color.FromArgb(27, 31, 38),
            Card2 = Color.FromArgb(36, 41, 50), Line = Color.FromArgb(45, 51, 62), Fg = Color.FromArgb(236, 239, 244), Dim = Color.FromArgb(138, 146, 160),
            Accent = Color.FromArgb(118, 214, 40), Accent2 = Color.FromArgb(0, 200, 83), Warn = Color.FromArgb(255, 176, 64), Blue = Color.FromArgb(64, 156, 255), Pink = Color.FromArgb(214, 92, 255);
        static string iconFont;
        public static Font F(float size, bool bold = false)
        {
            string fam = HasFont("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";
            return new Font(fam, size, bold ? FontStyle.Bold : FontStyle.Regular);
        }
        public static Font Icons(float size)
        {
            if (iconFont == null) iconFont = HasFont("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            return new Font(iconFont, size);
        }
        static bool HasFont(string n) { using (var f = new Font(n, 10)) return f.Name == n; }
        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
        public static GraphicsPath RR(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
        }
        public static void Hq(Graphics g) { g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit; g.PixelOffsetMode = PixelOffsetMode.HighQuality; }
    }

    static class Anim
    {
        static readonly Timer t = new Timer { Interval = 15 };
        static readonly List<Func<bool>> list = new List<Func<bool>>();
        static Anim() { t.Tick += (s, e) => { for (int i = list.Count - 1; i >= 0; i--) { bool keep; try { keep = list[i](); } catch { keep = false; } if (!keep) list.RemoveAt(i); } if (list.Count == 0) t.Stop(); }; }
        public static void Run(Func<bool> f) { list.Add(f); t.Start(); }
        public static float Ease(float k) { return 1 - (float)Math.Pow(1 - k, 3); }
        public static void Tween(float from, float to, int ms, Action<float> set, Action done = null)
        {
            var sw = Stopwatch.StartNew();
            Run(() => { float k = Math.Min(1f, sw.ElapsedMilliseconds / (float)ms); set(from + (to - from) * Ease(k)); if (k >= 1) { if (done != null) done(); return false; } return true; });
        }
        public static void Delay(int ms, Action a) { var sw = Stopwatch.StartNew(); Run(() => { if (sw.ElapsedMilliseconds < ms) return true; a(); return false; }); }
    }

    static class Logo
    {
        static readonly PointF[] Bolt = { new PointF(.60f, .12f), new PointF(.28f, .56f), new PointF(.48f, .56f), new PointF(.40f, .88f), new PointF(.72f, .44f), new PointF(.52f, .44f) };
        public static void Draw(Graphics g, RectangleF r, float glow = 0)
        {
            Theme.Hq(g);
            float w = r.Width;
            if (glow > 0)
                for (int i = 6; i >= 1; i--)
                    using (var p = Theme.RR(RectangleF.Inflate(r, i * w * .025f, i * w * .025f), w * .3f))
                    using (var b = new SolidBrush(Color.FromArgb((int)(10 * glow), Theme.Accent))) g.FillPath(b, p);
            using (var p = Theme.RR(r, w * .24f))
            {
                using (var b = new LinearGradientBrush(r, Color.FromArgb(30, 35, 44), Color.FromArgb(14, 16, 20), 90f)) g.FillPath(b, p);
                using (var pen = new Pen(Theme.Accent, Math.Max(1.5f, w * .04f))) g.DrawPath(pen, p);
            }
            var pts = Bolt.Select(pt => new PointF(r.X + pt.X * w, r.Y + pt.Y * r.Height)).ToArray();
            using (var b = new LinearGradientBrush(r, Color.FromArgb(182, 243, 106), Color.FromArgb(0, 200, 83), 90f)) g.FillPolygon(b, pts);
        }
    }

    // ================= Animated controls =================
    class AnimControl : Control
    {
        protected float hover, hoverT, press, pressT; bool running;
        public AnimControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand; ForeColor = Theme.Fg; Font = Theme.F(10f);
        }
        protected void Kick()
        {
            if (running) return; running = true;
            Anim.Run(() =>
            {
                if (IsDisposed) { running = false; return false; }
                hover += (hoverT - hover) * .2f; press += (pressT - press) * .35f;
                bool more = Math.Abs(hoverT - hover) > .004f || Math.Abs(pressT - press) > .004f;
                more |= Tick();
                if (!more) { hover = hoverT; press = pressT; }
                Invalidate(); running = more; return more;
            });
        }
        protected virtual bool Tick() { return false; }
        protected Color ParentBg { get { return Parent != null ? Parent.BackColor : Theme.Bg; } }
        protected override void OnMouseEnter(EventArgs e) { hoverT = 1; Kick(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hoverT = 0; pressT = 0; Kick(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressT = 1; Kick(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressT = 0; Kick(); base.OnMouseUp(e); }
    }

    class ModernButton : AnimControl
    {
        public bool Primary; public string Glyph;
        public ModernButton(string text, int w, bool primary = false, string glyph = null) { Text = text; Size = new Size(w, 38); Primary = primary; Glyph = glyph; }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(ParentBg); Theme.Hq(g);
            float s = press * 1.5f;
            var r = new RectangleF(s, s, Width - 1 - s * 2, Height - 1 - s * 2);
            using (var p = Theme.RR(r, 9))
            {
                if (Primary)
                {
                    using (var b = new LinearGradientBrush(r, Theme.Mix(Theme.Accent, Color.White, hover * .15f), Theme.Mix(Theme.Accent2, Color.White, hover * .15f), 0f)) g.FillPath(b, p);
                }
                else
                {
                    using (var b = new SolidBrush(Theme.Mix(Theme.Card2, Color.FromArgb(52, 59, 72), hover))) g.FillPath(b, p);
                    using (var pen = new Pen(Theme.Mix(Theme.Line, Theme.Accent, hover * .6f))) g.DrawPath(pen, p);
                }
            }
            var fc = Primary ? Color.FromArgb(10, 14, 8) : Theme.Fg;
            var tf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            if (Glyph != null)
            {
                using (var f = Theme.F(10f, true)) using (var gi = Theme.Icons(11f)) using (var b = new SolidBrush(fc))
                {
                    var ts = g.MeasureString(Text, f); float total = 22 + ts.Width; float x = (Width - total) / 2;
                    g.DrawString(Glyph, gi, b, new RectangleF(x, 0, 20, Height), tf);
                    g.DrawString(Text, f, b, new RectangleF(x + 22, 0, ts.Width + 4, Height), new StringFormat { LineAlignment = StringAlignment.Center });
                }
            }
            else using (var f = Theme.F(10f, true)) using (var b = new SolidBrush(fc)) g.DrawString(Text, f, b, new RectangleF(0, 0, Width, Height), tf);
        }
    }

    class ToggleSwitch : AnimControl
    {
        bool on; float k;
        public event EventHandler Toggled;
        public ToggleSwitch() { Size = new Size(52, 28); }
        public bool Checked { get { return on; } set { if (on == value) return; on = value; Kick(); } }
        public void SetInstant(bool v) { on = v; k = v ? 1 : 0; Invalidate(); }
        protected override bool Tick() { float t = on ? 1 : 0; k += (t - k) * .22f; return Math.Abs(t - k) > .003f; }
        protected override void OnClick(EventArgs e) { on = !on; Kick(); if (Toggled != null) Toggled(this, e); base.OnClick(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(ParentBg); Theme.Hq(g);
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            using (var p = Theme.RR(r, r.Height / 2))
            {
                using (var b = new SolidBrush(Theme.Mix(Theme.Mix(Color.FromArgb(58, 64, 76), Color.FromArgb(70, 78, 92), hover), Theme.Accent, k))) g.FillPath(b, p);
            }
            float d = r.Height - 6 + press * 2; float x = r.X + 3 + (r.Width - 6 - d) * k - (press * 2 * k);
            float w = d + press * 4;
            using (var p = Theme.RR(new RectangleF(x, r.Y + 3 - press, w, d), d / 2))
            using (var b = new SolidBrush(Theme.Mix(Color.FromArgb(220, 224, 230), Color.White, k))) g.FillPath(b, p);
        }
    }

    // Big animated power button for Gaming Mode
    class PowerButton : AnimControl
    {
        public bool On; float angle, glow, glowT, onK;
        public PowerButton() { Size = new Size(170, 170); }
        public void SetOn(bool v) { On = v; Kick(); }
        protected override bool Tick()
        {
            float t = On ? 1 : 0; onK += (t - onK) * .12f;
            if (On) { angle = (angle + 2.4f) % 360; glow = .5f + .5f * (float)Math.Sin(Environment.TickCount / 400.0); }
            return On || Math.Abs(t - onK) > .003f;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(ParentBg); Theme.Hq(g);
            float s = Math.Min(Width, Height); float pad = 14 + press * 4;
            var r = new RectangleF(pad, pad, s - pad * 2, s - pad * 2);
            if (onK > .01f)
                for (int i = 5; i >= 1; i--)
                {
                    var gr = RectangleF.Inflate(r, i * 2.2f, i * 2.2f);
                    using (var b = new SolidBrush(Color.FromArgb((int)(14 * onK * (.6f + .4f * glow)), Theme.Accent))) g.FillEllipse(b, gr);
                }
            using (var b = new LinearGradientBrush(r, Theme.Mix(Theme.Card2, Color.FromArgb(48, 56, 68), hover), Theme.Card, 90f)) g.FillEllipse(b, r);
            using (var pen = new Pen(Theme.Mix(Theme.Line, Theme.Accent, .25f * hover), 2)) g.DrawEllipse(pen, r);
            // spinning ring
            var rr = RectangleF.Inflate(r, -9, -9);
            using (var pen = new Pen(Color.FromArgb((int)(40 + 30 * hover), 255, 255, 255), 5)) g.DrawEllipse(pen, rr);
            if (onK > .01f)
                using (var pen = new Pen(Theme.Mix(Theme.Accent2, Theme.Accent, glow), 5) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawArc(pen, rr, angle, 300 * onK);
                }
            var col = Theme.Mix(Theme.Mix(Theme.Dim, Theme.Fg, hover), Theme.Accent, onK);
            using (var f = Theme.Icons(s * .2f)) using (var b = new SolidBrush(col))
                g.DrawString("", f, b, new RectangleF(0, -s * .05f, s, s), new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            using (var f = Theme.F(8.5f, true)) using (var b = new SolidBrush(col))
                g.DrawString(On ? "ON" : "OFF", f, b, new RectangleF(0, s * .62f, s, 20), new StringFormat { Alignment = StringAlignment.Center });
        }
    }

    // Stat card with animated ring + sparkline
    class StatCard : AnimControl
    {
        public string Title, Sub = ""; public Color Col; float v, target; public string suffix = "%"; public string Override;
        Queue<float> hist = new Queue<float>();
        public StatCard(string title, Color c) { Title = title; Col = c; Size = new Size(260, 120); Cursor = Cursors.Default; BackColor = Theme.Bg; }
        public void Set(float val, string sub) { target = Math.Max(0, Math.Min(100, val)); Sub = sub; hist.Enqueue(target); while (hist.Count > 40) hist.Dequeue(); Kick(); }
        protected override bool Tick() { v += (target - v) * .12f; return Math.Abs(target - v) > .05f; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(ParentBg); Theme.Hq(g);
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var p = Theme.RR(r, 14))
            {
                using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, p);
                using (var pen = new Pen(Theme.Mix(Theme.Line, Col, .5f * hover))) g.DrawPath(pen, p);
            }
            // sparkline
            if (hist.Count > 1)
            {
                var arr = hist.ToArray(); float x0 = 16, w = Width - 32, y0 = Height - 12, h = 34;
                var pts = arr.Select((a, i) => new PointF(x0 + w * i / 39f + (40 - arr.Length) * w / 39f, y0 - a / 100f * h)).ToArray();
                var fill = pts.Concat(new[] { new PointF(pts.Last().X, y0), new PointF(pts.First().X, y0) }).ToArray();
                using (var b = new LinearGradientBrush(new RectangleF(0, y0 - h, 10, h + 1), Color.FromArgb(70, Col), Color.FromArgb(0, Col), 90f)) g.FillPolygon(b, fill);
                using (var pen = new Pen(Color.FromArgb(200, Col), 1.6f)) g.DrawLines(pen, pts);
            }
            // ring
            var rr = new RectangleF(Width - 78, 14, 58, 58);
            using (var pen = new Pen(Theme.Card2, 7)) g.DrawEllipse(pen, rr);
            using (var pen = new Pen(Col, 7) { StartCap = LineCap.Round, EndCap = LineCap.Round }) if (v > .5f) g.DrawArc(pen, rr, -90, 360 * v / 100f);
            using (var f = Theme.F(9.5f)) using (var b = new SolidBrush(Theme.Dim)) g.DrawString(Title, f, b, 16, 14);
            using (var f = Theme.F(24f, true)) using (var b = new SolidBrush(Theme.Fg)) g.DrawString(Override ?? (v.ToString("0") + suffix), f, b, 12, 32);
            using (var f = Theme.F(8.5f)) using (var b = new SolidBrush(Theme.Dim)) g.DrawString(Sub, f, b, 16, 72);
        }
    }

    // Rounded card panel with animated border glow on hover
    class Card : Panel
    {
        static readonly List<Card> all = new List<Card>();
        static Timer poll;
        float hover, hoverT; public Color Glow = Theme.Accent; public int Radius = 14;
        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card; ForeColor = Theme.Fg; all.Add(this);
            if (poll == null)
            {
                poll = new Timer { Interval = 40 };
                poll.Tick += (s, e) =>
                {
                    foreach (var c in all.ToArray())
                    {
                        if (c.IsDisposed) { all.Remove(c); continue; }
                        if (!c.Visible || !c.IsHandleCreated) continue;
                        bool inside = c.ClientRectangle.Contains(c.PointToClient(Control.MousePosition));
                        float t = inside ? 1 : 0;
                        if (Math.Abs(c.hoverT - t) > .001f) { c.hoverT = t; c.Animate(); }
                    }
                };
                poll.Start();
            }
        }
        bool running;
        void Animate()
        {
            if (running) return; running = true;
            Anim.Run(() => { if (IsDisposed) return running = false; hover += (hoverT - hover) * .18f; bool more = Math.Abs(hoverT - hover) > .004f; if (!more) hover = hoverT; Invalidate(false); running = more; return more; });
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent != null ? Parent.BackColor : Theme.Bg); Theme.Hq(g);
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var p = Theme.RR(r, Radius))
            {
                using (var b = new SolidBrush(BackColor)) g.FillPath(b, p);
                using (var pen = new Pen(Theme.Mix(Theme.Line, Glow, hover * .55f), 1 + hover * .5f)) g.DrawPath(pen, p);
            }
            base.OnPaint(e);
        }
    }

    class Toast : Control
    {
        string msg = ""; Color col = Theme.Accent; int ver;
        public Toast() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); Visible = false; Size = new Size(360, 56); BackColor = Color.Transparent; }
        public void Show(Control host, string text, Color? c = null)
        {
            msg = text; col = c ?? Theme.Accent; int my = ++ver;
            using (var g = CreateGraphics()) using (var f = Theme.F(10f)) Width = Math.Min(560, (int)g.MeasureString(text, f).Width + 70);
            Left = host.ClientSize.Width - Width - 22; int target = host.ClientSize.Height - Height - 22;
            Top = host.ClientSize.Height + 4; Visible = true; BringToFront(); Invalidate();
            Anim.Tween(Top, target, 320, v => Top = (int)v);
            Anim.Delay(2800, () => { if (my != ver) return; Anim.Tween(Top, host.ClientSize.Height + 4, 280, v => Top = (int)v, () => { if (my == ver) Visible = false; }); });
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent.BackColor); Theme.Hq(g);
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var p = Theme.RR(r, 12)) { using (var b = new SolidBrush(Theme.Card2)) g.FillPath(b, p); using (var pen = new Pen(Color.FromArgb(120, col))) g.DrawPath(pen, p); }
            using (var b = new SolidBrush(col)) g.FillEllipse(b, 18, Height / 2 - 5, 10, 10);
            using (var f = Theme.F(10f)) using (var b = new SolidBrush(Theme.Fg)) g.DrawString(msg, f, b, new RectangleF(40, 0, Width - 50, Height), new StringFormat { LineAlignment = StringAlignment.Center });
        }
    }

    class NavButton : AnimControl
    {
        public string Glyph; public bool Selected;
        public NavButton(string text, string glyph) { Text = text; Glyph = glyph; Size = new Size(186, 44); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(ParentBg); Theme.Hq(g);
            float a = Math.Max(hover * .6f, Selected ? 1 : 0);
            using (var p = Theme.RR(new RectangleF(0, 2, Width - 1, Height - 5), 10))
            using (var b = new SolidBrush(Color.FromArgb((int)(a * 255), Theme.Card))) g.FillPath(b, p);
            var c = Selected ? Theme.Fg : Theme.Mix(Theme.Dim, Theme.Fg, hover);
            float nudge = hover * 3;
            using (var f = Theme.Icons(13f)) using (var b = new SolidBrush(Selected ? Theme.Accent : c)) g.DrawString(Glyph, f, b, new RectangleF(16 + nudge, 0, 30, Height), new StringFormat { LineAlignment = StringAlignment.Center });
            using (var f = Theme.F(10.5f, Selected)) using (var b = new SolidBrush(c)) g.DrawString(Text, f, b, new RectangleF(48 + nudge, 0, Width - 50, Height), new StringFormat { LineAlignment = StringAlignment.Center });
        }
    }

    class Sidebar : Panel
    {
        public float IndY = -100, IndT = -100;
        public Sidebar() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); BackColor = Theme.Side; }
        public void MoveTo(float y) { IndT = y; if (IndY < 0) IndY = y; Anim.Run(() => { IndY += (IndT - IndY) * .2f; Invalidate(new Rectangle(0, 0, 8, Height)); return Math.Abs(IndT - IndY) > .3f; }); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(BackColor); Theme.Hq(g);
            using (var p = Theme.RR(new RectangleF(2, IndY + 10, 4, 24), 2)) using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, p);
            using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
            Logo.Draw(g, new RectangleF(22, 24, 38, 38));
            using (var f = Theme.F(14f, true)) using (var b = new SolidBrush(Theme.Fg)) g.DrawString("GameBoost", f, b, 68, 24);
            using (var f = Theme.F(8.5f)) using (var b = new SolidBrush(Theme.Dim)) g.DrawString("Performance toolkit", f, b, 70, 46);
        }
    }

    class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Card2; } }
        public override Color MenuItemSelected { get { return Color.FromArgb(52, 59, 72); } }
        public override Color MenuItemBorder { get { return Theme.Line; } }
        public override Color MenuBorder { get { return Theme.Line; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card2; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card2; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card2; } }
    }

    // ================= Splash =================
    class Splash : Form
    {
        float angle, prog, progT, glow; string status = "Starting...";
        public Splash()
        {
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen; Size = new Size(460, 300);
            BackColor = Theme.Bg; ShowInTaskbar = true; Opacity = 0; DoubleBuffered = true; Text = "GameBoost";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            using (var p = Theme.RR(new RectangleF(0, 0, Width, Height), 18)) Region = new Region(p);
            Anim.Run(() => { if (IsDisposed) return false; angle = (angle + 5) % 360; prog += (progT - prog) * .1f; glow = .5f + .5f * (float)Math.Sin(Environment.TickCount / 300.0); Invalidate(); return true; });
        }
        public void Step(string s, float p) { status = s; progT = p; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g);
            using (var b = new LinearGradientBrush(ClientRectangle, Color.FromArgb(22, 26, 32), Theme.Bg, 90f)) g.FillRectangle(b, ClientRectangle);
            float s = 92, x = (Width - s) / 2, y = 56;
            Logo.Draw(g, new RectangleF(x, y, s, s), glow);
            using (var pen = new Pen(Color.FromArgb(160, Theme.Accent), 3) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(pen, x - 16, y - 16, s + 32, s + 32, angle, 80);
            using (var pen = new Pen(Color.FromArgb(70, Theme.Accent2), 3) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(pen, x - 16, y - 16, s + 32, s + 32, angle + 180, 50);
            var cf = new StringFormat { Alignment = StringAlignment.Center };
            using (var f = Theme.F(18f, true)) using (var b = new SolidBrush(Theme.Fg)) g.DrawString("GameBoost", f, b, new RectangleF(0, 184, Width, 34), cf);
            using (var f = Theme.F(9f)) using (var b = new SolidBrush(Theme.Dim)) g.DrawString(status, f, b, new RectangleF(0, 218, Width, 20), cf);
            var bar = new RectangleF(90, 254, Width - 180, 5);
            using (var p = Theme.RR(bar, 2.5f)) using (var b = new SolidBrush(Theme.Card2)) g.FillPath(b, p);
            if (prog > .01f) { var fr = new RectangleF(bar.X, bar.Y, bar.Width * prog, bar.Height); using (var p = Theme.RR(fr, 2.5f)) using (var b = new LinearGradientBrush(bar, Theme.Accent, Theme.Accent2, 0f)) g.FillPath(b, p); }
        }
    }

    // ================= Main window =================
    partial class MainForm : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);

        Monitor mon = new Monitor();
        List<ProcRow> rows = new List<ProcRow>();
        PerformanceCounter totalCpu;
        Sidebar side; Panel host; Label hTitle, hSub; Toast toast;
        List<NavButton> navs = new List<NavButton>(); List<Panel> pages = new List<Panel>(); int current = -1;
        // dashboard
        StatCard cCpu, cGpu, cRam; PowerButton power; Label gmStatus; ComboBox cbGame; ToggleSwitch tOverlay; ModernButton bFree; TextBox logBox; Card hero, logCard;
        // processes
        ListView lv; TextBox search; int sortCol = 1; bool sortDesc = true; int hotRow = -1;
        OverlayForm overlay;
        Timer timer;

        public MainForm()
        {
            Text = "GameBoost"; Size = new Size(1220, 900); MinimumSize = new Size(1020, 680); StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.F(9.5f); DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            side = new Sidebar { Dock = DockStyle.Left, Width = 214 };
            string[] names = { "Dashboard", "My PC", "Health Check", "Overclocking", "Security", "Processes", "Performance", "Tips" };
            string[] glyphs = { "\uE80F", "\uE977", "\uE95E", "\uEC4A", "\uEA18", "\uE9D9", "\uE945", "\uE82F" };
            for (int i = 0; i < names.Length; i++)
            {
                var n = new NavButton(names[i], glyphs[i]) { Location = new Point(14, 96 + i * 48) };
                int idx = i; n.Click += (s, e) => ShowPage(idx);
                navs.Add(n); side.Controls.Add(n);
            }
            var ver = new Label { Text = "v" + Updater.Current, ForeColor = Theme.Dim, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Font = Theme.F(8.5f) };
            side.Controls.Add(ver); side.Resize += (s, e) => ver.Location = new Point(24, side.Height - 34);

            host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            hTitle = new Label { Font = Theme.F(20f, true), ForeColor = Theme.Fg, AutoSize = true, Location = new Point(34, 24) };
            hSub = new Label { Font = Theme.F(9.5f), ForeColor = Theme.Dim, AutoSize = true, Location = new Point(36, 62) };
            host.Controls.Add(hTitle); host.Controls.Add(hSub);
            toast = new Toast(); host.Controls.Add(toast);

            pages.Add(BuildDashboard()); pages.Add(BuildSpecs()); pages.Add(BuildHealth()); pages.Add(BuildOc()); pages.Add(BuildSecurity()); pages.Add(BuildProcesses()); pages.Add(BuildToggles()); pages.Add(BuildTips()); pages.Add(BuildUpdate());
            foreach (var p in pages) { p.Visible = false; host.Controls.Add(p); }
            host.Resize += (s, e) => LayoutPages();

            Controls.Add(host); Controls.Add(side);

            try { totalCpu = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); totalCpu.NextValue(); }
            catch { try { totalCpu = new PerformanceCounter("Processor", "% Processor Time", "_Total"); totalCpu.NextValue(); } catch { } }

            GamingMode.RecoverFromCrash(Log);
            mon.Sample();
            timer = new Timer { Interval = 1500 }; timer.Tick += (s, e) => RefreshData(); timer.Start();
            FormClosing += (s, e) => { if (!restartingForUpdate && scanType != 0 && MessageBox.Show(this, "A virus scan is still running. It will keep running in Windows after GameBoost closes. Close anyway?", "GameBoost", MessageBoxButtons.YesNo) != DialogResult.Yes) { e.Cancel = true; return; } if (powerChanged) NvOc.SetPower(powerDefault); if (gCore != 0 || gMem != 0) { NvOc.SetDelta(NvOc.DOM_CORE, 0); NvOc.SetDelta(NvOc.DOM_MEM, 0); } if (session != null) session.Stop(); if (GamingMode.Active) GamingMode.Disable(Log); if (FpsTracker.Running) FpsTracker.Stop(); };
            Shown += (s, e) => { Updater.CleanupOld(); CheckForUpdate(); LayoutPages(); ShowPage(0); RefreshData(); StartDetect(); Anim.Tween(0, 1, 350, v => Opacity = v); };
            Opacity = 0;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); int cap = Theme.Bg.R | (Theme.Bg.G << 8) | (Theme.Bg.B << 16); DwmSetWindowAttribute(Handle, 35, ref cap, 4); } catch { }
        }

        void LayoutPages()
        {
            foreach (var p in pages) { p.Top = 96; p.Height = Math.Max(100, host.ClientSize.Height - 96); p.Width = host.ClientSize.Width - 34 - 30; if (!slideRunning) p.Left = 34; }
            LayoutDashboard();
        }

        bool slideRunning;
        void ShowPage(int i)
        {
            if (i == current) return;
            string[] titles = { "Dashboard", "My PC", "Health Check", "Overclocking", "Security", "Processes", "Performance", "Tips", "Update" };
            string[] subs = { "Live system load and one-click Gaming Mode", "Your hardware and where it ranks", "Is your PC performing the way its specs say it should?", "What's safe to overclock on YOUR hardware, and how", "Antivirus status, Defender settings and exclusions", "Everything running right now and what it's using", "Independent switches that stay how you set them", "What actually helps FPS, beyond Windows", "A new version of GameBoost is ready" };
            for (int n = 0; n < navs.Count; n++) { navs[n].Selected = n == i; navs[n].Invalidate(); }
            side.MoveTo(navs[i].Top);
            if (current >= 0) pages[current].Visible = false;
            current = i; var p = pages[i];
            hTitle.Text = titles[i]; hSub.Text = subs[i];
            Anim.Tween(14, 0, 300, v => { hTitle.Left = 34 + (int)v; hSub.Left = 36 + (int)v; });
            p.Left = 34 + 50; p.Visible = true; slideRunning = true;
            Anim.Tween(50, 0, 340, v => p.Left = 34 + (int)v, () => slideRunning = false);
            if (i == 5) RefreshList();
            if (i == 4) LoadSecurity();
        }

        void Notify(string s, Color? c = null) { toast.Show(host, s, c); Log(s); }
        void Log(string s) { if (logBox != null) logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "   " + s + "\r\n"); }
        Panel NewPage() { return new Panel { BackColor = Theme.Bg, AutoScroll = false }; }
        Label L(string t, float size, Color c, bool bold = false) { return new Label { Text = t, Font = Theme.F(size, bold), ForeColor = c, AutoSize = true, BackColor = Color.Transparent }; }

        // ---------- Dashboard ----------
        Panel dash;
        Panel BuildDashboard()
        {
            dash = NewPage();
            cCpu = new StatCard("CPU", Theme.Blue); cGpu = new StatCard("GPU", Theme.Accent); cRam = new StatCard("Memory", Theme.Pink);
            dash.Controls.AddRange(new Control[] { cCpu, cGpu, cRam });

            BuildTemps();
            hero = new Card { Height = 230 };
            power = new PowerButton { Location = new Point(20, 30) };
            power.Click += (s, e) => ToggleGaming();
            var t1 = L("Gaming Mode", 16f, Theme.Fg, true); t1.Location = new Point(210, 26);
            gmStatus = L("Off. Pick your game, then press the power button.", 9.5f, Theme.Dim); gmStatus.Location = new Point(212, 60);
            var t2 = L("GAME TO BOOST", 8f, Theme.Dim, true); t2.Location = new Point(212, 100);
            cbGame = new ComboBox { Location = new Point(212, 120), Width = 360, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Theme.Card2, ForeColor = Theme.Fg, Font = Theme.F(10f) };
            cbGame.DropDown += (s, e) => FillGames();
            cbGame.Format += (s, e) => { if (e.ListItem is KeyValuePair<int, string>) e.Value = ((KeyValuePair<int, string>)e.ListItem).Value; };
            bFree = new ModernButton("Free up RAM", 150, false, "") { Location = new Point(212, 168) };
            bFree.Click += (s, e) => FreeRam();
            tOverlay = new ToggleSwitch { Location = new Point(390, 173) };
            tOverlay.Toggled += (s, e) => ToggleOverlay();
            var t3 = L("FPS overlay", 10f, Theme.Fg); t3.Location = new Point(450, 176);
            hero.Controls.AddRange(new Control[] { power, t1, gmStatus, t2, cbGame, bFree, tOverlay, t3 });
            dash.Controls.Add(hero);

            logCard = new Card();
            var lt = L("Activity", 11f, Theme.Fg, true); lt.Location = new Point(18, 14);
            logBox = new TextBox { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Dim, Font = new Font("Consolas", 9f), ScrollBars = ScrollBars.None, Location = new Point(18, 44) };
            logCard.Controls.Add(lt); logCard.Controls.Add(logBox);
            logCard.Resize += (s, e) => logBox.Size = new Size(logCard.Width - 36, Math.Max(10, logCard.Height - 58));
            dash.Controls.Add(logCard);
            return dash;
        }

        void LayoutDashboard()
        {
            if (dash == null) return;
            int w = dash.Width, gap = 16, cw = (w - gap * 2) / 3;
            cCpu.Bounds = new Rectangle(0, 0, cw, 124); cGpu.Bounds = new Rectangle(cw + gap, 0, cw, 124); cRam.Bounds = new Rectangle((cw + gap) * 2, 0, w - (cw + gap) * 2, 124);
            LayoutTemps(w, 140);
            hero.Bounds = new Rectangle(0, 280, w, 230);
            logCard.Bounds = new Rectangle(0, 526, w, Math.Max(80, dash.Height - 526 - 24));
        }

        // ---------- Processes ----------
        Panel BuildProcesses()
        {
            var page = NewPage();
            var card = new Card { Dock = DockStyle.Fill, Padding = new Padding(14, 60, 14, 14) };
            var bar = new Panel { Dock = DockStyle.None, Location = new Point(16, 14), Height = 34, Width = 600, BackColor = Theme.Card };
            var sbox = new Card { BackColor = Theme.Card2, Radius = 9, Size = new Size(300, 34), Location = new Point(0, 0) };
            var sg = new Label { Text = "", Font = Theme.Icons(10.5f), ForeColor = Theme.Dim, AutoSize = true, Location = new Point(10, 9) };
            search = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Card2, ForeColor = Theme.Fg, Font = Theme.F(10f), Location = new Point(34, 8), Width = 256 };
            search.TextChanged += (s, e) => RefreshList();
            sbox.Controls.Add(sg); sbox.Controls.Add(search);
            var hint = L("Right-click a process for options. Click headers to sort.", 9f, Theme.Dim); hint.Location = new Point(316, 9);
            bar.Controls.Add(sbox); bar.Controls.Add(hint);

            lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, OwnerDraw = true, BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Fg, Font = Theme.F(9.5f), HeaderStyle = ColumnHeaderStyle.Clickable };
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(lv, true, null);
            lv.SmallImageList = new ImageList { ImageSize = new Size(1, 32) };
            lv.Columns.Add("Process", 240); lv.Columns.Add("CPU", 120); lv.Columns.Add("GPU", 120); lv.Columns.Add("Memory", 110); lv.Columns.Add("PID", 70); lv.Columns.Add("Type", 110); lv.Columns.Add("Window", 300);
            lv.DrawColumnHeader += DrawHeader;
            lv.DrawItem += (s, e) => { };
            lv.DrawSubItem += DrawCell;
            lv.ColumnClick += (s, e) => { if (sortCol == e.Column) sortDesc = !sortDesc; else { sortCol = e.Column; sortDesc = e.Column != 0; } RefreshList(); };
            lv.MouseMove += (s, e) => { var it = lv.GetItemAt(e.X, e.Y); int idx = it != null ? it.Index : -1; if (idx != hotRow) { int old = hotRow; hotRow = idx; InvRow(old); InvRow(idx); } };
            lv.MouseLeave += (s, e) => { int old = hotRow; hotRow = -1; InvRow(old); };
            var ctx = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkColors()), ForeColor = Theme.Fg, Font = Theme.F(9.5f), ShowImageMargin = false };
            ctx.Items.Add("Set as game to boost", null, (s, e) => { var r = Sel(); if (r != null) { FillGames(r.Pid); Notify("Game set: " + r.Name); } });
            ctx.Items.Add("Open file location", null, (s, e) => { var r = Sel(); if (r == null) return; try { using (var p = Process.GetProcessById(r.Pid)) Process.Start("explorer.exe", "/select,\"" + p.MainModule.FileName + "\""); } catch { } });
            ctx.Items.Add("What is this? (search online)", null, (s, e) => { var r = Sel(); if (r != null) Process.Start("https://www.bing.com/search?q=" + Uri.EscapeDataString(r.Name + ".exe what is it")); });
            foreach (ToolStripItem it in ctx.Items) it.ForeColor = Theme.Fg;
            lv.ContextMenuStrip = ctx;
            card.Controls.Add(lv); card.Controls.Add(bar);
            page.Controls.Add(card);
            page.Padding = new Padding(0, 0, 0, 24);
            return page;
        }
        void InvRow(int i) { if (i >= 0 && i < lv.Items.Count) lv.Invalidate(lv.Items[i].Bounds); }
        ProcRow Sel() { return lv.SelectedItems.Count == 0 ? null : lv.SelectedItems[0].Tag as ProcRow; }

        void DrawHeader(object s, DrawListViewColumnHeaderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(b, e.Bounds);
            string arrow = e.ColumnIndex == sortCol ? (sortDesc ? "  ▾" : "  ▴") : "";
            using (var f = Theme.F(8.5f, true)) using (var b = new SolidBrush(e.ColumnIndex == sortCol ? Theme.Accent : Theme.Dim))
                e.Graphics.DrawString(e.Header.Text.ToUpper() + arrow, f, b, new RectangleF(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height), new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter });
            using (var pen = new Pen(Theme.Line)) e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        }

        void DrawCell(object s, DrawListViewSubItemEventArgs e)
        {
            var g = e.Graphics; var r = e.Bounds; var row = (ProcRow)e.Item.Tag;
            Color bg = e.Item.Selected ? Color.FromArgb(40, 58, 34) : e.ItemIndex == hotRow ? Theme.Card2 : Theme.Card;
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            var tr = new RectangleF(r.X + 10, r.Y, r.Width - 14, r.Height);
            Color fc = !row.HasWindow ? Theme.Dim : Theme.Fg;
            if (e.ColumnIndex == 1 || e.ColumnIndex == 2)
            {
                double v = e.ColumnIndex == 1 ? row.Cpu : row.Gpu; Color c = e.ColumnIndex == 1 ? Theme.Blue : Theme.Accent;
                if (v >= (e.ColumnIndex == 1 ? 10 : 20)) c = Theme.Warn;
                var track = new RectangleF(r.X + 54, r.Y + r.Height / 2 - 3, r.Width - 66, 6);
                using (var p = Theme.RR(track, 3)) using (var b = new SolidBrush(Theme.Line)) g.FillPath(b, p);
                float w = (float)Math.Min(1, v / 100.0) * track.Width; if (v > 0.05 && w < 4) w = 4;
                if (w > 0) using (var p = Theme.RR(new RectangleF(track.X, track.Y, w, 6), 3)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
                using (var f = Theme.F(9.5f)) using (var b = new SolidBrush(v >= 0.1 ? Theme.Fg : Theme.Dim)) g.DrawString(v.ToString("0.0") + "%", f, b, new RectangleF(r.X + 8, r.Y, 46, r.Height), sf);
                return;
            }
            if (e.ColumnIndex == 0)
            {
                using (var b = new SolidBrush(row.HasWindow ? Theme.Accent : Theme.Line)) g.FillEllipse(b, r.X + 12, r.Y + r.Height / 2 - 3, 6, 6);
                tr = new RectangleF(r.X + 26, r.Y, r.Width - 30, r.Height);
            }
            if (e.ColumnIndex == 5)
            {
                var pill = new RectangleF(r.X + 8, r.Y + 7, row.HasWindow ? 48 : 86, r.Height - 14);
                using (var p = Theme.RR(pill, pill.Height / 2)) using (var b = new SolidBrush(row.HasWindow ? Color.FromArgb(40, Theme.Accent) : Theme.Card2)) g.FillPath(b, p);
                using (var f = Theme.F(8f, true)) using (var b = new SolidBrush(row.HasWindow ? Theme.Accent : Theme.Dim)) g.DrawString(e.SubItem.Text, f, b, pill, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                return;
            }
            using (var f = e.ColumnIndex == 0 ? Theme.F(9.5f, true) : Theme.F(9.5f)) using (var b = new SolidBrush(fc)) g.DrawString(e.SubItem.Text, f, b, tr, sf);
        }

        // ---------- Performance toggles ----------
        Panel BuildToggles()
        {
            var page = NewPage();
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, 24) };
            Action<Control> addCard = c => { flow.Controls.Add(c); };
            flow.Resize += (s, e) => { foreach (Control c in flow.Controls) c.Width = flow.ClientSize.Width - 22; };
            foreach (var t in Tweaks.All())
            {
                var card = new Card { Height = 84, Margin = new Padding(0, 0, 0, 12) };
                var name = L(t.Name, 11.5f, Theme.Fg, true); name.Location = new Point(22, 16);
                var desc = new Label { Text = t.Description, Font = Theme.F(9f), ForeColor = Theme.Dim, Location = new Point(22, 42), AutoSize = false, Height = 36, BackColor = Color.Transparent };
                var sw = new ToggleSwitch();
                card.Controls.Add(name); card.Controls.Add(desc); card.Controls.Add(sw);
                if (t.NeedsReboot)
                {
                    var tag = new Label { Text = "RESTART NEEDED", Font = Theme.F(7.5f, true), ForeColor = Theme.Warn, AutoSize = true, BackColor = Color.Transparent };
                    card.Controls.Add(tag);
                    name.SizeChanged += (s, e) => tag.Location = new Point(name.Right + 10, 20);
                    tag.Location = new Point(name.Right + 10, 20);
                }
                card.Resize += (s, e) => { sw.Location = new Point(card.Width - 80, 28); desc.Width = card.Width - 130; };
                var tw = t;
                bool cur = false; try { cur = tw.IsOn(); } catch { }
                sw.SetInstant(cur);
                sw.Toggled += (s, e) =>
                {
                    try { Cursor = Cursors.WaitCursor; tw.Set(sw.Checked); Notify(tw.Name + (sw.Checked ? " on" : " off") + (tw.NeedsReboot ? " (restart to apply)" : ""), sw.Checked ? Theme.Accent : Theme.Dim); }
                    catch (Exception ex) { Notify("Couldn't change: " + ex.Message, Theme.Warn); }
                    finally { Cursor = Cursors.Default; bool now = sw.Checked; try { now = tw.IsOn(); } catch { } if (now != sw.Checked) sw.Checked = now; }
                };
                addCard(card);
            }
            var vbs = new Card { Height = 84, Margin = new Padding(0, 0, 0, 12), Glow = Theme.Warn };
            var vn = L("Memory Integrity / VBS", 11.5f, Theme.Warn, true); vn.Location = new Point(22, 16);
            var vd = new Label { Text = "Turning it off can add 5-10% FPS in some games, but it's a security protection and some anti-cheats need it. GameBoost leaves it alone.", Font = Theme.F(9f), ForeColor = Theme.Dim, Location = new Point(22, 42), Height = 36, BackColor = Color.Transparent };
            var vb = new ModernButton("Open setting", 130);
            vb.Click += (s, e) => { try { Process.Start("windowsdefender://coreisolation"); } catch { } };
            vbs.Controls.Add(vn); vbs.Controls.Add(vd); vbs.Controls.Add(vb);
            vbs.Resize += (s, e) => { vb.Location = new Point(vbs.Width - 152, 23); vd.Width = vbs.Width - 190; };
            addCard(vbs);
            page.Controls.Add(flow);
            return page;
        }

        // ---------- Tips ----------
        Panel BuildTips()
        {
            var page = NewPage();
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, 24) };
            var sections = new[] {
                new { G = "", T = "What Gaming Mode does", C = Theme.Accent, Items = new[] { "Raises your chosen game to High CPU priority", "Lowers priority and trims RAM of idle background apps (nothing is closed)", "Pauses Windows Update, BITS, Delivery Optimization and Search indexing", "Sets the system timer to 1 ms and clears the standby memory cache", "Everything is restored when you turn it off or close the app" } },
                new { G = "", T = "FPS overlay", C = Theme.Blue, Items = new[] { "White, see-through, click-through counter in the top-right, updates every 0.1 s", "Tracks the game picked under Game to boost, otherwise whatever is rendering", "Works with DirectX 9-12 games in Borderless/Windowed mode" } },
                new { G = "", T = "NVIDIA settings", C = Theme.Accent2, Items = new[] { "Keep drivers current in the NVIDIA App (DDU for a clean reinstall)", "Manage 3D settings > Power management mode: Prefer maximum performance", "Low Latency Mode On, or use NVIDIA Reflex in-game when available", "G-SYNC: V-Sync on in NVCP, off in game, cap FPS ~3 below refresh" } },
                new { G = "", T = "Hardware & BIOS (one time)", C = Theme.Pink, Items = new[] { "Enable XMP/EXPO so RAM runs at its rated speed", "Enable Resizable BAR (Above 4G Decoding + ReBAR) for RTX 30/40/50", "Plug the monitor into the GPU, and set its max refresh rate in Windows", "Keep temps in check; thermal throttling kills FPS" } },
                new { G = "", T = "Skip these (placebo or risky)", C = Theme.Warn, Items = new[] { "Debloat scripts that delete services", "Registry 'FPS boost' packs", "RAM booster apps that run constantly" } } };
            foreach (var sct in sections)
            {
                var card = new Card { Height = 62 + sct.Items.Length * 26, Margin = new Padding(0, 0, 0, 12), Glow = sct.C };
                var ic = new Label { Text = sct.G, Font = Theme.Icons(15f), ForeColor = sct.C, AutoSize = true, Location = new Point(20, 18), BackColor = Color.Transparent };
                var tl = L(sct.T, 12f, Theme.Fg, true); tl.Location = new Point(54, 16);
                card.Controls.Add(ic); card.Controls.Add(tl);
                for (int i = 0; i < sct.Items.Length; i++)
                {
                    var li = L("•   " + sct.Items[i], 9.5f, Theme.Dim); li.Location = new Point(56, 52 + i * 26); card.Controls.Add(li);
                }
                flow.Controls.Add(card);
            }
            flow.Resize += (s, e) => { foreach (Control c in flow.Controls) c.Width = flow.ClientSize.Width - 22; };
            page.Controls.Add(flow);
            return page;
        }

        // ---------- Behavior ----------
        void FillGames(int selectPid = -1)
        {
            int keep = selectPid > 0 ? selectPid : (cbGame.SelectedItem is KeyValuePair<int, string> ? ((KeyValuePair<int, string>)cbGame.SelectedItem).Key : -1);
            var items = rows.Where(r => r.HasWindow && r.Name != "explorer" && r.Name != "GameBoost").OrderByDescending(r => r.Gpu).ThenByDescending(r => r.Cpu)
                .Select(r => new KeyValuePair<int, string>(r.Pid, r.Name + "   " + (string.IsNullOrEmpty(r.Title) ? "" : "- " + r.Title))).ToList();
            if (selectPid > 0 && !items.Any(i => i.Key == selectPid))
            { var r = rows.FirstOrDefault(x => x.Pid == selectPid); if (r != null) items.Insert(0, new KeyValuePair<int, string>(r.Pid, r.Name)); }
            cbGame.Items.Clear(); foreach (var i in items) cbGame.Items.Add(i);
            for (int i = 0; i < items.Count; i++) if (items[i].Key == keep) cbGame.SelectedIndex = i;
            if (cbGame.SelectedIndex < 0 && items.Count > 0) cbGame.SelectedIndex = 0;
        }
        int GamePid() { return cbGame.SelectedItem is KeyValuePair<int, string> ? ((KeyValuePair<int, string>)cbGame.SelectedItem).Key : -1; }

        void ToggleGaming()
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                if (!GamingMode.Active)
                {
                    if (cbGame.Items.Count == 0) FillGames();
                    string r = GamingMode.Enable(GamePid(), rows, Log);
                    gmStatus.Text = "On. " + r.Replace("Gaming Mode ON  -  ", "");
                    gmStatus.ForeColor = Theme.Accent; Notify("Gaming Mode on");
                }
                else { GamingMode.Disable(Log); gmStatus.Text = "Off. Everything restored."; gmStatus.ForeColor = Theme.Dim; Notify("Gaming Mode off. Everything restored", Theme.Dim); }
            }
            finally { Cursor = Cursors.Default; power.SetOn(GamingMode.Active); }
        }

        void ToggleOverlay()
        {
            if (tOverlay.Checked && overlay == null)
            {
                if (!FpsTracker.Start()) { tOverlay.Checked = false; Notify("FPS counter failed: " + FpsTracker.Error, Theme.Warn); return; }
                overlay = new OverlayForm(); overlay.TargetPid = GamePid; overlay.Show();
                Notify("FPS overlay on (top-right)");
            }
            else if (!tOverlay.Checked && overlay != null) { overlay.Close(); overlay = null; FpsTracker.Stop(); Notify("FPS overlay off", Theme.Dim); }
        }

        void FreeRam()
        {
            Cursor = Cursors.WaitCursor;
            long before = AvailMB(); int self = Process.GetCurrentProcess().Id;
            foreach (var r in rows) { if (r.Pid <= 4 || r.Pid == self || r.Cpu > 2) continue; try { using (var p = Process.GetProcessById(r.Pid)) Native.EmptyWorkingSet(p.Handle); } catch { } }
            Native.PurgeStandbyList();
            Cursor = Cursors.Default;
            Notify("Freed about " + Math.Max(0, AvailMB() - before).ToString("N0") + " MB of RAM (no apps closed)");
        }

        [StructLayout(LayoutKind.Sequential)] struct MEMSTAT { public uint len, load; public ulong total, avail, tp, ap, tv, av, ae; }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMSTAT m);
        static MEMSTAT Mem() { var m = new MEMSTAT { len = (uint)Marshal.SizeOf(typeof(MEMSTAT)) }; try { GlobalMemoryStatusEx(ref m); } catch { } if (m.total == 0) { m.total = 16UL << 30; m.avail = 9UL << 30; } return m; }
        static long AvailMB() { return (long)(Mem().avail / 1048576); }

        void RefreshData()
        {
            rows = mon.Sample();
            var m = Mem();
            float cpu = 0; try { if (totalCpu != null) cpu = Math.Min(100, totalCpu.NextValue()); } catch { }
            var topCpu = rows.OrderByDescending(r => r.Cpu).FirstOrDefault();
            var topGpu = rows.OrderByDescending(r => r.Gpu).FirstOrDefault();
            cCpu.Set(cpu, topCpu != null && topCpu.Cpu > .5 ? "Top: " + topCpu.Name + "  " + topCpu.Cpu.ToString("0") + "%" : Environment.ProcessorCount + " threads");
            cGpu.Set((float)mon.TotalGpu, topGpu != null && topGpu.Gpu > .5 ? "Top: " + topGpu.Name + "  " + topGpu.Gpu.ToString("0") + "%" : "Idle");
            double used = (m.total - m.avail) / 1073741824.0, tot = m.total / 1073741824.0;
            cRam.Set((float)(used / tot * 100), used.ToString("0.0") + " of " + tot.ToString("0.0") + " GB in use");
            if (current == 5) RefreshList();
        }

        void RefreshList()
        {
            if (lv == null) return;
            IEnumerable<ProcRow> view = rows;
            string q = search.Text.Trim();
            if (q.Length > 0) view = view.Where(r => r.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (r.Title ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            else view = view.Where(r => r.Cpu >= 0.1 || r.Gpu >= 0.1 || r.RamMB >= 150 || r.HasWindow);
            Func<ProcRow, IComparable> key;
            switch (sortCol)
            {
                case 0: key = r => r.Name; break; case 2: key = r => r.Gpu; break; case 3: key = r => r.RamMB; break;
                case 4: key = r => r.Pid; break; case 5: key = r => r.HasWindow ? 0 : 1; break; case 6: key = r => r.Title ?? ""; break;
                default: key = r => r.Cpu; break;
            }
            view = sortDesc ? view.OrderByDescending(key) : view.OrderBy(key);
            int selPid = Sel() != null ? Sel().Pid : -1; int top = lv.TopItem != null ? lv.TopItem.Index : 0;
            lv.BeginUpdate(); lv.Items.Clear();
            foreach (var r in view)
            {
                var it = new ListViewItem(new[] { r.Name, "", "", r.RamMB.ToString("N0") + " MB", r.Pid.ToString(), r.HasWindow ? "APP" : "BACKGROUND", r.Title ?? "" }) { Tag = r };
                lv.Items.Add(it); if (r.Pid == selPid) it.Selected = true;
            }
            lv.EndUpdate();
            try { if (top > 0 && top < lv.Items.Count) lv.TopItem = lv.Items[top]; } catch { }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var splash = new Splash(); splash.Show();
            var sw = Stopwatch.StartNew();
            Action<int> pump = ms => { var t = Stopwatch.StartNew(); while (t.ElapsedMilliseconds < ms) { Application.DoEvents(); System.Threading.Thread.Sleep(10); } };
            Anim.Tween(0, 1, 300, v => splash.Opacity = v);
            pump(250);
            splash.Step("Reading system counters...", .3f); pump(250);
            splash.Step("Loading GPU monitor...", .55f); pump(100);
            var main = new MainForm();
            splash.Step("Checking performance settings...", .85f); pump(300);
            splash.Step("Ready", 1f); pump(350);
            bool closed = false;
            Anim.Tween(1, 0, 250, v => splash.Opacity = v, () => closed = true);
            while (!closed) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
            splash.Close(); splash.Dispose();
            Application.Run(main);
        }
    }
}
