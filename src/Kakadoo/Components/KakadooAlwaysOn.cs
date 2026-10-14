using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using GH_IO.Serialization;
using Grasshopper.Kernel;

namespace Kakadoo
{
    /// <summary>
    /// Kakadoo Always-On: keeps listening while Run is true. When it hears the catchphrase (like "hey kakadoo"),
    /// the phrase that follows is output as text. The catchphrase is set in the right-click menu.
    /// </summary>
    public class KakadooAlwaysOn : GH_Component, IGH_VariableParameterComponent
    {
        const string DefaultCatchphrase = "hey kakadoo";
        const int MaxInfoLines = 1; // Info shows only the latest message, at index 0

        readonly ISpeechListener listener = SpeechListeners.Create();
        readonly List<string> info = new List<string>(); // newest first
        readonly object gate = new object();
        CancellationTokenSource cts;
        string recText = string.Empty;
        volatile string modelPath;

        // Trigger words: heard anywhere in a sentence -> the Hit output is true for the trigger seconds.
        // Only a volatile flag and a timer are touched from the listening thread; the canvas is refreshed through the UI queue.
        volatile bool triggered;
        long triggerUntil; // Environment.TickCount64 when the output goes back to false
        System.Threading.Timer triggerTimer;
        volatile string inputTriggers; // from the Trigger input; null when nothing is wired
        string triggerKey;
        System.Text.RegularExpressions.Regex triggerRegex;

        /// <summary>What you say to wake the component. Blank means every phrase is output.</summary>
        public string Catchphrase { get; set; } = DefaultCatchphrase;

        public KakadooAlwaysOn()
          : base("Kakadoo Always-On", "KkAO",
              "Always listening. Say the catchphrase (default \"hey kakadoo\") and then your command, and it is output as text",
              "Kakadoo", " Voice")
        {
        }

        public override void CreateAttributes() => m_attributes = new ProgressAttributes(this);

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBooleanParameter("Run", "Run", "Activate this to keep listening", GH_ParamAccess.item, false);
            pManager.AddTextParameter(KakadooComponent.WhisperInputName, KakadooComponent.WhisperInputName, "Optional: a Whisper model file (ggml-*.bin) or a folder containing one, for PCs without internet. Blank uses the automatic model.", GH_ParamAccess.item);
            pManager[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Status", "Status", "The latest thing the component heard or did", GH_ParamAccess.list);
            pManager.AddTextParameter("Text", "Text", "The last phrase said after the catchphrase", GH_ParamAccess.item);
        }

        // The trigger input and output only exist while "Trigger" is ticked in the right-click menu.
        const int TriggerInput = 2, HitOutput = 2;

        /// <summary>Whether the Trigger input and Hit output are shown.</summary>
        public bool TriggerOn { get; private set; }

        /// <summary>How long Hit stays true after a trigger word is heard.</summary>
        public double TriggerSeconds { get; set; } = 5;

        void SetTrigger(bool on)
        {
            if (on == TriggerOn) return;
            if (on) SetTether(false); // trigger words and the tether wire are two different ways of working
            TriggerOn = on;
            if (on)
            {
                Params.RegisterInputParam(new Grasshopper.Kernel.Parameters.Param_String
                {
                    Name = "Trigger",
                    NickName = "Trigger",
                    Description = "Words or phrases to listen for (a list). Hearing any of them in a sentence makes Hit true for the trigger seconds.",
                    Access = GH_ParamAccess.list,
                    Optional = true
                });
                Params.RegisterOutputParam(new Grasshopper.Kernel.Parameters.Param_Boolean
                {
                    Name = "Hit",
                    NickName = "Hit",
                    Description = "True for a few seconds after one of the trigger words is heard"
                });
            }
            else
            {
                if (Params.Input.Count > TriggerInput) Params.UnregisterInputParameter(Params.Input[TriggerInput]);
                if (Params.Output.Count > HitOutput) Params.UnregisterOutputParameter(Params.Output[HitOutput]);
                lock (gate) { triggerTimer?.Dispose(); triggerTimer = null; }
                triggered = false;
                inputTriggers = null;
            }
        }

        /// <summary>Whether the Tether output is shown. While it is, the Trigger input and Hit output are not.</summary>
        public bool TetherOn { get; private set; }

        /// <summary>Listen with Vosk instead of Whisper. The model input at position 1 is swapped to match.</summary>
        public bool UseVosk { get; private set; }

        void SetVosk(bool on)
        {
            if (on == UseVosk) return;
            UseVosk = on;
            var old = Params.Input[1];
            var sources = old.Sources.ToList();
            string name = on ? KakadooComponent.VoskInputName : KakadooComponent.WhisperInputName;
            var swapped = new Grasshopper.Kernel.Parameters.Param_String
            {
                Name = name,
                NickName = name,
                Description = on
                    ? "Optional: the folder of a Vosk model. Blank uses the Vosk model installed in the Kakadoo folder."
                    : "Optional: a Whisper model file (ggml-*.bin) or a folder containing one, for PCs without internet. Blank uses the automatic model.",
                Access = GH_ParamAccess.item,
                Optional = true
            };
            Params.UnregisterInputParameter(old);
            Params.RegisterInputParam(swapped, 1);
            foreach (var source in sources) swapped.AddSource(source);
        }

        void SetTether(bool on)
        {
            if (on == TetherOn) return;
            if (on) SetTrigger(false);
            TetherOn = on;
            if (on) Params.RegisterOutputParam(new TetherParam());
            else
            {
                var tether = Params.Output.OfType<TetherParam>().FirstOrDefault();
                if (tether != null) Params.UnregisterOutputParameter(tether);
            }
        }

        /// <summary>Hands the sentence to every Kakadoo component wired to the Tether output. Runs on the UI thread; the listening thread never waits for it.</summary>
        void Dispatch(string text)
        {
            if (!TetherOn) return;
            Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                var tether = Params.Output.OfType<TetherParam>().FirstOrDefault();
                if (tether == null) return;
                int sent = 0;
                foreach (var recipient in tether.Recipients)
                {
                    var target = recipient.Attributes?.GetTopLevel?.DocObject as KakadooComponent;
                    if (target != null && target.Tether(text)) sent++;
                }
                Info(sent == 0
                    ? "Nothing is wired to Tether yet. Connect it to a Kakadoo component's Tether input."
                    : "Sent to " + sent + (sent == 1 ? " component: " : " components: ") + text);
            }));
        }

        bool IGH_VariableParameterComponent.CanInsertParameter(GH_ParameterSide side, int index) => false;
        bool IGH_VariableParameterComponent.CanRemoveParameter(GH_ParameterSide side, int index) => false;
        IGH_Param IGH_VariableParameterComponent.CreateParameter(GH_ParameterSide side, int index) => null;
        bool IGH_VariableParameterComponent.DestroyParameter(GH_ParameterSide side, int index) => false;
        void IGH_VariableParameterComponent.VariableParameterMaintenance() { }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            bool run = false;
            string modelPath = null;
            DA.GetData(0, ref run);
            DA.GetData(1, ref modelPath);
            this.modelPath = modelPath;
            inputTriggers = null;
            if (TriggerOn)
            {
                var wired = new List<string>();
                if (Params.Input[TriggerInput].SourceCount > 0 && DA.GetDataList(TriggerInput, wired)) inputTriggers = string.Join(",", wired);
            }

            if (run && cts == null)
            {
                cts = new CancellationTokenSource();
                Message = "Listening";
                var token = cts.Token;
                Info(string.IsNullOrWhiteSpace(Catchphrase)
                    ? "Listening. Everything you say is output."
                    : "Listening. Say \"" + Catchphrase.Trim() + "\" and then your command.", refresh: false);
                Task.Run(() => ListenLoopAsync(token));
            }
            else if (!run && cts != null)
            {
                Stop();
            }

            if (!run)
            {
                Message = "Not Listening";
                lock (gate) { info.Clear(); info.Add("Press the button to start listening."); }
            }

            lock (gate)
            {
                DA.SetDataList(0, info.ToList());
                DA.SetData(1, recText);
                if (TriggerOn) DA.SetData(HitOutput, triggered);
            }
        }

        async Task ListenLoopAsync(CancellationToken token)
        {
            bool awaitingCommand = false;
            DateTime awaitUntil = DateTime.MinValue;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Helps the recogniser spell the catchphrase the way we match it.
                    ISpeechListener active = listener;
                    if (UseVosk)
                    {
                        string path = modelPath;
                        active = new VoskListener(string.IsNullOrWhiteSpace(path) ? SpeechListeners.DefaultVoskModelPath() : path, phrases: true);
                    }
                    else
                    {
                        SpeechListeners.SetHint(listener, string.IsNullOrWhiteSpace(Catchphrase) ? new string[0] : new[] { Catchphrase });
                        if (listener is IModelPathListener withModel) withModel.ModelPath = modelPath;
                    }

                    string heard = await SpeechListeners.ListenAsync(active, m => Info(m), token);
                    if (token.IsCancellationRequested) break;
                    if (string.IsNullOrWhiteSpace(heard)) continue;

                    CheckTriggers(heard);

                    // After "hey kakadoo" on its own, the next phrase is the command.
                    if (awaitingCommand && DateTime.UtcNow < awaitUntil)
                    {
                        awaitingCommand = false;
                        Publish(heard);
                        continue;
                    }
                    awaitingCommand = false;

                    if (!WakePhrase.Match(heard, Catchphrase, out string rest))
                    {
                        Info("Heard, but not the catchphrase: " + heard);
                    }
                    else if (!string.IsNullOrWhiteSpace(rest))
                    {
                        Publish(rest);
                    }
                    else
                    {
                        awaitingCommand = true;
                        awaitUntil = DateTime.UtcNow.AddSeconds(10);
                        Info("Catchphrase heard. Say your command...");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    // Stop on errors (no microphone, no speech engine) instead of retrying forever.
                    Info("Stopped: " + ErrorHelp.Report(this, e), refresh: false);
                    Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
                    {
                        Stop();
                        Message = "Error";
                        ExpireSolution(true);
                    }));
                    break;
                }
            }
        }

        void CheckTriggers(string heard)
        {
            if (!TriggerOn) return;
            var regex = TriggerRegex();
            if (regex == null || !regex.IsMatch(heard)) return;

            long hold = (long)(Math.Max(0.1, TriggerSeconds) * 1000);
            Interlocked.Exchange(ref triggerUntil, Environment.TickCount64 + hold);
            bool wasOn = triggered;
            triggered = true;
            // One timer; hearing a word again just moves the deadline.
            lock (gate)
            {
                if (triggerTimer == null) triggerTimer = new System.Threading.Timer(_ => TriggerTick(), null, hold, Timeout.Infinite);
                else triggerTimer.Change(hold, Timeout.Infinite);
            }
            if (!wasOn) Info("Trigger heard.");
        }

        void TriggerTick()
        {
            long left = Interlocked.Read(ref triggerUntil) - Environment.TickCount64;
            if (left > 0)
            {
                lock (gate) triggerTimer?.Change(left, Timeout.Infinite);
                return;
            }
            triggered = false;
            RequestRefresh();
        }

        /// <summary>Whole-word, case-insensitive match for the comma-separated trigger words; rebuilt only when the words change.</summary>
        System.Text.RegularExpressions.Regex TriggerRegex()
        {
            string words = inputTriggers ?? "";
            if (words == triggerKey) return triggerRegex;
            triggerKey = words;
            var parts = words.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim()).Where(w => w.Length > 0)
                .Select(w => System.Text.RegularExpressions.Regex.Escape(w).Replace(@"\ ", @"\s+"));
            triggerRegex = parts.Any()
                ? new System.Text.RegularExpressions.Regex(@"(?<!\w)(?:" + string.Join("|", parts) + @")(?!\w)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                : null;
            return triggerRegex;
        }

        void Publish(string command)
        {
            lock (gate) recText = command;
            Info("Command: " + command);
            Dispatch(command);
        }

        readonly RefreshGate refreshGate = new RefreshGate();

        void RequestRefresh() => UiRefresh.Request(this, refreshGate);

        void Info(string message, bool refresh = true)
        {
            lock (gate)
            {
                info.Insert(0, message);
                while (info.Count > MaxInfoLines) info.RemoveAt(info.Count - 1);
            }
            if (refresh)
                RequestRefresh();
        }

        void Stop()
        {
            cts?.Cancel();
            lock (gate) { triggerTimer?.Dispose(); triggerTimer = null; }
            triggered = false;
            cts = null;
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            Stop();
            base.RemovedFromDocument(document);
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "Catchphrase (blank = output everything you say):", null, false);
            Menu_AppendTextItem(menu, Catchphrase ?? "", null, (box, text) => Catchphrase = text, true);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "Speech engine: Whisper", (s, e) => { SetVosk(false); Params.OnParametersChanged(); ExpireSolution(true); }, true, !UseVosk);
            Menu_AppendItem(menu, "Speech engine: Vosk", (s, e) => { SetVosk(true); Params.OnParametersChanged(); ExpireSolution(true); }, true, UseVosk);
            Menu_AppendSeparator(menu);
            if (!TetherOn)
            Menu_AppendItem(menu, "Trigger", (s, e) =>
            {
                SetTrigger(!TriggerOn);
                Params.OnParametersChanged();
                ExpireSolution(true);
            }, true, TriggerOn);
            Menu_AppendItem(menu, "Tether (start other Kakadoo components by voice)", (s, e) =>
            {
                SetTether(!TetherOn);
                Params.OnParametersChanged();
                ExpireSolution(true);
            }, true, TetherOn);
            if (TriggerOn)
            {
                Menu_AppendItem(menu, "Trigger seconds (how long Hit stays true):", null, false);
                Menu_AppendTextItem(menu, TriggerSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), null, (box, text) =>
                {
                    if (double.TryParse((text ?? "").Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds)
                        && seconds >= 0.1 && seconds <= 600)
                        TriggerSeconds = seconds;
                }, true);
            }
            ProgressAttributes.AppendMenu(menu, this);
        }

        public override bool Write(GH_IWriter writer)
        {
            writer.SetString("Catchphrase", Catchphrase ?? "");
            writer.SetBoolean("TriggerOn", TriggerOn);
            writer.SetBoolean("TetherOn", TetherOn);
            writer.SetBoolean("UseVosk", UseVosk);
            writer.SetDouble("TriggerSeconds", TriggerSeconds);
            ProgressAttributes.Write(this, writer);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            string phrase = null;
            if (reader.TryGetString("Catchphrase", ref phrase)) Catchphrase = phrase;
            // The trigger parameters are created before the base class reads the saved parameters, so the counts match.
            bool on = false;
            double seconds = 5;
            if (reader.TryGetDouble("TriggerSeconds", ref seconds)) TriggerSeconds = Math.Max(0.1, Math.Min(600, seconds));
            if (reader.TryGetBoolean("TriggerOn", ref on) && on) SetTrigger(true);
            bool vosk = false;
            if (reader.TryGetBoolean("UseVosk", ref vosk) && vosk) SetVosk(true);
            bool tether = false;
            if (reader.TryGetBoolean("TetherOn", ref tether) && tether) SetTether(true);
            ProgressAttributes.Read(this, reader);
            return base.Read(reader);
        }

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon => Icons.Load("KakadooAlwaysOn");

        public override Guid ComponentGuid => new Guid("77110198-f3ad-4f6b-ab58-aa3a6a61a742");
    }
}
