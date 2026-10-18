using System;
using System.Drawing;
using System.Linq;

using Grasshopper.Kernel;

namespace Kakadoo
{
    /// <summary>
    /// Kakadoo Settings: one place for the settings every Kakadoo component shares (microphone, speech model, animation).
    /// Optional; without it the defaults saved on this PC are used. Values apply for the session, and "remember" saves them as the PC defaults.
    /// </summary>
    public class KakadooSettings : GH_Component
    {
        public KakadooSettings()
          : base("Kakadoo Settings", "KkSet",
              "Shared settings for all Kakadoo components: microphone, speech model and the listening animation. Leave an input empty to keep the current value.",
              "Kakadoo", "Utilities")
        {
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Microphone", "Mic", "Microphone to listen with: its name or its number in the Microphones output (0 = first). Empty = system default", GH_ParamAccess.item);
            pManager.AddTextParameter("Model", "Model", "Speech model: \"auto\", \"fast\" (smaller, quicker) or \"accurate\" (bigger, more precise). A model that is not on this PC yet is downloaded automatically the first time it is needed (needs internet).", GH_ParamAccess.item);
            pManager.AddTextParameter("Preset", "Preset", "A bundle of listening settings: \"standard\", \"quiet\" (quiet room, more sensitive), \"noisy\" (workshop, needs a louder voice), \"fast\" (quick replies, smaller model) or \"accurate\" (waits longer, bigger model). Model, if set, overrides the preset's model.", GH_ParamAccess.item);
            pManager.AddBooleanParameter("Animation", "Anim", "Show the red listening animation", GH_ParamAccess.item);
            pManager.AddBooleanParameter("Remember", "Save", "True = also keep these values as the defaults on this PC", GH_ParamAccess.item, false);
            foreach (var p in Params.Input) p.Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Microphones", "Mics", "The microphones found on this PC", GH_ParamAccess.list);
            pManager.AddTextParameter("Active", "A", "The settings in use right now", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // Only one settings component counts per document, so two of them can't fight over the values.
            var doc = OnPingDocument();
            if (doc != null && doc.Objects.OfType<KakadooSettings>().First() != this)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "There is another Kakadoo Settings component in this document. Only the first one is used.");
                return;
            }

            string mic = null, model = null;
            bool animation = true, remember = false;
            bool hasMic = Params.Input[0].SourceCount > 0 && DA.GetData(0, ref mic);
            bool hasModel = Params.Input[1].SourceCount > 0 && DA.GetData(1, ref model);
            string preset = null;
            bool hasPreset = Params.Input[2].SourceCount > 0 && DA.GetData(2, ref preset);
            bool hasAnim = Params.Input[3].SourceCount > 0 && DA.GetData(3, ref animation);
            DA.GetData(4, ref remember);

            if (hasModel && !new[] { "auto", "fast", "accurate" }.Contains((model ?? "").Trim().ToLowerInvariant()))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Model must be auto, fast or accurate.");
                hasModel = false;
            }

            var devices = Microphone.Devices();
            // A plain number picks that microphone from the list (0 = first), which is quicker than typing its name.
            if (hasMic && int.TryParse((mic ?? "").Trim(), out int index) && index >= 0 && index < devices.Length) mic = devices[index];

            if (hasPreset && !new[] { "standard", "quiet", "noisy", "fast", "accurate" }.Contains((preset ?? "").Trim().ToLowerInvariant()))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Preset must be standard, quiet, noisy, fast or accurate.");
                hasPreset = false;
            }

            Settings.Apply(hasMic ? mic : null, hasModel ? model : null, hasAnim ? animation : (bool?)null, hasPreset ? preset : null, remember);

            if (hasMic && !string.IsNullOrWhiteSpace(mic) &&
                !devices.Any(d => mic.StartsWith(d, StringComparison.OrdinalIgnoreCase) || d.StartsWith(mic, StringComparison.OrdinalIgnoreCase)))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No microphone called \"" + mic + "\" was found; the default is used.");

            DA.SetDataList(0, devices);
            DA.SetData(1, "Microphone: " + (string.IsNullOrEmpty(Settings.Microphone) ? "system default" : Settings.Microphone)
                + " | Preset: " + Settings.Preset
                + " | Model: " + Settings.ModelPreset
                + " | Animation: " + (Settings.Animate ? "on" : "off"));
        }

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon => Icons.Load("KakadooSettings");

        public override Guid ComponentGuid => new Guid("c3a57e12-5b9d-4f08-9e6a-2d41b8f7a903");
    }
}
