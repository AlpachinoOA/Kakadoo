using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Kakadoo
{
    /// <summary>Tells whether the speech engine chosen in Kakadoo Settings is ready to listen, so a component can show an orange warning.</summary>
    static class EngineCheck
    {
        /// <summary>Why the chosen engine cannot listen yet, or null when it can.</summary>
        public static string Problem()
        {
            string why = Find();
            return why == null ? null : "Speech engine not ready: " + why;
        }

        static string Find()
        {
            try
            {
                switch (Settings.Engine)
                {
                    case "parakeet":
                        try { ParakeetModel.Resolve(); return null; }
                        catch (SpeechSetupException e) { return e.Message; }
                    case "vosk":
                        return VoskListener.FindModelRoot(SpeechListeners.DefaultVoskModelPath()) == null
                            ? "Vosk is chosen but no Vosk model is installed. Open Kakadoo Settings > Models, download one and press Use." : null;
                    case "custom":
                        return string.IsNullOrWhiteSpace(Settings.CustomModelPath) || !File.Exists(Settings.CustomModelPath)
                            ? "the custom model is not set or its file is missing. Choose one in Kakadoo Settings > Models." : null;
                    case "system":
                        return SpeechListeners.IsWindows ? WindowsProblem() : "Windows speech recognition only exists on Windows. Choose another engine in Kakadoo Settings > Models.";
                    default:
                        string path = Settings.WhisperModelPath;
                        return !string.IsNullOrWhiteSpace(path) && !File.Exists(path) && !Directory.Exists(path)
                            ? "the Whisper model file is missing: " + path + ". Choose another in Kakadoo Settings > Models." : null;
                }
            }
            catch (Exception) { return null; }
        }

        // Kept separate so System.Speech is never loaded on Mac.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static string WindowsProblem() => SystemSpeechListener.LanguageProblem();
    }
}
