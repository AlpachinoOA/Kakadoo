using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Kakadoo_Final
{
    /// <summary>Finds a catchphrase like "hey kakadoo" in recognised speech, allowing for small spelling differences.</summary>
    static class WakePhrase
    {
        static readonly Regex Word = new Regex(@"[\p{L}\p{N}']+", RegexOptions.Compiled);

        /// <summary>
        /// True if <paramref name="text"/> starts with the catchphrase (or has it after a word or two, like "okay, hey kakadoo").
        /// <paramref name="rest"/> is whatever was said after it (empty if the catchphrase was said alone).
        /// A blank catchphrase matches everything.
        /// </summary>
        public static bool Match(string text, string phrase, out string rest)
        {
            rest = text ?? "";
            if (string.IsNullOrWhiteSpace(phrase)) return true;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var wake = Words(phrase).Select(w => w.Value).ToList();
            if (wake.Count == 0) return true;

            var tokens = Words(text);
            int bestEnd = -1, bestDistance = int.MaxValue;

            for (int start = 0; start <= Math.Min(2, tokens.Count - wake.Count); start++)
            {
                int total = 0;
                bool ok = true;
                for (int i = 0; i < wake.Count && ok; i++)
                {
                    string spoken = Normalize(tokens[start + i].Value), expected = Normalize(wake[i]);
                    int distance = Levenshtein(spoken, expected);
                    if (distance > Allowed(expected)) ok = false;
                    total += distance;
                }

                if (ok && total < bestDistance)
                {
                    bestDistance = total;
                    var last = tokens[start + wake.Count - 1];
                    bestEnd = last.Index + last.Length;
                }
            }

            if (bestEnd < 0) return false;
            rest = text.Substring(bestEnd).Trim(' ', ',', '.', '!', '?', ':', ';', '-');
            return true;
        }

        static List<Match> Words(string s) => Word.Matches(s).Cast<Match>().ToList();

        /// <summary>Lower case, no apostrophes, and "c" written as "k" so "cockatoo" and "kakadoo" look alike.</summary>
        static string Normalize(string word) => word.ToLowerInvariant().Replace("'", "").Replace('c', 'k').Replace("ph", "f");

        /// <summary>How many letters may differ: strict for short words, looser for long ones.</summary>
        static int Allowed(string word) => word.Length <= 3 ? 1 : word.Length <= 5 ? 2 : (int)(word.Length * 0.45);

        static int Levenshtein(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var swap = prev; prev = cur; cur = swap;
            }
            return prev[b.Length];
        }
    }
}
