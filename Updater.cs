using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace GameBoost
{
    class UpdateInfo { public Version Ver; public string Tag, Notes, Url, Sha256; public long Size; public DateTime Published; }

    static class Updater
    {
        public const string Current = "5.9";
        const string Api = "https://api.github.com/repos/PowerfulClock/GameBoost/contents/version.json";
        const string RawApi = "https://raw.githubusercontent.com/PowerfulClock/GameBoost/main/version.json";
        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameBoost", "update"); } }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            var w = new WebClient(); w.Headers[HttpRequestHeader.UserAgent] = "GameBoost/" + Current; return w;
        }
        static string J(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
        }

        // Returns null when up to date or offline
        public static UpdateInfo Check()
        {
            try
            {
                string json; try { using (var w = Client()) { w.Headers[HttpRequestHeader.Accept] = "application/vnd.github.raw"; json = w.DownloadString(Api); } } catch { using (var w = Client()) json = w.DownloadString(RawApi); }
                var u = new UpdateInfo { Tag = J(json, "version") ?? "", Notes = J(json, "notes") ?? "", Url = J(json, "url"), Sha256 = (J(json, "sha256") ?? "").ToLower() };
                Version v; if (!Version.TryParse(u.Tag.TrimStart('v', 'V'), out v) || u.Url == null) return null; u.Ver = v;
                if (v <= Version.Parse(Current)) return null;
                if (u.Sha256.Length != 64) u.Sha256 = null;
                var sz = System.Text.RegularExpressions.Regex.Match(json, "\"size\"\\s*:\\s*(\\d+)"); if (sz.Success) u.Size = long.Parse(sz.Groups[1].Value);
                DateTime pd; if (DateTime.TryParse(J(json, "date"), out pd)) u.Published = pd;
                return u;
            }
            catch { return null; }
        }

        // Downloads and verifies; returns path of the new exe, or throws
        public static string Download(UpdateInfo u, Action<float, long> progress)
        {
            Directory.CreateDirectory(Dir);
            string tmp = Path.Combine(Dir, "GameBoost.new.exe"); try { File.Delete(tmp); } catch { }
            using (var w = Client())
            using (var src = w.OpenRead(u.Url))
            using (var dst = File.Create(tmp))
            {
                var buf = new byte[65536]; long got = 0; int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0) { dst.Write(buf, 0, n); got += n; progress(u.Size > 0 ? Math.Min(1f, got / (float)u.Size) : 0, got); }
            }
            var fi = new FileInfo(tmp);
            if (u.Size > 0 && fi.Length != u.Size) throw new Exception("Download was incomplete. Try again.");
            using (var f = File.OpenRead(tmp)) { var b = new byte[2]; f.Read(b, 0, 2); if (b[0] != 'M' || b[1] != 'Z') throw new Exception("Downloaded file isn't a valid program."); }
            if (u.Sha256 != null)
            {
                string h; using (var sha = SHA256.Create()) using (var f = File.OpenRead(tmp)) h = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLower();
                if (h != u.Sha256) { File.Delete(tmp); throw new Exception("Security check failed: the file doesn't match the published checksum."); }
            }
            return tmp;
        }

        // Swaps the exe after this process exits, then relaunches
        public static void InstallAndRestart(string newExe)
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName; int pid = Process.GetCurrentProcess().Id;
            string bat = Path.Combine(Dir, "apply_update.cmd");
            File.WriteAllText(bat,
                "@echo off\r\n" +
                ":wait\r\ntasklist /fi \"PID eq " + pid + "\" | find \"" + pid + "\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
                "move /y \"" + exe + "\" \"" + exe + ".old\" >nul\r\n" +
                "move /y \"" + newExe + "\" \"" + exe + "\" >nul || move /y \"" + exe + ".old\" \"" + exe + "\" >nul\r\n" +
                "start \"\" \"" + exe + "\" --updated\r\n" +
                "del \"%~f0\"\r\n");
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }

        public static void CleanupOld()
        {
            try { var old = Process.GetCurrentProcess().MainModule.FileName + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
        }
    }
}
