using System.Threading;
using System.Threading.Tasks;

namespace Kakadoo
{
    /// <summary>
    /// The speech engine chosen in Kakadoo Settings (Whisper, Parakeet, Vosk, Windows speech or Custom), looked up on every listen so a change
    /// there applies to every Kakadoo component at once. An explicit model path (the component's input) only applies to Whisper.
    /// </summary>
    public class EngineListener : ISpeechListener, IHintedListener, IModelPathListener
    {
        readonly WhisperListener whisper = new WhisperListener();
        readonly ParakeetListener parakeet = new ParakeetListener();
        readonly CustomListener custom = new CustomListener();

        ISpeechListener windows;

        /// <summary>Vosk only: wait for a whole phrase instead of recording a fixed window (Always-On).</summary>
        public bool Phrases { get; set; }

        public string Hint { get; set; }

        public string ModelPath { get; set; }

        public Task<string> ListenOnceAsync(CancellationToken token)
        {
            switch (Settings.Engine)
            {
                case "parakeet":
                    parakeet.ModelPath = ModelPath;
                    return parakeet.ListenOnceAsync(token);
                case "vosk":
                    return new VoskListener(string.IsNullOrWhiteSpace(ModelPath) ? SpeechListeners.DefaultVoskModelPath() : ModelPath, 5, Phrases).ListenOnceAsync(token);
                case "system":
                    return (windows ?? (windows = SpeechListeners.CreateWindows(true))).ListenOnceAsync(token);
                case "custom":
                    custom.Hint = Hint;
                    custom.ModelPath = ModelPath;
                    return custom.ListenOnceAsync(token);
                default:
                    whisper.Hint = Hint;
                    whisper.ModelPath = ModelPath;
                    return whisper.ListenOnceAsync(token);
            }
        }
    }
}
