using System;
using System.IO;

namespace Kakadoo
{
    /// <summary>
    /// Asks whether a model can really be used before it is accepted. Whisper, custom and Parakeet models are loaded by their engine (the engine
    /// keeps them, so the first command is not slower for it). Vosk and Llama are checked by their files only, because loading a whole language model
    /// just to test it would take minutes and gigabytes.
    /// </summary>
    static class ModelCheck
    {
        /// <summary>Returns null when the model can be used, otherwise a sentence saying why not. Can take a few seconds: call it off the UI thread.</summary>
        public static string Verify(ModelKind kind, string path)
        {
            path = (path ?? "").Trim().Trim('"');
            if (path.Length == 0) return null;
            try
            {
                switch (kind)
                {
                    case ModelKind.Whisper:
                    case ModelKind.Custom:
                        string file = WhisperModel.Locate(out _, path);
                        if (file == null) return "No Whisper model file was found at " + path + ".";
                        if (new FileInfo(file).Length < 10 * 1048576) return Path.GetFileName(file) + " is too small to be a Whisper model.";
                        WhisperListener.Warmup(file);
                        WhisperListener.SelfTest(file);
                        return null;

                    case ModelKind.Parakeet:
                        string folder = ParakeetModel.Resolve(path);
                        ParakeetListener.Warmup(folder);
                        ParakeetListener.Decode(folder, new float[16000]); // a second of silence: loads fine but cannot compute, if it is damaged
                        return null;

                    case ModelKind.Vosk:
                        if (!Directory.Exists(path)) return "The folder does not exist: " + path;
                        return VoskListener.FindModelRoot(path) == null ? "That is not a Vosk model folder (it needs the folders am and conf)." : null;

                    default:
                        if (!File.Exists(path)) return "The file does not exist: " + path;
                        ModelFiles.CheckIsGguf(path);
                        if (new FileInfo(path).Length < 50 * 1048576) return "That file is too small to be a language model.";
                        return Gguf.Problem(path);
                }
            }
            catch (Exception e)
            {
                return ErrorHelp.Explain(e);
            }
        }
    }

    /// <summary>Reads the start of a GGUF file (version and the model family) without loading it, to tell language models from other GGUF files.</summary>
    static class Gguf
    {
        // Families llama.cpp can load that are not language models: they cannot answer a command.
        static readonly string[] NotChat = { "bert", "nomic-bert", "nomic-bert-moe", "jina-bert-v2", "jina-bert-v3", "modern-bert", "neo-bert", "clip", "whisper", "wavtokenizer-dec", "t5encoder", "gemma-embedding" };

        /// <summary>Null when the file looks like a language model that can run; otherwise the reason it can't.</summary>
        public static string Problem(string file)
        {
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var r = new BinaryReader(stream, System.Text.Encoding.UTF8))
                {
                    r.ReadBytes(4);
                    uint version = r.ReadUInt32();
                    if (version < 2 || version > 3) return "That GGUF file has version " + version + ", which this version of llama.cpp cannot read.";
                    r.ReadUInt64(); // tensor count
                    ulong kvCount = r.ReadUInt64();
                    for (ulong i = 0; i < Math.Min(kvCount, 4000UL); i++)
                    {
                        string key = ReadString(r);
                        uint type = r.ReadUInt32();
                        if (key == "general.architecture" && type == 8)
                        {
                            string arch = ReadString(r);
                            return Array.IndexOf(NotChat, arch) >= 0
                                ? "That is a '" + arch + "' model, not a language model that can follow commands. Use a chat model such as Llama 2 Chat."
                                : null;
                        }
                        Skip(r, type);
                    }
                }
                return "That GGUF file does not say which kind of model it is.";
            }
            catch (EndOfStreamException) { return "That GGUF file is damaged or cut off."; }
            catch (InvalidDataException e) { return e.Message; }
        }

        static string ReadString(BinaryReader r)
        {
            ulong length = r.ReadUInt64();
            if (length > 1 << 20) throw new InvalidDataException("That GGUF file is damaged.");
            return System.Text.Encoding.UTF8.GetString(r.ReadBytes((int)length));
        }

        static void Skip(BinaryReader r, uint type)
        {
            switch (type)
            {
                case 0: case 1: case 7: r.ReadBytes(1); break;
                case 2: case 3: r.ReadBytes(2); break;
                case 4: case 5: case 6: r.ReadBytes(4); break;
                case 10: case 11: case 12: r.ReadBytes(8); break;
                case 8: ReadString(r); break;
                case 9:
                    uint inner = r.ReadUInt32();
                    ulong count = r.ReadUInt64();
                    if (count > 10000000UL) throw new InvalidDataException("That GGUF file is damaged.");
                    for (ulong i = 0; i < count; i++) Skip(r, inner);
                    break;
                default: throw new InvalidDataException("That GGUF file uses a value type this check does not know.");
            }
        }
    }
}
