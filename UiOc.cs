using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace GameBoost
{
    // Card that stacks its children vertically, wrapping labels to its width
    class StackCard : Card
    {
        public int Pad = 22; public List<Control> Rows = new List<Control>();
        public void AddRow(Control c, int spaceBefore = 6) { c.Tag = spaceBefore; Rows.Add(c); Controls.Add(c); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }
        public void Relayout()
        {
            int y = Pad - 4, w = Width - Pad * 2; if (w < 50) return;
            foreach (var c in Rows)
            {
                y += c.Tag is int ? (int)c.Tag : 6;
                if (c is Label)
                {
                    var l = (Label)c; if (l.Left < Pad) l.Left = Pad;
                    int lw = Math.Max(40, w - (l.Left - Pad));
                    var sz = TextRenderer.MeasureText(l.Text, l.Font, new Size(lw, 0), TextFormatFlags.WordBreak);
                    l.AutoSize = false; l.Size = new Size(lw, sz.Height + 2);
                }
                else if (c is FlowLayoutPanel) { c.Left = Pad - 2; c.Width = w; }
                c.Top = y; y += c.Height;
            }
            Height = y + Pad;
        }
    }

    partial class MainForm
    {
        FlowLayoutPanel ocFlow; List<Result> lastHealth = new List<Result>(), lastSession = new List<Result>();

        Panel BuildOc()
        {
            var page = NewPage(); ocFlow = VFlow();
            var wait = new Card { Height = 70 }; var wl = L("Detecting your hardware...", 11f, Theme.Fg, true); wl.Location = new Point(22, 22); wait.Controls.Add(wl);
            ocFlow.Controls.Add(wait); page.Controls.Add(ocFlow);
            return page;
        }

        static Color OcColor(int lvl) { return lvl == 0 ? Theme.Accent : lvl == 1 ? Theme.Warn : lvl == 2 ? Color.FromArgb(255, 95, 95) : Theme.Dim; }

        Label Wrap(string t, float size, Color c, bool bold = false, int indent = 0)
        {
            var l = new Label { Text = t, Font = Theme.F(size, bold), ForeColor = c, AutoSize = true, BackColor = Color.Transparent };
            l.Left = 22 + indent; return l;
        }

        void FillOc()
        {
            if (ocFlow == null || hw == null) return;
            int scroll = ocFlow.VerticalScroll.Value;
            ocFlow.SuspendLayout();
            foreach (Control c in ocFlow.Controls.Cast<Control>().ToList()) { ocFlow.Controls.Remove(c); c.Dispose(); }

            // Disclaimer
            var warn = new StackCard { Glow = Theme.Warn, Margin = new Padding(0, 0, 0, 12) };
            var wt = Wrap("⚠  Overclocking runs hardware beyond its factory settings", 11.5f, Theme.Warn, true); warn.AddRow(wt, 0);
            warn.AddRow(Wrap("It can cause crashes, data loss and extra heat, and some kinds can void your warranty. GameBoost never changes overclock settings itself. It checks what's safe for YOUR hardware and walks you through it. You're in control of every change.", 9.5f, Theme.Dim));
            ocFlow.Controls.Add(warn);

            // Readiness
            var rd = OcAdvisor.Readiness(hw, lastHealth, lastSession);
            var rc = new StackCard { Glow = OcColor(rd.Level), Margin = new Padding(0, 0, 0, 12) };
            rc.AddRow(Wrap("READINESS FOR YOUR PC", 8f, Theme.Dim, true), 0);
            rc.AddRow(Wrap(rd.Verdict, 17f, OcColor(rd.Level), true), 2);
            foreach (var ch in rd.Checks)
            {
                string icon = ch[0] == "ok" ? "✔" : ch[0] == "warn" ? "!" : "✖"; Color col = ch[0] == "ok" ? Theme.Accent : ch[0] == "warn" ? Theme.Warn : Color.FromArgb(255, 95, 95);
                var l = Wrap(icon + "   " + ch[1], 9.5f, ch[0] == "ok" ? Theme.Fg : col); rc.AddRow(l, 8);
            }
            ocFlow.Controls.Add(rc);

            if (hw.IsNvidia && (hw.GpuName.Contains("RTX") || hw.GpuName.Contains("GTX"))) ocFlow.Controls.Add(GpuTunerCard());

            foreach (var s in OcAdvisor.Build(hw, lastHealth).Concat(new[] { OcAdvisor.Testing() })) ocFlow.Controls.Add(SectionCard(s));
            ocFlow.ResumeLayout();
            foreach (Control c in ocFlow.Controls) { c.Width = ocFlow.ClientSize.Width - 22; if (c is StackCard) ((StackCard)c).Relayout(); }
            try { ocFlow.VerticalScroll.Value = Math.Min(scroll, ocFlow.VerticalScroll.Maximum); } catch { }
        }

        StackCard SectionCard(OcSection s)
        {
            var col = OcColor(s.Level);
            var c = new StackCard { Glow = col, Margin = new Padding(0, 0, 0, 12) };
            var head = new Label { Text = s.Glyph, Font = Theme.Icons(15f), ForeColor = col, AutoSize = true, BackColor = Color.Transparent, Location = new Point(22, 20) };
            var title = Wrap(s.Title, 12.5f, Theme.Fg, true); title.Left = 56;
            var pill = new Pill(s.LevelText, col); c.Controls.Add(head); c.Controls.Add(pill);
            c.AddRow(title, 0);
            c.Resize += (o, e) => pill.Location = new Point(c.Width - pill.Width - 20, 18);
            c.AddRow(Wrap(s.Recommendation, 10.5f, Theme.Fg), 12);
            if (!string.IsNullOrEmpty(s.Gain) && s.Gain != "-") c.AddRow(Wrap("Expected gain:  " + s.Gain, 9.5f, Theme.Accent), 6);
            if (s.Steps.Count > 0)
            {
                c.AddRow(Wrap("HOW TO DO IT", 8f, Theme.Dim, true), 14);
                for (int i = 0; i < s.Steps.Count; i++) c.AddRow(Wrap((i + 1) + ".   " + s.Steps[i], 9.5f, Theme.Fg, false, 4), 6);
            }
            if (s.Warnings.Count > 0)
            {
                c.AddRow(Wrap("WARNINGS", 8f, Theme.Warn, true), 14);
                foreach (var w in s.Warnings) c.AddRow(Wrap("⚠   " + w, 9.5f, Theme.Warn, false, 4), 6);
            }
            if (s.Notes.Count > 0)
            {
                c.AddRow(Wrap("GOOD TO KNOW", 8f, Theme.Dim, true), 14);
                foreach (var n in s.Notes) c.AddRow(Wrap("•   " + n, 9.5f, Theme.Dim, false, 4), 6);
            }
            if (s.Buttons.Count > 0)
            {
                var row = new FlowLayoutPanel { Height = 44, BackColor = Theme.Card, WrapContents = false };
                foreach (var b in s.Buttons)
                {
                    var btn = new ModernButton(b[0], 170, b[1] != "#health" && s.Buttons.IndexOf(b) == 0 && s.Level == 0, b[1] == "#health" ? "" : "") { Margin = new Padding(0, 4, 10, 0) };
                    string target = b[1];
                    btn.Click += (o, e) =>
                    {
                        if (target == "#health") ShowPage(2);
                        else try { Process.Start(target); } catch { Notify("Couldn't open the link", Theme.Warn); }
                    };
                    row.Controls.Add(btn);
                }
                c.AddRow(row, 16);
            }
            return c;
        }
    }
}
