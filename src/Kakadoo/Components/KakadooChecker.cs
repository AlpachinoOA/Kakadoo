using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

using Grasshopper.Kernel;

namespace Kakadoo
{
    /// <summary>
    /// Kakadoo Checker: tests the speech engine, microphone and models, and reports every result,
    /// including the ones that failed or were skipped.
    /// </summary>
    public class KakadooChecker : KakadooComponent
    {
        readonly object gate = new object();
        volatile bool running;
        bool lastRun;

        public KakadooChecker()
          : base("Kakadoo Checker", "KkChk",
              "Checks that your microphone, speech engine and model are ready for Kakadoo",
              "Kakadoo", "Utilities")
        {
        }

        protected override bool SupportsTether => false;

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBooleanParameter("Run", "R", "Connect a Boolean Toggle to test the microphone", GH_ParamAccess.item, false);
            AddWhisperInput(pManager);
            pManager.AddTextParameter(LlamaInputName, LlamaInputName, "Path to the Llama GGUF model file to check (Kakadoo Llama)", GH_ParamAccess.item);
            pManager.AddTextParameter(VoskInputName, VoskInputName, "Path to the Vosk model folder to check (Kakadoo Vosk)", GH_ParamAccess.item);
            pManager[2].Optional = true;
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Output", "O", "Check results", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            bool run = false;
            string whisperPath = null, llamaPath = null, voskPath = null;
            DA.GetData(0, ref run);
            DA.GetData(1, ref whisperPath);
            DA.GetData(2, ref llamaPath);
            DA.GetData(3, ref voskPath);

            bool trigger = run && !lastRun;
            lastRun = run;

            if (trigger && !running)
            {
                running = true;
                Message = "Checking";
                lock (gate) messages.Clear();
                Task.Run(() => RunChecksAsync(whisperPath, llamaPath, voskPath));
            }
            else if (!run && !running)
            {
                Message = "Not Running";
            }

            lock (gate) DA.SetDataList(0, new List<string>(messages));
        }

        async Task RunChecksAsync(string whisperPath, string llamaPath, string voskPath)
        {
            int failed = 0;
            try
            {
                // The Parakeet and custom models are the ones chosen in the Kakadoo settings.
                var totals = await SystemChecks.RunAsync(whisperPath, llamaPath, voskPath, null, null, Log, r => Log(r.Line));
                failed = totals.failed;
                Log("Result: " + totals.passed + " passed, " + totals.failed + " failed, " + totals.skipped + " skipped.");
            }
            finally
            {
                running = false;
                Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
                {
                    Message = failed == 0 ? "All checks passed" : "Error";
                    ExpireSolution(true);
                }));
            }
        }

        void Log(string message)
        {
            lock (gate) messages.Add(message);
            RequestRefresh();
        }

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon => Icons.Load("KakadooChecker");

        public override Guid ComponentGuid => new Guid("a91e5d38-7f02-4c6b-b3d4-0e8c1f52a769");
    }
}
