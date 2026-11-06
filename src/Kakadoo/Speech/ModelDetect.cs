using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Kakadoo
{
    /// <summary>What a file or folder turned out to be, and where the model really is.</summary>
    sealed class DetectedModel
    {
        public ModelKind Kind;
        public string Path, Label;
    }

    /// <summary>Works out which kind of model a file or folder holds, so the user never has to say.</summary>
    static class ModelDetect
    {
        /// <summary>Looks at the path itself, then (for a folder) inside it, up to two levels down. Null when nothing is recognised.</summary>
        public static DetectedModel Find(string path)
        {
            path = (path ?? "").Trim().Trim('"');
            try
            {
                var here = Identify(path);
                if (here != null || !Directory.Exists(path)) return here;

                foreach (var file in Directory.GetFiles(path).Where(f => IsModelFile(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    if (Identify(file) is DetectedModel m) return m;
                foreach (var dir in Directory.GetDirectories(path).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    if (Identify(dir) is DetectedModel m) return m;
                    foreach (var file in Directory.GetFiles(dir).Where(f => IsModelFile(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                        if (Identify(file) is DetectedModel f) return f;
                }
            }
            catch (Exception) { }
            return null;
        }

        static bool IsModelFile(string file)
        {
            string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
            return ext == ".gguf" || ext == ".bin";
        }

        static DetectedModel Identify(string path)
        {
            if (Directory.Exists(path))
            {
                string parakeet = ParakeetModel.FindModelFolder(path);
                if (parakeet != null) return new DetectedModel { Kind = ModelKind.Parakeet, Path = parakeet, Label = "Parakeet speech model" };
                string vosk = VoskListener.FindModelRoot(path);
                if (vosk != null) return new DetectedModel { Kind = ModelKind.Vosk, Path = vosk, Label = "Vosk speech model" };
                return null;
            }
            if (!File.Exists(path)) return null;

            var magic = new byte[4];
            using (var stream = File.OpenRead(path))
                if (stream.Read(magic, 0, 4) < 4) return null;
            string head = Encoding.ASCII.GetString(magic);
            if (head == "GGUF") return new DetectedModel { Kind = ModelKind.Llama, Path = path, Label = "Llama language model" };
            if (head == "lmgg") return new DetectedModel { Kind = ModelKind.Custom, Path = path, Label = "Whisper-compatible speech model" };
            return null;
        }
    }
}
