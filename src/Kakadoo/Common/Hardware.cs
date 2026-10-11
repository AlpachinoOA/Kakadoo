using System;
using System.Runtime.InteropServices;

namespace Kakadoo
{
    static class Hardware
    {
        public static int LogicalCores => Environment.ProcessorCount;

        /// <summary>
        /// Cores worth giving to Llama: about half the logical cores (roughly the physical ones), and always
        /// leaving at least two free so Rhino and Windows stay responsive.
        /// </summary>
        public static int RecommendedThreads => Math.Max(1, Math.Min(LogicalCores / 2, LogicalCores - 2));

        public static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    }

    /// <summary>The "Device" input of Kakadoo Llama: "cpu", "gpu", or either followed by ";" and a core count, e.g. "gpu;6".</summary>
    static class DeviceSetting
    {
        public const string Help = "Device must be cpu or gpu, optionally followed by ; and the number of CPU cores to use, e.g. cpu, gpu or cpu;6.";

        public static string Default => Hardware.IsMac ? "gpu" : "cpu";

        /// <summary>Case-insensitive. Blank means the default device and the recommended core count.</summary>
        public static bool TryParse(string text, out bool gpu, out int threads, out string error)
        {
            gpu = Hardware.IsMac;
            threads = Hardware.RecommendedThreads;
            error = null;

            if (string.IsNullOrWhiteSpace(text)) return true;

            var parts = text.Split(';');
            string device = parts[0].Trim().ToLowerInvariant();
            if (device != "cpu" && device != "gpu") { error = "'" + text.Trim() + "' is not valid. " + Help; return false; }
            gpu = device == "gpu";

            if (parts.Length > 2) { error = "'" + text.Trim() + "' is not valid. " + Help; return false; }
            if (parts.Length == 2)
            {
                if (!int.TryParse(parts[1].Trim(), out int n) || n < 1) { error = "'" + parts[1].Trim() + "' is not a valid core count. " + Help; return false; }
                if (n > Hardware.LogicalCores) { error = "This PC has " + Hardware.LogicalCores + " cores, so " + n + " is too many. Recommended: " + Hardware.RecommendedThreads + "."; return false; }
                threads = n;
            }
            return true;
        }
    }
}
