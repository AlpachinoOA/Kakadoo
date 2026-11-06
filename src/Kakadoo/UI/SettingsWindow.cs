using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Kakadoo
{
    /// <summary>
    /// The Kakadoo settings window: General (microphone, language, sounds, custom words, history, device), Models (cards with accuracy and
    /// speed, download, choose, delete, and a check that the model really runs), Checker and About. Everything applies and is saved right away.
    /// Plain WinForms; it costs nothing while closed.
    /// </summary>
    sealed class SettingsWindow : Form
    {
        const string SupportEmail = "atakanalpokan@gmail.com";

        static readonly Font Normal = new Font("Segoe UI", 9.5f);
        static readonly Font Small = new Font("Segoe UI", 8.5f);
        static readonly Font Bold = new Font("Segoe UI Semibold", 9.5f);
        static readonly Font Heading = new Font("Segoe UI Semibold", 14f);
        static readonly Font Section = new Font("Segoe UI Semibold", 11f);

        static readonly string[] PresetValues = { "standard", "quiet", "noisy", "fast", "accurate" };
        static readonly string[] PresetNames =
        {
            "Standard", "Quiet room (more sensitive)", "Noisy workshop (needs a louder voice)",
            "Fast (quick replies, smaller model)", "Accurate (waits longer, bigger model)"
        };

        static SettingsWindow current;

        readonly List<Button> tabs = new List<Button>();
        readonly List<FlowLayoutPanel> pages = new List<FlowLayoutPanel>();
        readonly List<ModelRow> rows = new List<ModelRow>();
        readonly List<ModelRow> customRows = new List<ModelRow>();
        readonly List<Action> syncs = new List<Action>();
        readonly Action onSettingsChanged;
        readonly Panel content = new Panel { Dock = DockStyle.Fill };
        readonly System.Windows.Forms.Timer wordsTimer = new System.Windows.Forms.Timer { Interval = 700 };

        readonly List<string> engineKeys = new List<string>();
        ComboBox accBox, speedBox, famBox, engineBox, micBox, presetBox, langBox, outputBox, filterBox, llamaDeviceBox;
        ToggleSwitch muteSwitch, feedbackSwitch;
        TrackBar volumeBar;
        Label volumeLabel;
        Button refreshButton, testButton;
        TextBox wordsBox;
        SettingRow langRow, llamaDeviceRow;
        Panel dupBanner;
        CardHost downloadedHost, availableHost, customHost;
        Label dupLabel;
        List<DuplicateGroup> duplicates = new List<DuplicateGroup>();
        string customSignature;
        int selectedTab;
        bool loading = true, scanning, scanAgain;

        // the Checker tab
        Button runChecks;
        Label checkStatus;
        Panel checkResults;
        readonly List<Label> checkLabels = new List<Label>();
        bool checking;

        IEnumerable<ModelRow> AllRows => rows.Concat(customRows);

        public static void ShowWindow()
        {
            if (current != null && !current.IsDisposed)
            {
                if (current.WindowState == FormWindowState.Minimized) current.WindowState = FormWindowState.Normal;
                current.Activate();
                return;
            }
            try
            {
                current = new SettingsWindow();
                current.Show(Grasshopper.Instances.DocumentEditor);
            }
            catch (Exception e)
            {
                current = null;
                MessageBox.Show("The Kakadoo settings window could not open: " + e.Message, "Kakadoo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        SettingsWindow()
        {
            Text = "Kakadoo Settings";
            ClientSize = new Size(Theme.Px(900), Theme.Px(660));
            MinimumSize = new Size(Theme.Px(700), Theme.Px(480));
            StartPosition = FormStartPosition.CenterParent;
            Font = Normal;
            ShowInTaskbar = false;
            AllowDrop = true;
            DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += async (s, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped && dropped.Length > 0) { SelectPage(1); await GuardAsync(() => AddOwnModel(dropped[0])); }
            };

            var logo = Icons.Load("Kakadoo");
            if (logo != null) { using (var big = new Bitmap(logo, 32, 32)) Icon = Icon.FromHandle(big.GetHicon()); }

            // A left sidebar: logo and name on top, the tabs as pills (the current one filled yellow), dark mode at the bottom.
            var bar = new Panel { Name = "side", Dock = DockStyle.Left, Width = Theme.Px(190) };
            if (logo != null) bar.Controls.Add(new PictureBox { Image = logo, SizeMode = PictureBoxSizeMode.Zoom, Left = Theme.Px(18), Top = Theme.Px(18), Width = Theme.Px(30), Height = Theme.Px(30) });
            bar.Controls.Add(new Label { Text = "Kakadoo", Font = Heading, AutoSize = false, Left = Theme.Px(54), Top = Theme.Px(16), Width = Theme.Px(130), Height = Theme.Px(34) });
            Controls.Add(content);
            Controls.Add(bar);

            string[] names = { "General", "Models", "Checker", "About" };
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                var tab = new Button
                {
                    Text = names[i], Left = Theme.Px(12), Top = Theme.Px(74) + i * Theme.Px(44), Width = bar.Width - Theme.Px(24), Height = Theme.Px(38),
                    FlatStyle = FlatStyle.Flat, Tag = "tab", Cursor = Cursors.Hand, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Theme.Px(14), 0, 0, 0)
                };
                tab.FlatAppearance.BorderSize = 0;
                tab.Click += (s, e) => SelectPage(index);
                tabs.Add(tab);
                bar.Controls.Add(tab);
            }

            var darkSwitch = new ToggleSwitch { Checked = Settings.Dark, Left = Theme.Px(18), Top = bar.Height - Theme.Px(48), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            var darkLabel = new Label { Text = "Dark mode", AutoSize = false, Left = darkSwitch.Right + Theme.Px(10), Width = Theme.Px(96), Height = darkSwitch.Height, Top = darkSwitch.Top, Font = Normal, Anchor = AnchorStyles.Bottom | AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft };
            darkSwitch.CheckedChanged += (s, e) => { Settings.Dark = darkSwitch.Checked; ApplyTheme(); };
            bar.Controls.Add(darkLabel);
            bar.Controls.Add(darkSwitch);

            // A model chosen elsewhere (a panel on the canvas) shows up here, and the other way round.
            onSettingsChanged = () => Post(RefreshAll);
            Settings.Changed += onSettingsChanged;

            pages.Add(BuildGeneral());
            pages.Add(BuildModels());
            pages.Add(BuildChecker());
            pages.Add(BuildAbout());
            wordsTimer.Tick += (s, e) => { wordsTimer.Stop(); if (wordsBox != null) CustomWords.WriteText(wordsBox.Text); };

            SelectPage(0);
            ApplyTheme();
            loading = false;

            FormClosing += (s, e) =>
            {
                if (AllRows.Any(r => r.Busy))
                {
                    if (MessageBox.Show(this, "A download is still running. Cancel it and close?", "Kakadoo", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    { e.Cancel = true; return; }
                    foreach (var r in AllRows) r.Cancel();
                }
                if (wordsTimer.Enabled) { wordsTimer.Stop(); CustomWords.WriteText(wordsBox.Text); }
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.TitleBar(Handle, Settings.Dark);
        }

        void ApplyTheme()
        {
            Theme.Apply(this);
            Theme.TitleBar(Handle, Settings.Dark);
            UpdateTabs();
            Invalidate(true);
        }

        void SelectPage(int index)
        {
            selectedTab = index;
            for (int i = 0; i < pages.Count; i++) pages[i].Visible = i == index;
            UpdateTabs();
            if (index == 1) RefreshAll();
        }

        void UpdateTabs()
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                bool on = i == selectedTab;
                tabs[i].BackColor = on ? Theme.Yellow : Theme.Side;
                tabs[i].ForeColor = on ? Color.FromArgb(30, 30, 30) : Theme.Grey;
                tabs[i].FlatAppearance.MouseOverBackColor = on ? Theme.Yellow : Theme.Border;
                tabs[i].Font = i == selectedTab ? Bold : Normal;
                tabs[i].Invalidate();
            }
        }

        // ---- Page building blocks ----

        FlowLayoutPanel NewPage()
        {
            var page = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, Padding = new Padding(Theme.Px(26), Theme.Px(18), Theme.Px(26), Theme.Px(18)), Visible = false
            };
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(page, true);
            page.Resize += (s, e) => Fit(page);
            content.Controls.Add(page);
            return page;
        }

        /// <summary>Gives every item the page width, and every wrapping label the height its text needs, so nothing is cut off.</summary>
        static void Fit(FlowLayoutPanel page)
        {
            int width = Math.Max(Theme.Px(260), page.ClientSize.Width - page.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
            foreach (Control c in page.Controls)
            {
                if (c.Dock != DockStyle.None) continue;
                c.Width = width;
                if (c is Label label && label.Tag is string tag && tag.Contains("wrap"))
                    label.Height = Theme.TextHeight(label.Text, label.Font, width) + Theme.Px(2);
            }
        }

        static Label Text_(string text, Font font, string tag = "") =>
            new Label { Text = text, Font = font, AutoSize = false, Tag = tag + " wrap", Margin = new Padding(0, 0, 0, Theme.Px(2)), Height = Theme.Px(22) };

        void AddHeading(FlowLayoutPanel page, string text, string caption = null)
        {
            page.Controls.Add(Text_(text, Heading));
            if (caption != null) page.Controls.Add(Text_(caption, Normal, "muted"));
        }

        void AddSection(FlowLayoutPanel page, string text, string caption = null)
        {
            var label = Text_(text, Section);
            label.Margin = new Padding(0, Theme.Px(18), 0, Theme.Px(4));
            page.Controls.Add(label);
            if (caption != null) page.Controls.Add(Text_(caption, Small, "muted"));
        }

        static ComboBox NewCombo(int width, params string[] items)
        {
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Theme.Px(width) };
            box.Items.AddRange(items);
            return box;
        }

        SettingRow Toggle(string title, string description, bool value, Action<bool> set, out ToggleSwitch toggle)
        {
            var sw = new ToggleSwitch { Checked = value };
            sw.CheckedChanged += (s, e) => { if (!loading) set(sw.Checked); };
            toggle = sw;
            return new SettingRow(title, description, new Control[] { sw });
        }

        // ---- General ----

        FlowLayoutPanel BuildGeneral()
        {
            var page = NewPage();
            AddHeading(page, "General");

            micBox = NewCombo(280, "System default");
            string[] devices;
            try { devices = Microphone.Devices(); } catch (Exception) { devices = new string[0]; }
            micBox.Items.AddRange(devices);
            string wanted = Settings.Microphone;
            int pick = string.IsNullOrEmpty(wanted) ? -1 : Array.FindIndex(devices, d => wanted.StartsWith(d, StringComparison.OrdinalIgnoreCase) || d.StartsWith(wanted, StringComparison.OrdinalIgnoreCase));
            micBox.SelectedIndex = pick + 1;
            micBox.SelectedIndexChanged += (s, e) => SaveGeneral();
            page.Controls.Add(new SettingRow("Microphone", "The microphone Kakadoo listens with.", new Control[] { micBox }));

            presetBox = NewCombo(280, PresetNames);
            presetBox.SelectedIndex = Math.Max(0, Array.IndexOf(PresetValues, Settings.Preset));
            presetBox.SelectedIndexChanged += (s, e) => SaveGeneral();
            page.Controls.Add(new SettingRow("Listening preset", "How sensitive the microphone is and how long a pause ends a phrase.", new Control[] { presetBox }));

            page.Controls.Add(Toggle("Breathing animation", "Show the ring that breathes on a component while it listens. It applies to every Kakadoo component.", Settings.Animate,
                v => Settings.Apply(null, null, v, null, true), out _));

            AddSection(page, "Sound");
            page.Controls.Add(Toggle("Mute While Recording",
                "When enabled, Kakadoo mutes your system audio while recording. This means music or whatever else is playing will temporarily be silenced.",
                Settings.MuteWhileRecording, v => Settings.MuteWhileRecording = v, out muteSwitch));
            page.Controls.Add(Toggle("Audio Feedback",
                "When enabled, Kakadoo plays a sound when recording starts and stops. This gives you a clear audio cue that Kakadoo is listening.",
                Settings.AudioFeedback, v => { Settings.AudioFeedback = v; UpdateFeedbackEnabled(); }, out feedbackSwitch));

            outputBox = NewCombo(230);
            FillOutputDevices();
            outputBox.SelectedIndexChanged += (s, e) =>
            {
                if (!loading) Settings.FeedbackDevice = outputBox.SelectedIndex <= 0 ? "" : (string)outputBox.SelectedItem;
            };
            refreshButton = SmallButton("Refresh");
            refreshButton.Click += (s, e) => { loading = true; FillOutputDevices(); loading = false; };
            testButton = SmallButton("Test");
            testButton.Click += (s, e) => { if (SpeechListeners.IsWindows) AudioFeedback.Play(true, false); };
            page.Controls.Add(new SettingRow("Output Device",
                "Choose which audio device plays the feedback sounds. Only available when Audio Feedback is enabled. Use the refresh button to rescan for devices.",
                new Control[] { outputBox, refreshButton, testButton }));

            volumeBar = new TrackBar { Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, Width = Theme.Px(170), Value = Settings.FeedbackVolume, AutoSize = false, Height = Theme.Px(30) };
            volumeLabel = new Label { Text = Settings.FeedbackVolume + "%", AutoSize = false, Width = Theme.Px(40), Height = Theme.Px(22), TextAlign = ContentAlignment.MiddleLeft, Tag = "muted" };
            volumeBar.ValueChanged += (s, e) => volumeLabel.Text = volumeBar.Value + "%";
            volumeBar.MouseUp += (s, e) => Settings.FeedbackVolume = volumeBar.Value;
            volumeBar.KeyUp += (s, e) => Settings.FeedbackVolume = volumeBar.Value;
            page.Controls.Add(new SettingRow("Volume",
                "Adjust the volume of the audio feedback sounds. Only available when Audio Feedback is enabled.",
                new Control[] { volumeBar, volumeLabel }));
            UpdateFeedbackEnabled();

            AddSection(page, "Text");
            wordsBox = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Height = Theme.Px(130), BorderStyle = BorderStyle.FixedSingle, Font = Normal };
            wordsBox.Text = CustomWords.ReadText().Replace("\r\n", "\n").Replace("\n", "\r\n");
            wordsBox.TextChanged += (s, e) => { if (!loading) { wordsTimer.Stop(); wordsTimer.Start(); } };
            page.Controls.Add(new SettingRow("Custom Words",
                "Add words that are frequently misheard during transcription. Kakadoo will automatically correct them to the spelling you give, and also gives those spellings to Whisper as hints. "
                + "This is useful for names, jargon or technical terms the model gets wrong. It is a plain replacement and is imperfect.\r\n"
                + "One per line, as: misheard -> corrected. Examples:   cacadu -> Kakadoo    astro js -> Astro.js    c j -> CJ",
                null, wordsBox));
            page.Controls.Add(Toggle("Append Trailing Space",
                "When enabled, Kakadoo adds a space after the text that Kakadoo Simple and Always-On output. Convenient for continuous dictation where you want a space between consecutive texts.",
                Settings.AppendTrailingSpace, v => Settings.AppendTrailingSpace = v, out _));

            AddSection(page, "Components");
            var history = new NumericUpDown { Minimum = 1, Maximum = 100, Value = Settings.HistoryLimit, Width = Theme.Px(70), BorderStyle = BorderStyle.FixedSingle };
            history.ValueChanged += (s, e) => { if (!loading) Settings.HistoryLimit = (int)history.Value; };
            page.Controls.Add(new SettingRow("Keep last runs",
                "How many runs every Kakadoo component keeps in its Output. A component can still choose its own number in its right-click menu.",
                new Control[] { history }));

            llamaDeviceBox = NewCombo(150, "CPU", "GPU");
            llamaDeviceBox.SelectedIndex = Settings.Device == "gpu" ? 1 : 0;
            llamaDeviceBox.SelectedIndexChanged += (s, e) => { if (!loading) { Settings.Device = llamaDeviceBox.SelectedIndex == 1 ? "gpu" : "cpu"; UpdateDeviceNote(); } };
            llamaDeviceRow = new SettingRow("Kakadoo Llama device", "", new Control[] { llamaDeviceBox });
            page.Controls.Add(llamaDeviceRow);

            syncs.Add(UpdateLanguageNote);
            syncs.Add(UpdateDeviceNote);
            UpdateLanguageNote();
            UpdateDeviceNote();
            return page;
        }

        void FillOutputDevices()
        {
            outputBox.Items.Clear();
            outputBox.Items.Add("System default");
            if (SpeechListeners.IsWindows) outputBox.Items.AddRange(AudioFeedback.OutputDevices());
            string wanted = Settings.FeedbackDevice;
            int index = string.IsNullOrEmpty(wanted) ? 0 : outputBox.Items.IndexOf(wanted);
            outputBox.SelectedIndex = Math.Max(0, index);
        }

        void UpdateFeedbackEnabled()
        {
            bool on = Settings.AudioFeedback && SpeechListeners.IsWindows;
            foreach (Control c in new Control[] { outputBox, refreshButton, testButton, volumeBar, volumeLabel }) if (c != null) c.Enabled = on;
        }

        void SyncEngine()
        {
            bool was = loading;
            loading = true;
            engineKeys.Clear();
            var names = new List<string>();
            void Add(string key, string name) { engineKeys.Add(key); names.Add(name); }
            Add("whisper", "Whisper");
            Add("parakeet", "Parakeet (NVIDIA)");
            Add("vosk", "Vosk (light, offline)");
            if (SpeechListeners.IsWindows) Add("system", "Windows speech recognition");
            if (!string.IsNullOrWhiteSpace(Settings.CustomModelPath)) Add("custom", "Custom model");
            engineBox.Items.Clear();
            foreach (var n in names) engineBox.Items.Add(n);
            engineBox.SelectedIndex = Math.Max(0, engineKeys.IndexOf(Settings.Engine));
            loading = was;
            UpdateLanguageNote();
        }

        static string WindowsSpeechNote()
        {
            string installed = "";
            try
            {
                var cultures = System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers().Select(r => r.Culture.Name).Distinct().ToList();
                installed = cultures.Count == 0 ? " None is installed yet." : " Installed: " + string.Join(", ", cultures) + ".";
            }
            catch (Exception) { }
            return "Built into Windows: works offline with nothing to download, but is less accurate. It also listens when Whisper cannot start." + installed
                + " To add a language: Windows settings > Time & language > Language & region, add the language and keep its speech option ticked.";
        }

        void SaveGeneral()
        {
            if (loading) return;
            string mic = micBox.SelectedIndex <= 0 ? "" : (string)micBox.SelectedItem;
            Settings.Apply(mic, null, null, PresetValues[Math.Max(0, presetBox.SelectedIndex)], true);
            Settings.Language = Languages.All[Math.Max(0, langBox.SelectedIndex)][0];
        }

        /// <summary>Explains what the language affects, or warns when the chosen model cannot do it.</summary>
        void UpdateLanguageNote()
        {
            if (langRow == null) return;
            string code = Settings.Language;
            string problem = null;

            string whisper = ModelSync.Get(ModelKind.Whisper);
            if (code != "en" && code != "auto" && whisper.Length > 0 && WhisperModel.IsEnglishOnly(whisper))
                problem = "The chosen Whisper model is English only. Choose a multilingual one (without .en) on the Models page.";

            string parakeet = ModelSync.Get(ModelKind.Parakeet);
            var entry = ModelCatalog.Of(ModelKind.Parakeet).FirstOrDefault(e => ModelSync.SamePath(e.FolderPath, parakeet));
            if (problem == null && entry != null && ((entry.EnglishOnly && code != "en" && code != "auto") || (!entry.EnglishOnly && !Languages.ParakeetV3Supports(code))))
                problem = "The chosen Parakeet model does not cover " + Languages.Name(code) + ". Use Parakeet v3 (European languages) or a Whisper multilingual model.";

            if (problem == null && Settings.Engine == "system" && SpeechListeners.IsWindows) problem = SystemSpeechListener.LanguageProblem();
            if (problem == null && Settings.Engine == "vosk" && ModelSync.Get(ModelKind.Vosk).Length == 0)
                problem = "Vosk is the chosen engine but no Vosk model is installed. Download one below.";

            langRow.DescriptionLabel.Tag = problem == null ? "muted" : "warn";
            langRow.DescriptionLabel.ForeColor = problem == null ? Theme.Grey : Theme.Warn;
            langRow.Description = problem ?? "Used by Whisper, Parakeet and Custom models. With the Automatic model, other languages download a multilingual Whisper model. "
                + "Vosk uses the language of its own model. Spoken number words (\"twenty\") are English only; digits work in every language.";
        }

        void UpdateDeviceNote()
        {
            if (llamaDeviceRow == null) return;
            bool gpu = LlamaNative.GpuAvailable;
            llamaDeviceRow.Description = "Where Kakadoo Llama runs its language model. A change applies to the next command, without restarting Rhino. Whisper, Parakeet and Vosk run on the processor: the speech engines bundled with Kakadoo have no GPU version. A Device input connected to the component still wins. "
                + (gpu ? "A graphics card was found." : "No usable graphics card was found, so GPU falls back to the CPU.");
        }

        // ---- Models ----

        FlowLayoutPanel BuildModels()
        {
            var page = NewPage();
            AddHeading(page, "Models", "Select a model or download more. Models differ in accuracy and speed; click a downloaded card to use it. Every model is checked to run before it is used.");

            AddSection(page, "Speech", "How Kakadoo hears you. This applies to every Kakadoo component that listens.");
            engineBox = NewCombo(280, "Whisper");
            engineBox.SelectedIndexChanged += (s, e) =>
            {
                if (loading || engineBox.SelectedIndex < 0 || engineBox.SelectedIndex >= engineKeys.Count) return;
                string pick = engineKeys[engineBox.SelectedIndex];
                if (pick == "parakeet" && string.IsNullOrWhiteSpace(Settings.ParakeetModelName) && string.IsNullOrWhiteSpace(Settings.ParakeetModelPath))
                    MessageBox.Show(this, "No Parakeet model is chosen yet. Download one below and press Use.", "Kakadoo");
                else if (pick == "vosk" && ModelSync.Get(ModelKind.Vosk).Length == 0)
                    MessageBox.Show(this, "No Vosk model is installed yet. Download one below and press Use.", "Kakadoo");
                else if (pick == "system" && SystemSpeechListener.LanguageProblem() is string problem)
                    MessageBox.Show(this, problem, "Kakadoo");
                else Settings.Engine = pick;
                SyncEngine();
            };
            page.Controls.Add(new SettingRow("Speech engine",
                "Changing this changes the engine of these components: Kakadoo, Kakadoo Simple, Kakadoo Always-On, Kakadoo GPT and Kakadoo Llama. Pressing Use on a model below switches it for you.",
                new Control[] { engineBox }));
            syncs.Add(SyncEngine);
            SyncEngine();

            langBox = NewCombo(280, Languages.All.Select(l => l[1]).ToArray());
            langBox.SelectedIndex = Math.Max(0, Array.FindIndex(Languages.All, l => l[0] == Settings.Language));
            langBox.SelectedIndexChanged += (s, e) => { SaveGeneral(); UpdateLanguageNote(); };
            langRow = new SettingRow("Speech language", "", new Control[] { langBox });
            page.Controls.Add(langRow);

            if (SpeechListeners.IsWindows)
            {
                var winButton = SmallButton("Add languages…", true);
                winButton.Click += (s, e) => OpenUrl("ms-settings:regionlanguage");
                page.Controls.Add(new SettingRow("Windows speech recognition", WindowsSpeechNote(), new Control[] { winButton }));
            }

            var links = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, Theme.Px(4)) };
            links.Controls.Add(LinkButton("Open models folder", () => OpenFolder(ModelCatalog.Folder)));
            links.Controls.Add(LinkButton("More Llama models: TheBloke on Hugging Face", () => OpenUrl("https://huggingface.co/TheBloke")));
            page.Controls.Add(links);
            page.Controls.Add(BuildDuplicateBanner());

            // Filters: they combine, e.g. English + accurate + fast.
            filterBox = NewCombo(150, "All languages", "English only", "Multi-language");
            accBox = NewCombo(150, "Any accuracy", "Good or better", "Great");
            speedBox = NewCombo(150, "Any speed", "Fast or better", "Very fast");
            famBox = NewCombo(150, new[] { "All families" }.Concat(ModelCatalog.Families.Where(f => f != "Custom")).ToArray());
            var filters = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = new Padding(0, Theme.Px(4), 0, Theme.Px(8)) };
            foreach (var box in new[] { filterBox, accBox, speedBox, famBox })
            {
                box.SelectedIndex = 0;
                box.SelectedIndexChanged += (s, e) => RefreshAll();
                box.Margin = new Padding(0, 0, Theme.Px(10), Theme.Px(4));
                filters.Controls.Add(box);
            }
            var resetFilters = LinkButton("Reset filters", () => { foreach (var box in new[] { filterBox, accBox, speedBox, famBox }) box.SelectedIndex = 0; });
            filters.Controls.Add(resetFilters);
            page.Controls.Add(filters);

            foreach (var entry in ModelCatalog.All) rows.Add(new ModelRow(this, entry));

            AddSection(page, "Downloaded Models");
            downloadedHost = new CardHost("No model is downloaded yet.");
            page.Controls.Add(downloadedHost);

            AddSection(page, "Available to Download");
            availableHost = new CardHost("Everything is downloaded.");
            page.Controls.Add(availableHost);

            AddSection(page, "Your own models",
                "Add any model file or folder from anywhere on this PC, or drop it on this window. Kakadoo works out what it is (Llama .gguf, Whisper-compatible .bin, Parakeet folder, Vosk folder), "
                + "looks inside a folder for the model, checks that it runs, and starts using it. Whisper-compatible files you add are kept in the list below.");
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, Theme.Px(6)) };
            var addFile = SmallButton("Add a model file…", true);
            var addFolder = SmallButton("Add a model folder…", true);
            var openCustom = SmallButton("Open custom folder");
            addFile.Click += async (s, e) => await GuardAsync(() => AddOwnModelFromDialog(false));
            addFolder.Click += async (s, e) => await GuardAsync(() => AddOwnModelFromDialog(true));
            openCustom.Click += (s, e) => OpenFolder(ModelCatalog.CustomFolder);
            buttons.Controls.Add(addFile);
            buttons.Controls.Add(addFolder);
            buttons.Controls.Add(openCustom);
            page.Controls.Add(buttons);

            customHost = new CardHost("No custom models yet.", false);
            page.Controls.Add(customHost);
            return page;
        }

        async Task AddOwnModelFromDialog(bool folder)
        {
            string path;
            if (folder)
            {
                using (var dialog = new FolderBrowserDialog { Description = "Choose a model folder (or a folder that contains one)" })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    path = dialog.SelectedPath;
                }
            }
            else
            {
                using (var dialog = new OpenFileDialog { Filter = "Model files (*.gguf;*.bin)|*.gguf;*.bin|All files|*.*", Title = "Choose a model file" })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    path = dialog.FileName;
                }
            }
            await AddOwnModel(path);
        }

        /// <summary>Works out which kind of model a file or folder is, checks that it runs and starts using it.</summary>
        async Task AddOwnModel(string path)
        {
            var found = ModelDetect.Find(path);
            if (found == null)
            {
                MessageBox.Show(this, "Kakadoo could not find a model in that " + (Directory.Exists(path) ? "folder" : "file") + ".\n\nIt understands:\n"
                    + "  - Llama language models (.gguf)\n  - Whisper-compatible speech models (ggml .bin)\n"
                    + "  - Parakeet folders (encoder, decoder and joiner .onnx files and tokens.txt)\n  - Vosk model folders (with am and conf inside)",
                    "Kakadoo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!await AcceptModel(found.Kind, found.Path)) return;
            ModelSync.Set(found.Kind, found.Path);
            RefreshAll();
            MessageBox.Show(this, "Found a " + found.Label + " and started using it:\n" + found.Path, "Kakadoo", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Checks that a model runs before it is accepted; tells the user why when it does not.</summary>
        async Task<bool> AcceptModel(ModelKind kind, string path)
        {
            UseWaitCursor = true;
            string error;
            try { error = await Task.Run(() => ModelCheck.Verify(kind, path)); }
            catch (Exception e) { error = ErrorHelp.Explain(e); }
            finally { UseWaitCursor = false; }
            if (error == null) return true;
            MessageBox.Show(this, "This model cannot be used:\n\n" + error, "Kakadoo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        bool PassesFilter(ModelEntry e)
        {
            if (filterBox == null) return true;
            int f = filterBox.SelectedIndex;
            if (!(f == 0 || (f == 1 && e.EnglishOnly) || (f == 2 && !e.EnglishOnly))) return false;
            if (e.Accuracy < (accBox.SelectedIndex == 1 ? 3 : accBox.SelectedIndex == 2 ? 4 : 0)) return false;
            if (e.Speed < (speedBox.SelectedIndex == 1 ? 3 : speedBox.SelectedIndex == 2 ? 4 : 0)) return false;
            return famBox.SelectedIndex <= 0 || e.Family == (string)famBox.SelectedItem;
        }

        /// <summary>Puts every card in "Downloaded" or "Available" and rebuilds the list of custom models.</summary>
        void ArrangeCards()
        {
            if (downloadedHost == null) return;
            var have = new List<ModelRow>();
            var want = new List<ModelRow>();
            foreach (var row in rows)
            {
                if (!PassesFilter(row.Entry)) continue;
                (row.Entry.Installed ? have : want).Add(row);
            }
            have = have.OrderBy(r => Array.IndexOf(ModelCatalog.Families, r.Entry.Family)).ToList();
            want = want.OrderBy(r => Array.IndexOf(ModelCatalog.Families, r.Entry.Family)).ToList();
            bool filtered = filterBox.SelectedIndex > 0 || accBox.SelectedIndex > 0 || speedBox.SelectedIndex > 0 || famBox.SelectedIndex > 0;
            downloadedHost.SetEmptyText(filtered ? "No downloaded model matches these filters." : "No model is downloaded yet.");
            availableHost.SetEmptyText(filtered ? "No model to download matches these filters." : "Everything is downloaded.");
            downloadedHost.SetCards(have);
            availableHost.SetCards(want);

            var entries = ModelCatalog.CustomEntries();
            string signature = string.Join("|", entries.Select(e => e.FilePath + e.SizeMb));
            if (signature != customSignature)
            {
                customSignature = signature;
                foreach (var row in customRows) row.Dispose();
                customRows.Clear();
                foreach (var entry in entries) customRows.Add(new ModelRow(this, entry));
            }
            customHost.SetCards(customRows);
        }

        Control BuildDuplicateBanner()
        {
            dupBanner = new Panel { Name = "banner", Height = Theme.Px(46), Margin = new Padding(0, Theme.Px(4), 0, Theme.Px(8)), Visible = false };
            dupLabel = new Label { AutoSize = false, Left = Theme.Px(12), Top = 0, Height = dupBanner.Height, TextAlign = ContentAlignment.MiddleLeft, Font = Small };
            var button = SmallButton("Delete duplicates…", true);
            dupBanner.Controls.AddRange(new Control[] { dupLabel, button });
            dupBanner.Resize += (s, e) =>
            {
                button.Left = dupBanner.Width - button.Width - Theme.Px(12);
                button.Top = (dupBanner.Height - button.Height) / 2;
                dupLabel.Width = Math.Max(50, button.Left - Theme.Px(18));
            };
            button.Click += (s, e) => DeleteDuplicates();
            return dupBanner;
        }

        void ScanDuplicates()
        {
            if (scanning) { scanAgain = true; return; }
            scanning = true;
            Task.Run(() => { try { return ModelDuplicates.Find(); } catch (Exception) { return new List<DuplicateGroup>(); } })
                .ContinueWith(t => Post(() =>
                {
                    scanning = false;
                    duplicates = t.Result;
                    long bytes = duplicates.Sum(g => g.Bytes);
                    dupBanner.Visible = duplicates.Count > 0;
                    dupLabel.Text = duplicates.Count + " model" + (duplicates.Count == 1 ? " is" : "s are") + " saved more than once ("
                        + (bytes >= 1000000000 ? (bytes / 1e9).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB" : bytes / 1048576 + " MB") + " wasted).";
                    if (scanAgain) { scanAgain = false; ScanDuplicates(); }
                }));
        }

        void DeleteDuplicates()
        {
            var text = new System.Text.StringBuilder("One copy of each model is kept, the others are deleted:\n");
            foreach (var g in duplicates)
            {
                text.Append("\nKeep:   ").Append(g.Keep);
                foreach (var r in g.Remove) text.Append("\nDelete: ").Append(r);
                text.Append('\n');
            }
            if (MessageBox.Show(this, text.ToString(), "Delete duplicate models", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            var failed = new List<string>();
            foreach (var g in duplicates)
                foreach (var r in g.Remove)
                {
                    try { File.Delete(r); }
                    catch (Exception e) { failed.Add(Path.GetFileName(r) + ": " + e.Message); }
                }
            if (failed.Count > 0) MessageBox.Show(this, "Could not delete:\n" + string.Join("\n", failed), "Kakadoo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshAll();
        }

        /// <summary>A setting row to use a model file from anywhere on disk. It is checked first, so a file that does not run is refused.</summary>

        // ---- Checker ----

        FlowLayoutPanel BuildChecker()
        {
            var page = NewPage();
            AddHeading(page, "Checker", "Tests your microphone, speech engines, models, processor and graphics card, and tells you what is missing. Speak while it checks the microphone.");

            runChecks = SmallButton("Run checks", true);
            runChecks.Click += async (s, e) => await GuardAsync(RunChecksAsync);
            page.Controls.Add(InRow(runChecks));

            checkStatus = Text_("", Normal, "muted");
            page.Controls.Add(checkStatus);

            checkResults = new Panel { Height = Theme.Px(10), Margin = new Padding(0, Theme.Px(4), 0, 0) };
            checkResults.Resize += (s, e) => LayoutChecks();
            page.Controls.Add(checkResults);
            return page;
        }

        async Task RunChecksAsync()
        {
            if (checking) return;
            checking = true;
            runChecks.Enabled = false;
            foreach (var l in checkLabels) l.Dispose();
            checkLabels.Clear();
            checkResults.Controls.Clear();
            checkStatus.Text = "Running the checks...";
            Fit(pages[2]);

            try
            {
                var totals = await Task.Run(() => SystemChecks.RunAsync(null, null, null, null, null,
                    note => Post(() => checkStatus.Text = note),
                    r => Post(() => AddCheckResult(r))));
                checkStatus.Text = "Result: " + totals.passed + " passed, " + totals.failed + " failed, " + totals.skipped + " skipped.";
                checkStatus.Tag = totals.failed == 0 ? "ok wrap" : "error wrap";
                checkStatus.ForeColor = totals.failed == 0 ? Theme.Ok : Theme.Error;
            }
            catch (Exception e) { checkStatus.Text = "The checks stopped: " + e.Message; }
            finally { checking = false; runChecks.Enabled = true; }
        }

        void AddCheckResult(CheckResult r)
        {
            var label = new Label
            {
                Text = r.Line, Font = Normal, AutoSize = false,
                Tag = r.Outcome == CheckOutcome.Passed ? "ok" : r.Outcome == CheckOutcome.Failed ? "error" : "muted",
                ForeColor = r.Outcome == CheckOutcome.Passed ? Theme.Ok : r.Outcome == CheckOutcome.Failed ? Theme.Error : Theme.Grey
            };
            checkLabels.Add(label);
            checkResults.Controls.Add(label);
            LayoutChecks();
        }

        void LayoutChecks()
        {
            if (checkResults == null || checkResults.Width < 60) return;
            int y = 0;
            foreach (var label in checkLabels)
            {
                int h = Theme.TextHeight(label.Text, label.Font, checkResults.Width) + Theme.Px(8);
                label.SetBounds(0, y, checkResults.Width, h);
                y += h;
            }
            checkResults.Height = Math.Max(Theme.Px(10), y);
        }

        // ---- About ----

        FlowLayoutPanel BuildAbout()
        {
            var page = NewPage();
            AddHeading(page, "About");
            var version = typeof(SettingsWindow).Assembly.GetName().Version;
            page.Controls.Add(Text_("Kakadoo " + (version == null ? "" : version.ToString()), Section));
            page.Controls.Add(Text_("Manipulate Grasshopper sliders with your voice. Speech recognition and language models run on this PC.", Normal));
            page.Controls.Add(Text_("Data folder: " + ModelCatalog.Folder, Small, "muted"));
            page.Controls.Add(Text_("Settings file: " + Path.Combine(ModelCatalog.Folder, "settings.txt"), Small, "muted"));
            page.Controls.Add(InRow(LinkButton("Open data folder", () => OpenFolder(ModelCatalog.Folder))));

            AddSection(page, "Backup", "Save all your settings and custom words to one file, move them to another PC, or start again from the defaults. "
                + "Downloaded models are not part of the backup. Importing or resetting first keeps a copy of the current settings in " + Path.Combine(ModelCatalog.Folder, "settings.backup.txt") + ".");
            var backup = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, Theme.Px(6)) };
            var export = SmallButton("Export settings…");
            var import = SmallButton("Import settings…");
            var reset = SmallButton("Reset to defaults");
            export.Click += (s, e) => ExportSettings();
            import.Click += (s, e) => ImportSettings();
            reset.Click += (s, e) => ResetSettings();
            backup.Controls.AddRange(new Control[] { export, import, reset });
            page.Controls.Add(backup);

            AddSection(page, "Support", "Questions, problems or ideas for Kakadoo? Write an email to " + SupportEmail + ".");
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            var mail = SmallButton("Email support", true);
            mail.Click += (s, e) => OpenUrl("mailto:" + SupportEmail + "?subject=" + Uri.EscapeDataString("Kakadoo " + (version == null ? "" : version.ToString())));
            var source = SmallButton("Kakadoo on GitHub");
            source.Click += (s, e) => OpenUrl("https://github.com/AlpachinoOA/Kakadoo");
            buttons.Controls.Add(mail);
            buttons.Controls.Add(source);
            page.Controls.Add(buttons);

            AddSection(page, "Acknowledgements", "Kakadoo stands on the work of others. The models and tools it uses, each under its own licence (see THIRD_PARTY_NOTICES.txt):");
            var credits = new (string Text, (string Name, string Url)[] Links)[]
            {
                ("Whisper (OpenAI, MIT licence) with whisper.cpp and Whisper.net: speech recognition.",
                    new[] { ("Whisper", "https://github.com/openai/whisper"), ("whisper.cpp", "https://github.com/ggerganov/whisper.cpp"), ("Whisper.net", "https://github.com/sandrohanea/whisper.net") }),
                ("Parakeet TDT v2 and v3 (NVIDIA, CC-BY-4.0) run with sherpa-onnx (k2-fsa, Apache 2.0) and ONNX Runtime (Microsoft, MIT).",
                    new[] { ("Parakeet v2", "https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2"), ("Parakeet v3", "https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3"), ("sherpa-onnx", "https://github.com/k2-fsa/sherpa-onnx"), ("ONNX Runtime", "https://onnxruntime.ai") }),
                ("Vosk (Alpha Cephei, Apache 2.0): offline speech recognition that understands spoken numbers.",
                    new[] { ("Vosk", "https://alphacephei.com/vosk/"), ("Vosk models", "https://alphacephei.com/vosk/models") }),
                ("Llama 2 Chat (Meta, Llama 2 Community Licence), the GGUF files by TheBloke, run with llama.cpp and LLamaSharp (MIT).",
                    new[] { ("Llama 2", "https://ai.meta.com/llama/"), ("TheBloke", "https://huggingface.co/TheBloke"), ("llama.cpp", "https://github.com/ggerganov/llama.cpp"), ("LLamaSharp", "https://github.com/SciSharp/LLamaSharp") }),
                ("NAudio (Mark Heath, MIT): recording and sounds. Grasshopper and Rhino by McNeel.",
                    new[] { ("NAudio", "https://github.com/naudio/NAudio"), ("Grasshopper", "https://www.grasshopper3d.com") }),
            };
            foreach (var credit in credits)
            {
                page.Controls.Add(Text_("\u2022  " + credit.Text, Small, "muted"));
                var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = new Padding(Theme.Px(14), 0, 0, Theme.Px(6)) };
                foreach (var link in credit.Links)
                {
                    string url = link.Url;
                    row.Controls.Add(LinkButton(link.Name, () => OpenUrl(url)));
                }
                page.Controls.Add(row);
            }
            return page;
        }

        // ---- Backup ----

        void ExportSettings()
        {
            using (var dialog = new SaveFileDialog { Filter = "Kakadoo settings (*.txt)|*.txt", FileName = "kakadoo-settings.txt", Title = "Export settings" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(dialog.FileName, Settings.ExportText()); MessageBox.Show(this, "Saved to " + dialog.FileName, "Kakadoo"); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the file: " + e.Message, "Kakadoo", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        void ImportSettings()
        {
            string text;
            using (var dialog = new OpenFileDialog { Filter = "Kakadoo settings (*.txt)|*.txt|All files|*.*", Title = "Import settings" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { text = File.ReadAllText(dialog.FileName); }
                catch (Exception e) { MessageBox.Show(this, "Could not read the file: " + e.Message, "Kakadoo", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            }
            if (!text.StartsWith("# Kakadoo settings backup"))
            {
                MessageBox.Show(this, "That is not a Kakadoo settings backup.", "Kakadoo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show(this, "Replace all your settings and custom words with the ones in this file? Model paths in it must exist on this PC.", "Kakadoo", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            ApplyBackup(text);
        }

        void ResetSettings()
        {
            if (MessageBox.Show(this, "Reset every setting and the custom words to the defaults? Downloaded models stay.", "Kakadoo", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            ApplyBackup("# Kakadoo settings backup\n");
        }

        /// <summary>Keeps a copy of what is there, applies the backup, and reopens the window so every control shows the new values.</summary>
        void ApplyBackup(string text)
        {
            try { File.WriteAllText(Path.Combine(ModelCatalog.Folder, "settings.backup.txt"), Settings.ExportText()); } catch (Exception) { }
            string error = Settings.ImportText(text);
            if (error != null) { MessageBox.Show(this, error, "Kakadoo", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            BeginInvoke(new Action(() => { Close(); ShowWindow(); }));
        }

        // ---- Helpers ----

        /// <summary>Keeps a button at its own size when it sits directly on a page (a page gives every item its full width).</summary>
        static FlowLayoutPanel InRow(Control control) =>
            new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, Theme.Px(4), 0, Theme.Px(4)) }.With(control);

        static void OpenFolder(string folder)
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception e) { MessageBox.Show("Could not open " + url + ": " + e.Message, "Kakadoo"); }
        }

        public void RefreshRows()
        {
            foreach (var row in AllRows) row.UpdateState();
        }

        /// <summary>Redraws everything that depends on the saved settings or on the files on disk.</summary>
        public void RefreshAll()
        {
            if (IsDisposed) return;
            ArrangeCards();
            RefreshRows();
            downloadedHost?.LayoutCards();
            availableHost?.LayoutCards();
            customHost?.LayoutCards();
            foreach (var sync in syncs) sync();
            ScanDuplicates();
        }

        /// <summary>Runs something from a button or a background result; a failure becomes a message, never a crash.</summary>
        void Guard(Action action)
        {
            try { action(); }
            catch (Exception e) { Complain(e); }
        }

        async Task GuardAsync(Func<Task> action)
        {
            try { await action(); }
            catch (Exception e) { Complain(e); }
        }

        void Complain(Exception e)
        {
            if (IsDisposed) return;
            try { MessageBox.Show(this, "Something went wrong: " + ErrorHelp.Explain(e), "Kakadoo", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            catch (Exception) { }
        }

        public void Post(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => Guard(action))); } catch (InvalidOperationException) { }
        }

        public static Button SmallButton(string text, bool accent = false)
        {
            var b = new Button
            {
                Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Font = Small, Padding = new Padding(Theme.Px(6), Theme.Px(1), Theme.Px(6), Theme.Px(1)), MinimumSize = new Size(0, Theme.Px(28)), Tag = accent ? "accent" : ""
            };
            return b;
        }

        static Button LinkButton(string text, Action click)
        {
            var b = new Button
            {
                Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = Small, Margin = new Padding(0, 0, Theme.Px(10), Theme.Px(4)), Tag = "link"
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (s, e) => click();
            return b;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && current == this) current = null;
            if (disposing) { Settings.Changed -= onSettingsChanged; wordsTimer.Dispose(); }
            base.Dispose(disposing);
        }
    }

    static class ControlExtensions
    {
        public static T With<T>(this T parent, Control child) where T : Control { parent.Controls.Add(child); return parent; }
    }
}
