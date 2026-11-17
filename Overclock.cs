using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameBoost
{
    // Level: 0 Recommended/safe, 1 Caution, 2 Not recommended, 3 Not possible
    class OcSection
    {
        public string Title, Glyph, LevelText, Recommendation, Gain; public int Level;
        public List<string> Steps = new List<string>(), Warnings = new List<string>(), Notes = new List<string>();
        public List<string[]> Buttons = new List<string[]>(); // {text, url or "#health"}
    }

    class OcReadiness { public string Verdict; public int Level; public List<string[]> Checks = new List<string[]>(); } // {"ok"/"warn"/"bad", text}

    static class OcAdvisor
    {
        public static string Chipset(HwInfo h)
        {
            var m = Regex.Match(h.Board, @"\b([ZBHXAQW])\s?(\d{3})E?\b", RegexOptions.IgnoreCase);
            return m.Success ? (m.Groups[1].Value.ToUpper() + m.Groups[2].Value) : "";
        }
        public static uint Microcode()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    var b = k.GetValue("Update Revision") as byte[];
                    if (b != null && b.Length >= 8) return BitConverter.ToUInt32(b, 4);
                }
            }
            catch { }
            return 0;
        }
        static int IntelGen(string n, out string suffix)
        {
            suffix = ""; var m = Regex.Match(n, @"i[3579]-(\d{4,5})([A-Z]*)", RegexOptions.IgnoreCase);
            if (!m.Success) return 0; suffix = m.Groups[2].Value.ToUpper(); string num = m.Groups[1].Value;
            return num.Length == 5 ? int.Parse(num.Substring(0, 2)) : int.Parse(num.Substring(0, 1));
        }

        public static OcReadiness Readiness(HwInfo h, List<Result> health, List<Result> session)
        {
            var r = new OcReadiness(); int worst = 0;
            Action<string, string> add = (s, t) => { r.Checks.Add(new[] { s, t }); if (s == "bad") worst = Math.Max(worst, 2); else if (s == "warn") worst = Math.Max(worst, 1); };
            if (h.IsLaptop) add("warn", "Laptop: cooling and power are shared and tight. CPU and RAM overclocking are generally locked; only careful GPU tuning makes sense.");
            else add("ok", "Desktop PC: overclocking is possible depending on the parts below.");
            if (health == null || health.Count == 0) add("warn", "Run the Health Check first so GameBoost can confirm your cooling holds up at stock settings.");
            else
            {
                var bad = health.Where(x => x.Level == 3 && (x.Title.Contains("Clock stability") || x.Title.Contains("temperature") || x.Title.Contains("throttling") || x.Title.Contains("CPU boost"))).ToList();
                var warn = health.Where(x => x.Level == 2 && (x.Title.Contains("Clock stability") || x.Title.Contains("temperature") || x.Title.Contains("performance limit"))).ToList();
                if (bad.Count > 0) add("bad", "Health Check found heat/throttling problems at STOCK settings (" + string.Join(", ", bad.Select(b => b.Title)) + "). Fix cooling before overclocking anything.");
                else if (warn.Count > 0) add("warn", "Health Check shows the CPU is already running warm or power-limited at stock. Expect little headroom.");
                else add("ok", "Health Check passed: cooling holds clocks at stock.");
            }
            if (session != null && session.Count > 0)
            {
                var g = session.FirstOrDefault(x => x.Title == "GPU heat while gaming");
                if (g != null && g.Level >= 2) add(g.Level == 3 ? "bad" : "warn", "GPU already runs hot while gaming (" + g.Value + "). Improve airflow before raising GPU clocks.");
                else if (g != null) add("ok", "GPU temperatures while gaming are healthy (" + g.Value + ").");
            }
            else add("warn", "Record a Gaming session (Health Check page) to verify GPU heat under real load.");
            if (h.Bios.Length >= 14)
            {
                var m = Regex.Match(h.Bios, @"\((\d{4})-(\d{2})");
                if (m.Success) { var d = new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), 1); if ((DateTime.Now - d).TotalDays > 540) add("warn", "BIOS is from " + d.ToString("MMM yyyy") + ". Update it first: newer BIOSes fix memory compatibility and voltage safety issues."); else add("ok", "BIOS is reasonably recent (" + d.ToString("MMM yyyy") + ")."); }
            }
            add("warn", "Power supply can't be detected by software. Overclocking adds load; a quality PSU with headroom matters.");
            r.Level = worst; r.Verdict = worst == 2 ? "Not ready: fix the red items first" : worst == 1 ? "Ready with caution" : "Ready to overclock";
            return r;
        }

        public static List<OcSection> Build(HwInfo h, List<Result> health)
        {
            var list = new List<OcSection>(); string chip = Chipset(h);
            bool unstableStock = health != null && health.Any(x => x.Level == 3 && (x.Title.Contains("Clock stability") || x.Title.Contains("CPU temperature")));

            // ===== RAM =====
            var ram = new OcSection { Title = "Memory (RAM): XMP / EXPO", Glyph = "" };
            bool intel = !h.IsRyzen; string sfx; int igen = IntelGen(h.CpuName, out sfx);
            bool memOcLocked = h.IsLaptop || (intel && (chip.StartsWith("H") && chip != "H770" && chip != "H670") ) || (intel && chip.StartsWith("B") && igen > 0 && igen < 12);
            if (h.IsLaptop)
            {
                ram.Level = 3; ram.LevelText = "Not possible"; ram.Recommendation = "Laptop memory runs at a fixed speed set by the manufacturer.";
                ram.Gain = "-"; ram.Notes.Add("Make sure both memory slots are filled (dual channel) if your laptop has two slots. That's the one memory upgrade that helps.");
            }
            else if (memOcLocked)
            {
                ram.Level = 3; ram.LevelText = "Not supported"; ram.Recommendation = "Your motherboard chipset (" + chip + ") doesn't allow memory overclocking, so XMP won't be available.";
                ram.Gain = "-";
            }
            else if (h.RamCfg == 0)
            {
                ram.Level = 1; ram.LevelText = "Check manually"; ram.Recommendation = "Windows didn't report your RAM speed. Open Task Manager > Performance > Memory and compare 'Speed' with the speed on your RAM's box/listing.";
                ram.Gain = "If it's lower than rated, enabling XMP/EXPO typically gives 5-15% more FPS in CPU-heavy games.";
            }
            else if (h.XmpLikelyOff)
            {
                ram.Level = 0; ram.LevelText = "Recommended · Safe"; ram.Recommendation = "Turn on your RAM's XMP/EXPO profile. It's running at " + h.RamType + "-" + h.RamCfg + ", below what performance kits are built for.";
                ram.Gain = "Typically 5-15% more FPS in CPU-heavy games, more on Ryzen. The single best free upgrade.";
                ram.Steps.AddRange(new[] {
                    "Restart and tap Del (or F2) to enter the BIOS.",
                    intel ? "Find 'XMP' / 'Extreme Memory Profile' (often on the main/EZ screen or under Ai Tweaker / OC / Tweaker)." : "Find 'EXPO' (or 'DOCP' on ASUS, 'A-XMP' on MSI) on the main/EZ screen or under OC / Tweaker.",
                    "Select Profile 1. Leave every other memory setting on Auto.",
                    "Press F10 to save and restart. The first boot can take 1-2 minutes while memory trains; that's normal.",
                    "Back in Windows, open GameBoost > My PC and confirm the new speed, then run the Health Check." });
                ram.Warnings.Add("XMP/EXPO is technically overclocking. If you get crashes or blue screens afterwards, disable it again (or pick a lower profile).");
                if (h.IsRyzen && h.RamType.Contains("DDR5")) ram.Warnings.Add("AM5 Ryzen: SoC voltage must stay at or below 1.30 V. Updated BIOSes cap this automatically; never raise it by hand. DDR5-6000 is the sweet spot.");
                if (h.RamType == "DDR4") ram.Notes.Add("DDR4 XMP kits normally run 1.35 V, which is fine for daily use.");
                else if (h.RamType.Contains("DDR5")) ram.Notes.Add("DDR5 EXPO/XMP profiles normally use 1.25-1.40 V DRAM voltage, which is fine for daily use.");
                ram.Notes.Add("Update the BIOS first if the system won't boot with the profile; new BIOSes greatly improve memory compatibility.");
            }
            else
            {
                ram.Level = 1; ram.LevelText = "Already tuned · Advanced only"; ram.Recommendation = "XMP/EXPO looks enabled (" + h.RamType + "-" + h.RamCfg + "). Further gains need manual timing tuning.";
                ram.Gain = "Small: roughly 1-5% in CPU-limited games, for hours of testing.";
                ram.Steps.AddRange(new[] { "Only for experienced users: tighten primary timings (tCL, tRCD, tRP) one step at a time.", "Test every change with TestMem5 or OCCT memory test for at least 30 minutes.", "Memory errors can silently corrupt files. Back up first." });
                ram.Warnings.Add("Don't exceed ~1.45 V DRAM voltage for daily use" + (h.IsRyzen ? ", and never push SoC voltage above 1.30 V on Ryzen." : "."));
            }
            if (!h.IsLaptop && h.Sticks == 1) ram.Notes.Insert(0, "You have one RAM stick. Adding a matching second stick (dual channel) is a bigger win than any memory overclock.");
            ram.Buttons.Add(new[] { "Run Health Check", "#health" });
            list.Add(ram);

            // ===== GPU =====
            var gpu = new OcSection { Title = "Graphics card", Glyph = "" };
            bool laptopGpu = h.IsLaptop || h.GpuName.IndexOf("Laptop", StringComparison.OrdinalIgnoreCase) >= 0;
            if (h.IsNvidia && h.GpuName.Contains("RTX"))
            {
                gpu.Level = laptopGpu ? 1 : 0; gpu.LevelText = laptopGpu ? "Caution · Auto only" : "Recommended · Warranty-safe";
                gpu.Recommendation = "Use NVIDIA's built-in Automatic Tuning. It finds a stable overclock for your exact card and doesn't void the warranty.";
                gpu.Gain = "Usually 3-8% more FPS. Manual tuning can add a bit more.";
                gpu.Steps.AddRange(new[] {
                    "Open the NVIDIA App > System > Performance.",
                    "Turn on Automatic Tuning. It runs for 10-20 minutes; don't use the PC meanwhile.",
                    "When it finishes, play normally. It re-checks your card automatically over time.",
                    "Use GameBoost's Gaming session monitor to confirm temperatures stay healthy." });
                if (!laptopGpu)
                {
                    gpu.Notes.Add("MANUAL (advanced, MSI Afterburner): raise Power Limit to max, then core clock in +15 to +25 MHz steps, testing 10+ minutes each. Then memory in +50 to +100 MHz steps. Back off at the first crash, flicker or artifact.");
                    gpu.Notes.Add("Past about +200 MHz core you're into diminishing returns and higher crash risk. Leave voltage sliders alone.");
                    gpu.Notes.Add("Undervolting (same clocks at lower voltage) is a great alternative: cooler, quieter, and often faster because it holds boost longer.");
                }
                else gpu.Warnings.Add("Laptop GPUs share cooling with the CPU. Stick to Automatic Tuning and watch temperatures; skip manual overclocks.");
                gpu.Warnings.Add("Manual overclocks with third-party tools are at your own risk. If games crash or show colored artifacts, reset to default.");
                gpu.Buttons.Add(new[] { "Get NVIDIA App", "https://www.nvidia.com/en-us/software/nvidia-app/" });
                if (!laptopGpu) gpu.Buttons.Add(new[] { "MSI Afterburner", "https://www.msi.com/Landing/afterburner/graphics-cards" });
            }
            else if (Regex.IsMatch(h.GpuName, @"Radeon\s*RX", RegexOptions.IgnoreCase))
            {
                gpu.Level = 1; gpu.LevelText = "Caution"; gpu.Recommendation = "Use AMD Software (Adrenalin) > Performance > Tuning. Try 'Undervolt GPU' first, then 'Overclock GPU' presets.";
                gpu.Gain = "Usually 3-8%.";
                gpu.Steps.AddRange(new[] { "Open AMD Software > Performance > Tuning.", "Choose the 'Undervolt GPU' preset and play for a while.", "If stable, try 'Overclock GPU'. Reset if games crash." });
                gpu.Buttons.Add(new[] { "AMD Software", "https://www.amd.com/en/products/software/adrenalin.html" });
            }
            else
            {
                gpu.Level = 3; gpu.LevelText = "Not worthwhile"; gpu.Recommendation = "Integrated or older graphics have little to no overclocking headroom.";
                gpu.Gain = "-";
            }
            if (gpu.Level <= 1 && health != null && health.Any(x => x.Title.Contains("GPU") && x.Level == 3)) { gpu.Level = 2; gpu.LevelText = "Fix heat first"; gpu.Warnings.Insert(0, "Your Health Check flagged a GPU heat problem. Don't overclock until that's fixed."); }
            list.Add(gpu);

            // ===== CPU =====
            var cpu = new OcSection { Title = "Processor (CPU)", Glyph = "" };
            bool x3d = h.CpuName.Contains("X3D"); var rz = Regex.Match(h.CpuName, @"Ryzen\s*\d\s*(\d)(\d{3})"); int rgen = rz.Success ? int.Parse(rz.Groups[1].Value) : 0;
            bool oemBoard = chip.Length == 0;
            if (h.IsLaptop)
            {
                cpu.Level = 3; cpu.LevelText = "Not possible"; cpu.Recommendation = "Laptop CPUs are locked. Use the manufacturer's performance mode (e.g. Armoury Crate, Legion Space, Omen Gaming Hub) while plugged in.";
                cpu.Gain = "-";
            }
            else if (intel && (igen == 13 || igen == 14))
            {
                uint mc = Microcode();
                cpu.Level = 2; cpu.LevelText = "Not recommended";
                cpu.Recommendation = "Intel 13th/14th Gen desktop CPUs are affected by Intel's 'Vmin Shift' instability issue. Don't overclock; protect the chip instead.";
                cpu.Gain = "Not worth the risk on this generation.";
                cpu.Steps.Add("Update your motherboard BIOS to one with Intel microcode 0x12B or newer.");
                cpu.Steps.Add("In the BIOS, select the 'Intel Default Settings' / 'Intel Baseline/Performance' power profile instead of unlimited board defaults.");
                cpu.Steps.Add("If you want lower temperatures, a small undervolt is the only tuning worth doing here.");
                if (mc > 0)
                {
                    if (mc < 0x12B) cpu.Warnings.Insert(0, "Your CPU microcode is 0x" + mc.ToString("X") + ", which is OLDER than the 0x12B fix. Update your BIOS as soon as possible.");
                    else cpu.Notes.Insert(0, "Your microcode is 0x" + mc.ToString("X") + ", which includes Intel's fix (0x12B or newer). Good.");
                }
                cpu.Warnings.Add("If this CPU already crashes at stock settings, contact Intel support; affected chips are covered by an extended warranty.");
                cpu.Buttons.Add(new[] { "Find BIOS update", "https://www.google.com/search?q=" + Uri.EscapeDataString(h.Board + " BIOS update") });
            }
            else if (intel)
            {
                bool unlocked = sfx.Contains("K") || Regex.IsMatch(h.CpuName, @"Ultra\s*\d\s*\d{3}K", RegexOptions.IgnoreCase);
                bool zboard = chip.StartsWith("Z");
                if (!unlocked || (!zboard && !oemBoard))
                {
                    cpu.Level = 3; cpu.LevelText = "Not possible";
                    cpu.Recommendation = !unlocked ? "Your Intel CPU is locked (no 'K' in the model), so its clock multiplier can't be raised." : "Your CPU is unlocked, but a " + chip + " board doesn't support CPU overclocking (that needs a Z-series board).";
                    cpu.Gain = "-"; cpu.Notes.Add("What does help: XMP for your RAM, good cooling so it holds boost, and the Ultimate Performance power plan.");
                }
                else if (oemBoard)
                {
                    cpu.Level = 3; cpu.LevelText = "Likely locked"; cpu.Recommendation = "This looks like a prebuilt (OEM) motherboard; they usually hide overclocking options.";
                    cpu.Gain = "-";
                }
                else
                {
                    cpu.Level = 1; cpu.LevelText = "Caution · Advanced";
                    cpu.Recommendation = "Unlocked CPU on a Z-series board. Modest gains are possible, but modern Intel chips already boost close to their limit.";
                    cpu.Gain = "Usually 0-5% in games. Undervolting often gives similar results with less heat.";
                    cpu.Steps.AddRange(new[] { "Use Intel XTU or the BIOS. Raise the P-core multiplier by 1 (100 MHz) at a time.", "Stress test 20-30 min each step (OCCT / Cinebench loop) while watching temperatures; stay under ~90 °C.", "If it crashes, add a tiny bit of voltage or go back one step. Keep core voltage under ~1.40 V for daily use." });
                    cpu.Warnings.Add("CPU overclocking runs the chip outside Intel's spec and can void the warranty.");
                    cpu.Buttons.Add(new[] { "Intel XTU", "https://www.intel.com/content/www/us/en/download/17881/intel-extreme-tuning-utility-intel-xtu.html" });
                }
            }
            else // AMD Ryzen
            {
                bool a_board = chip.StartsWith("A");
                if (oemBoard) { cpu.Level = 3; cpu.LevelText = "Likely locked"; cpu.Recommendation = "This looks like a prebuilt (OEM) motherboard; they usually hide PBO and Curve Optimizer."; cpu.Gain = "-"; }
                else
                {
                    cpu.Level = 1; cpu.LevelText = x3d && rgen < 9 ? "Caution · Undervolt only" : "Caution · Low risk";
                    cpu.Recommendation = x3d && rgen < 9
                        ? "X3D chips before the 9000 series can't be clock-overclocked, but PBO + negative Curve Optimizer (an undervolt) lets them boost higher and run cooler."
                        : "Use Precision Boost Overdrive (PBO) with a negative Curve Optimizer. It's AMD's built-in, automatic way to boost higher. Manual all-core overclocks are worse for gaming.";
                    cpu.Gain = x3d ? "About 0-5% in games, 3-10% in multi-core; mainly cooler and more consistent clocks." : "About 2-7%, plus lower temperatures from the undervolt.";
                    cpu.Steps.AddRange(new[] {
                        "BIOS > (OC / Tweaker / Advanced) > AMD Overclocking > Precision Boost Overdrive: set to Enabled/Advanced with Motherboard limits.",
                        "Curve Optimizer: All Cores, Negative, magnitude 10. Save and test.",
                        "If stable after an hour of gaming + a stress test, try 15, then 20. Most chips settle between -10 and -30; per-core values are better if you're patient.",
                        "Random reboots, especially at idle or light load, mean the offset is too aggressive: step back by 5.",
                        "Easiest alternative: AMD Ryzen Master > Auto OC / Curve Optimizer wizard." });
                    if (x3d && rgen >= 9) cpu.Notes.Add("Your 9000-series X3D supports full overclocking, but PBO is still the best path for gaming. AMD's stated safe manual range is about 1.28-1.31 V.");
                    if (a_board) cpu.Warnings.Add("A-series boards (" + chip + ") have weaker power delivery and may hide PBO. Keep limits conservative.");
                    cpu.Warnings.Add("PBO and Curve Optimizer are outside AMD's stock spec. They're widely used and low risk, but technically not covered by warranty.");
                    cpu.Buttons.Add(new[] { "Ryzen Master", "https://www.amd.com/en/products/software/ryzen-master.html" });
                }
            }
            if (cpu.Level <= 1 && unstableStock) { cpu.Level = 2; cpu.LevelText = "Fix cooling first"; cpu.Warnings.Insert(0, "Your Health Check found throttling/heat at stock. Overclocking will make it worse. Improve the cooler or airflow first."); }
            cpu.Buttons.Add(new[] { "Run Health Check", "#health" });
            list.Add(cpu);
            return list;
        }

        public static OcSection Testing()
        {
            var t = new OcSection { Title = "Testing, warning signs & recovery", Glyph = "", Level = 1, LevelText = "Read before you start" };
            t.Recommendation = "Change ONE thing at a time, and test it before changing the next.";
            t.Gain = "";
            t.Steps.AddRange(new[] {
                "Before anything: run GameBoost's Health Check and a Gaming session to get a stock baseline.",
                "After each change: run the Health Check again, then 30+ minutes of real gaming with the session monitor.",
                "Heavier tests: OCCT (CPU, memory, GPU), TestMem5 for RAM, 3DMark or Unigine loops for the GPU.",
                "Compare FPS with your baseline. If it didn't improve, the change isn't worth keeping." });
            t.Warnings.AddRange(new[] {
                "Signs of an unstable overclock: game crashes, blue screens, random reboots, freezing, flickering or colored artifacts, corrupted files.",
                "Never overclock while temperatures are already high. Heat plus voltage is what wears chips out.",
                "Won't boot after a BIOS change? Most boards revert after 3 failed boots. Otherwise clear CMOS (motherboard button/jumper, or remove the coin battery for a minute)." });
            t.Notes.Add("GPU overclocks reset by clicking 'Reset' in the NVIDIA App / Afterburner or reinstalling the driver.");
            t.Buttons.Add(new[] { "OCCT", "https://www.ocbase.com/" });
            return t;
        }
    }
}
