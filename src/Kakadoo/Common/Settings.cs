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

        /// <summary>Raised (on any thread) after a saved setting changed to a different value.</summary>
        public static event Action Changed;

        /// <summary>Counts changes; engines that failed compare it to know whether to try again.</summary>
        public static int Version { get; private set; }

        /// <summary>A Whisper model file, or a folder containing ggml-*.bin files. Empty means automatic.</summary>
        public static string WhisperModelPath
        {
            get => Get("whisperModelPath");
            set => Set("whisperModelPath", value);
        }

        /// <summary>A Whisper model chosen in the settings window, e.g. "base.en". Empty means the preset decides.</summary>
        public static string WhisperModelName
        {
            get => Get("whisperModel") ?? "";
            set => Set("whisperModel", value);
        }

        /// <summary>A Vosk model folder of your own. Empty means the one installed in the Kakadoo folder.</summary>
        public static string VoskModelPath
        {
            get => Get("voskModelPath") ?? "";
            set => Set("voskModelPath", value);
        }

        static bool Flag(string key) => Get(key) == "true";

        static int Number(string key, int fallback, int min, int max) =>
            int.TryParse(Get(key), out int v) ? Math.Max(min, Math.Min(max, v)) : fallback;

        /// <summary>Dark colours in the settings window.</summary>
        public static bool Dark { get => Flag("dark"); set => Set("dark", value ? "true" : ""); }

        /// <summary>Mute the system audio while Kakadoo records, so music is not picked up.</summary>
        public static bool MuteWhileRecording { get => Flag("muteWhileRecording"); set => Set("muteWhileRecording", value ? "true" : ""); }

        /// <summary>Play a short sound when recording starts and stops.</summary>
        public static bool AudioFeedback { get => Flag("audioFeedback"); set => Set("audioFeedback", value ? "true" : ""); }

        /// <summary>The playback device for the feedback sounds. Empty means the system default.</summary>
        public static string FeedbackDevice { get => Get("feedbackDevice") ?? ""; set => Set("feedbackDevice", value); }

        /// <summary>Volume of the feedback sounds, 0 to 100.</summary>
        public static int FeedbackVolume { get => Number("feedbackVolume", 50, 0, 100); set => Set("feedbackVolume", value == 50 ? "" : value.ToString()); }

        /// <summary>Add a space after the text that Kakadoo Simple and Always-On output.</summary>
        public static bool AppendTrailingSpace { get => Flag("appendTrailingSpace"); set => Set("appendTrailingSpace", value ? "true" : ""); }

        /// <summary>How many runs the components keep in their Output by default (a component can still choose its own).</summary>
        public static int HistoryLimit { get => Number("historyLimit", 4, 1, 100); set => Set("historyLimit", value == 4 ? "" : value.ToString()); }

        /// <summary>Where Kakadoo Llama runs: "cpu" or "gpu".</summary>
        public static string Device
        {
            get { string v = Get("device"); return v == "cpu" || v == "gpu" ? v : DeviceSetting.Default; }
            set => Set("device", value == DeviceSetting.Default ? "" : value);
        }

        /// <summary>The speech language: "en" (default), "auto" or another code from <see cref="Languages"/>.</summary>
        public static string Language
        {
            get { string v = (Get("language") ?? "").Trim().ToLowerInvariant(); return Languages.IsKnown(v) ? v : "en"; }
            set => Set("language", Languages.IsKnown((value ?? "").Trim().ToLowerInvariant()) && value.Trim().ToLowerInvariant() != "en" ? value.Trim().ToLowerInvariant() : "");
        }

        /// <summary>The speech engine every Kakadoo component listens with: "whisper", "parakeet", "vosk", "system" (Windows) or "custom".</summary>
        public static string Engine
        {
            get { string v = (Get("engine") ?? "").Trim().ToLowerInvariant(); return v == "parakeet" || v == "custom" || v == "system" || v == "vosk" ? v : "whisper"; }
            set => Set("engine", value == "parakeet" || value == "custom" || value == "system" || value == "vosk" ? value : "");
        }

        /// <summary>The Parakeet model chosen in the settings window, e.g. "parakeet-v2".</summary>
        public static string ParakeetModelName
        {
            get => Get("parakeetModel") ?? "";
            set => Set("parakeetModel", value);
        }

        /// <summary>A Parakeet model folder of your own. Wins over the name.</summary>
        public static string ParakeetModelPath
        {
            get => Get("parakeetModelPath") ?? "";
            set => Set("parakeetModelPath", value);
        }

        /// <summary>The Whisper-compatible model file used by Kakadoo Custom.</summary>
        public static string CustomModelPath
        {
            get => Get("customModelPath") ?? "";
            set => Set("customModelPath", value);
        }

        /// <summary>The Llama .gguf file used when the component's model path input is empty.</summary>
        public static string LlamaModelPath
        {
            get => Get("llamaModelPath") ?? "";
            set => Set("llamaModelPath", value);
        }

        // Values applied without "remember" last for this Rhino session only; "remember" copies them into the file.
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

        /// <summary>Whether the listening ripple animates.</summary>
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
            if (!changed || Changed == null) return;
            foreach (Action handler in Changed.GetInvocationList())
            {
                try { handler(); }
                catch (Exception) { /* a listener that fails must not stop the setting from applying */ }
            }

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
            bool changed;
            lock (Gate)
            {
                Load();
                data.TryGetValue(key, out string old);
                if (string.IsNullOrWhiteSpace(value)) data.Remove(key);
                else data[key] = value.Trim();
                changed = (old ?? "") != (string.IsNullOrWhiteSpace(value) ? "" : value.Trim());

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
            if (changed) Changed?.Invoke();
        }

        const string WordsMarker = "[custom-words]";

        /// <summary>All saved settings and the custom words as one text, for a backup file.</summary>
        public static string ExportText()
        {
            string lines;
            lock (Gate) { Load(); lines = string.Join("\n", data.Select(kv => kv.Key + "=" + kv.Value)); }
            return "# Kakadoo settings backup\n" + lines + "\n" + WordsMarker + "\n" + CustomWords.ReadText().Replace("\r\n", "\n") + "\n";
        }

        /// <summary>Replaces every setting (and the custom words) from a backup text. Returns an error sentence, or null when it worked.</summary>
        public static string ImportText(string text)
        {
            text = (text ?? "").Replace("\r\n", "\n");
            if (!text.StartsWith("# Kakadoo settings backup")) return "That is not a Kakadoo settings backup.";
            int marker = text.IndexOf(WordsMarker, StringComparison.Ordinal);
            string settingsPart = marker >= 0 ? text.Substring(0, marker) : text;
            string wordsPart = marker >= 0 ? text.Substring(marker + WordsMarker.Length).Trim('\n') : "";

            var fresh = new Dictionary<string, string>();
            foreach (var line in settingsPart.Split('\n'))
            {
                int i = line.IndexOf('=');
                if (i > 0 && !line.StartsWith("#")) fresh[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
            lock (Gate)
            {
                Load();
                data = fresh;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.WriteAllLines(FilePath, data.Select(kv => kv.Key + "=" + kv.Value));
                }
                catch (Exception e) { return "The settings could not be saved: " + e.Message; }
                Version++;
            }
            CustomWords.WriteText(wordsPart);
            Changed?.Invoke();
            return null;
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
