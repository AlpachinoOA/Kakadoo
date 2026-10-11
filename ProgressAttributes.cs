using System;
using System.Drawing;

using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

namespace Kakadoo_Final
{
    /// <summary>
    /// Component look that adds a download bar underneath while the speech model is downloading
    /// and the component is the one waiting for it.
    /// </summary>
    class ProgressAttributes : GH_ComponentAttributes
    {
        static bool hooked;

        public ProgressAttributes(IGH_Component owner) : base(owner)
        {
            if (hooked) return;
            hooked = true;
            ModelDownload.Changed += () => Rhino.RhinoApp.InvokeOnUiThread(new Action(() => Grasshopper.Instances.RedrawCanvas()));
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);
            if (channel != GH_CanvasChannel.Objects || !ModelDownload.Active) return;
            if (Owner.Message != "Listening" && Owner.Message != "Checking") return;

            var b = Bounds;
            float width = Math.Max(b.Width, 190f);
            var bar = new RectangleF(b.Left + (b.Width - width) / 2f, b.Bottom + 8f, width, 9f);

            using (var back = new SolidBrush(Color.FromArgb(70, 40, 40, 40)))
            using (var fill = new SolidBrush(Color.FromArgb(255, 254, 202, 18))) // the Kakadoo yellow
            using (var edge = new Pen(Color.FromArgb(160, 40, 40, 40)))
            using (var font = new Font(GH_FontServer.Standard.FontFamily, 8f))
            {
                graphics.FillRectangle(back, bar);
                graphics.FillRectangle(fill, bar.Left, bar.Top, (float)(bar.Width * ModelDownload.Fraction), bar.Height);
                graphics.DrawRectangle(edge, bar.Left, bar.Top, bar.Width, bar.Height);

                string size = ModelDownload.TotalBytes > 0
                    ? string.Format("  {0:0}%  ({1} / {2} MB)", ModelDownload.Fraction * 100, ModelDownload.DoneBytes / 1048576, ModelDownload.TotalBytes / 1048576)
                    : "";
                graphics.DrawString(ModelDownload.Title + size, font, Brushes.Black, bar.Left, bar.Bottom + 3f);
                graphics.DrawString("Saving to: " + ModelDownload.Folder, font, Brushes.DimGray, bar.Left, bar.Bottom + 3f + font.Height);
            }
        }
    }
}
