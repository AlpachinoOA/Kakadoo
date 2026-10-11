using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Kakadoo
{
    /// <summary>Lets a listener be nudged towards expected words, such as slider names.</summary>
    public interface IHintedListener
    {
        string Hint { get; set; }
    }

    /// <summary>Lets a listener use a specific model file or folder instead of the shared setting.</summary>
    public interface IModelPathListener
    {
        string ModelPath { get; set; }
    }

    /// <summary>A speech engine can't be used at all (missing model, missing libraries). Another engine may still work.</summary>
    public class SpeechSetupException : Exception
    {
        public SpeechSetupException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// Offline speech recognition with Whisper (whisper.cpp). Free and local; much more accurate than the
    /// built-in Windows recognizer, also with accents and background noise.
    /// </summary>
    public class WhisperListener : ISpeechListener, IHintedListener, IModelPathListener
    {
        const int SampleRate = 16000;

        static readonly object Gate = new object();
        // One engine per model file. They are kept, never disposed, because another component may be using one.
        static readonly Dictionary<string, WhisperFactory> Factories = new Dictionary<string, WhisperFactory>(StringComparer.OrdinalIgnoreCase);

        public string Hint { get; set; }

        /// <summary>A model file or folder just for this listener. Blank uses the shared setting.</summary>
        public string ModelPath { get; set; }

        /// <summary>Loads the engine ahead of time so the first command isn't slow. Throws <see cref="SpeechSetupException"/> if it can't start.</summary>
        public static void Warmup(string modelFile)
        {
            try { GetFactory(modelFile); }
            catch (Exception e) when (!(e is SpeechSetupException)) { throw new SpeechSetupException(Friendly(e), e); }
        }

        public async Task<string> ListenOnceAsync(CancellationToken token)
        {
            // Set up the engine first (this is where a missing model or library shows up), then listen.
            string modelFile;
            try
            {
                modelFile = WhisperModel.EnsureAvailable(ModelPath); // downloads once if there is no local model
                GetFactory(modelFile);
            }
            catch (Exception e) when (!(e is SpeechSetupException))
            {
                throw new SpeechSetupException(Friendly(e), e);
            }

            byte[] pcm = await Task.Run(() => Microphone.RecordPhrase(SampleRate, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(15), token), token);
            if (pcm.Length < SampleRate * 2 * 0.4) return null; // nothing said, or just a click

            return await TranscribeAsync(modelFile, pcm, token);
        }

        static string Friendly(Exception e)
        {
            if (e is DllNotFoundException || e is BadImageFormatException || e is TypeInitializationException || e is EntryPointNotFoundException)
                return "The Whisper libraries could not be loaded (" + e.Message + "). Reinstall the Kakadoo package and make sure the Microsoft Visual C++ Redistributable (x64) is installed.";
            return e.Message;
        }

        async Task<string> TranscribeAsync(string modelFile, byte[] pcm, CancellationToken token)
        {
            var samples = new float[pcm.Length / 2];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;

            var builder = GetFactory(modelFile).CreateBuilder()
                .WithThreads(Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1)))
                .WithLanguage(WhisperModel.IsEnglishOnly(modelFile) ? "en" : "auto")
                // Speed: a command is one short phrase, so skip the extras Whisper does for long recordings.
                .WithSingleSegment()
                .WithNoContext()
                .WithTemperature(0f)
                .WithTemperatureInc(0f)
                // Whisper always processes 30 s of audio; only look at as much as was actually recorded (50 frames per second).
                .WithAudioContextSize(Math.Min(1500, Math.Max(256, (int)(samples.Length / (double)SampleRate * 50) + 64)));
            if (!string.IsNullOrWhiteSpace(Hint))
                builder = builder.WithPrompt(Hint);

            var text = new StringBuilder();
            using (var processor = builder.Build())
            {
                await foreach (var segment in processor.ProcessAsync(samples, token))
                    text.Append(segment.Text).Append(' ');
            }

            return Clean(text.ToString());
        }

        /// <summary>Removes Whisper's non-speech markers like [BLANK_AUDIO] or (music).</summary>
        static string Clean(string raw)
        {
            string cleaned = Regex.Replace(raw, @"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*", " ");
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

            // Small models sometimes repeat a short phrase; keep each sentence once, in order.
            var kept = new List<string>();
            foreach (var s in Regex.Split(cleaned, @"(?<=[.!?])\s+").Where(s => s.Length > 0))
                if (kept.Count == 0 || !string.Equals(kept[kept.Count - 1], s, StringComparison.OrdinalIgnoreCase)) kept.Add(s);
            cleaned = string.Join(" ", kept);

            return cleaned.Length == 0 ? null : cleaned;
        }

        static WhisperFactory GetFactory(string modelFile)
        {
            lock (Gate)
            {
                if (!Factories.TryGetValue(modelFile, out var factory))
                {
                    WhisperNative.Register();
                    Factories[modelFile] = factory = WhisperFactory.FromPath(modelFile);
                }
                return factory;
            }
        }
    }

    /// <summary>
    /// Finds the Whisper model. Order: a path the user set (menu or KAKADOO_WHISPER_MODEL), then a "models" folder next to the
    /// plugin, then ~/Kakadoo/models, then a one-time download. The path can be a model file or a folder with ggml-*.bin files,
    /// so offices without internet access can install the model by hand.
    /// </summary>
    static class WhisperModel
    {
        static readonly string[] Preference = { "small.en", "base.en", "small", "base", "medium.en", "medium", "tiny.en", "tiny" };
        static readonly object Gate = new object();

        /// <summary>
        /// Model name to download when none is installed. Override with KAKADOO_WHISPER_MODEL set to a name (e.g. "base.en", "small").
        /// Otherwise "small.en" (466 MB, most accurate) on a capable PC and "base.en" (141 MB, about 3x faster) on a modest one.
        /// </summary>
        public static string Name =>
            EnvValue is string v && !LooksLikePath(v) ? v : AutoName;

        static string AutoName
        {
            get
            {
                double ramGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0;
                return ramGb >= 8 && Environment.ProcessorCount >= 6 ? "small.en" : "base.en";
            }
        }

        static string EnvValue
        {
            get
            {
                string v = Environment.GetEnvironmentVariable("KAKADOO_WHISPER_MODEL");
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim().Trim('"');
            }
        }

        static bool LooksLikePath(string v) =>
            v.IndexOfAny(new[] { '\\', '/' }) >= 0 || v.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || System.IO.Directory.Exists(v);

        /// <summary>The path the user configured (environment variable first, then the right-click setting), or null.</summary>
        public static string UserPath
        {
            get
            {
                if (EnvValue is string v && LooksLikePath(v)) return v;
                string saved = Settings.WhisperModelPath;
                return string.IsNullOrWhiteSpace(saved) ? null : saved.Trim().Trim('"');
            }
        }

        /// <summary>The "models" folder next to the plugin, for machines set up by IT.</summary>
        public static string PluginFolder =>
            Path.Combine(Path.GetDirectoryName(typeof(WhisperModel).Assembly.Location), "models");

        /// <summary>Where downloaded models are stored.</summary>
        public static string Directory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Kakadoo", "models");

        public static string FilePath => Path.Combine(Directory, "ggml-" + Name + ".bin");

        public static string DownloadUrl => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-" + Name + ".bin";

        public static bool IsEnglishOnly(string file) =>
            Path.GetFileName(file).IndexOf(".en", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// The model file to use without downloading anything, and where it came from; null if there isn't one.
        /// Throws <see cref="SpeechSetupException"/> if the user set a path that doesn't work.
        /// </summary>
        public static string Locate(out string source, string overridePath = null)
        {
            source = null;

            string user = string.IsNullOrWhiteSpace(overridePath) ? UserPath : overridePath.Trim().Trim('"');
            if (user != null)
            {
                if (File.Exists(user)) { CheckIsModel(user); source = "your model path"; return user; }
                if (System.IO.Directory.Exists(user))
                {
                    string inFolder = PickFrom(user);
                    if (inFolder == null)
                        throw new SpeechSetupException("No Whisper model (a file like ggml-base.en.bin) was found in the folder " + user + ".");
                    CheckIsModel(inFolder);
                    source = "your model folder";
                    return inFolder;
                }
                throw new SpeechSetupException("The Whisper model path does not exist: " + user);
            }

            foreach (var (folder, label) in new[] { (PluginFolder, "the plugin's models folder"), (Directory, "the Kakadoo models folder") })
            {
                if (!System.IO.Directory.Exists(folder)) continue;
                string exact = Path.Combine(folder, "ggml-" + Name + ".bin");
                string found = File.Exists(exact) ? exact : PickFrom(folder);
                if (found != null) { source = label; return found; }
            }
            return null;
        }

        /// <summary>Whisper models start with the bytes "lmgg" (the ggml magic number). Anything else would crash the engine or give garbage.</summary>
        static void CheckIsModel(string file)
        {
            var magic = new byte[4];
            using (var stream = File.OpenRead(file))
                if (stream.Read(magic, 0, 4) < 4 || Encoding.ASCII.GetString(magic) != "lmgg")
                    throw new SpeechSetupException(Path.GetFileName(file) + " is not a Whisper model (expected a ggml-*.bin file from huggingface.co/ggerganov/whisper.cpp).");
        }

        /// <summary>The best model in a folder: the one for this PC if present, otherwise by preference, otherwise any .bin.</summary>
        static string PickFrom(string folder)
        {
            string Path_(string n) => Path.Combine(folder, "ggml-" + n + ".bin");
            foreach (var name in new[] { Name }.Concat(Preference))
                if (File.Exists(Path_(name))) return Path_(name);

            return System.IO.Directory.GetFiles(folder, "ggml-*.bin").OrderBy(f => f).FirstOrDefault()
                ?? System.IO.Directory.GetFiles(folder, "*.bin").OrderBy(f => f).FirstOrDefault();
        }

        /// <summary>The instructions for installing the model by hand, for when downloading isn't possible.</summary>
        public static string ManualInstallHelp =>
            "No internet? Download " + DownloadUrl + " on another PC and save it in " + Directory + " (or in " + PluginFolder
            + "), or set its location in the right-click menu: 'Whisper model file or folder'.";

        /// <summary>Returns the model file to use, downloading it once if there isn't one. Blocks until it is there.</summary>
        public static string EnsureAvailable(string overridePath = null)
        {
            lock (Gate)
            {
                string local = Locate(out _, overridePath);
                if (local != null) return local;

                if (Environment.GetEnvironmentVariable("KAKADOO_OFFLINE") == "1")
                    throw new SpeechSetupException("No Whisper model is installed and KAKADOO_OFFLINE is set. " + ManualInstallHelp);

                System.IO.Directory.CreateDirectory(Directory);
                string target = FilePath;
                string temp = target + ".download";

                try
                {
                    ModelDownload.Start("Downloading Whisper model '" + Name + "' (once)", Directory);
                    using (var http = new HttpClient(new HttpClientHandler { UseProxy = true, DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials }) { Timeout = TimeSpan.FromMinutes(30) })
                    using (var response = http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                    {
                        response.EnsureSuccessStatusCode();
                        long total = response.Content.Headers.ContentLength ?? 0;
                        using (var source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                        using (var file = File.Create(temp))
                        {
                            var buffer = new byte[81920];
                            long done = 0;
                            int read;
                            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                file.Write(buffer, 0, read);
                                done += read;
                                ModelDownload.Report(done, total);
                            }
                            if (total > 0 && done != total)
                                throw new IOException("the download was cut off (" + done / 1048576 + " of " + total / 1048576 + " MB)");
                        }
                    }
                    CheckIsModel(temp); // a cut-off download or an error page would not be a model
                    File.Move(temp, target);
                }
                catch (Exception e)
                {
                    if (File.Exists(temp)) File.Delete(temp);
                    throw new SpeechSetupException("Could not download the Whisper model '" + Name + "' (" + e.Message + "). " + ManualInstallHelp, e);
                }
                finally
                {
                    ModelDownload.Finish();
                }
                return target;
            }
        }
    }

    /// <summary>Points Whisper.net at the native libraries inside the plugin folder.</summary>
    static class WhisperNative
    {
        static bool registered;

        public static void Register()
        {
            if (registered) return;

#if NET7_0_OR_GREATER
            // The Whisper libraries use AVX2 instructions; on an older processor they would crash instead of failing cleanly.
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64 && !System.Runtime.Intrinsics.X86.Avx2.IsSupported)
                throw new SpeechSetupException("The processor of this PC is too old for Whisper (it needs AVX2).");
#endif

            // Whisper.net looks for its libraries in runtimes/<platform> next to its own dll; those folders are filled by the NuGet package.
            string root = Path.Combine(Path.GetDirectoryName(typeof(WhisperNative).Assembly.Location), "runtimes");
            bool mac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            string dir = Path.Combine(root, mac
                ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "macos-arm64" : "macos-x64")
                : "win-x64");
            if (!System.IO.Directory.Exists(dir))
                throw new SpeechSetupException("The Whisper libraries were not found in " + dir + ". Reinstall the Kakadoo package.");

            string Lib(string name) => Path.Combine(dir, mac ? "lib" + name + ".dylib" : name + ".dll");

            // Load the pieces in dependency order, then tell Whisper.net where its main library is.
            foreach (var dep in new[] { "ggml-base-whisper", "ggml-cpu-whisper", "ggml-blas-whisper", "ggml-metal-whisper", "ggml-whisper" })
                if (File.Exists(Lib(dep))) NativeLibrary.TryLoad(Lib(dep), out _);

            RuntimeOptions.LibraryPath = Lib("whisper");
            registered = true;
        }
    }
}
