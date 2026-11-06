using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Kakadoo
{
    /// <summary>
    /// Words the speech model often gets wrong, with what they should be, kept in ~/Kakadoo/custom-words.txt (one "heard -> correct" per line).
    /// Applied to everything that is recognised, and the correct words are also given to Whisper as hints so it spells them right in the first place.
    /// </summary>
    static class CustomWords
    {
        static readonly object Gate = new object();
        static DateTime loadedAt;
        static List<KeyValuePair<Regex, string>> rules = new List<KeyValuePair<Regex, string>>();
        static List<string> targets = new List<string>();

        public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Kakadoo", "custom-words.txt");

        public static string ReadText()
        {
            try { return File.Exists(FilePath) ? File.ReadAllText(FilePath) : ""; }
            catch (Exception) { return ""; }
        }

        public static void WriteText(string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                if (string.IsNullOrWhiteSpace(text)) { if (File.Exists(FilePath)) File.Delete(FilePath); }
                else File.WriteAllText(FilePath, text);
            }
            catch (Exception) { /* the words still apply for this session through the next Reload */ }
            Reload(text);
        }

        /// <summary>Parses "heard -> correct" lines; blank lines and lines starting with # are ignored. Also accepts "=>" and "=".</summary>
        public static List<KeyValuePair<string, string>> Parse(string text)
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (var raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int i = line.IndexOf("->", StringComparison.Ordinal), skip = 2;
                if (i < 0) { i = line.IndexOf("=>", StringComparison.Ordinal); }
                if (i < 0) { i = line.IndexOf('='); skip = 1; }
                if (i <= 0) continue;
                string heard = line.Substring(0, i).Trim().Trim('"', '“', '”');
                string correct = line.Substring(i + skip).Trim().Trim('"', '“', '”');
                if (heard.Length > 0 && correct.Length > 0) list.Add(new KeyValuePair<string, string>(heard, correct));
            }
            return list;
        }

        static void Reload(string text)
        {
            var parsed = Parse(text);
            var built = parsed
                .OrderByDescending(p => p.Key.Length) // longer phrases first, so "astro js" wins over "astro"
                .Select(p => new KeyValuePair<Regex, string>(
                    new Regex(@"(?<!\w)" + Regex.Escape(p.Key).Replace(@"\ ", @"\s+") + @"(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), p.Value))
                .ToList();
            lock (Gate)
            {
                rules = built;
                targets = parsed.Select(p => p.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                loadedAt = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
            }
        }

        static void EnsureFresh()
        {
            DateTime stamp;
            try { stamp = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue; }
            catch (Exception) { return; }
            lock (Gate) if (stamp == loadedAt && loadedAt != default) return;
            Reload(ReadText());
        }

        /// <summary>Replaces the misheard words in a recognised phrase.</summary>
        public static string Apply(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            EnsureFresh();
            List<KeyValuePair<Regex, string>> current;
            lock (Gate) current = rules;
            foreach (var rule in current) text = rule.Key.Replace(text, rule.Value.Replace("$", "$$"));
            return text;
        }

        /// <summary>The correct spellings, to hint to Whisper.</summary>
        public static IEnumerable<string> Targets()
        {
            EnsureFresh();
            lock (Gate) return targets.ToList();
        }
    }
}
