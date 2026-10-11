using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Kakadoo_Final
{
    /// <summary>Finds a number in spoken text, as digits or English words ("twenty five point five").</summary>
    static class NumberWords
    {
        static readonly Dictionary<string, int> Units = new Dictionary<string, int>
        {
            ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6,
            ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12,
            ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17,
            ["eighteen"] = 18, ["nineteen"] = 19
        };

        static readonly Dictionary<string, int> Tens = new Dictionary<string, int>
        {
            ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
            ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90
        };

        static readonly Regex Digits = new Regex(@"^[-+]?\d*\.\d+$|^[-+]?\d+$", RegexOptions.Compiled);

        public static bool TryExtract(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var words = text.ToLowerInvariant().Split(new[] { ' ', '-', ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                if (Digits.IsMatch(words[i]))
                    return double.TryParse(words[i], NumberStyles.Float, CultureInfo.InvariantCulture, out value);

                if (words[i] == "minus" || words[i] == "negative")
                {
                    if (TryParseRun(words, i + 1, out value)) { value = -value; return true; }
                }
                else if (TryParseRun(words, i, out value))
                {
                    return true;
                }
            }
            return false;
        }

        // Parses a run of number words starting at 'start': integer part, then optional "point" digits.
        static bool TryParseRun(string[] words, int start, out double value)
        {
            value = 0;
            int total = 0, current = 0, i = start;
            bool any = false;

            for (; i < words.Length; i++)
            {
                string w = words[i];
                if (Units.TryGetValue(w, out int u)) { current += u; any = true; }
                else if (Tens.TryGetValue(w, out int t)) { current += t; any = true; }
                else if (w == "hundred" && any) { current = (current == 0 ? 1 : current) * 100; }
                else if (w == "thousand" && any) { total += (current == 0 ? 1 : current) * 1000; current = 0; }
                else if (w == "and" && any && i + 1 < words.Length && (Units.ContainsKey(words[i + 1]) || Tens.ContainsKey(words[i + 1]))) { }
                else break;
            }
            if (!any) return false;

            value = total + current;

            if (i < words.Length && words[i] == "point")
            {
                double scale = 0.1;
                for (i++; i < words.Length && Units.TryGetValue(words[i], out int d) && d < 10; i++, scale /= 10)
                    value += d * scale;
            }
            return true;
        }
    }
}
