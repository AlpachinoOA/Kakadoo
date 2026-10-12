using System;
using System.Drawing;
using System.Drawing.Drawing2D;

using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using Grasshopper.Kernel.Types;

namespace Kakadoo
{
    /// <summary>The (empty) data a Tether wire carries. The wire itself is what matters: it says which components the Always-On component tethers.</summary>
    public sealed class TetherGoo : GH_Goo<string>
    {
        public TetherGoo() { Value = ""; }
        public TetherGoo(string text) { Value = text ?? ""; }

        public override bool IsValid => true;
        public override string TypeName => "Tether";
        public override string TypeDescription => "A Kakadoo tether signal";
        public override IGH_Goo Duplicate() => new TetherGoo(Value);
        public override string ToString() => Value;
    }

    /// <summary>
    /// The Tether connection between Kakadoo components. A Tether output (Always-On) can only be wired to a Tether input (listening components),
    /// so it can't be mixed up with ordinary data wires.
    /// </summary>
    public sealed class TetherParam : GH_Param<TetherGoo>
    {
        public TetherParam()
          : base("Tether", "Tether", "Connect Always-On's Tether output to the Tether input of a Kakadoo component to start it by voice. The sentence you say is passed along.",
              "Kakadoo", "Voice", GH_ParamAccess.item)
        {
            Optional = true;
        }

        public override Guid ComponentGuid => new Guid("5e8d1f2a-7c34-4b69-a0d5-3f6b92c18e47");

        public override GH_Exposure Exposure => GH_Exposure.hidden;
    }

    /// <summary>Draws the incoming Tether wire as a faint red curve instead of the normal grey wire.</summary>
    sealed class TetherAttributes : GH_LinkedParamAttributes
    {
        public TetherAttributes(IGH_Param owner, IGH_Attributes parent) : base(owner, parent)
        {
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            if (channel != GH_CanvasChannel.Wires)
            {
                base.Render(canvas, graphics, channel);
                return;
            }

            var end = InputGrip;
            var smoothing = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Color.FromArgb(110, 226, 36, 36), 2f))
            {
                foreach (var source in Owner.Sources)
                {
                    if (source.Attributes == null) continue;
                    var start = source.Attributes.OutputGrip;
                    float reach = Math.Max(40f, Math.Abs(end.X - start.X) / 2f);
                    graphics.DrawBezier(pen, start, new PointF(start.X + reach, start.Y), new PointF(end.X - reach, end.Y), end);
                }
            }
            graphics.SmoothingMode = smoothing;
        }
    }
}
