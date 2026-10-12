using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using GH_IO.Serialization;
using Grasshopper.GUI;
using Grasshopper.Kernel;

namespace Kakadoo
{
    /// <summary>Status messages grouped into runs. Only the newest runs are kept.</summary>
    public class MessageLog : IEnumerable<string>
    {
        readonly List<List<string>> runs = new List<List<string>>(); // oldest first

        public int Limit { get; set; } = 4;

        public bool IsEmpty { get { lock (runs) return runs.Count == 0; } }

        /// <summary>Starts a new run, dropping the oldest ones beyond the limit.</summary>
        public void Clear()
        {
            lock (runs)
            {
                runs.Add(new List<string>());
                Trim();
            }
        }

        public void Add(string message)
        {
            lock (runs)
            {
                if (runs.Count == 0) runs.Add(new List<string>());
                runs[runs.Count - 1].Add(message);
            }
        }

        public void Trim()
        {
            lock (runs) while (runs.Count > Math.Max(1, Limit)) runs.RemoveAt(0);
        }

        /// <summary>All messages, newest run first, with a separator line between runs.</summary>
        public IEnumerator<string> GetEnumerator()
        {
            var lines = new List<string>();
            lock (runs)
            {
                foreach (var run in Enumerable.Reverse(runs))
                {
                    if (lines.Count > 0) lines.Add("---");
                    lines.AddRange(run);
                }
            }
            return lines.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Base for Kakadoo components that report status messages. Adds a "Keep last runs" setting to the right-click menu.</summary>
    public abstract class KakadooComponent : GH_Component, IGH_VariableParameterComponent
    {
        protected readonly MessageLog messages = new MessageLog();
        readonly RefreshGate refreshGate = new RefreshGate();

        /// <summary>Recomputes this component from any thread, safely and merged (see <see cref="UiRefresh"/>).</summary>
        protected void RequestRefresh() => UiRefresh.Request(this, refreshGate);

        // ---- Tether: start this component from an Always-On component and give it the sentence that was said ----
        string tetherText;
        int tetherPending;

        /// <summary>Whether this component can be started by an Always-On component. Off for the Checker.</summary>
        protected virtual bool SupportsTether => true;

        /// <summary>Whether the Tether input is shown (a right-click setting).</summary>
        public bool TetherOn { get; private set; }

        // The Run input is not needed (and gets in the way) when the component is started by voice from Always-On, so it is swapped out.
        IGH_Param removedRun;
        int removedRunIndex;

        /// <summary>Index of the Run input, or -1 while it is swapped out for the Tether input.</summary>
        protected int RunIndex => Params.Input.FindIndex(p => p.Name == "Run");

        void SetTether(bool on)
        {
            if (on == TetherOn) return;
            TetherOn = on;
            if (on)
            {
                int run = RunIndex;
                if (run >= 0)
                {
                    removedRun = Params.Input[run];
                    removedRunIndex = run;
                    Params.UnregisterInputParameter(removedRun);
                }
                var tetherParam = new TetherParam();
                Params.RegisterInputParam(tetherParam);
                tetherParam.Attributes = new TetherAttributes(tetherParam, Attributes); // the wire is drawn faint red
            }
            else
            {
                var tether = Params.Input.OfType<TetherParam>().FirstOrDefault();
                if (tether != null) Params.UnregisterInputParameter(tether);
                if (removedRun != null)
                {
                    Params.RegisterInputParam(removedRun, Math.Min(removedRunIndex, Params.Input.Count));
                    removedRun = null;
                }
                Interlocked.Exchange(ref tetherText, null);
                Interlocked.Exchange(ref tetherPending, 0);
            }
        }

        /// <summary>Called by Always-On on the UI thread. Returns false if this component doesn't take tether signals.</summary>
        public bool Tether(string text)
        {
            if (!TetherOn) return false;
            Interlocked.Exchange(ref tetherText, text);
            Interlocked.Exchange(ref tetherPending, 1);
            OnPingDocument()?.ScheduleSolution(5, d => ExpireSolution(false));
            return true;
        }

        /// <summary>
        /// Call at the top of SolveInstance: true when a tether signal arrived and the component is free to start. If it is still busy
        /// with an earlier command, the signal is dropped (with a note on the canvas) so the text can't leak into a later run.
        /// </summary>
        protected bool ConsumeTether(bool canStart)
        {
            if (Interlocked.Exchange(ref tetherPending, 0) == 0) return false;
            if (canStart) return true;
            Interlocked.Exchange(ref tetherText, null);
            return false;
        }

        volatile bool tetheredRun;

        /// <summary>Call when a run ends. If it was started by Always-On, shows a single ripple to say the change happened.</summary>
        protected void FinishTethered()
        {
            if (!tetheredRun) return;
            tetheredRun = false;
            Rhino.RhinoApp.InvokeOnUiThread(new Action(() => ProgressAttributes.Pulse(this)));
        }

        /// <summary>Gets the sentence to work on: the one Always-On passed along, or else what <paramref name="listen"/> hears.</summary>
        protected async Task<string> HearAsync(Func<Task<string>> listen)
        {
            string passed = Interlocked.Exchange(ref tetherText, null);
            if (passed != null)
            {
                tetheredRun = true; // one ripple when the change is done (FinishTethered)
                return passed;
            }
            return await listen();
        }

        bool IGH_VariableParameterComponent.CanInsertParameter(GH_ParameterSide side, int index) => false;
        bool IGH_VariableParameterComponent.CanRemoveParameter(GH_ParameterSide side, int index) => false;
        IGH_Param IGH_VariableParameterComponent.CreateParameter(GH_ParameterSide side, int index) => null;
        bool IGH_VariableParameterComponent.DestroyParameter(GH_ParameterSide side, int index) => false;
        void IGH_VariableParameterComponent.VariableParameterMaintenance() { }

        protected KakadooComponent(string name, string nickname, string description, string category, string subCategory)
          : base(name, nickname, description, category, subCategory)
        {
        }

        public override void CreateAttributes() => m_attributes = new ProgressAttributes(this);

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendItem(menu, "Keep last runs:", null, false);
            Menu_AppendTextItem(menu, messages.Limit.ToString(), null, (box, text) =>
            {
                if (int.TryParse(text, out int limit) && limit >= 1 && limit <= 100)
                {
                    messages.Limit = limit;
                    messages.Trim();
                    ExpireSolution(true);
                }
            }, true);

            if (SupportsTether)
                Menu_AppendItem(menu, "Tether input (start by voice from Always-On)", (s, e) =>
                {
                    SetTether(!TetherOn);
                    Params.OnParametersChanged();
                    ExpireSolution(true);
                }, true, TetherOn);
            ProgressAttributes.AppendMenu(menu, this);
            if (UsesWhisper) AppendWhisperMenu(menu);
        }

        /// <summary>Whether this component listens with Whisper, and so shows the model path setting.</summary>
        protected virtual bool UsesWhisper => true;

        /// <summary>The "Whisper model file or folder" entry, shared by every component that listens with Whisper.</summary>
        public static void AppendWhisperMenu(ToolStripDropDown menu)
        {
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "Whisper model file or folder (blank = automatic):", null, false);
            Menu_AppendTextItem(menu, Settings.WhisperModelPath ?? "", null, (box, text) => Settings.WhisperModelPath = text, true);
        }

        public override bool Write(GH_IWriter writer)
        {
            writer.SetInt32("HistoryLimit", messages.Limit);
            writer.SetBoolean("TetherOn", TetherOn);
            ProgressAttributes.Write(this, writer);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            int limit = 4;
            if (reader.TryGetInt32("HistoryLimit", ref limit)) messages.Limit = Math.Max(1, limit);
            // The Tether input is created before the base class reads the saved parameters, so the counts match.
            bool tether = false;
            if (SupportsTether && reader.TryGetBoolean("TetherOn", ref tether) && tether) SetTether(true);
            ProgressAttributes.Read(this, reader);
            return base.Read(reader);
        }
    }
}
