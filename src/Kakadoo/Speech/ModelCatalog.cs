using System;
using System.Drawing;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace Kakadoo
{
    enum ModelKind { Whisper, Vosk, Llama, Parakeet, Custom }

    /// <summary>One file of a model that comes as several files.</summary>
    sealed class ModelFile
    {
        public string Name, Url;
        public long Bytes;
    }

    /// <summary>A model the settings window can download, select and delete. Whisper with an empty Id means "automatic".</summary>
    sealed class ModelEntry
    {
        public ModelKind Kind;
        public string Id, Title, Note, Tip, Url, Lang;
        public long SizeMb;
        public bool EnglishOnly;
        public ModelFile[] Files;        // Parakeet
        public string ExternalPath;      // Custom: a file outside the custom folder

        /// <summary>How accurate and how fast the model is, 0 to 5 (0 = unknown). Shown as the two small bars on the model card.</summary>
        public double Accuracy => ModelCatalog.RatingOf(this).Item1;

        public double Speed => ModelCatalog.RatingOf(this).Item2;

        /// <summary>"English only", "Multi-language"... shown at the bottom left of the model card.</summary>
        public string LanguageTag =>
            Kind == ModelKind.Custom ? "Custom"
            : Kind == ModelKind.Llama ? "Understands commands in English"
            : Kind == ModelKind.Vosk && Lang != null ? Lang
            : Kind == ModelKind.Parakeet && !EnglishOnly ? "25 European languages"
            : EnglishOnly ? "English only" : "Multi-language";

        /// <summary>The model's page on its source website, for reading about it.</summary>
        public string Page =>
            Kind == ModelKind.Custom ? null
            : Kind == ModelKind.Parakeet ? "https://huggingface.co/nvidia/parakeet-tdt-0.6b-" + (EnglishOnly ? "v2" : "v3")
            : Kind == ModelKind.Vosk ? "https://alphacephei.com/vosk/models"
            : IsAuto ? "https://huggingface.co/ggerganov/whisper.cpp"
            : Url != null && Url.Contains("/resolve/") ? Url.Substring(0, Url.IndexOf("/resolve/", StringComparison.Ordinal)) : null;

        /// <summary>The family the model belongs to, used to group and colour the cards.</summary>
        public string Family =>
            Kind == ModelKind.Whisper ? "Whisper" : Kind == ModelKind.Parakeet ? "Parakeet" : Kind == ModelKind.Vosk ? "Vosk"
            : Kind == ModelKind.Llama ? "Llama 2" : "Custom";

        public Color FamilyColor => ModelCatalog.ColorOf(Family);

        public bool IsAuto => Kind == ModelKind.Whisper && Id.Length == 0;

        /// <summary>A custom model that lives outside the custom folder; removing it only forgets it.</summary>
        public bool IsExternal => Kind == ModelKind.Custom && ExternalPath != null;

        public string Size => SizeMb <= 0 ? "" : SizeMb >= 1000 ? (SizeMb / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB" : SizeMb + " MB";

        public string FolderPath => Kind == ModelKind.Parakeet ? ParakeetModel.FolderFor(Id) : Kind == ModelKind.Vosk ? ModelCatalog.VoskFolderFor(Id) : null;

        public string FilePath =>
            Kind == ModelKind.Whisper ? Path.Combine(WhisperModel.Directory, "ggml-" + Id + ".bin")
            : Kind == ModelKind.Llama ? Path.Combine(ModelCatalog.LlamaFolder, Id)
            : Kind == ModelKind.Custom ? (ExternalPath ?? Path.Combine(ModelCatalog.CustomFolder, Id))
            : null;

        /// <summary>The path to check when asking whether the model runs.</summary>
        public string RunPath => IsAuto ? null : FolderPath ?? FilePath;

        public bool Installed
        {
            get
            {
                if (IsAuto) return true;
                switch (Kind)
                {
                    case ModelKind.Vosk: return VoskListener.FindModelRoot(FolderPath) != null;
                    case ModelKind.Parakeet: return Files.All(f => File.Exists(Path.Combine(FolderPath, f.Name)));
                    default: return File.Exists(FilePath);
                }
            }
        }

        public bool CanUse => true;

        public bool CanDelete => !IsAuto;

        public bool InUse
        {
            get
            {
                switch (Kind)
                {
                    case ModelKind.Whisper:
                        // A path typed or chosen that is this very file counts as choosing the model.
                        if (Settings.Engine != "whisper") return false;
                        return string.IsNullOrWhiteSpace(Settings.WhisperModelPath)
                            ? string.Equals(Settings.WhisperModelName, Id, StringComparison.OrdinalIgnoreCase)
                            : !IsAuto && ModelSync.SamePath(Settings.WhisperModelPath, FilePath);
                    case ModelKind.Llama:
                        return ModelSync.SamePath(Settings.LlamaModelPath, FilePath);
                    case ModelKind.Parakeet:
                        return Settings.Engine == "parakeet" && string.IsNullOrWhiteSpace(Settings.ParakeetModelPath) && string.Equals(Settings.ParakeetModelName, Id, StringComparison.OrdinalIgnoreCase);
                    case ModelKind.Custom:
                        return Settings.Engine == "custom" && ModelSync.SamePath(Settings.CustomModelPath, FilePath);
                    case ModelKind.Vosk:
                        if (Settings.Engine != "vosk") return false;
                        return string.IsNullOrWhiteSpace(Settings.VoskModelPath) ? Id == "vosk-small-en" : ModelSync.SamePath(Settings.VoskModelPath, FolderPath);
                    default:
                        return Installed;
                }
            }
        }

        public void Use()
        {
            switch (Kind)
            {
                case ModelKind.Whisper: Settings.WhisperModelPath = ""; Settings.WhisperModelName = Id; Settings.Engine = "whisper"; break;
                case ModelKind.Llama: Settings.LlamaModelPath = FilePath; break;
                case ModelKind.Vosk: Settings.VoskModelPath = Id == "vosk-small-en" ? "" : FolderPath; Settings.Engine = "vosk"; break;
                case ModelKind.Parakeet: Settings.ParakeetModelPath = ""; Settings.ParakeetModelName = Id; Settings.Engine = "parakeet"; break;
                case ModelKind.Custom: Settings.CustomModelPath = FilePath; Settings.Engine = "custom"; break;
            }
        }

        public void Delete()
        {
            bool wasInUse = InUse;
            if (Kind == ModelKind.Vosk) { if (Directory.Exists(FolderPath)) Directory.Delete(FolderPath, true); }
            else if (Kind == ModelKind.Parakeet) { if (Directory.Exists(FolderPath)) Directory.Delete(FolderPath, true); }
            else if (!IsExternal && File.Exists(FilePath)) File.Delete(FilePath);

            if (!wasInUse) return;
            switch (Kind)
            {
                case ModelKind.Whisper: Settings.WhisperModelName = ""; break;
                case ModelKind.Llama: Settings.LlamaModelPath = ""; break;
                case ModelKind.Vosk: Settings.VoskModelPath = ""; break;
                case ModelKind.Parakeet: Settings.ParakeetModelName = ""; break;
                case ModelKind.Custom: Settings.CustomModelPath = ""; break;
            }
            if (Kind == ModelKind.Parakeet || Kind == ModelKind.Custom || Kind == ModelKind.Vosk) Settings.Engine = "whisper";
        }

        /// <summary>Downloads and installs the model; blocks until done. Throws on failure or cancellation.</summary>
        public void Install(Action<long, long> report, CancellationToken ct)
        {
            switch (Kind)
            {
                case ModelKind.Whisper: ModelFiles.Download(Url, FilePath, report, ct, WhisperModel.CheckIsModel); break;
                case ModelKind.Llama: ModelFiles.Download(Url, FilePath, report, ct, ModelFiles.CheckIsGguf); break;
                case ModelKind.Vosk: ModelFiles.DownloadZip(Url, FolderPath, report, ct); break;
                case ModelKind.Parakeet:
                    long total = Files.Sum(f => f.Bytes), before = 0;
                    foreach (var file in Files)
                    {
                        long offset = before;
                        ModelFiles.Download(file.Url, Path.Combine(FolderPath, file.Name), (d, t) => report(offset + d, total), ct);
                        before += file.Bytes;
                    }
                    break;
            }
        }
    }

    static class ModelCatalog
    {
        static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Kakadoo");

        public static string Folder => Root;

        public static readonly string[] Families = { "Whisper", "Parakeet", "Vosk", "Llama 2", "Custom" };

        public static string FamilyNote(string family) =>
            family == "Whisper" ? "OpenAI speech recognition, many sizes and languages"
            : family == "Parakeet" ? "NVIDIA speech recognition, very accurate and fast"
            : family == "Vosk" ? "Light offline recognition in many languages"
            : family == "Llama 2" ? "Language models for Kakadoo Llama"
            : "Your own models";

        public static Color ColorOf(string family) =>
            family == "Whisper" ? Color.FromArgb(66, 133, 244)
            : family == "Parakeet" ? Color.FromArgb(118, 185, 0)
            : family == "Vosk" ? Color.FromArgb(156, 106, 222)
            : family == "Llama 2" ? Color.FromArgb(239, 125, 50)
            : Color.FromArgb(0, 168, 160);
        public static string LlamaFolder => Path.Combine(Root, "llama");
        public static string VoskFolder => Path.Combine(Root, "vosk-model");

        /// <summary>The first Vosk model keeps its old folder; the others each get their own.</summary>
        public static string VoskFolderFor(string id) => id == "vosk-small-en" ? VoskFolder : Path.Combine(Root, "vosk-models", id);

        static ModelEntry Vosk(string id, string zip, string lang, string title, long mb, string note, string tip) => new ModelEntry
        {
            Kind = ModelKind.Vosk, Id = id, Title = title, SizeMb = mb, Lang = lang, EnglishOnly = lang == "English", Note = note, Tip = tip,
            Url = "https://alphacephei.com/vosk/models/" + zip + ".zip"
        };

        /// <summary>Drop any Whisper-compatible .bin file here and it shows up under Custom models.</summary>
        public static string CustomFolder => Path.Combine(Root, "custom-models");

        static ModelEntry Whisper(string id, string title, string note, string tip, long mb) => new ModelEntry
        {
            Kind = ModelKind.Whisper, Id = id, Title = title, Note = note, Tip = tip, SizeMb = mb, EnglishOnly = id.EndsWith(".en"),
            Url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-" + id + ".bin"
        };

        static ModelEntry Llama(string repo, string file, string title, string note, string tip, long mb) => new ModelEntry
        {
            Kind = ModelKind.Llama, Id = file, Title = title, Note = note, Tip = tip, SizeMb = mb,
            Url = "https://huggingface.co/TheBloke/" + repo + "/resolve/main/" + file
        };

        static ModelEntry Parakeet(string id, string repo, string title, string note, string tip, bool englishOnly, params long[] bytes)
        {
            string url = "https://huggingface.co/csukuangfj/" + repo + "/resolve/main/";
            string[] names = { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" };
            return new ModelEntry
            {
                Kind = ModelKind.Parakeet, Id = id, Title = title, Note = note, Tip = tip, EnglishOnly = englishOnly, SizeMb = bytes.Sum() / 1048576,
                Files = names.Select((n, i) => new ModelFile { Name = n, Url = url + n, Bytes = bytes[i] }).ToArray()
            };
        }

        const string MultiTip = "Tip: needed for any language other than English. Choose the language in General.";

        public static readonly ModelEntry[] All =
        {
            new ModelEntry { Kind = ModelKind.Whisper, Id = "", Title = "Automatic", Note = "Kakadoo picks small or base for this PC and downloads it when needed",
                Tip = "Tip: leave this on unless you want to choose a model yourself. It follows the language you choose." },
            Whisper("tiny.en", "Whisper tiny (English)", "Fastest, least accurate", "Tip: only for very slow PCs; it often mishears short slider names.", 75),
            Whisper("base.en", "Whisper base (English)", "Fast, good on modest PCs", "Tip: the best starting point on a laptop.", 142),
            Whisper("small.en", "Whisper small (English)", "More accurate, needs a capable PC", "Tip: recommended with 8 GB of memory and 6 or more cores.", 466),
            Whisper("medium.en", "Whisper medium (English)", "Most accurate, slow without a strong PC", "Tip: needs about 16 GB of memory, and each command takes noticeably longer.", 1533),
            Whisper("tiny", "Whisper tiny (multilingual)", "Fastest multilingual model, least accurate", MultiTip, 75),
            Whisper("base", "Whisper base (multilingual)", "Fast multilingual model", MultiTip, 142),
            Whisper("small", "Whisper small (multilingual)", "Good accuracy in many languages", MultiTip, 466),
            Whisper("medium", "Whisper medium (multilingual)", "Best accuracy in many languages, needs a strong PC", MultiTip, 1533),

            Parakeet("parakeet-v2", "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8", "Parakeet v2 (English)", "Very accurate English, fast on the CPU",
                "Tip: often more accurate than Whisper small for English, and quick. Use it with the Kakadoo Parakeet component. Needs about 2 GB of memory.", true,
                651000000, 7000000, 1900000, 94000),
            Parakeet("parakeet-v3", "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", "Parakeet v3 (25 European languages)", "Accurate in English, German, French, Spanish, Italian, Russian and more",
                "Tip: no Turkish, Arabic or Asian languages; use a Whisper multilingual model for those. Detects the language by itself.", false,
                651000000, 11800000, 6500000, 94000),

            Vosk("vosk-small-en", "vosk-model-small-en-us-0.15", "English", "Vosk small (English)", 40, "Offline recognition that understands spoken numbers",
                "Tip: tiny and runs anywhere, but less accurate than Whisper or Parakeet. Used by Kakadoo Vosk and as the Mac backup."),
            Vosk("vosk-en-lgraph", "vosk-model-en-us-0.22-lgraph", "English", "Vosk English (larger)", 128, "More accurate English, still light",
                "Tip: a good step up from the small model when Vosk is your engine."),
            Vosk("vosk-small-tr", "vosk-model-small-tr-0.3", "Turkish", "Vosk small (Turkish)", 35, "Offline Turkish recognition",
                "Tip: Parakeet has no Turkish; this or a multilingual Whisper model does."),
            Vosk("vosk-small-de", "vosk-model-small-de-0.15", "German", "Vosk small (German)", 45, "Offline German recognition", "Tip: press Use after downloading."),
            Vosk("vosk-small-fr", "vosk-model-small-fr-0.22", "French", "Vosk small (French)", 41, "Offline French recognition", "Tip: press Use after downloading."),
            Vosk("vosk-small-es", "vosk-model-small-es-0.42", "Spanish", "Vosk small (Spanish)", 39, "Offline Spanish recognition", "Tip: press Use after downloading."),
            Vosk("vosk-small-it", "vosk-model-small-it-0.22", "Italian", "Vosk small (Italian)", 48, "Offline Italian recognition", "Tip: press Use after downloading."),
            Vosk("vosk-small-ru", "vosk-model-small-ru-0.22", "Russian", "Vosk small (Russian)", 45, "Offline Russian recognition", "Tip: press Use after downloading."),

            Llama("Llama-2-7B-Chat-GGUF", "llama-2-7b-chat.Q2_K.gguf", "Llama 2 7B Chat, Q2_K", "Smallest, lowest quality",
                "Tip: only when memory is very tight (about 5 GB free). It often picks the wrong slider.", 2830),
            Llama("Llama-2-7B-Chat-GGUF", "llama-2-7b-chat.Q4_K_M.gguf", "Llama 2 7B Chat, Q4_K_M", "Recommended: good balance of speed and quality",
                "Tip: the best choice for most PCs; needs about 6 GB of free memory.", 4080),
            Llama("Llama-2-7B-Chat-GGUF", "llama-2-7b-chat.Q5_K_M.gguf", "Llama 2 7B Chat, Q5_K_M", "Slightly better quality, a bit slower",
                "Tip: needs about 7 GB of free memory.", 4780),
            Llama("Llama-2-13B-chat-GGUF", "llama-2-13b-chat.Q4_K_M.gguf", "Llama 2 13B Chat, Q4_K_M", "Best understanding, needs about 10 GB of free memory",
                "Tip: slow on a CPU; choose GPU in the component's Device input.", 7870),
        };

        static readonly Dictionary<string, (double, double)> Ratings = new Dictionary<string, (double, double)>
        {
            [""] = (3, 3),
            ["tiny.en"] = (1, 5), ["base.en"] = (2, 4.5), ["small.en"] = (3, 3), ["medium.en"] = (4, 1.5),
            ["tiny"] = (1, 5), ["base"] = (2, 4.5), ["small"] = (3, 3), ["medium"] = (4, 1.5),
            ["parakeet-v2"] = (4.5, 4), ["parakeet-v3"] = (4, 4),
            ["vosk-small-en"] = (2, 5), ["vosk-en-lgraph"] = (3, 4),
            ["llama-2-7b-chat.Q2_K.gguf"] = (1.5, 4), ["llama-2-7b-chat.Q4_K_M.gguf"] = (3, 3),
            ["llama-2-7b-chat.Q5_K_M.gguf"] = (3.5, 2.5), ["llama-2-13b-chat.Q4_K_M.gguf"] = (4.5, 1),
        };

        public static (double, double) RatingOf(ModelEntry e) =>
            e.Kind != ModelKind.Custom && Ratings.TryGetValue(e.Id, out var r) ? r : e.Kind == ModelKind.Vosk ? (2, 5) : (0, 0);

        public static IEnumerable<ModelEntry> Of(ModelKind kind) => All.Where(e => e.Kind == kind);

        /// <summary>The custom Whisper-compatible models: every .bin in the custom folder, plus the chosen one if it lives elsewhere.</summary>
        public static List<ModelEntry> CustomEntries()
        {
            var list = new List<ModelEntry>();
            try
            {
                if (Directory.Exists(CustomFolder))
                    foreach (var file in Directory.GetFiles(CustomFolder, "*.bin").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                        list.Add(MakeCustom(Path.GetFileName(file), null, new FileInfo(file).Length));
            }
            catch (Exception) { }

            string chosen = Settings.CustomModelPath;
            if (!string.IsNullOrWhiteSpace(chosen) && File.Exists(chosen) && !list.Any(e => ModelSync.SamePath(e.FilePath, chosen)))
                list.Add(MakeCustom(Path.GetFileName(chosen), chosen, new FileInfo(chosen).Length));
            return list;
        }

        static ModelEntry MakeCustom(string name, string external, long bytes) => new ModelEntry
        {
            Kind = ModelKind.Custom, Id = name, Title = name, ExternalPath = external, SizeMb = bytes / 1048576,
            Note = external == null ? "Custom model in the custom folder" : "Your own file: " + external,
            Tip = "Tip: used by the Kakadoo Custom component. It must be a Whisper-compatible ggml .bin file."
        };
    }

    static class ModelFiles
    {
        static readonly HttpClient Http = new HttpClient(new HttpClientHandler { UseProxy = true, DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials })
        {
            Timeout = TimeSpan.FromHours(2)
        };

        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Locks =
            new System.Collections.Concurrent.ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Downloads to a .download file first, so a cut-off download never looks like a model. Only one download of a file runs at a time
        /// (the settings window and a component can ask for the same model); the second waits and then finds the file there.
        /// </summary>
        public static void Download(string url, string target, Action<long, long> report, CancellationToken ct, Action<string> validate = null)
        {
            object gate = Locks.GetOrAdd(target, _ => new object());
            while (!Monitor.TryEnter(gate, 200)) ct.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(target)) return;
                DownloadCore(url, target, report, ct, validate);
            }
            finally { Monitor.Exit(gate); }
        }

        static void DownloadCore(string url, string target, Action<long, long> report, CancellationToken ct, Action<string> validate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string temp = target + ".download";
            try
            {
                using (var response = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult())
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
                            ct.ThrowIfCancellationRequested();
                            file.Write(buffer, 0, read);
                            done += read;
                            report(done, total);
                        }
                        if (total > 0 && done != total)
                            throw new IOException("the download was cut off (" + done / 1048576 + " of " + total / 1048576 + " MB)");
                    }
                }
                validate?.Invoke(temp);
                if (File.Exists(target)) File.Delete(target);
                File.Move(temp, target);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                throw;
            }
        }

        /// <summary>Downloads a zip and unpacks it into <paramref name="folder"/>, replacing whatever was there.</summary>
        public static void DownloadZip(string url, string folder, Action<long, long> report, CancellationToken ct)
        {
            string zip = folder + ".zip";
            string staging = folder + ".new";
            try
            {
                if (File.Exists(zip)) File.Delete(zip);
                Download(url, zip, report, ct);
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                ZipFile.ExtractToDirectory(zip, staging);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                Directory.Move(staging, folder);
            }
            finally
            {
                try { if (File.Exists(zip)) File.Delete(zip); } catch (Exception) { }
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception) { }
            }
        }

        /// <summary>GGUF files start with the bytes "GGUF".</summary>
        public static void CheckIsGguf(string file)
        {
            var magic = new byte[4];
            using (var stream = File.OpenRead(file))
                if (stream.Read(magic, 0, 4) < 4 || Encoding.ASCII.GetString(magic) != "GGUF")
                    throw new IOException(Path.GetFileName(file) + " is not a GGUF model file.");
        }
    }

    sealed class DuplicateGroup
    {
        public string Keep;
        public List<string> Remove = new List<string>();
        public long Bytes;
    }

    /// <summary>Finds the same model file saved more than once in the places Kakadoo looks, and picks the copy to keep.</summary>
    static class ModelDuplicates
    {
        const long MinBytes = 1048576;

        public static List<DuplicateGroup> Find()
        {
            var files = new List<string>();
            void Add(string folder, string pattern)
            {
                try { if (Directory.Exists(folder)) files.AddRange(Directory.GetFiles(folder, pattern)); }
                catch (Exception) { }
            }
            Add(WhisperModel.Directory, "*.bin");
            Add(WhisperModel.PluginFolder, "*.bin");
            Add(ModelCatalog.CustomFolder, "*.bin");
            Add(ModelCatalog.LlamaFolder, "*.gguf");
            Add(ModelCatalog.Folder, "*.gguf");
            Add(WhisperModel.Directory, "*.gguf");

            var groups = new List<DuplicateGroup>();
            var bySize = files.Select(f => Path.GetFullPath(f)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(f => new FileInfo(f)).Where(i => i.Length >= MinBytes)
                .GroupBy(i => i.Extension.ToLowerInvariant() + i.Length).Where(g => g.Count() > 1);

            foreach (var sameSize in bySize)
            {
                foreach (var same in sameSize.GroupBy(i => Fingerprint(i.FullName)).Where(g => g.Key != null && g.Count() > 1))
                {
                    var ordered = same.Select(i => i.FullName).OrderByDescending(Score).ThenBy(f => f.Length).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                    groups.Add(new DuplicateGroup { Keep = ordered[0], Remove = ordered.Skip(1).ToList(), Bytes = same.First().Length * (ordered.Count - 1) });
                }
            }
            return groups;
        }

        /// <summary>Which copy to keep: the one a setting points at, then one in the Kakadoo folder with the standard name.</summary>
        static int Score(string file)
        {
            int score = 0;
            foreach (var chosen in new[] { Settings.WhisperModelPath, Settings.LlamaModelPath, Settings.VoskModelPath, Settings.CustomModelPath })
            {
                if (string.IsNullOrWhiteSpace(chosen)) continue;
                if (ModelSync.SamePath(chosen, file) || (Directory.Exists(chosen) && ModelSync.SamePath(chosen, Path.GetDirectoryName(file)))) score += 100;
            }
            if (file.StartsWith(ModelCatalog.Folder, StringComparison.OrdinalIgnoreCase)) score += 10;
            if (ModelCatalog.All.Any(e => e.Kind != ModelKind.Parakeet && e.FilePath != null && ModelSync.SamePath(e.FilePath, file))) score += 5;
            return score;
        }

        /// <summary>Size plus a hash of the first and last megabyte: cheap, and enough to tell copies of a model apart from other files.</summary>
        static string Fingerprint(string file)
        {
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    var buffer = new byte[(int)Math.Min(MinBytes, stream.Length)];
                    ReadFully(stream, buffer);
                    sha.TransformBlock(buffer, 0, buffer.Length, null, 0);
                    stream.Seek(-buffer.Length, SeekOrigin.End);
                    ReadFully(stream, buffer);
                    sha.TransformFinalBlock(buffer, 0, buffer.Length);
                    return stream.Length + ":" + Convert.ToBase64String(sha.Hash);
                }
            }
            catch (Exception) { return null; }
        }

        static void ReadFully(Stream s, byte[] buffer)
        {
            int got = 0;
            while (got < buffer.Length) { int n = s.Read(buffer, got, buffer.Length - got); if (n <= 0) break; got += n; }
        }
    }
}
