using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Kakadoo
{
    /// <summary>Light and dark colours for the settings window, and the code that applies them. Controls ask for their colours when they paint.</summary>
    static class Theme
    {
        public static readonly Color Yellow = Color.FromArgb(254, 202, 18); // the Kakadoo yellow
        public static readonly Color OnYellow = Color.FromArgb(40, 40, 40);

        static Color C(int r, int g, int b) => Color.FromArgb(r, g, b);

        public static bool Dark => Settings.Dark;
        public static Color Bg => Dark ? C(30, 30, 33) : Color.White;
        public static Color Side => Dark ? C(22, 22, 25) : C(246, 246, 246);
        public static Color Card => Dark ? C(42, 42, 46) : Color.White;
        public static Color CardActive => Dark ? C(64, 56, 28) : C(255, 248, 222);
        public static Color Line => Dark ? C(66, 66, 72) : C(225, 225, 225);
        public static Color Ink => Dark ? C(236, 236, 238) : C(40, 40, 40);
        public static Color Grey => Dark ? C(165, 165, 172) : C(110, 110, 110);
        public static Color Tip => Dark ? C(130, 172, 216) : C(60, 100, 150);
        public static Color Error => Dark ? C(255, 122, 122) : C(190, 30, 30);
        public static Color Warn => Dark ? C(255, 178, 84) : C(190, 90, 0);
        public static Color Ok => Dark ? C(122, 207, 142) : C(40, 120, 60);
        public static Color Link => Dark ? C(122, 177, 236) : C(30, 100, 180);
        public static Color Input => Dark ? C(54, 54, 59) : Color.White;
        public static Color Border => Dark ? C(92, 92, 99) : C(190, 190, 190);
        public static Color Track => Dark ? C(72, 72, 78) : C(228, 228, 228);
        public static Color Banner => Dark ? C(72, 62, 30) : C(255, 246, 214);

        static float dpi;

        /// <summary>Scales a size in pixels to this screen's scaling (125%, 150%...), so text and boxes keep their proportions.</summary>
        public static int Px(int value)
        {
            if (dpi == 0)
            {
                try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX; }
                catch (Exception) { dpi = 96; }
                if (dpi < 96) dpi = 96;
            }
            return (int)Math.Round(value * dpi / 96.0);
        }

        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = Math.Max(2, radius * 2);
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top;

        /// <summary>The height needed to show a text in a given width, so nothing is cut off.</summary>
        public static int TextHeight(string text, Font font, int width) =>
            string.IsNullOrEmpty(text) ? 0 : TextRenderer.MeasureText(text, font, new Size(Math.Max(20, width - Px(10)), 0), Wrap).Height + 2;

        // ---- Applying the colours ----

        /// <summary>Colours a control and everything inside it for the current theme. Roles come from Control.Tag: "muted", "tip", "error", "warn", "ok", "accent", "link", "danger", "wrap".</summary>
        public static void Apply(Control control)
        {
            string tag = control.Tag as string ?? "";
            switch (control)
            {
                case Form form:
                    form.BackColor = Bg; form.ForeColor = Ink;
                    break;
                case Label label:
                    label.ForeColor = tag.Contains("error") ? Error : tag.Contains("warn") ? Warn : tag.Contains("ok") ? Ok : tag.Contains("muted") ? Grey : tag.Contains("tip") ? Tip : Ink;
                    break;
                case Button button:
                    StyleButton(button, tag);
                    break;
                case ComboBox combo:
                    combo.FlatStyle = FlatStyle.Flat; combo.BackColor = Input; combo.ForeColor = Ink;
                    break;
                case TextBox box:
                    box.BackColor = Input; box.ForeColor = Ink;
                    break;
                case NumericUpDown number:
                    number.BackColor = Input; number.ForeColor = Ink;
                    break;
                case CheckBox check:
                    check.ForeColor = Ink;
                    break;
                case TrackBar track:
                    track.BackColor = control.Parent?.BackColor ?? Bg;
                    break;
                case ModelRow row:
                    row.BackColor = row.CardColor;
                    row.Invalidate();
                    break;
                case ToggleSwitch _:
                    control.Invalidate();
                    break;
                case Panel panel:
                    panel.BackColor = panel.Name == "side" ? Side : panel.Name == "banner" ? Banner : Bg;
                    break;
            }
            if (control is FlowLayoutPanel || control is TextBox || control is ComboBox || control is NumericUpDown) NativeTheme(control);
            foreach (Control child in control.Controls) Apply(child);
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        static readonly System.Collections.Generic.HashSet<Control> Hooked = new System.Collections.Generic.HashSet<Control>();

        /// <summary>Gives the scroll bars and drop-down lists of a control the dark look (the Windows "DarkMode_Explorer" theme), or back the light one.</summary>
        static void NativeTheme(Control c)
        {
            void Set() { try { SetWindowTheme(c.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null); } catch (Exception) { } }
            if (c.IsHandleCreated) { Set(); return; }
            if (Hooked.Add(c)) c.HandleCreated += (s, e) => Set();
        }

        static void StyleButton(Button b, string tag)
        {
            b.FlatStyle = FlatStyle.Flat;
            if (tag.Contains("accent"))
            {
                b.BackColor = Yellow; b.ForeColor = OnYellow; b.FlatAppearance.BorderColor = Yellow; b.FlatAppearance.BorderSize = 1;
            }
            else if (tag.Contains("link") || tag.Contains("danger"))
            {
                b.BackColor = b.Parent?.BackColor ?? Bg;
                b.ForeColor = tag.Contains("danger") ? Error : Link;
                b.FlatAppearance.BorderSize = 0;
            }
            else if (!tag.Contains("tab"))
            {
                b.BackColor = Input; b.ForeColor = Ink; b.FlatAppearance.BorderColor = Border; b.FlatAppearance.BorderSize = 1;
            }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Makes the window's title bar dark too (Windows 10 version 2004 and later; harmless elsewhere).</summary>
        public static void TitleBar(IntPtr handle, bool dark)
        {
            try
            {
                int on = dark ? 1 : 0;
                if (DwmSetWindowAttribute(handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(handle, 19, ref on, 4);
            }
            catch (Exception) { }
        }
    }

    /// <summary>An on/off switch in the style of phone settings.</summary>
    sealed class ToggleSwitch : Control
    {
        bool isChecked;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(Theme.Px(42), Theme.Px(22));
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
        }

        public bool Checked
        {
            get => isChecked;
            set { if (isChecked == value) return; isChecked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
        }

        public event EventHandler CheckedChanged;

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); }

        protected override void OnClick(EventArgs e) { base.OnClick(e); if (Enabled) Checked = !Checked; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Parent != null) g.Clear(Parent.BackColor);
            var track = new Rectangle(0, 0, Width - 1, Height - 1);
            Color on = Enabled ? Theme.Yellow : Color.FromArgb(120, Theme.Yellow);
            using (var path = Theme.Rounded(track, Height / 2))
            using (var brush = new SolidBrush(isChecked ? on : Theme.Track))
                g.FillPath(brush, path);
            int knob = Height - Theme.Px(6);
            int x = isChecked ? Width - knob - Theme.Px(3) - 1 : Theme.Px(3);
            using (var brush = new SolidBrush(Enabled ? Color.White : Color.FromArgb(200, 200, 200)))
                g.FillEllipse(brush, x, (Height - knob) / 2, knob, knob);
        }
    }

    /// <summary>One setting: a title, a description that wraps (so it is never cut off), and a control on the right or underneath.</summary>
    sealed class SettingRow : Panel
    {
        static readonly Font TitleFont = new Font("Segoe UI Semibold", 9.5f);
        static readonly Font DescFont = new Font("Segoe UI", 8.5f);

        readonly Label title, desc;
        readonly Control[] right;
        readonly Control below;

        public SettingRow(string titleText, string description, Control[] rightControls = null, Control belowControl = null)
        {
            Margin = new Padding(0, 0, 0, Theme.Px(6));
            title = new Label { Text = titleText, Font = TitleFont, AutoSize = false, AutoEllipsis = true };
            desc = new Label { Text = description, Font = DescFont, AutoSize = false, Tag = "muted" };
            right = rightControls ?? new Control[0];
            below = belowControl;
            Controls.Add(title);
            Controls.Add(desc);
            foreach (var c in right)
            {
                Controls.Add(c);
                c.SizeChanged += (s, e) => Relayout();
                c.VisibleChanged += (s, e) => Relayout();
            }
            if (below != null) Controls.Add(below);
            // text goes behind the controls, so it can never hide one
            title.SendToBack();
            desc.SendToBack();
            Relayout();
        }

        public string Description { get => desc.Text; set { desc.Text = value; Relayout(); } }

        public Label DescriptionLabel => desc;

        protected override void OnResize(EventArgs eventargs) { base.OnResize(eventargs); Relayout(); }

        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); Relayout(); }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Relayout(); }

        bool busy;

        void Relayout()
        {
            if (busy || Width < 50) return;
            busy = true;
            try
            {
                int gap = Theme.Px(8);
                int rightWidth = right.Where(c => c.Visible).Sum(c => c.Width + gap);
                bool stacked = Width - rightWidth - Theme.Px(12) < Theme.Px(260);
                int textWidth = stacked ? Width : Width - rightWidth - Theme.Px(12);
                int titleHeight = Theme.Px(22);
                int descHeight = Theme.TextHeight(desc.Text, DescFont, textWidth);

                title.SetBounds(0, 0, textWidth, titleHeight);
                desc.SetBounds(0, titleHeight, textWidth, descHeight);
                int block = titleHeight + descHeight;

                int x = stacked ? 0 : Width;
                int tallest = 0;
                foreach (var c in stacked ? right : right.Reverse())
                {
                    if (!c.Visible) continue;
                    if (stacked) { c.Left = x; c.Top = block + Theme.Px(6); x += c.Width + gap; }
                    else { x -= c.Width; c.Left = x; c.Top = Math.Max(0, (block - c.Height) / 2); x -= gap; }
                    tallest = Math.Max(tallest, c.Top + c.Height);
                }

                int y = Math.Max(block, tallest) + Theme.Px(6);
                if (below != null)
                {
                    below.SetBounds(0, y, Width, below.Height);
                    y += below.Height + Theme.Px(4);
                }
                Height = y + Theme.Px(8);
            }
            finally { busy = false; }
        }

        /// <summary>Call after a control on the right was shown, hidden or resized.</summary>
        public void Refit() => Relayout();
    }
}
