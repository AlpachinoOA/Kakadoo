using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Kakadoo
{
    /// <summary>
    /// Kakadoo Vosk: offline version of Kakadoo. Say a slider's name and a number, in digits or words.
    /// </summary>
    public class KakadooVosk : KakadooComponent
    {
        readonly object gate = new object();
        volatile bool running;
        bool lastRun;

        public KakadooVosk()
          : base("Kakadoo Vosk", "KkVsk",
              "Say a slider's name and a number, fully offline. Spoken numbers like \"twelve\" work too. Larger Vosk models can take several minutes to load, depending on the speed of your machine",
              "Kakadoo", " Voice")
        {
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter("Sliders", "S", "Connect the sliders you want to manipulate", GH_ParamAccess.list);
            pManager.AddTextParameter(VoskInputName, VoskInputName, "Path to the folder of the Vosk model", GH_ParamAccess.item);
            pManager.AddBooleanParameter("Run", "R", "Connect a button", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Output", "O", "Status messages", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string modelPath = null;
            bool run = false;
            int pathIndex = Params.Input.FindIndex(p => p.Name == VoskInputName); // absent while Tether is on
            if (pathIndex >= 0) DA.GetData(pathIndex, ref modelPath);
            if (RunIndex >= 0) DA.GetData(RunIndex, ref run);

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
                Log("Listening for 5 seconds... Speak now.", clear: true);
                if (notice != null) Log(notice);
                Task.Run(() => RunCommandAsync(sliders, modelPath));
            }
            else if (!run && !running)
            {
                Message = "Not Listening";
            }

            lock (gate) DA.SetDataList(0, messages.ToList());
        }

        async Task RunCommandAsync(List<GH_NumberSlider> sliders, string modelPath)
        {
            try
            {
                if (sliders.Count == 0) { Log("No named sliders connected. Connect number sliders that have a name."); return; }

                string command;
                try
                {
                    command = await HearAsync(() =>
                    {
                        if (string.IsNullOrWhiteSpace(modelPath)) throw new SpeechSetupException("No Vosk model path provided. Connect the folder of a Vosk model.");
                        return new VoskListener(modelPath).ListenOnceAsync(CancellationToken.None);
                    });
                }
                catch (Exception e) { Log(ErrorHelp.Report(this, e)); return; }

                if (string.IsNullOrWhiteSpace(command)) { Log("Could not understand audio. Please try again."); return; }
                command = command.ToLowerInvariant();

                bool hasNumber = NumberWords.TryExtract(command, out double value);
                Log("Recognized speech: " + command + (hasNumber ? ", Number representation: " + value : ""));
                if (!hasNumber) { Log("Could not find a number. Invalid command."); return; }

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

        protected override Bitmap Icon => Icons.Load("KakadooVosk");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        public override Guid ComponentGuid => new Guid("e2b46a95-0d3c-47f8-9a1e-58c7f3d20b61");
    }
}
