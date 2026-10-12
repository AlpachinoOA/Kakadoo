using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

using GH_IO.Serialization;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

namespace Kakadoo
{
    /// <summary>
    /// Component look that adds a download bar underneath while the speech model is downloading
    /// and the component is the one waiting for it. While listening it also draws a breathing ring (switchable per component).
    /// </summary>
    class ProgressAttributes : GH_ComponentAttributes
    {
        static bool hooked;

        const int FrameMs = 66;        // about 15 frames per second, only while a visible component is listening
        const double PeriodMs = 1800;  // one ripple cycle
        const long StaleMs = 600;      // not drawn for this long (off screen) means it stops animating

        static readonly Dictionary<ProgressAttributes, long> active = new Dictionary<ProgressAttributes, long>();
        static Timer timer;

        /// <summary>Whether the listening ring breathes. Saved with the component.</summary>
        public bool Animate { get; set; } = true;

        public ProgressAttributes(IGH_Component owner) : base(owner)
        {
            if (hooked) return;
            hooked = true;
            bool wasActive = false;
            ModelDownload.Changed += () => Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                if (wasActive && !ModelDownload.Active)
                {
                    // Show "Model ready" for a few seconds, then repaint once more to remove it.
                    readyUntil = Environment.TickCount64 + 4000;
                    var clear = new System.Threading.Timer(_ => Rhino.RhinoApp.InvokeOnUiThread(new Action(() => Grasshopper.Instances.RedrawCanvas())), null, 4300, System.Threading.Timeout.Infinite);
                    GC.KeepAlive(clear);
                }
                wasActive = ModelDownload.Active;
                Grasshopper.Instances.RedrawCanvas();
            }));
        }

        static long readyUntil;
        volatile string noteText;
        volatile NoteKind noteKind;
        long noteUntil;

        /// <summary>Shows a short message under a component on the canvas for a while (errors in red, hints in grey).</summary>
        public static void ShowNote(GH_Component owner, string text, NoteKind kind, int seconds)
        {
            var attributes = owner.Attributes as ProgressAttributes;
            if (attributes == null) return;
            attributes.noteKind = kind;
            attributes.noteText = text;
            attributes.noteUntil = Environment.TickCount64 + seconds * 1000L;
            Rhino.RhinoApp.InvokeOnUiThread(new Action(() => Grasshopper.Instances.RedrawCanvas()));
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);
            if (channel != GH_CanvasChannel.Objects) return;
            DrawListeningRing(graphics);

            float y = Bounds.Bottom + 8f;
            using (var font = new Font(GH_FontServer.Standard.FontFamily, 8f))
            {
                if (ModelDownload.Active && IsListening) y = DrawDownload(graphics, font, y);
                else if (IsListening && Environment.TickCount64 < readyUntil)
                    y = DrawText(graphics, font, "Speech model ready.", Color.FromArgb(40, 120, 60), y);

                string text = noteText;
                if (text != null)
                {
                    if (Environment.TickCount64 > noteUntil) noteText = null;
                    else DrawText(graphics, font, text, noteKind == NoteKind.Error ? Color.FromArgb(190, 30, 30) : Color.DimGray, y);
                }
            }
        }

        float DrawText(Graphics graphics, Font font, string text, Color color, float y)
        {
            float width = Math.Max(Bounds.Width, 230f);
            var area = new RectangleF(Bounds.Left + (Bounds.Width - width) / 2f, y, width, 400f);
            var size = graphics.MeasureString(text, font, (int)width);
            using (var brush = new SolidBrush(color))
                graphics.DrawString(text, font, brush, new RectangleF(area.Left, area.Top, width, size.Height));
            return y + size.Height + 4f;
        }

        float DrawDownload(Graphics graphics, Font font, float y)
        {
            var b = Bounds;
            float width = Math.Max(b.Width, 190f);
            var bar = new RectangleF(b.Left + (b.Width - width) / 2f, y, width, 9f);

            using (var back = new SolidBrush(Color.FromArgb(70, 40, 40, 40)))
            using (var fill = new SolidBrush(Color.FromArgb(255, 254, 202, 18))) // the Kakadoo yellow
            using (var edge = new Pen(Color.FromArgb(160, 40, 40, 40)))
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
            y = bar.Bottom + 3f + font.Height * 2 + 4f;

            // After half a minute with less than half done, point at the smaller model.
            if (Environment.TickCount64 - ModelDownload.StartedAt > 30000 && ModelDownload.Fraction < 0.5)
                y = DrawText(graphics, font, "Slow download? Right-click the component and choose \"Use the fast model\".", Color.DimGray, y);
            return y;
        }

        bool IsListening => Owner.Message == "Listening" || Owner.Message == "Checking";

        void DrawListeningRing(Graphics graphics)
        {
            if (!IsListening) { Unregister(); return; }

            bool animate = Animate && Settings.Animate;
            if (animate) Register(); else Unregister();

            var smoothing = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (!animate)
            {
                DrawRipple(graphics, 3f, 150, 2.5f); // still red outline when the animation is off
            }
            else
            {
                // Two ripples start at the component's edge, grow outward and fade away.
                double cycle = (Environment.TickCount64 % (long)PeriodMs) / PeriodMs;
                // The louder the microphone hears, the stronger and wider the ripples (a quiet room still shows a gentle one).
                double amp = 0.45 + 0.55 * Math.Min(1.0, Microphone.Level * 10.0);
                for (int i = 0; i < 2; i++)
                {
                    double t = (cycle + i * 0.5) % 1.0;
                    double fade = 1.0 - t;
                    // Thick and strong at the edge, thinner and fainter the further out it travels.
                    DrawRipple(graphics, (float)(1 + 20 * t * (0.6 + 0.4 * amp)), (int)(230 * fade * fade * amp), (float)(0.6 + 4.2 * fade * fade * amp));
                }
            }
            graphics.SmoothingMode = smoothing;
        }

        void DrawRipple(Graphics graphics, float grow, int alpha, float width)
        {
            if (alpha < 4) return;
            using (var path = RoundedRect(RectangleF.Inflate(Bounds, grow, grow), 4f + grow))
            using (var pen = new Pen(Color.FromArgb(alpha, 226, 36, 36), width)) // recording red
                graphics.DrawPath(pen, path);
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2f;
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // One shared UI-thread timer for all components. It only runs while a visible component is listening and animating.
        void Register()
        {
            active[this] = Environment.TickCount64;
            if (timer != null) return;
            timer = new Timer { Interval = FrameMs };
            timer.Tick += (s, e) => Tick();
            timer.Start();
        }

        void Unregister()
        {
            if (active.Remove(this)) StopTimerIfIdle();
        }

        static void StopTimerIfIdle()
        {
            if (active.Count > 0 || timer == null) return;
            timer.Stop();
            timer.Dispose();
            timer = null;
        }

        // Repaint only the animated components instead of the whole canvas, which keeps big definitions responsive.
        static void InvalidateActive()
        {
            var canvas = Grasshopper.Instances.ActiveCanvas;
            if (canvas == null) return;
            foreach (var a in active.Keys)
            {
                var area = Rectangle.Ceiling(canvas.Viewport.ProjectRectangle(RectangleF.Inflate(a.Bounds, 24f, 24f)));
                if (area.IntersectsWith(canvas.ClientRectangle)) canvas.Invalidate(area);
            }
        }

        static void Tick()
        {
            long now = Environment.TickCount64;
            List<ProgressAttributes> drop = null;
            bool redraw = false;
            foreach (var pair in active)
            {
                var a = pair.Key;
                if (!(a.Animate && Settings.Animate) || !a.IsListening || a.Owner.OnPingDocument() == null || now - pair.Value > StaleMs)
                    (drop ?? (drop = new List<ProgressAttributes>())).Add(a);
                else redraw = true;
            }
            if (drop != null) foreach (var a in drop) active.Remove(a);
            if (redraw) InvalidateActive();
            StopTimerIfIdle();
        }

        /// <summary>Adds the "Breathing animation" toggle to a component's menu.</summary>
        public static void AppendMenu(ToolStripDropDown menu, GH_Component owner)
        {
            var attributes = owner.Attributes as ProgressAttributes;
            if (attributes == null) return;
            if (ModelDownload.Active && Settings.ModelPreset != "fast")
                GH_DocumentObject.Menu_AppendItem(menu, "Use the fast model (smaller download, next start)", (s, e) =>
                {
                    Settings.Apply(null, "fast", null, null, false);
                    ShowNote(owner, "The fast model will be used the next time you start listening.", NoteKind.Hint, 8);
                });
            GH_DocumentObject.Menu_AppendItem(menu, "Breathing animation", (s, e) =>
            {
                attributes.Animate = !attributes.Animate;
                Grasshopper.Instances.RedrawCanvas();
            }, true, attributes.Animate);
        }

        public static void Write(GH_Component owner, GH_IWriter writer)
        {
            var attributes = owner.Attributes as ProgressAttributes;
            if (attributes != null) writer.SetBoolean("Animate", attributes.Animate);
        }

        public static void Read(GH_Component owner, GH_IReader reader)
        {
            bool animate = true;
            var attributes = owner.Attributes as ProgressAttributes;
            if (attributes != null && reader.TryGetBoolean("Animate", ref animate)) attributes.Animate = animate;
        }
    }
}

namespace Kakadoo
{
    enum NoteKind { Hint, Error }
}
