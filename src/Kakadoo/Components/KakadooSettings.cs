using System;
using System.Drawing;

using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

namespace Kakadoo
{
    /// <summary>
    /// Kakadoo Settings: no inputs or outputs. Right-click it (or double-click) to open the settings window with the shared settings
    /// (microphone, listening preset, animation) and the model manager. Everything is saved as the defaults on this PC.
    /// </summary>
    public class KakadooSettings : GH_Component
    {
        public KakadooSettings()
          : base("Kakadoo Settings", "KkSet",
              "Right-click (or double-click) and choose \"Open Kakadoo Settings\" to set the microphone, the listening preset and the animation, and to download or choose speech and language models.",
              "Kakadoo", "Utilities")
        {
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
        }

        public override void CreateAttributes() => m_attributes = new SettingsAttributes(this);

        protected override void AppendAdditionalComponentMenuItems(System.Windows.Forms.ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "Open Kakadoo Settings", (s, e) => SettingsWindow.ShowWindow());
        }

        sealed class SettingsAttributes : GH_ComponentAttributes
        {
            public SettingsAttributes(IGH_Component owner) : base(owner) { }

            public override GH_ObjectResponse RespondToMouseDoubleClick(GH_Canvas sender, GH_CanvasMouseEvent e)
            {
                SettingsWindow.ShowWindow();
                return GH_ObjectResponse.Handled;
            }
        }

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon => Icons.Load("KakadooSettings");

        public override Guid ComponentGuid => new Guid("c3a57e12-5b9d-4f08-9e6a-2d41b8f7a903");
    }
}
