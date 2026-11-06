using System;
using System.Drawing;

using Grasshopper.Kernel;

namespace Kakadoo
{
    /// <summary>Kakadoo Parakeet: say a slider's name and a number, like Kakadoo, but with the NVIDIA Parakeet model.</summary>
    public class KakadooParakeet : KakadooSliderVoice
    {
        readonly ParakeetListener listener = new ParakeetListener();

        public KakadooParakeet()
          : base("Kakadoo Parakeet", "KkPrk",
              "Say a slider's name and a number to set it, e.g. \"height 20\", recognised by the NVIDIA Parakeet model (download it in Kakadoo Settings > Models)")
        {
        }

        protected override ISpeechListener Listener => listener;

        protected override void AddModelInput(GH_InputParamManager pManager)
        {
            int i = pManager.AddTextParameter(ParakeetInputName, ParakeetInputName,
                "Optional: a Parakeet model folder (encoder, decoder and joiner .onnx files and tokens.txt). Blank uses the Parakeet model chosen in Kakadoo Settings.", GH_ParamAccess.item);
            pManager[i].Optional = true;
        }

        protected override void ApplyModelInput(IGH_DataAccess DA)
        {
            string path = null;
            int i = Params.Input.FindIndex(p => p.Name == ParakeetInputName);
            if (i >= 0) DA.GetData(i, ref path);
            listener.ModelPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim().Trim('"');
        }

        protected override Bitmap Icon => Icons.Load("KakadooParakeet");

        public override GH_Exposure Exposure => GH_Exposure.hidden;

        public override Guid ComponentGuid => new Guid("6f2d1c84-3a7b-4e59-9c0d-81b5a2e47f36");
    }
}
