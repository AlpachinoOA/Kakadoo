using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Grasshopper.Kernel;

namespace Kakadoo
{
    /// <summary>
    /// Kakadoo Checker: tests the speech engine, microphone and models, and reports every result,
    /// including the ones that failed or were skipped.
    /// </summary>
    public class KakadooChecker : KakadooComponent
    {
        readonly object gate = new object();
        volatile bool running;
        bool lastRun;
        int passed, failed, skipped;

        public KakadooChecker()
          : base("Kakadoo Checker", "KkChk",
              "Checks that your microphone, speech engine and model are ready for Kakadoo",
              "Kakadoo", "Utilities")
        {
        }

        protected override bool SupportsTether => false;

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBooleanParameter("Run", "R", "Connect a Boolean Toggle to test the microphone", GH_ParamAccess.item, false);
            AddWhisperInput(pManager);
            pManager.AddTextParameter("Llama Model Directory Path", "Llama Model Directory Path", "Path to the Llama GGUF model file to check (Kakadoo Llama)", GH_ParamAccess.item);
            pManager.AddTextParameter("Vosk Model Directory Path", "Vosk Model Directory Path", "Path to the Vosk model folder to check (Kakadoo Vosk)", GH_ParamAccess.item);
            pManager[2].Optional = true;
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Output", "O", "Check results", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            bool run = false;
            string whisperPath = null, llamaPath = null, voskPath = null;
            DA.GetData(0, ref run);
            DA.GetData(1, ref whisperPath);
            DA.GetData(2, ref llamaPath);
            DA.GetData(3, ref voskPath);

            bool trigger = run && !lastRun;
            lastRun = run;

            if (trigger && !running)
            {
                running = true;
                Message = "Checking";
                lock (gate) messages.Clear();
                Task.Run(() => RunChecksAsync(whisperPath, llamaPath, voskPath));
            }
            else if (!run && !running)
            {
                Message = "Not Running";
            }

            lock (gate) DA.SetDataList(0, new List<string>(messages));
        }

        async Task RunChecksAsync(string whisperPath, string llamaPath, string voskPath)
        {
            passed = failed = skipped = 0;
            try
            {
                // Every check runs on its own, so one failure never hides the others.
                await Step("Speech engine (Whisper)", () => Task.FromResult(CheckSpeechEngine(whisperPath)));
                await Step("Backup speech engine", () => Task.FromResult(CheckFallbackEngine()));
                await Step("Microphone", CheckMicrophoneAsync);
                await Step("Processor cores (Kakadoo Llama)", () => Task.FromResult(CheckCores()));
                await Step("Graphics card (Kakadoo Llama)", () => Task.FromResult(CheckGraphics()));
                await Step("Llama model (Kakadoo Llama)", () => Task.FromResult(CheckLlama(llamaPath)));
                await Step("Vosk model (Kakadoo Vosk)", () => Task.FromResult(CheckVosk(voskPath)));
                await Step("OpenAI connection (Kakadoo GPT)", CheckOpenAiAsync);
            }
            catch (Exception e)
            {
                Log("[FAILED] Check crashed: " + e.Message);
                failed++;
            }
            finally
            {
                Log("Result: " + passed + " passed, " + failed + " failed, " + skipped + " skipped.");
                running = false;
                Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
                {
                    Message = failed == 0 ? "All checks passed" : "Error";
                    ExpireSolution(true);
                }));
            }
        }

        /// <summary>Outcome of one check: pass, fail, or skipped because it isn't set up.</summary>
        enum Outcome { Passed, Failed, Skipped }

        async Task Step(string name, Func<Task<(Outcome outcome, string text)>> check)
        {
            (Outcome outcome, string text) result;
            try
            {
                result = await check();
            }
            catch (Exception e)
            {
                result = (Outcome.Failed, e.Message);
            }

            switch (result.outcome)
            {
                case Outcome.Passed: passed++; Log("[OK] " + name + ": " + result.text); break;
                case Outcome.Failed: failed++; Log("[FAILED] " + name + ": " + result.text); break;
                default: skipped++; Log("[SKIPPED] " + name + ": " + result.text); break;
            }
        }

        (Outcome, string) CheckSpeechEngine(string whisperPath)
        {
            try
            {
                string file = WhisperModel.Locate(out string source, whisperPath);
                if (file == null)
                {
                    Log("No Whisper model found on this PC. Downloading '" + WhisperModel.Name + "' once (free) to " + WhisperModel.Directory + " ...");
                    file = WhisperModel.EnsureAvailable(whisperPath);
                    source = "download";
                }

                WhisperListener.Warmup(file);
                return (Outcome.Passed, Path.GetFileName(file) + " (" + new FileInfo(file).Length / (1024 * 1024) + " MB) from " + source + ": " + file);
            }
            catch (Exception e)
            {
                return (Outcome.Failed, e.Message);
            }
        }

        // Used automatically when Whisper can't start.
        (Outcome, string) CheckFallbackEngine()
        {
            if (Hardware.IsMac)
            {
                string root = VoskListener.FindModelRoot(SpeechListeners.DefaultVoskModelPath());
                return root != null
                    ? (Outcome.Passed, "Vosk model found at " + root + ".")
                    : (Outcome.Skipped, "no Vosk model installed. Optional: it would be used if Whisper can't start.");
            }

            try
            {
                int languages = WindowsRecognizerCount();
                return languages > 0
                    ? (Outcome.Passed, "Windows speech recognition is available (" + languages + " language(s)). It is used if Whisper can't start, but is less accurate.")
                    : (Outcome.Skipped, "no Windows speech language installed. Optional: add one in Settings > Time & language > Speech.");
            }
            catch (Exception e)
            {
                return (Outcome.Skipped, "Windows speech recognition is not available (" + e.Message + ").");
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static int WindowsRecognizerCount() => System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers().Count;

        async Task<(Outcome, string)> CheckMicrophoneAsync()
        {
            byte[] audio = await Task.Run(() => Microphone.Record(16000, TimeSpan.FromSeconds(1), CancellationToken.None));
            if (audio.Length == 0) return (Outcome.Failed, "no audio was recorded.");

            double peak = Microphone.Peak(audio);
            if (peak == 0) return (Outcome.Failed, Hardware.IsMac
                ? "the microphone records only silence. Most likely Rhino has no microphone permission: open System Settings > Privacy & Security > Microphone, turn on Rhino 8 (add it if missing), then restart Rhino. Otherwise check the input device in System Settings > Sound."
                : "the microphone records only silence. It may be muted, or the wrong device is selected in the Windows sound settings.");
            string level = (peak * 100).ToString("0.0", CultureInfo.InvariantCulture);
            return (Outcome.Passed, "recorded 1 second (peak level " + level + "/100 - speak while checking to see it rise).");
        }

        (Outcome, string) CheckCores()
        {
            int cores = Hardware.LogicalCores, recommended = Hardware.RecommendedThreads;
            return (Outcome.Passed, "this PC has " + cores + " cores. Recommended for Llama: " + recommended
                + " (set the Device input to cpu;" + recommended + " or gpu;" + recommended + "). Leaving a few free keeps Rhino responsive.");
        }

        (Outcome, string) CheckGraphics()
        {
            if (Hardware.IsMac)
                return (Outcome.Passed, "Apple GPU (Metal) is used when Device is gpu.");

            long vram = VideoMemory.DetectBytes();
            if (vram <= 0)
                return (Outcome.Skipped, "no dedicated video memory was detected. Use Device = cpu.");

            return (Outcome.Passed, "about " + vram / (1024 * 1024) + " MB of video memory. Device = gpu can use it; layers that don't fit run on the CPU.");
        }

        (Outcome, string) CheckLlama(string path)
        {
            path = path?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(path))
                return (Outcome.Skipped, "no 'Llama Model Directory Path' connected. Connect the path to your .gguf file to check it.");
            if (!File.Exists(path))
                return (Outcome.Failed, "file not found at " + path + ".");

            var magic = new byte[4];
            using (var stream = File.OpenRead(path))
                stream.Read(magic, 0, 4);
            if (Encoding.ASCII.GetString(magic) != "GGUF")
                return (Outcome.Failed, Path.GetFileName(path) + " is not a GGUF model file.");

            long sizeMb = new FileInfo(path).Length / (1024 * 1024);
            long ramMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
            if (sizeMb > ramMb * 0.9)
                return (Outcome.Failed, Path.GetFileName(path) + " is " + sizeMb + " MB but this PC has only " + ramMb + " MB of memory. Use a smaller (e.g. 7B or Q4) model.");

            if (!LlamaNative.LibrariesPresent()) // don't load them: that would lock Llama to the CPU build for this session
                return (Outcome.Failed, "the llama.cpp libraries were not found in the plugin folder.");

            return (Outcome.Passed, Path.GetFileName(path) + " (" + sizeMb + " MB, this PC has " + ramMb + " MB of memory).");
        }

        (Outcome, string) CheckVosk(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return (Outcome.Skipped, "no 'Vosk Model Directory Path' connected. Connect the model folder to check it.");

            string root = VoskListener.FindModelRoot(path);
            if (root == null)
                return (Outcome.Failed, "no Vosk model found at " + path + ". It should contain the folders 'am' and 'conf'.");

            return (Outcome.Passed, root);
        }

        // Only Kakadoo GPT needs the internet, so a failure here is reported as skipped, not as an error.
        async Task<(Outcome, string)> CheckOpenAiAsync()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                {
                    await http.GetAsync("https://api.openai.com/");
                    return (Outcome.Passed, "api.openai.com is reachable.");
                }
            }
            catch (Exception)
            {
                return (Outcome.Skipped, "api.openai.com is not reachable. Only Kakadoo GPT needs the internet; the other components work offline.");
            }
        }

        void Log(string message)
        {
            lock (gate) messages.Add(message);
            RequestRefresh();
        }

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon => Icons.Load("KakadooChecker");

        public override Guid ComponentGuid => new Guid("a91e5d38-7f02-4c6b-b3d4-0e8c1f52a769");
    }
}
