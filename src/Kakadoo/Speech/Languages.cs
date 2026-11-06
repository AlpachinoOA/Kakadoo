using System.Collections.Generic;
using System.Linq;

namespace Kakadoo
{
    /// <summary>The speech languages Kakadoo offers. The code is the one Whisper uses; "en" is the default.</summary>
    static class Languages
    {
        public static readonly string[][] All =
        {
            new[] { "en", "English" }, new[] { "auto", "Automatic (detect the language)" },
            new[] { "de", "German" }, new[] { "fr", "French" }, new[] { "es", "Spanish" }, new[] { "it", "Italian" },
            new[] { "pt", "Portuguese" }, new[] { "nl", "Dutch" }, new[] { "tr", "Turkish" }, new[] { "pl", "Polish" },
            new[] { "ru", "Russian" }, new[] { "uk", "Ukrainian" }, new[] { "sv", "Swedish" }, new[] { "da", "Danish" },
            new[] { "fi", "Finnish" }, new[] { "cs", "Czech" }, new[] { "sk", "Slovak" }, new[] { "sl", "Slovenian" },
            new[] { "hr", "Croatian" }, new[] { "bg", "Bulgarian" }, new[] { "ro", "Romanian" }, new[] { "hu", "Hungarian" },
            new[] { "el", "Greek" }, new[] { "lt", "Lithuanian" }, new[] { "lv", "Latvian" }, new[] { "et", "Estonian" },
            new[] { "mt", "Maltese" }, new[] { "ar", "Arabic" }, new[] { "hi", "Hindi" }, new[] { "zh", "Chinese" },
            new[] { "ja", "Japanese" }, new[] { "ko", "Korean" }
        };

        // The 25 European languages Parakeet v3 was trained on.
        static readonly HashSet<string> ParakeetV3 = new HashSet<string>
        {
            "en", "bg", "hr", "cs", "da", "nl", "et", "fi", "fr", "de", "el", "hu", "it", "lv", "lt", "mt", "pl", "pt", "ro", "sk", "sl", "es", "sv", "ru", "uk"
        };

        public static bool IsKnown(string code) => All.Any(l => l[0] == code);

        public static string Name(string code) => All.FirstOrDefault(l => l[0] == code)?[1] ?? code;

        /// <summary>True when the chosen language is English, so the English-only models (.en, Parakeet v2) can be used.</summary>
        public static bool IsEnglish => Settings.Language == "en";

        public static bool ParakeetV3Supports(string code) => code == "auto" || ParakeetV3.Contains(code);
    }
}
