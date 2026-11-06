using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Kakadoo
{
    /// <summary>
    /// A model as a card: name, an "Active" capsule, a short description, two small bars for accuracy and speed, a tip, the language it
    /// understands, and the buttons that fit its state (Download, Use, Delete). Click an installed card to use it.
    /// </summary>
    sealed class ModelRow : Panel
    {
        static readonly Font TitleFont = new Font("Segoe UI Semibold", 11f);
        static readonly Font DescFont = new Font("Segoe UI", 9f);
        static readonly Font TipFont = new Font("Segoe UI", 8f, FontStyle.Italic);
        static readonly Font SmallFont = new Font("Segoe UI", 8.5f);
        static readonly Font TinyFont = new Font("Segoe UI", 8f);
        static readonly Font CapsuleFont = new Font("Segoe UI Semibold", 8f);

        readonly SettingsWindow window;
        readonly ModelEntry entry;
        readonly Button primary, secondary, source;
        readonly Stopwatch throttle = new Stopwatch();
        CancellationTokenSource cts;
        double fraction;
        long done, total;
        string error;
        bool checking;

        public bool Busy { get; private set; }

        public ModelEntry Entry => entry;

        public ModelRow(SettingsWindow window, ModelEntry entry)
        {
            this.window = window;
            this.entry = entry;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Margin = new Padding(0);

            primary = SettingsWindow.SmallButton("", true);
            primary.Tag = "accent";
            secondary = SettingsWindow.SmallButton("Delete");
            secondary.Tag = "danger";
            source = SettingsWindow.SmallButton("Source page");
            source.Tag = "";
            Controls.Add(primary);
            Controls.Add(secondary);
            Controls.Add(source);
            source.Click += (s, e) => { try { Process.Start(new ProcessStartInfo(entry.Page) { UseShellExecute = true }); } catch (Exception) { } };
            primary.Click += (s, e) => OnPrimary();
            secondary.Click += (s, e) => OnDelete();
            MouseClick += (s, e) => { if (CanActivateByClick) OnPrimary(); };
            UpdateState();
        }

        /// <summary>The colour inside the card: tinted while the model is the one in use. The buttons on it take this colour too.</summary>
        public Color CardColor => entry.Installed && entry.InUse && !Busy ? Theme.CardActive : Theme.Card;

        bool CanActivateByClick => !Busy && !checking && entry.Installed && entry.CanUse && !entry.InUse;

        // ---- Layout ----

        int Pad => Theme.Px(14);
        int FooterHeight => Theme.Px(42);
        bool HasRatings => entry.Accuracy > 0;
        int RatingsWidth => HasRatings ? Theme.Px(190) : 0;

        string Description => error ?? (Busy || checking ? "" : entry.Note + (entry.Kind == ModelKind.Custom && entry.SizeMb > 0 ? "  ·  " + entry.Size : ""));

        /// <summary>Works out the height this card needs at a given width, so no text is cut off.</summary>
        public int MeasureHeight(int width)
        {
            int textWidth = width - 2 * Pad - RatingsWidth;
            int desc = Theme.TextHeight(Description, DescFont, textWidth);
            int tip = Theme.TextHeight(entry.Tip, TipFont, width - 2 * Pad);
            return Pad + Theme.Px(26) + Math.Max(desc, Theme.Px(6)) + (tip > 0 ? Theme.Px(6) + tip : 0) + Theme.Px(10) + FooterHeight;
        }

        public void ApplyHeight()
        {
            int h = MeasureHeight(Width);
            if (h != Height) Height = h;
            PlaceButtons();
            Invalidate();
        }

        void PlaceButtons()
        {
            int x = Width - Pad;
            int y = Height - FooterHeight + (FooterHeight - primary.Height) / 2;
            foreach (var b in new[] { secondary, primary, source })
            {
                if (!b.Visible) continue;
                x -= b.Width;
                b.Left = x;
                b.Top = y;
                x -= Theme.Px(6);
            }
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            PlaceButtons();
        }

        // ---- Painting ----

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Bg);

            bool installed = entry.Installed;
            bool inUse = installed && entry.InUse && !Busy;
            // Flat card with a stripe on the left: yellow while the model is in use.
            using (var fill = new SolidBrush(inUse ? Theme.CardActive : Theme.Card)) g.FillRectangle(fill, 0, 0, Width, Height - 1);
            using (var border = new Pen(Theme.Line)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 2);
            using (var stripe = new SolidBrush(entry.FamilyColor)) g.FillRectangle(stripe, 0, 0, Theme.Px(5), Height - 1);

            int pad = Pad;
            TextRenderer.DrawText(g, entry.Title, TitleFont, new Point(pad, pad), Theme.Ink, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            if (inUse)
            {
                int titleWidth = TextRenderer.MeasureText(g, entry.Title, TitleFont, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                const string label = "IN USE";
                int textWidth = TextRenderer.MeasureText(g, label, CapsuleFont, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding).Width;
                var chip = new Rectangle(pad + titleWidth + Theme.Px(10), pad + Theme.Px(3), textWidth + Theme.Px(14), Theme.Px(18));
                using (var path = Theme.Rounded(chip, Theme.Px(3)))
                using (var brush = new SolidBrush(Theme.Yellow))
                    g.FillPath(brush, path);
                TextRenderer.DrawText(g, label, CapsuleFont, chip, Theme.OnYellow, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            if (HasRatings) DrawRatings(g);

            int y = pad + Theme.Px(26);
            string description = Description;
            int descHeight = Theme.TextHeight(description, DescFont, Width - 2 * pad - RatingsWidth);
            if (description.Length > 0)
                TextRenderer.DrawText(g, description, DescFont, new Rectangle(pad, y, Width - 2 * pad - RatingsWidth, descHeight), error != null ? Theme.Error : Theme.Grey, Theme.Wrap);
            y += Math.Max(descHeight, Theme.Px(6));

            if (!string.IsNullOrEmpty(entry.Tip))
            {
                y += Theme.Px(6);
                int tipHeight = Theme.TextHeight(entry.Tip, TipFont, Width - 2 * pad);
                TextRenderer.DrawText(g, entry.Tip, TipFont, new Rectangle(pad, y, Width - 2 * pad, tipHeight), Theme.Tip, Theme.Wrap);
            }

            int footerTop = Height - FooterHeight;
            using (var line = new Pen(Theme.Line))
                g.DrawLine(line, pad, footerTop, Width - pad, footerTop);

            int mid = footerTop + FooterHeight / 2;
            if (Busy || checking)
            {
                string status = checking ? "Checking that it runs..." : (fraction * 100).ToString("0") + "%  (" + done / 1048576 + (total > 0 ? " / " + total / 1048576 : "") + " MB)";
                TextRenderer.DrawText(g, status, SmallFont, new Point(pad, mid - Theme.Px(14)), Theme.Grey, TextFormatFlags.NoPadding);
                var track = new Rectangle(pad, mid + Theme.Px(3), Math.Max(40, Width - 2 * pad - Theme.Px(160)), Theme.Px(5));
                using (var path = Theme.Rounded(track, track.Height / 2))
                using (var brush = new SolidBrush(Theme.Track))
                    g.FillPath(brush, path);
                if (Busy && fraction > 0)
                {
                    var bar = new Rectangle(track.Left, track.Top, Math.Max(track.Height, (int)(track.Width * fraction)), track.Height);
                    using (var path = Theme.Rounded(bar, bar.Height / 2))
                    using (var brush = new SolidBrush(Theme.Yellow))
                        g.FillPath(brush, path);
                }
            }
            else
            {
                string tag = entry.LanguageTag;
                int tagWidth = TextRenderer.MeasureText(g, tag, SmallFont, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                var chip = new Rectangle(pad, mid - Theme.Px(10), tagWidth + Theme.Px(16), Theme.Px(20));
                using (var path = Theme.Rounded(chip, chip.Height / 2))
                using (var outline = new Pen(Theme.Border))
                    g.DrawPath(outline, path);
                TextRenderer.DrawText(g, tag, SmallFont, chip, Theme.Grey, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }

        /// <summary>Accuracy and speed as five dots each.</summary>
        void DrawRatings(Graphics g)
        {
            int x0 = Width - Pad - Theme.Px(5 * 12);
            DrawDots(g, "accuracy", entry.Accuracy, x0, Pad + Theme.Px(3));
            DrawDots(g, "speed", entry.Speed, x0, Pad + Theme.Px(21));
        }

        static void DrawDots(Graphics g, string label, double rating, int x, int y)
        {
            int d = Theme.Px(8), step = Theme.Px(12);
            TextRenderer.DrawText(g, label, TinyFont, new Rectangle(x - Theme.Px(70), y - Theme.Px(3), Theme.Px(62), Theme.Px(14)), Theme.Grey,
                TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
            int filled = (int)Math.Round(Math.Max(0, Math.Min(5, rating)));
            for (int i = 0; i < 5; i++)
            {
                var dot = new Rectangle(x + i * step, y, d, d);
                if (i < filled) using (var brush = new SolidBrush(Theme.Yellow)) g.FillEllipse(brush, dot);
                else using (var brush = new SolidBrush(Theme.Track)) g.FillEllipse(brush, dot);
            }
        }

        // ---- Actions ----

        void OnPrimary()
        {
            if (Busy) { Cancel(); return; }
            if (checking) return;
            if (entry.Installed) { UseAfterCheck(); return; }
            Start();
        }

        /// <summary>A model is only chosen once it has been shown to run, so a broken file never becomes the setting.</summary>
        void UseAfterCheck()
        {
            error = null;
            checking = true;
            UpdateState();
            Task.Run(() => ModelCheck.Verify(entry.Kind, entry.RunPath)).ContinueWith(t => window.Post(() =>
            {
                checking = false;
                string problem = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result;
                if (problem == null) entry.Use();
                else error = "Cannot use it: " + problem;
                window.RefreshAll();
            }));
        }

        void OnDelete()
        {
            string what = entry.IsExternal ? "Remove " + entry.Title + " from the list? The file stays where it is." : "Delete " + entry.Title + " (" + entry.Size + ") from this PC?";
            if (MessageBox.Show(window, what, "Kakadoo", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { entry.Delete(); error = null; }
            catch (Exception e) { error = "Could not delete: " + e.Message; }
            window.RefreshAll();
        }

        void Start()
        {
            error = null;
            Busy = true;
            fraction = 0; done = total = 0;
            cts = new CancellationTokenSource();
            var token = cts.Token;
            throttle.Restart();
            UpdateState();

            Task.Run(() =>
            {
                try
                {
                    entry.Install((d, t) =>
                    {
                        if (throttle.ElapsedMilliseconds < 120 && d != t) return;
                        throttle.Restart();
                        window.Post(() => { done = d; total = t; fraction = t > 0 ? Math.Min(1.0, (double)d / t) : 0; Invalidate(); });
                    }, token);
                    // A cut-off or damaged download would otherwise only show up at the first command.
                    string problem = ModelCheck.Verify(entry.Kind, entry.RunPath);
                    return problem == null ? null : "Downloaded, but it cannot run: " + problem;
                }
                catch (OperationCanceledException) { return null; }
                catch (Exception e) { return token.IsCancellationRequested ? null : "Download failed: " + e.Message; }
            }).ContinueWith(t => window.Post(() =>
            {
                Busy = false;
                error = t.Result;
                window.RefreshAll();
            }));
        }

        public void Cancel() => cts?.Cancel();

        public void UpdateState()
        {
            bool installed = entry.Installed;
            bool inUse = installed && entry.InUse;
            BackColor = CardColor;

            if (Busy)
            {
                primary.Text = "Cancel";
                primary.Tag = "link";
                primary.Visible = true;
                secondary.Visible = false;
            }
            else if (checking)
            {
                primary.Visible = false;
                secondary.Visible = false;
            }
            else
            {
                primary.Tag = "accent";
                primary.Text = installed ? "Use" : "Download  " + entry.Size;
                primary.Visible = !installed || (entry.CanUse && !inUse);
                secondary.Text = entry.IsExternal ? "Remove" : "Delete";
                secondary.Visible = installed && entry.CanDelete;
            }
            source.Visible = entry.Page != null && !Busy && !checking;
            Cursor = CanActivateByClick ? Cursors.Hand : Cursors.Default;
            Theme.Apply(primary);
            Theme.Apply(secondary);
            Theme.Apply(source);
            ApplyHeight();
        }
    }

    /// <summary>A coloured heading above the cards of one model family.</summary>
    sealed class FamilyHeader : Control
    {
        readonly string family;
        int count;

        public FamilyHeader(string family)
        {
            this.family = family;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = Theme.Px(30);
        }

        public void SetCount(int n) { if (n != count) { count = n; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            var color = ModelCatalog.ColorOf(family);
            int dot = Theme.Px(12);
            using (var brush = new SolidBrush(color)) g.FillEllipse(brush, 0, (Height - dot) / 2, dot, dot);
            var bold = new Font("Segoe UI Semibold", 10f);
            var small = new Font("Segoe UI", 8.5f);
            string title = family + "  (" + count + ")";
            TextRenderer.DrawText(g, title, bold, new Point(dot + Theme.Px(8), Theme.Px(5)), Theme.Ink, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            int w = TextRenderer.MeasureText(g, title, bold, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            TextRenderer.DrawText(g, ModelCatalog.FamilyNote(family), small, new Point(dot + Theme.Px(18) + w, Theme.Px(7)), Theme.Grey, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            bold.Dispose();
            small.Dispose();
        }
    }

    /// <summary>A vertical stack of model cards that lays itself out to the width it is given.</summary>
    sealed class CardHost : Panel
    {
        readonly List<ModelRow> cards = new List<ModelRow>();
        readonly Dictionary<string, FamilyHeader> headers = new Dictionary<string, FamilyHeader>();
        readonly bool grouped;
        readonly Label empty = new Label { AutoSize = false, Tag = "muted", Font = new Font("Segoe UI", 9f) };
        bool laying;

        public void SetEmptyText(string text) { if (empty.Text != text) { empty.Text = text; Invalidate(true); } }

        public CardHost(string emptyText, bool grouped = true)
        {
            this.grouped = grouped;
            Margin = new Padding(0, 0, 0, Theme.Px(8));
            empty.Text = emptyText;
            Height = Theme.Px(30);
        }

        public void SetCards(IList<ModelRow> wanted)
        {
            if (!cards.SequenceEqual(wanted))
            {
                SuspendLayout();
                Controls.Clear();
                cards.Clear();
                cards.AddRange(wanted);
                foreach (var h in headers.Values) h.Visible = false;
                foreach (var card in cards)
                {
                    if (grouped && !headers.ContainsKey(card.Entry.Family)) headers[card.Entry.Family] = new FamilyHeader(card.Entry.Family);
                    Controls.Add(card);
                }
                foreach (var h in headers.Values) Controls.Add(h);
                if (cards.Count == 0) Controls.Add(empty);
                ResumeLayout(false);
            }
            LayoutCards();
        }

        public void LayoutCards()
        {
            if (laying || Width < 60) return;
            laying = true;
            try
            {
                int y = 0;
                string family = null;
                foreach (var card in cards)
                {
                    if (grouped && card.Entry.Family != family)
                    {
                        family = card.Entry.Family;
                        var header = headers[family];
                        header.SetCount(cards.Count(c => c.Entry.Family == family));
                        header.SetBounds(0, y, Width, header.Height);
                        header.Visible = true;
                        y += header.Height + Theme.Px(4);
                    }
                    card.SetBounds(0, y, Width, card.Height);
                    card.ApplyHeight();
                    y += card.Height + Theme.Px(10);
                }
                if (cards.Count == 0) { empty.SetBounds(0, Theme.Px(4), Width, Theme.Px(24)); y = Theme.Px(30); }
                Height = Math.Max(Theme.Px(8), y - (cards.Count > 0 ? Theme.Px(10) : 0));
            }
            finally { laying = false; }
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            LayoutCards();
        }
    }
}
