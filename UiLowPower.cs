using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameBoost
{
    partial class MainForm
    {
        [StructLayout(LayoutKind.Sequential)] struct PPTS { public uint Version, ControlMask, StateMask; }
        [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr h, int cls, ref PPTS info, int size);
        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr h);
        bool lowPower;

        // While minimized: stop UI refresh, lower GameBoost's own priority, use Windows Efficiency mode,
        // and release memory. Gaming Mode, overlay, GPU overclock watchdog, scans and update checks keep running.
        void SetLowPower(bool on)
        {
            if (on == lowPower) return; lowPower = on;
            var me = Process.GetCurrentProcess();
            Card.Paused = on;
            if (on)
            {
                timer.Stop(); if (tempTimer != null) tempTimer.Stop();
                bool overlayOn = overlay != null;
                try { me.PriorityClass = overlayOn ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Idle; } catch { }
                // Efficiency mode (EcoQoS) only when the overlay isn't running, so FPS readings stay responsive
                if (!overlayOn) SetEco(me, true);
                GC.Collect(); GC.WaitForPendingFinalizers(); try { EmptyWorkingSet(me.Handle); } catch { }
                Log("Minimized: GameBoost switched to low-power mode.");
            }
            else
            {
                SetEco(me, false);
                try { me.PriorityClass = ProcessPriorityClass.Normal; } catch { }
                timer.Start(); if (tempTimer != null) tempTimer.Start();
                RefreshData(); PollTemps();
            }
        }

        static void SetEco(Process p, bool on)
        {
            try { var s = new PPTS { Version = 1, ControlMask = 1, StateMask = on ? 1u : 0u }; SetProcessInformation(p.Handle, 4, ref s, Marshal.SizeOf(typeof(PPTS))); } catch { }
        }
    }
}
