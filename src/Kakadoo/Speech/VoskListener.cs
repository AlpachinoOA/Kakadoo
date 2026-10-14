using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json.Linq;
using Vosk;

namespace Kakadoo
{
    /// <summary>Offline speech recognition with Vosk. Records a fixed window, then transcribes it.</summary>
    public class VoskListener : ISpeechListener
    {
        const int SampleRate = 16000;
        static readonly Dictionary<string, Model> Models = new Dictionary<string, Model>();

        readonly string modelPath;
        readonly int seconds;

        public VoskListener(string modelPath, int seconds = 5)
        {
            this.modelPath = modelPath;
            this.seconds = seconds;
        }

        public Task<string> ListenOnceAsync(CancellationToken token)
        {
            return Task.Run(() =>
            {
                string root = FindModelRoot(modelPath)
                    ?? throw new DirectoryNotFoundException("No Vosk model found at " + modelPath + ". Please provide the folder that contains 'am' and 'conf'.");

                NativeResolver.Register();
                var model = GetModel(root);
                byte[] audio = Microphone.Record(SampleRate, TimeSpan.FromSeconds(seconds), token);

                using (var recognizer = new VoskRecognizer(model, SampleRate))
                {
                    recognizer.AcceptWaveform(audio, audio.Length);
                    string text = (string)JObject.Parse(recognizer.FinalResult())["text"];
                    return string.IsNullOrWhiteSpace(text) ? null : text;
                }
            }, token);
        }

        /// <summary>
        /// The folder holding the model files (am, conf, ...). Accepts the parent folder of a downloaded model too.
        /// Returns null if none is found; a bad path would otherwise crash Vosk's native code.
        /// </summary>
        public static string FindModelRoot(string path)
        {
            path = path?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;
            if (IsModelFolder(path)) return path;
            return Directory.GetDirectories(path).FirstOrDefault(IsModelFolder);
        }

        static bool IsModelFolder(string dir) =>
            Directory.Exists(Path.Combine(dir, "am")) && Directory.Exists(Path.Combine(dir, "conf"));

        static Model GetModel(string path)
        {
            lock (Models)
            {
                if (!Models.TryGetValue(path, out var model))
                    Models[path] = model = new Model(path);
                return model;
            }
        }
    }

    /// <summary>Finds libvosk in the plugin's runtimes folder, since Rhino doesn't probe it for plugins.</summary>
    static class NativeResolver
    {
        static bool registered;

        public static void Register()
        {
#if !NETFRAMEWORK
            if (registered) return;
            registered = true;
            MacTrust.Prepare();
            NativeLibrary.SetDllImportResolver(typeof(Model).Assembly, (name, assembly, path) =>
            {
                if (name != "libvosk") return IntPtr.Zero;

                string dir = Path.GetDirectoryName(typeof(NativeResolver).Assembly.Location);
                string file = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? Path.Combine(dir, "runtimes", "osx", "native", "libvosk.dylib")
                    : Path.Combine(dir, "runtimes", "win-x64", "native", "libvosk.dll");

                return File.Exists(file) && NativeLibrary.TryLoad(file, out var handle) ? handle : IntPtr.Zero;
            });
#endif
        }
    }

}
