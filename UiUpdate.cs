using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace GameBoost
{
    partial class MainForm
    {
        UpdateInfo upd; NavButton navUpd; FlowLayoutPanel updFlow; bool updating;

        Panel BuildUpdate()
        {
            var page = NewPage(); updFlow = VFlow(); page.Controls.Add(updFlow); return page;
        }

        void CheckForUpdate()
        {
            new Thread(() =>
            {
                var u = Updater.Check(); if (u == null) return;
                try { BeginInvoke((Action)(() => { if (updating || (upd != null && u.Ver <= upd.Ver)) return; upd = u; ShowUpdateNav(); })); } catch { }
            }) { IsBackground = true }.Start();
        }

        System.Windows.Forms.Timer updTimer;
        void StartUpdateWatch()
        {
            CheckForUpdate();
            updTimer = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 }; // every 5 minutes
            updTimer.Tick += (s, e) => CheckForUpdate(); updTimer.Start();
        }

        void ShowUpdateNav()
        {
            if (navUpd == null)
            {
                navUpd = new NavButton("Update", "") { Location = new Point(14, 96 + navs.Count * 48) };
                int idx = navs.Count; navUpd.Click += (s, e) => ShowPage(idx);
                navs.Add(navUpd); side.Controls.Add(navUpd);
                // slide in
                int x = navUpd.Left; navUpd.Left = -200; Anim.Tween(-200, x, 450, v => navUpd.Left = (int)v);
            }
            FillUpdate();
            Notify("Update available: v" + upd.Ver + ". Open the Update tab to install", Theme.Accent);
        }

        void FillUpdate()
        {
            updFlow.Controls.Clear();
            var c = new StackCard { Glow = Theme.Accent, Margin = new Padding(0, 0, 0, 12) };
            c.AddRow(Wrap("NEW VERSION AVAILABLE", 8f, Theme.Dim, true), 0);
            c.AddRow(Wrap("v" + Updater.Current + "   →   v" + upd.Ver, 22f, Theme.Accent, true), 4);
            c.AddRow(Wrap((upd.Published > DateTime.MinValue ? "Released " + upd.Published.ToLocalTime().ToString("MMM d, yyyy") + "   ·   " : "") + (upd.Size / 1024) + " KB" + (upd.Sha256 != null ? "   ·   checksum verified on download" : ""), 9.5f, Theme.Dim), 6);
            if (upd.Notes.Length > 0) { c.AddRow(Wrap("WHAT'S NEW", 8f, Theme.Dim, true), 16); c.AddRow(Wrap(upd.Notes, 10f, Theme.Fg), 6); }

            var btn = new ModernButton("Update now", 180, true, "");
            var bar = new ProgressLine { Height = 10, Visible = false };
            var status = Wrap("GameBoost will restart when the update finishes. Any active overclock or Gaming Mode is reset first.", 9f, Theme.Dim);
            var row = new FlowLayoutPanel { Height = 44, BackColor = Theme.Card }; btn.Margin = new Padding(0, 4, 0, 0); row.Controls.Add(btn);
            c.AddRow(row, 18); c.AddRow(bar, 12); c.AddRow(status, 8);
            c.Resize += (o, e) => bar.Width = c.Width - 44;

            btn.Click += (o, e) =>
            {
                if (updating) return; updating = true; btn.Enabled = false; btn.Text = "Updating...";
                bar.Visible = true; bar.Busy = true; bar.Set(0); c.Relayout();
                status.Text = "Downloading..."; status.ForeColor = Theme.Blue;
                var u = upd;
                new Thread(() =>
                {
                    try
                    {
                        string file = Updater.Download(u, (f, got) => { try { BeginInvoke((Action)(() => { bar.Set(f * 0.9f); status.Text = "Downloading...  " + (int)(f * 100) + "%  (" + (got / 1024) + " of " + (u.Size / 1024) + " KB)"; })); } catch { } });
                        BeginInvoke((Action)(() =>
                        {
                            bar.Set(1f); status.Text = "✔ Update downloaded and verified. Restarting in 3..."; status.ForeColor = Theme.Accent;
                            int n = 3; var t = new System.Windows.Forms.Timer { Interval = 1000 };
                            t.Tick += (s2, e2) =>
                            {
                                n--; if (n > 0) { status.Text = "✔ Update downloaded and verified. Restarting in " + n + "..."; return; }
                                t.Stop();
                                try { Updater.InstallAndRestart(file); restartingForUpdate = true; Close(); }
                                catch (Exception ex) { status.Text = "Couldn't install: " + ex.Message; status.ForeColor = Theme.Warn; updating = false; btn.Enabled = true; btn.Text = "Try again"; }
                            };
                            t.Start();
                        }));
                    }
                    catch (Exception ex)
                    {
                        try { BeginInvoke((Action)(() => { bar.Busy = false; status.Text = "Update failed: " + ex.Message; status.ForeColor = Theme.Warn; updating = false; btn.Enabled = true; btn.Text = "Try again"; c.Relayout(); })); } catch { }
                    }
                }) { IsBackground = true }.Start();
            };
            updFlow.Controls.Add(c); c.Width = updFlow.ClientSize.Width - 22; c.Relayout();
        }

        bool restartingForUpdate;
    }
}
