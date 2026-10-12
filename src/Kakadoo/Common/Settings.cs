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

        /// <summary>Name of the microphone to use. Empty means the system default.</summary>
        public static string Microphone => sessionMicrophone ?? Get("microphone") ?? "";

        static string sessionPreset;

        static string Normalize(string v, params string[] allowed)
        {
            v = (v ?? "").Trim().ToLowerInvariant();
            return Array.IndexOf(allowed, v) >= 0 ? v : allowed[0];
        }

        /// <summary>The model chosen on purpose: "auto", "fast" (base.en) or "accurate" (small.en).</summary>
        static string ChosenModel => Normalize(sessionModel ?? Get("modelPreset"), "auto", "fast", "accurate");

        /// <summary>
        /// A bundle of listening settings: "standard", "quiet" (quiet room, more sensitive), "noisy" (workshop, needs a louder voice),
        /// "fast" (quick replies, smaller model) or "accurate" (waits longer, bigger model).
        /// </summary>
        public static string Preset => Normalize(sessionPreset ?? Get("preset"), "standard", "quiet", "noisy", "fast", "accurate");

        /// <summary>How much louder than the room a voice must be to count as speech.</summary>
        public static double NoiseFactor => Preset == "quiet" ? 2.5 : Preset == "noisy" ? 5.5 : 3.5;

        /// <summary>How long a pause ends a phrase, in milliseconds.</summary>
        public static int EndSilenceMs => Preset == "fast" ? 450 : Preset == "quiet" ? 600 : Preset == "noisy" ? 800 : Preset == "accurate" ? 900 : 700;

        /// <summary>"auto", "fast" (base.en) or "accurate" (small.en): the chosen model, otherwise the one the preset implies.</summary>
        public static string ModelPreset
        {
            get
            {
                string chosen = ChosenModel;
                if (chosen != "auto") return chosen;
                return Preset == "fast" ? "fast" : Preset == "accurate" ? "accurate" : "auto";
            }
        }

        /// <summary>Whether the listening ripple animates. A component can still switch it off for itself.</summary>
        public static bool Animate => sessionAnimate ?? Get("animate") != "false";


        /// <summary>Applies values for this session; null leaves a value as it is. With <paramref name="remember"/> they also become the PC defaults.</summary>
        public static void Apply(string microphone, string model, bool? animate, string preset, bool remember)
        {
            bool changed = false;
            if (microphone != null && microphone.Trim() != Microphone) { sessionMicrophone = microphone.Trim(); changed = true; }
            if (model != null && Normalize(model, "auto", "fast", "accurate") != ChosenModel) { sessionModel = Normalize(model, "auto", "fast", "accurate"); changed = true; }
            if (preset != null && Normalize(preset, "standard", "quiet", "noisy", "fast", "accurate") != Preset) { sessionPreset = Normalize(preset, "standard", "quiet", "noisy", "fast", "accurate"); changed = true; }
            if (animate.HasValue && animate.Value != Animate) { sessionAnimate = animate; changed = true; }
            if (changed) lock (Gate) Version++;

            if (remember)
            {
                Set("microphone", Microphone);
                Set("modelPreset", ChosenModel);
                Set("preset", Preset);
                Set("animate", Animate ? "" : "false");
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
