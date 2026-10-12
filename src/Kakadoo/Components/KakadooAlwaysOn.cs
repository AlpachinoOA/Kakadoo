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
    public class KakadooAlwaysOn : GH_Component
    {
        const string DefaultCatchphrase = "hey kakadoo";
        const int MaxInfoLines = 8;

        readonly ISpeechListener listener = SpeechListeners.Create();
        readonly List<string> info = new List<string>(); // newest first
        readonly object gate = new object();
        CancellationTokenSource cts;
        string recText = string.Empty;
        volatile string modelPath;

        // Trigger words: heard anywhere in a sentence -> the "trigger" output is true for the hold time.
        // Only a volatile flag and a timer are touched from the listening thread; the canvas is refreshed through the UI queue.
        volatile bool triggered;
        long triggerUntil; // Environment.TickCount64 when the output goes back to false
        System.Threading.Timer triggerTimer;
        volatile string inputTriggers; // from the Triggers input; null when nothing is wired
        string triggerKey;
        System.Text.RegularExpressions.Regex triggerRegex;

        /// <summary>What you say to wake the component. Blank means every phrase is output.</summary>
        public string Catchphrase { get; set; } = DefaultCatchphrase;

        /// <summary>Words or phrases, separated by commas. Hearing any of them in a sentence sets the trigger output to true for a while.</summary>
        public string TriggerWords { get; set; } = "";

        public KakadooAlwaysOn()
          : base("Kakadoo Always-On", "KkAO",
              "Always listening. Say the catchphrase (default \"hey kakadoo\") and then your command, and it is output as text",
              "Kakadoo", "Voice")
        {
        }

        public override void CreateAttributes() => m_attributes = new ProgressAttributes(this);

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBooleanParameter("run", "run", "Activate this to keep listening", GH_ParamAccess.item, false);
            pManager.AddTextParameter("Model Path", "M", "Optional: a Whisper model file (ggml-*.bin) or a folder containing one, for PCs without internet. Blank uses the automatic model.", GH_ParamAccess.item);
            pManager[1].Optional = true;
            pManager.AddTextParameter("Triggers", "T", "Optional: trigger words (a list). Hearing any of them in a sentence makes the trigger output true for a few seconds. Overrides the right-click setting.", GH_ParamAccess.list);
            pManager[2].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("info", "info", "What the component heard and is doing, newest first", GH_ParamAccess.list);
            pManager.AddTextParameter("text", "text", "The last phrase said after the catchphrase", GH_ParamAccess.item);
            pManager.AddBooleanParameter("trigger", "trigger", "True for a few seconds after one of the trigger words is heard (set in the right-click menu)", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            bool run = false;
            string modelPath = null;
            DA.GetData(0, ref run);
            DA.GetData(1, ref modelPath);
            this.modelPath = modelPath;
            var wired = new List<string>();
            inputTriggers = Params.Input[2].SourceCount > 0 && DA.GetDataList(2, wired) ? string.Join(",", wired) : null;

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
                DA.SetData(2, triggered);
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
                    SpeechListeners.SetHint(listener, string.IsNullOrWhiteSpace(Catchphrase) ? new string[0] : new[] { Catchphrase });
                    if (listener is IModelPathListener withModel) withModel.ModelPath = modelPath;

                    string heard = await SpeechListeners.ListenAsync(listener, m => Info(m), token);
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
                    Info("Stopped: " + e.Message, refresh: false);
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
            var regex = TriggerRegex();
            if (regex == null || !regex.IsMatch(heard)) return;

            long hold = (long)(Settings.TriggerHold * 1000);
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
            string words = inputTriggers ?? TriggerWords ?? "";
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
        }

        int refreshPending;

        /// <summary>
        /// Asks for a recompute from any thread. Repeated requests are merged, and the update is scheduled with the document, so it waits
        /// for a running solution instead of interrupting it (important in big definitions).
        /// </summary>
        void RequestRefresh()
        {
            if (Interlocked.Exchange(ref refreshPending, 1) == 1) return;
            Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                Interlocked.Exchange(ref refreshPending, 0);
                OnPingDocument()?.ScheduleSolution(5, d => ExpireSolution(false));
            }));
        }

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
            Menu_AppendItem(menu, "Trigger words (comma separated; true for a few seconds when heard):", null, false);
            Menu_AppendTextItem(menu, TriggerWords ?? "", null, (box, text) => TriggerWords = text, true);
            ProgressAttributes.AppendMenu(menu, this);
        }

        public override bool Write(GH_IWriter writer)
        {
            writer.SetString("Catchphrase", Catchphrase ?? "");
            writer.SetString("TriggerWords", TriggerWords ?? "");
            ProgressAttributes.Write(this, writer);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            string phrase = null;
            if (reader.TryGetString("Catchphrase", ref phrase)) Catchphrase = phrase;
            string triggers = null;
            if (reader.TryGetString("TriggerWords", ref triggers)) TriggerWords = triggers;
            ProgressAttributes.Read(this, reader);
            return base.Read(reader);
        }

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon => Icons.Load("KakadooAlwaysOn");

        public override Guid ComponentGuid => new Guid("77110198-f3ad-4f6b-ab58-aa3a6a61a742");
    }
}
