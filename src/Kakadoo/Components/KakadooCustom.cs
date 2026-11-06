using System;
using System.Drawing;

using Grasshopper.Kernel;

namespace Kakadoo
{
    /// <summary>Kakadoo Custom: say a slider's name and a number, like Kakadoo, but with a Whisper-compatible model of your own.</summary>
    public class KakadooCustom : KakadooSliderVoice
    {
        readonly CustomListener listener = new CustomListener();

        public KakadooCustom()
          : base("Kakadoo Custom", "KkCst",
              "Say a slider's name and a number to set it, e.g. \"height 20\", recognised by a custom Whisper-compatible model (choose it in Kakadoo Settings > Models > Custom models)")
        {
        }

        protected override ISpeechListener Listener => listener;

        protected override void AddModelInput(GH_InputParamManager pManager)
        {
            int i = pManager.AddTextParameter(CustomInputName, CustomInputName,
                "Optional: a Whisper-compatible model file (ggml .bin). Blank uses the custom model chosen in Kakadoo Settings.", GH_ParamAccess.item);
            pManager[i].Optional = true;
        }

        protected override void ApplyModelInput(IGH_DataAccess DA)
        {
            string path = null;
            int i = Params.Input.FindIndex(p => p.Name == CustomInputName);
            if (i >= 0) DA.GetData(i, ref path);
            listener.ModelPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim().Trim('"');
        }

        protected override Bitmap Icon => Icons.Load("KakadooCustom");

        public override GH_Exposure Exposure => GH_Exposure.hidden;

        public override Guid ComponentGuid => new Guid("b84e5a19-2c6d-4f70-8a13-e9d7c05b3a62");
    }
}
