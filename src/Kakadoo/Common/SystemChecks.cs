using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Kakadoo
{
    /// <summary>Outcome of one check: pass, fail, or skipped because it isn't set up.</summary>
    enum CheckOutcome { Passed, Failed, Skipped }

    sealed class CheckResult
    {
        public string Name, Text;
        public CheckOutcome Outcome;

        public string Line => (Outcome == CheckOutcome.Passed ? "[OK] " : Outcome == CheckOutcome.Failed ? "[FAILED] " : "[SKIPPED] ") + Name + ": " + Text;
    }

    /// <summary>
    /// The checks behind Kakadoo Checker and the Checker tab of the settings window: microphone, speech engines, models, cores and graphics card.
    /// Every check runs on its own, so one failure never hides the others.
    /// </summary>
    static class SystemChecks
    {
        /// <summary>Runs every check. A path that is null or empty means "the model chosen in the Kakadoo settings".</summary>
        public static async Task<(int passed, int failed, int skipped)> RunAsync(string whisperPath, string llamaPath, string voskPath, string parakeetPath, string customPath,
            Action<string> note, Action<CheckResult> report)
        {
            int passed = 0, failed = 0, skipped = 0;

            async Task Step(string name, Func<Task<(CheckOutcome, string)>> check)
            {
                (CheckOutcome outcome, string text) result;
                try { result = await check(); }
                catch (Exception e) { result = (CheckOutcome.Failed, e.Message); }

                switch (result.outcome)
                {
                    case CheckOutcome.Passed: passed++; break;
                    case CheckOutcome.Failed: failed++; break;
                    default: skipped++; break;
                }
                report(new CheckResult { Name = name, Outcome = result.outcome, Text = result.text });
            }

            try
            {
                await Step("Speech language", () => Task.FromResult((CheckOutcome.Passed, Languages.Name(Settings.Language))));
                await Step("Speech engine (Whisper)", () => Task.FromResult(CheckSpeechEngine(whisperPath, note)));
                await Step("Backup speech engine", () => Task.FromResult(CheckFallbackEngine()));
                await Step("Parakeet model (Kakadoo Parakeet)", () => Task.FromResult(CheckModel(ModelKind.Parakeet, parakeetPath, "no Parakeet model is chosen. Download one in Settings > Models.")));
                await Step("Custom model (Kakadoo Custom)", () => Task.FromResult(CheckModel(ModelKind.Custom, customPath, "no custom model is chosen. Add one in Settings > Models > Custom models.")));
                await Step("Microphone", CheckMicrophoneAsync);
                await Step("Processor cores (Kakadoo Llama)", () => Task.FromResult(CheckCores()));
                await Step("Graphics card (Kakadoo Llama)", () => Task.FromResult(CheckGraphics()));
                await Step("Llama model (Kakadoo Llama)", () => Task.FromResult(CheckLlama(llamaPath)));
                await Step("Vosk model (Kakadoo Vosk)", () => Task.FromResult(CheckVosk(voskPath)));
                await Step("OpenAI connection (Kakadoo GPT)", CheckOpenAiAsync);
            }
            catch (Exception e)
            {
                report(new CheckResult { Name = "Check", Outcome = CheckOutcome.Failed, Text = "crashed: " + e.Message });
                failed++;
            }
            return (passed, failed, skipped);
        }

        static (CheckOutcome, string) CheckSpeechEngine(string whisperPath, Action<string> note)
        {
            try
            {
                string file = WhisperModel.Locate(out string source, whisperPath);
                if (file == null)
                {
                    note("No Whisper model found on this PC. Downloading '" + WhisperModel.Name + "' once (free) to " + WhisperModel.Directory + " ...");
                    file = WhisperModel.EnsureAvailable(whisperPath);
                    source = "download";
                }

                WhisperListener.Warmup(file);
                return (CheckOutcome.Passed, Path.GetFileName(file) + " (" + new FileInfo(file).Length / (1024 * 1024) + " MB) from " + source + ": " + file);
            }
            catch (Exception e)
            {
                return (CheckOutcome.Failed, e.Message);
            }
        }

        // Used automatically when Whisper can't start.
        static (CheckOutcome, string) CheckFallbackEngine()
        {
            if (Hardware.IsMac)
            {
                string root = VoskListener.FindModelRoot(SpeechListeners.DefaultVoskModelPath());
                return root != null
                    ? (CheckOutcome.Passed, "Vosk model found at " + root + ".")
                    : (CheckOutcome.Skipped, "no Vosk model installed. Optional: it would be used if Whisper can't start.");
            }

            try
            {
                int languages = WindowsRecognizerCount();
                return languages > 0
                    ? (CheckOutcome.Passed, "Windows speech recognition is available (" + languages + " language(s)). It is used if Whisper can't start, but is less accurate.")
                    : (CheckOutcome.Skipped, "no Windows speech language installed. Optional: add one in Settings > Time & language > Speech.");
            }
            catch (Exception e)
            {
                return (CheckOutcome.Skipped, "Windows speech recognition is not available (" + e.Message + ").");
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static int WindowsRecognizerCount() => System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers().Count;

        /// <summary>Parakeet and custom models are loaded and tried on a second of silence, exactly as when they are chosen.</summary>
        static (CheckOutcome, string) CheckModel(ModelKind kind, string path, string whenNone)
        {
            if (string.IsNullOrWhiteSpace(path)) path = ModelSync.Get(kind);
            if (string.IsNullOrWhiteSpace(path)) return (CheckOutcome.Skipped, whenNone);
            string problem = ModelCheck.Verify(kind, path);
            return problem == null ? (CheckOutcome.Passed, path + " runs.") : (CheckOutcome.Failed, problem);
        }

        static async Task<(CheckOutcome, string)> CheckMicrophoneAsync()
        {
            byte[] audio = await Task.Run(() => Microphone.Record(16000, TimeSpan.FromSeconds(1), CancellationToken.None));
            if (audio.Length == 0) return (CheckOutcome.Failed, "no audio was recorded.");

            double peak = Microphone.Peak(audio);
            if (peak == 0) return (CheckOutcome.Failed, Hardware.IsMac
                ? "the microphone records only silence. Most likely Rhino has no microphone permission: open System Settings > Privacy & Security > Microphone, turn on Rhino 8 (add it if missing), then restart Rhino. Otherwise check the input device in System Settings > Sound."
                : "the microphone records only silence. It may be muted, or the wrong device is selected in the Windows sound settings.");
            string level = (peak * 100).ToString("0.0", CultureInfo.InvariantCulture);
            return (CheckOutcome.Passed, "recorded 1 second (peak level " + level + "/100 - speak while checking to see it rise).");
        }

        static (CheckOutcome, string) CheckCores()
        {
            int cores = Hardware.LogicalCores, recommended = Hardware.RecommendedThreads;
            return (CheckOutcome.Passed, "this PC has " + cores + " cores. Recommended for Llama: " + recommended
                + " (set the Device input to cpu;" + recommended + " or gpu;" + recommended + "). Leaving a few free keeps Rhino responsive.");
        }

        static (CheckOutcome, string) CheckGraphics()
        {
            if (Hardware.IsMac)
                return (CheckOutcome.Passed, "Apple GPU (Metal) is used when Device is gpu.");

            long vram = VideoMemory.DetectBytes();
            if (vram <= 0)
                return (CheckOutcome.Skipped, "no dedicated video memory was detected. Use Device = cpu.");

            return (CheckOutcome.Passed, "about " + vram / (1024 * 1024) + " MB of video memory. Device = gpu can use it; layers that don't fit run on the CPU.");
        }

        static (CheckOutcome, string) CheckLlama(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) path = Settings.LlamaModelPath;
            path = path?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(path))
                return (CheckOutcome.Skipped, "no Llama model is chosen. Connect a path to your .gguf file, or choose one in the Kakadoo settings, to check it.");
            if (!File.Exists(path))
                return (CheckOutcome.Failed, "file not found at " + path + ".");

            string problem = ModelCheck.Verify(ModelKind.Llama, path);
            if (problem != null) return (CheckOutcome.Failed, problem);

            long sizeMb = new FileInfo(path).Length / (1024 * 1024);
            long ramMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
            if (sizeMb > ramMb * 0.9)
                return (CheckOutcome.Failed, Path.GetFileName(path) + " is " + sizeMb + " MB but this PC has only " + ramMb + " MB of memory. Use a smaller (e.g. 7B or Q4) model.");

            if (!LlamaNative.LibrariesPresent()) // don't load them here: the checker should not change what the session has loaded
                return (CheckOutcome.Failed, "the llama.cpp libraries were not found in the plugin folder.");

            return (CheckOutcome.Passed, Path.GetFileName(path) + " (" + sizeMb + " MB, this PC has " + ramMb + " MB of memory).");
        }

        static (CheckOutcome, string) CheckVosk(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) path = ModelSync.Get(ModelKind.Vosk);
            if (string.IsNullOrWhiteSpace(path))
                return (CheckOutcome.Skipped, "no Vosk model is installed or connected. Download one in Settings > Models, or connect the model folder.");

            string root = VoskListener.FindModelRoot(path);
            if (root == null)
                return (CheckOutcome.Failed, "no Vosk model found at " + path + ". It should contain the folders 'am' and 'conf'.");

            return (CheckOutcome.Passed, root);
        }

        // Only Kakadoo GPT needs the internet, so a failure here is reported as skipped, not as an error.
        static async Task<(CheckOutcome, string)> CheckOpenAiAsync()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                {
                    await http.GetAsync("https://api.openai.com/");
                    return (CheckOutcome.Passed, "api.openai.com is reachable.");
                }
            }
            catch (Exception)
            {
                return (CheckOutcome.Skipped, "api.openai.com is not reachable. Only Kakadoo GPT needs the internet; the other components work offline.");
            }
        }
    }
}
