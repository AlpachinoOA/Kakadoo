using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Grasshopper.Kernel;

namespace Kakadoo
{
    /// <summary>
    /// Kakadoo: say a slider's name and a number, e.g. "set width to 10", and that slider takes the value.
    /// </summary>
    public class KakadooSliders : KakadooComponent
    {

        readonly ISpeechListener listener = SpeechListeners.Create();
        readonly object gate = new object();
        volatile bool running;
        bool lastRun;

        public KakadooSliders()
          : base("Kakadoo", "Kkdoo",
              "Say a slider's name and a number to set it, e.g. \"height 20\"",
              "Kakadoo", " Voice")
        {
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter("Sliders", "S", "Connect the sliders you want to manipulate", GH_ParamAccess.list);
            pManager.AddBooleanParameter("Run", "R", "Connect a button", GH_ParamAccess.item, false);
            AddWhisperInput(pManager);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Output", "O", "Status messages", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            bool run = false;
            if (RunIndex >= 0) DA.GetData(RunIndex, ref run);
            ApplyWhisperInput(DA, listener);

            // Only trigger on the rising edge so re-solves (e.g. after a slider change) don't relisten.
            bool woken = ConsumeTether(!running);
            bool trigger = (run && !lastRun) || woken;
            lastRun = run;

            if (trigger && !running)
            {
                var sliders = SliderUtil.GetSliders(Params.Input[0]);
                string notice = SliderUtil.Notice(Params.Input[0]);
                running = true;
                Message = "Listening";
                Log("Listening...", clear: true);
                if (notice != null) Log(notice);
                Task.Run(() => RunCommandAsync(sliders));
            }
            else if (!run && !running)
            {
                Message = "Not Listening";
            }

            lock (gate) DA.SetDataList(0, messages.ToList());
        }

        async Task RunCommandAsync(List<Grasshopper.Kernel.Special.GH_NumberSlider> sliders)
        {
            try
            {
                if (sliders.Count == 0) { Log("No named sliders connected. Connect number sliders that have a name."); return; }

                string command;
                SpeechListeners.SetHint(listener, SliderUtil.Names(sliders));
                try { command = await HearAsync(() => SpeechListeners.ListenAsync(listener, m => Log(m))); }
                catch (Exception e) { Log("Speech error: " + ErrorHelp.Report(this, e)); return; }

                if (string.IsNullOrWhiteSpace(command)) { Log("Could not understand audio. Please try again."); return; }
                command = command.ToLowerInvariant();
                Log("Recognized speech: " + command);

                if (!NumberWords.TryExtract(command, out double value)) { Log("No number found in the command, e.g. \"height 20\" or \"height twenty\"."); return; }

                var matched = SliderUtil.MatchByName(sliders, command);
                if (matched.Count == 0) { Log("No slider name found in the command."); return; }

                string limited = SliderUtil.SetValues(matched, value);
                Log("Changed " + SliderUtil.Describe(matched) + " to value: " + value + limited);
            }
            catch (Exception e)
            {
                Log("An error occurred: " + ErrorHelp.Report(this, e));
            }
            finally
            {
                running = false;
                FinishTethered();
                Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
                {
                    Message = "Not Listening";
                    ExpireSolution(true);
                }));
            }
        }

        void Log(string message, bool clear = false)
        {
            lock (gate)
            {
                if (clear) messages.Clear();
                messages.Add(message);
            }
            RequestRefresh();
        }

        protected override Bitmap Icon => Icons.Load("Kakadoo");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        public override Guid ComponentGuid => new Guid("3e8a6b27-5c14-4f0d-a1b9-7d92c40e5f18");
    }
}
