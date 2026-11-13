using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using SherpaOnnx;

namespace Kakadoo
{
    /// <summary>Finds a Parakeet model: a folder with encoder, decoder and joiner .onnx files and tokens.txt (the sherpa-onnx layout).</summary>
    static class ParakeetModel
    {
        public static string Folder =>
            Path.Combine(ModelCatalog.ModelsRoot, "parakeet");

        public static string FolderFor(string id) => Path.Combine(Folder, id);

        static string Pick(string dir, string prefix)
        {
            var found = Directory.GetFiles(dir, prefix + "*.onnx");
            return found.OrderByDescending(f => f.IndexOf("int8", StringComparison.OrdinalIgnoreCase) >= 0).ThenBy(f => f).FirstOrDefault();
        }

        public static bool IsModelFolder(string dir) =>
            Directory.Exists(dir) && File.Exists(Path.Combine(dir, "tokens.txt")) && Pick(dir, "encoder") != null && Pick(dir, "decoder") != null && Pick(dir, "joiner") != null;

        /// <summary>The folder holding the model files; accepts the parent folder of a downloaded model too. Null if there is none.</summary>
        public static string FindModelFolder(string path)
        {
            path = path?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;
            if (IsModelFolder(path)) return path;
            return Directory.GetDirectories(path).FirstOrDefault(IsModelFolder);
        }

        public static void FilesOf(string folder, out string encoder, out string decoder, out string joiner, out string tokens)
        {
            encoder = Pick(folder, "encoder");
            decoder = Pick(folder, "decoder");
            joiner = Pick(folder, "joiner");
            tokens = Path.Combine(folder, "tokens.txt");
        }

        /// <summary>The model folder to use: the given path, or else the one chosen in Kakadoo Settings. Throws if there is none that works.</summary>
        public static string Resolve(string overridePath = null)
        {
            string chosen = string.IsNullOrWhiteSpace(overridePath) ? ModelSync.Get(ModelKind.Parakeet) : overridePath.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(chosen))
                throw new SpeechSetupException("No Parakeet model is chosen. Open Kakadoo Settings > Models and download one (v2 for English, v3 for other European languages).");
            if (!Directory.Exists(chosen))
                throw new SpeechSetupException("The Parakeet model folder does not exist: " + chosen);
            string folder = FindModelFolder(chosen);
            if (folder == null)
                throw new SpeechSetupException("That is not a Parakeet model folder (it needs encoder, decoder and joiner .onnx files and tokens.txt): " + chosen);
            return folder;
        }

        /// <summary>The catalog model that lives in this folder, if it is one.</summary>
        static ModelEntry CatalogEntry(string folder) =>
            ModelCatalog.Of(ModelKind.Parakeet).FirstOrDefault(e => ModelSync.SamePath(e.FolderPath, folder));

        /// <summary>Throws when the chosen language needs a different model than this one.</summary>
        public static void CheckLanguage(string folder)
        {
            string lang = Settings.Language;
            var entry = CatalogEntry(folder);
            if (entry == null) return; // a model of your own: can't know what it was trained on
            if (entry.EnglishOnly && lang != "en" && lang != "auto")
                throw new SpeechSetupException("Parakeet " + entry.Id + " understands English only, but the language is set to " + Languages.Name(lang) + ". Use the multilingual v3 model or a Whisper multilingual model, or set the language to English.");
            if (!entry.EnglishOnly && !Languages.ParakeetV3Supports(lang))
                throw new SpeechSetupException("Parakeet v3 does not cover " + Languages.Name(lang) + ". Use a Whisper multilingual model for it, or choose another language.");
        }
    }

    /// <summary>Points sherpa-onnx at the native libraries inside the plugin folder.</summary>
    static class ParakeetNative
    {
        static bool registered;

        public static void Register()
        {
            if (registered) return;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                throw new SpeechSetupException("Parakeet is only available on Windows for now.");

            string dir = Path.Combine(Path.GetDirectoryName(typeof(ParakeetNative).Assembly.Location), "runtimes", "win-x64", "native");
            // onnxruntime first, because the sherpa library depends on it; both are matched by name when the managed side asks for them.
            foreach (var name in new[] { "onnxruntime.dll", "sherpa-onnx-c-api.dll" })
            {
                string file = Path.Combine(dir, name);
                if (!File.Exists(file)) throw new SpeechSetupException("The Parakeet library " + name + " was not found in " + dir + ". Reinstall the Kakadoo package.");
                NativeLibrary.Load(file);
            }
            registered = true;
        }
    }

    /// <summary>Offline speech recognition with NVIDIA Parakeet, run by sherpa-onnx on the CPU. English (v2) or 25 European languages (v3).</summary>
    public class ParakeetListener : ISpeechListener, IModelPathListener
    {
        const int SampleRate = 16000;

        static readonly object Gate = new object();
        static readonly Dictionary<string, OfflineRecognizer> Recognizers = new Dictionary<string, OfflineRecognizer>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A model folder just for this listener. Blank uses the one chosen in Kakadoo Settings.</summary>
        public string ModelPath { get; set; }

        /// <summary>Loads the model so the first command isn't slow. Throws <see cref="SpeechSetupException"/> if it can't run.</summary>
        public static void Warmup(string folder)
        {
            try { GetRecognizer(folder); }
            catch (Exception e) when (!(e is SpeechSetupException)) { throw new SpeechSetupException(Friendly(e), e); }
        }

        public async Task<string> ListenOnceAsync(CancellationToken token)
        {
            string folder = ParakeetModel.Resolve(ModelPath);
            ParakeetModel.CheckLanguage(folder);

            var loading = Task.Run(() => GetRecognizer(folder));
            byte[] pcm = await Task.Run(() => Microphone.RecordPhrase(SampleRate, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(15), token), token);
            try { await loading; }
            catch (Exception e) when (!(e is SpeechSetupException)) { throw new SpeechSetupException(Friendly(e), e); }
            if (pcm.Length < SampleRate * 2 * 0.4) return null; // nothing said, or just a click

            var samples = new float[pcm.Length / 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
            return await Task.Run(() => Decode(folder, samples), token);
        }

        /// <summary>Recognises mono 16 kHz samples (-1..1). Public so a model can be tried without the microphone.</summary>
        public static string Decode(string folder, float[] samples)
        {
            var recognizer = GetRecognizer(folder);
            lock (recognizer)
            {
                using (var stream = recognizer.CreateStream())
                {
                    stream.AcceptWaveform(SampleRate, samples);
                    recognizer.Decode(stream);
                    return Clean(stream.Result.Text);
                }
            }
        }

        /// <summary>Parakeet writes punctuated sentences ("Height 20."); the commands only need the words.</summary>
        static string Clean(string raw)
        {
            string text = Regex.Replace(raw ?? "", @"\s+", " ").Trim();
            text = Regex.Replace(text, @"[.!?,;:]+(?=\s|$)", "").Trim();
            return text.Length == 0 ? null : text;
        }

        static string Friendly(Exception e)
        {
            if (e is DllNotFoundException || e is BadImageFormatException || e is TypeInitializationException || e is EntryPointNotFoundException)
                return "The Parakeet libraries could not be loaded (" + e.Message + "). Reinstall the Kakadoo package and make sure the Microsoft Visual C++ Redistributable (x64) is installed.";
            return e.Message;
        }

        static OfflineRecognizer GetRecognizer(string folder)
        {
            lock (Gate)
            {
                if (Recognizers.TryGetValue(folder, out var existing)) return existing;
                ParakeetNative.Register();
                ParakeetModel.FilesOf(folder, out string encoder, out string decoder, out string joiner, out string tokens);

                var config = new OfflineRecognizerConfig();
                config.FeatConfig.SampleRate = SampleRate;
                config.FeatConfig.FeatureDim = 80;
                config.ModelConfig.Transducer.Encoder = encoder;
                config.ModelConfig.Transducer.Decoder = decoder;
                config.ModelConfig.Transducer.Joiner = joiner;
                config.ModelConfig.Tokens = tokens;
                config.ModelConfig.ModelType = "nemo_transducer";
                config.ModelConfig.Provider = "cpu";
                config.ModelConfig.NumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1));
                config.DecodingMethod = "greedy_search";

                var recognizer = new OfflineRecognizer(config);
                Recognizers[folder] = recognizer; // kept, never disposed, because another component may be using it
                return recognizer;
            }
        }
    }
}
