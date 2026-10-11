using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Grasshopper.Kernel;

namespace Kakadoo_Final
{
    /// <summary>
    /// Kakadoo Simple: listens once and outputs what was said as text.
    /// </summary>
    public class KakadooSimple : KakadooComponent
    {
        readonly ISpeechListener listener = SpeechListeners.Create();
        readonly object gate = new object();
        string command = string.Empty;
        volatile bool running;
        bool lastRun;

        public KakadooSimple()
          : base("Kakadoo Simple", "KkSmp",
              "Listens once and returns what you said as text",
              "Kakadoo", "Voice")
        {
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBooleanParameter("Run", "R", "Connect a button", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Output", "O", "Status messages", GH_ParamAccess.list);
            pManager.AddTextParameter("Command", "C", "The recognised speech, in lowercase", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            bool run = false;
            DA.GetData(0, ref run);

            // Only trigger on the rising edge so re-solves don't relisten.
            bool trigger = run && !lastRun;
            lastRun = run;

            if (trigger && !running)
            {
                running = true;
                Message = "Listening";
                lock (gate) messages.Clear();
                Task.Run(ListenAsync);
            }
            else if (!run && !running)
            {
                Message = "Not Listening";
            }

            lock (gate)
            {
                DA.SetDataList(0, messages.ToList());
                DA.SetData(1, command);
            }
        }

        async Task ListenAsync()
        {
            try
            {
                string text = await SpeechListeners.ListenAsync(listener, m => { lock (gate) messages.Add(m); });
                lock (gate)
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        command = string.Empty;
                        messages.Add("Could not understand audio. Please try again.");
                    }
                    else
                    {
                        command = text.ToLowerInvariant();
                        messages.Add("Recognized speech: " + text);
                    }
                }
            }
            catch (Exception e)
            {
                lock (gate) messages.Add("Speech error: " + e.Message);
            }
            finally
            {
                running = false;
                Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
                {
                    Message = "Not Listening";
                    ExpireSolution(true);
                }));
            }
        }

        protected override Bitmap Icon => Icons.Load("KakadooSimple");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        public override Guid ComponentGuid => new Guid("7d13f8a4-96b2-4e5c-8c07-b4a2e91d6f35");
    }
}
