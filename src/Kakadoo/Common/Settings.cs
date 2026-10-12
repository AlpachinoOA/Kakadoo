using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Kakadoo
{
    /// <summary>
    /// Settings shared by every Kakadoo component, kept in ~/Kakadoo/settings.txt (one "key=value" per line) so they
    /// survive restarts and apply to all documents. Deliberately plain, so it can be edited by hand or deployed by IT.
    /// </summary>
    static class Settings
    {
        static readonly object Gate = new object();
        static Dictionary<string, string> data;

        static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Kakadoo", "settings.txt");

        /// <summary>Counts changes; engines that failed compare it to know whether to try again.</summary>
        public static int Version { get; private set; }

        /// <summary>A Whisper model file, or a folder containing ggml-*.bin files. Empty means automatic.</summary>
        public static string WhisperModelPath
        {
            get => Get("whisperModelPath");
            set => Set("whisperModelPath", value);
        }

        // Values set by a Kakadoo Settings component apply for this Rhino session only; "remember" copies them into the file.
        static string sessionMicrophone, sessionModel;
        static bool? sessionAnimate;
        static double? sessionHold;

        /// <summary>Name of the microphone to use. Empty means the system default.</summary>
        public static string Microphone => sessionMicrophone ?? Get("microphone") ?? "";

        /// <summary>"auto", "fast" (base.en) or "accurate" (small.en).</summary>
        public static string ModelPreset
        {
            get
            {
                string v = (sessionModel ?? Get("modelPreset") ?? "auto").Trim().ToLowerInvariant();
                return v == "fast" || v == "accurate" ? v : "auto";
            }
        }

        /// <summary>Whether the listening ripple animates. A component can still switch it off for itself.</summary>
        public static bool Animate => sessionAnimate ?? Get("animate") != "false";

        /// <summary>How long (seconds) the Always-On trigger output stays true after a trigger word is heard.</summary>
        public static double TriggerHold => sessionHold ?? (double.TryParse(Get("triggerHold"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double h) ? h : 5.0);

        /// <summary>Applies values for this session; null leaves a value as it is. With <paramref name="remember"/> they also become the PC defaults.</summary>
        public static void Apply(string microphone, string model, bool? animate, double? hold, bool remember)
        {
            bool changed = false;
            if (microphone != null && microphone.Trim() != Microphone) { sessionMicrophone = microphone.Trim(); changed = true; }
            if (model != null && model.Trim().ToLowerInvariant() != ModelPreset) { sessionModel = model.Trim().ToLowerInvariant(); changed = true; }
            if (animate.HasValue && animate.Value != Animate) { sessionAnimate = animate; changed = true; }
            if (hold.HasValue && Math.Abs(hold.Value - TriggerHold) > 1e-9) { sessionHold = Math.Max(0.1, Math.Min(600, hold.Value)); changed = true; }
            if (changed) lock (Gate) Version++;

            if (remember)
            {
                Set("microphone", Microphone);
                Set("modelPreset", ModelPreset);
                Set("animate", Animate ? "" : "false");
                Set("triggerHold", TriggerHold.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        static string Get(string key)
        {
            lock (Gate)
            {
                Load();
                return data.TryGetValue(key, out string v) ? v : null;
            }
        }

        static void Set(string key, string value)
        {
            lock (Gate)
            {
                Load();
                if (string.IsNullOrWhiteSpace(value)) data.Remove(key);
                else data[key] = value.Trim();

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.WriteAllLines(FilePath, data.Select(kv => kv.Key + "=" + kv.Value));
                }
                catch (Exception)
                {
                    // The setting still applies for this session if the file can't be written.
                }
                Version++;
            }
        }

        static void Load()
        {
            if (data != null) return;
            data = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) data[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch (Exception)
            {
                // An unreadable file is treated as empty.
            }
        }
    }
}
