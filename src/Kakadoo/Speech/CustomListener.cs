using System.Threading;
using System.Threading.Tasks;

namespace Kakadoo
{
    /// <summary>Listens with a Whisper-compatible model of your own: the one chosen under Custom models in Kakadoo Settings, or a path given to the component.</summary>
    public class CustomListener : ISpeechListener, IHintedListener, IModelPathListener
    {
        readonly WhisperListener inner = new WhisperListener();

        public string Hint { get => inner.Hint; set => inner.Hint = value; }

        public string ModelPath { get; set; }

        public Task<string> ListenOnceAsync(CancellationToken token)
        {
            string path = string.IsNullOrWhiteSpace(ModelPath) ? Settings.CustomModelPath : ModelPath;
            if (string.IsNullOrWhiteSpace(path))
                throw new SpeechSetupException("No custom model is chosen. Open Kakadoo Settings > Models > Custom models, put a Whisper-compatible .bin file in the custom folder and press Use.");
            if (!System.IO.File.Exists(path))
                throw new SpeechSetupException("The custom model file is missing: " + path + ". Choose another in Kakadoo Settings > Models.");
            inner.ModelPath = path;
            return inner.ListenOnceAsync(token);
        }
    }
}
