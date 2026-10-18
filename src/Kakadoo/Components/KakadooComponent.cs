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

        // Run and the speech-model inputs are not needed (and get in the way) when the component is started by voice from
        // Always-On, which does the listening, so they are swapped out for the Tether input.
        readonly List<KeyValuePair<int, IGH_Param>> removedInputs = new List<KeyValuePair<int, IGH_Param>>();
        static bool GoesWithTether(IGH_Param p) => p.Name == "Run" || p.Name == WhisperInputName || p.Name == VoskInputName;

        /// <summary>Index of the Run input, or -1 while it is swapped out for the Tether input.</summary>
        protected int RunIndex => Params.Input.FindIndex(p => p.Name == "Run");

        void SetTether(bool on)
        {
            if (on == TetherOn) return;
            TetherOn = on;
            if (on)
            {
                for (int i = Params.Input.Count - 1; i >= 0; i--)
                {
                    if (!GoesWithTether(Params.Input[i])) continue;
                    removedInputs.Insert(0, new KeyValuePair<int, IGH_Param>(i, Params.Input[i]));
                    Params.UnregisterInputParameter(Params.Input[i]);
                }
                var tetherParam = new TetherParam();
                Params.RegisterInputParam(tetherParam);
                EnsureTetherAttributes();
            }
            else
            {
                var tether = Params.Input.OfType<TetherParam>().FirstOrDefault();
                if (tether != null) Params.UnregisterInputParameter(tether);
                foreach (var removed in removedInputs) // lowest index first, so each goes back where it was
                    Params.RegisterInputParam(removed.Value, Math.Min(removed.Key, Params.Input.Count));
                removedInputs.Clear();
                Interlocked.Exchange(ref tetherText, null);
                Interlocked.Exchange(ref tetherPending, 0);
            }
        }

        /// <summary>
        /// Makes the Tether input draw its wire red. Loading a saved file recreates the parameters' attributes, so this runs again
        /// when the component joins a document and before each solve, not just when Tether is switched on.
        /// </summary>
        void EnsureTetherAttributes()
        {
            if (Attributes == null) return;
            foreach (var tether in Params.Input.OfType<TetherParam>())
                if (!(tether.Attributes is TetherAttributes))
                    tether.Attributes = new TetherAttributes(tether, Attributes);
        }

        public override void AddedToDocument(GH_Document document)
        {
            base.AddedToDocument(document);
            EnsureTetherAttributes();
        }

        protected override void BeforeSolveInstance()
        {
            base.BeforeSolveInstance();
            EnsureTetherAttributes();
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
        }

        public const string WhisperInputName = "Whisper Model Path";
        public const string VoskInputName = "Vosk Model Directory Path";

        /// <summary>Adds the optional "Whisper Model Path" input, shared by every component that listens with Whisper.</summary>
        protected static void AddWhisperInput(GH_InputParamManager pManager)
        {
            int i = pManager.AddTextParameter(WhisperInputName, WhisperInputName,
                "Optional: a Whisper model file (ggml-*.bin) or a folder containing one, for PCs without internet. Blank uses the automatic model.", GH_ParamAccess.item);
            pManager[i].Optional = true;
        }

        /// <summary>Reads the Whisper Model Path input and hands it to the listener (null = automatic model).</summary>
        protected void ApplyWhisperInput(IGH_DataAccess DA, ISpeechListener listener)
        {
            string path = null;
            int i = Params.Input.FindIndex(p => p.Name == WhisperInputName);
            if (i >= 0) DA.GetData(i, ref path);
            if (listener is IModelPathListener withModel) withModel.ModelPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim().Trim('"');
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
