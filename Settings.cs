using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Kakadoo_Final
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

        /// <summary>Counts changes; engines that failed compare it to know whether to try again.</summary>
        public static int Version { get; private set; }

        /// <summary>A Whisper model file, or a folder containing ggml-*.bin files. Empty means automatic.</summary>
        public static string WhisperModelPath
        {
            get => Get("whisperModelPath");
            set => Set("whisperModelPath", value);
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
            lock (Gate)
            {
                Load();
                if (string.IsNullOrWhiteSpace(value)) data.Remove(key);
                else data[key] = value.Trim();

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
