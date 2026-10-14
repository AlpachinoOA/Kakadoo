using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Kakadoo
{
    /// <summary>
    /// macOS blocks native libraries that came from a download: Gatekeeper quarantines them and Apple Silicon refuses unsigned code.
    /// Before the first load, this clears the quarantine flag on the plugin folder and signs every dylib ad hoc (no Apple account needed).
    /// </summary>
    static class MacTrust
    {
        static bool done;

        public static void Prepare()
        {
            if (done || !RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;
            done = true;

            try
            {
                string dir = Path.GetDirectoryName(typeof(MacTrust).Assembly.Location);
                string marker = Path.Combine(dir, ".kakadoo-trusted");
                if (File.Exists(marker)) return; // each plugin version has its own folder, so this runs once per install

                Run("/usr/bin/xattr", "-dr com.apple.quarantine \"" + dir + "\"");
                foreach (var file in Directory.GetFiles(dir, "*.dylib", SearchOption.AllDirectories))
                    Run("/usr/bin/codesign", "--force --sign - \"" + file + "\"");
                File.WriteAllText(marker, "");
            }
            catch
            {
                // Best effort: if this fails, loading fails with the usual error and the README lists the manual commands.
            }
        }

        static void Run(string tool, string args)
        {
            var info = new ProcessStartInfo(tool, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            using (var p = Process.Start(info))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
            }
        }
    }
}
