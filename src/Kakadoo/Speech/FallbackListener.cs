using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Kakadoo
{
    /// <summary>
    /// Tries speech engines in order. If the best one can't be set up (no model and no internet, missing libraries),
    /// the next one is used and the user is told. Engines are created only when first needed, so a missing
    /// engine never stops a component from loading.
    /// </summary>
    public class FallbackListener : ISpeechListener, IHintedListener, IModelPathListener
    {
        class Engine
        {
            public string Name;
            public Func<ISpeechListener> Create;
            public ISpeechListener Instance;
            public string Failure;
            public DateTime RetryAfter;
            public int FailedAtSettingsVersion;
            public string FailedModelPath;
        }

        readonly List<Engine> engines = new List<Engine>();
        readonly object gate = new object();
        string note;

        public string Hint { get; set; }
        public string ModelPath { get; set; }

        public FallbackListener Add(string name, Func<ISpeechListener> create)
        {
            engines.Add(new Engine { Name = name, Create = create });
            return this;
        }

        /// <summary>A message about a switch to another engine, once; null if there is nothing new to report.</summary>
        public string TakeNote()
        {
            lock (gate)
            {
                string n = note;
                note = null;
                return n;
            }
        }

        public async Task<string> ListenOnceAsync(CancellationToken token)
        {
            var failures = new List<string>();

            for (int i = 0; i < engines.Count; i++)
            {
                var engine = engines[i];

                // An engine that failed is skipped for a while, unless a setting changed that might fix it.
                if (engine.Failure != null && DateTime.UtcNow < engine.RetryAfter && engine.FailedAtSettingsVersion == Settings.Version && engine.FailedModelPath == ModelPath)
                {
                    failures.Add(engine.Name + ": " + engine.Failure);
                    continue;
                }

                try
                {
                    engine.Instance = engine.Instance ?? engine.Create();
                    if (engine.Instance is IHintedListener hinted) hinted.Hint = Hint;
                    if (engine.Instance is IModelPathListener modelPath) modelPath.ModelPath = ModelPath;

                    string result = await engine.Instance.ListenOnceAsync(token);
                    engine.Failure = null;
                    return result;
                }
                catch (Exception e) when (IsSetupFailure(e))
                {
                    engine.Failure = e.Message;
                    engine.RetryAfter = DateTime.UtcNow.AddSeconds(30);
                    engine.FailedAtSettingsVersion = Settings.Version;
                    engine.FailedModelPath = ModelPath;
                    failures.Add(engine.Name + ": " + e.Message);

                    if (i + 1 < engines.Count)
                        lock (gate) note = engine.Name + " is unavailable: " + e.Message + " Trying " + engines[i + 1].Name + " instead (less accurate).";
                }
            }

            throw new SpeechSetupException("Speech recognition is not available. " + string.Join(" ", failures));
        }

        /// <summary>Problems with the engine itself, as opposed to the microphone or the user's speech.</summary>
        static bool IsSetupFailure(Exception e) =>
            e is SpeechSetupException || e is DllNotFoundException || e is TypeLoadException || e is TypeInitializationException
            || e is FileNotFoundException || e is BadImageFormatException || e is PlatformNotSupportedException
            || e is EntryPointNotFoundException || e is DirectoryNotFoundException;
    }
}
