using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Speech.Recognition;
using System.Threading;
using System.Threading.Tasks;

namespace Kakadoo
{
    /// <summary>Listens to the microphone for a single phrase.</summary>
    public interface ISpeechListener
    {
        /// <summary>Returns the recognised phrase, or null if nothing was understood.</summary>
        Task<string> ListenOnceAsync(CancellationToken token);
    }

    public class SystemSpeechListener : ISpeechListener
    {
        readonly bool strict;

        /// <param name="strict">True when the user chose Windows speech: a language that is not installed is an error. False (the backup engine) uses any installed language.</param>
        public SystemSpeechListener(bool strict = false) { this.strict = strict; }

        static System.Collections.Generic.List<CultureInfo> Installed() =>
            SpeechRecognitionEngine.InstalledRecognizers().Select(r => r.Culture).ToList();

        /// <summary>Why Windows speech cannot listen in the language chosen in the settings, or null when it can.</summary>
        public static string LanguageProblem()
        {
            try
            {
                var installed = Installed();
                if (installed.Count == 0)
                    return "No Windows speech language is installed. Add one in Windows settings > Time & language > Language & region (keep its speech option ticked), or choose another speech engine.";
                string code = Settings.Language;
                if (code == "auto" || installed.Any(c => c.TwoLetterISOLanguageName == code)) return null;
                return "Windows speech recognition has no " + Languages.Name(code) + " installed (installed: " + string.Join(", ", installed.Select(c => c.EnglishName)) + "). "
                    + "Add it in Windows settings > Time & language > Language & region, or pick another speech engine or language in Kakadoo Settings > Models.";
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// The installed recognizer closest to the user's language: same language first, then English, then any.
        /// Windows only has recognizers for the speech languages installed in its settings, which often isn't the UI language.
        /// </summary>
        CultureInfo PickRecognizer()
        {
            if (strict && LanguageProblem() is string problem) throw new SpeechSetupException(problem);
            string wanted = Settings.Language;
            if (strict && wanted != "auto")
            {
                var all = Installed();
                return all.FirstOrDefault(c => c.Name == CultureInfo.CurrentUICulture.Name && c.TwoLetterISOLanguageName == wanted)
                    ?? all.First(c => c.TwoLetterISOLanguageName == wanted);
            }

            var installed = SpeechRecognitionEngine.InstalledRecognizers().Select(r => r.Culture).ToList();
            if (installed.Count == 0)
                throw new SpeechSetupException("No speech recognizer is installed. Add a speech language in Windows Settings > Time & language > Speech.");

            var ui = CultureInfo.CurrentUICulture;
            return installed.FirstOrDefault(c => c.Name == ui.Name)
                ?? installed.FirstOrDefault(c => c.TwoLetterISOLanguageName == ui.TwoLetterISOLanguageName)
                ?? installed.FirstOrDefault(c => c.TwoLetterISOLanguageName == "en")
                ?? installed[0];
        }

        public Task<string> ListenOnceAsync(CancellationToken token)
        {
            return Task.Run(() =>
            {
                using (var engine = new SpeechRecognitionEngine(PickRecognizer()))
                {
                    engine.LoadGrammar(new DictationGrammar());
                    engine.SetInputToDefaultAudioDevice();
                    var result = engine.Recognize(TimeSpan.FromSeconds(8));
                    return result?.Text;
                }
            }, token);
        }
    }
}

namespace Kakadoo
{
    /// <summary>Picks the speech engine for the current platform.</summary>
    public static class SpeechListeners
    {
        public static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;

        /// <summary>
        /// Whisper (offline, free) first. If it can't be set up, Windows falls back to the built-in recognizer and Mac to Vosk
        /// (when a Vosk model is installed). Set KAKADOO_ENGINE to whisper, system (Windows) or vosk to use just one.
        /// Nothing is loaded until the first command, so creating this never fails.
        /// </summary>
        public static ISpeechListener Create(bool phrases = false)
        {
            string choice = Environment.GetEnvironmentVariable("KAKADOO_ENGINE")?.Trim().ToLowerInvariant();
            var chain = new FallbackListener();

            bool whisper = choice == null || choice == "whisper" || choice == "";
            if (whisper) chain.Add("Whisper", () => new EngineListener { Phrases = phrases });

            if (choice == "system" || (whisper && choice == null && IsWindows))
                chain.Add("Windows speech recognition", () => CreateWindows());

            if (choice == "vosk" || (whisper && choice == null && !IsWindows && VoskListener.FindModelRoot(DefaultVoskModelPath()) != null))
                chain.Add("Vosk", () => new VoskListener(DefaultVoskModelPath()));

            return chain;
        }

        /// <summary>Listens once and reports a switch to a backup engine through <paramref name="log"/>.</summary>
        public static async Task<string> ListenAsync(ISpeechListener listener, Action<string> log, CancellationToken token = default)
        {
            try
            {
                return CustomWords.Apply(await listener.ListenOnceAsync(token));
            }
            finally
            {
                if (listener is FallbackListener fallback && fallback.TakeNote() is string note) log(note);
            }
        }

        /// <summary>Tells listeners that support it which words to expect, e.g. the slider names.</summary>
        public static void SetHint(ISpeechListener listener, IEnumerable<string> words)
        {
            if (listener is IHintedListener hinted)
            {
                // Whisper only reads a few hundred characters of hint, and a long one can upset it.
                string hint = string.Join(", ", words.Where(w => !string.IsNullOrWhiteSpace(w)));
                if (hint.Length > 400) hint = hint.Substring(0, hint.LastIndexOf(", ", 400, StringComparison.Ordinal) is int i && i > 0 ? i : 400);
                hinted.Hint = hint;
            }
        }

        // Kept separate so System.Speech is never loaded on Mac.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ISpeechListener CreateWindows(bool strict = false) => new SystemSpeechListener(strict);

        /// <summary>KAKADOO_VOSK_MODEL, else "vosk-model" next to the plugin, else ~/Kakadoo/vosk-model.</summary>
        public static string DefaultVoskModelPath()
        {
            string env = Environment.GetEnvironmentVariable("KAKADOO_VOSK_MODEL");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            if (!string.IsNullOrWhiteSpace(Settings.VoskModelPath)) return Settings.VoskModelPath;

            string beside = Path.Combine(Path.GetDirectoryName(typeof(SpeechListeners).Assembly.Location), "vosk-model");
            if (Directory.Exists(beside)) return beside;

            return ModelCatalog.VoskFolder;
        }
    }
}
