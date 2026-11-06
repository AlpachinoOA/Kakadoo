using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Kakadoo
{
    /// <summary>The model chosen on this PC, as one path per kind, read and written the same way by the settings window and the panels.</summary>
    static class ModelSync
    {
        /// <summary>The kind of model the chosen speech engine uses.</summary>
        public static ModelKind SpeechKind => Settings.Engine == "parakeet" ? ModelKind.Parakeet : Settings.Engine == "custom" ? ModelKind.Custom : Settings.Engine == "vosk" ? ModelKind.Vosk : ModelKind.Whisper;

        /// <summary>The path to show for a kind. Empty means automatic.</summary>
        public static string Get(ModelKind kind)
        {
            switch (kind)
            {
                case ModelKind.Whisper:
                    if (!string.IsNullOrWhiteSpace(Settings.WhisperModelPath)) return Settings.WhisperModelPath;
                    if (!string.IsNullOrWhiteSpace(Settings.WhisperModelName)) return Path.Combine(WhisperModel.Directory, "ggml-" + Settings.WhisperModelName + ".bin");
                    // Automatic: show the file that will really be used, so the panel is not blank.
                    try { return WhisperModel.Locate(out _) ?? ""; }
                    catch (Exception) { return ""; }
                case ModelKind.Vosk:
                    if (!string.IsNullOrWhiteSpace(Settings.VoskModelPath)) return Settings.VoskModelPath;
                    string installed = SpeechListeners.DefaultVoskModelPath();
                    return VoskListener.FindModelRoot(installed) != null ? installed : "";
                case ModelKind.Parakeet:
                    if (!string.IsNullOrWhiteSpace(Settings.ParakeetModelPath)) return Settings.ParakeetModelPath;
                    return string.IsNullOrWhiteSpace(Settings.ParakeetModelName) ? "" : ParakeetModel.FolderFor(Settings.ParakeetModelName);
                case ModelKind.Custom:
                    return Settings.CustomModelPath;
                default:
                    return Settings.LlamaModelPath;
            }
        }

        /// <summary>Chooses a model from a path typed into a panel. A path that is one of the downloaded catalog models picks that model.</summary>
        public static void Set(ModelKind kind, string path)
        {
            path = (path ?? "").Trim().Trim('"').Trim();
            switch (kind)
            {
                case ModelKind.Whisper:
                    string name = CatalogWhisperName(path);
                    if (path.Length == 0 || name != null)
                    {
                        Settings.WhisperModelPath = "";
                        Settings.WhisperModelName = name ?? "";
                    }
                    else Settings.WhisperModelPath = path;
                    Settings.Engine = "whisper";
                    break;
                case ModelKind.Vosk:
                    Settings.VoskModelPath = SamePath(path, ModelCatalog.VoskFolder) ? "" : path;
                    break;
                case ModelKind.Parakeet:
                    string id = ModelCatalog.Of(ModelKind.Parakeet).FirstOrDefault(e => SamePath(e.FolderPath, path))?.Id;
                    if (path.Length == 0 || id != null) { Settings.ParakeetModelPath = ""; Settings.ParakeetModelName = id ?? ""; }
                    else Settings.ParakeetModelPath = path;
                    Settings.Engine = path.Length == 0 ? "whisper" : "parakeet";
                    break;
                case ModelKind.Custom:
                    Settings.CustomModelPath = path;
                    Settings.Engine = path.Length == 0 ? "whisper" : "custom";
                    break;
                default:
                    Settings.LlamaModelPath = path;
                    break;
            }
        }

        static string CatalogWhisperName(string path)
        {
            if (path.Length == 0) return null;
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!SamePath(dir, WhisperModel.Directory)) return null;
                return ModelCatalog.Of(ModelKind.Whisper).FirstOrDefault(e => !e.IsAuto && SamePath(e.FilePath, path))?.Id;
            }
            catch (Exception) { return null; }
        }

        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// Keeps the model path inputs of one component in step with the Kakadoo settings, both ways. A path input with nothing connected gets a
    /// panel holding the chosen model path; editing that panel changes the setting (once the model has been checked to run), and changing the
    /// setting (in the window, or through another component's panel) rewrites the panel. A panel the user connected themselves is never touched.
    /// </summary>
    sealed class ModelPanelSync
    {
        const string Tag = "Kakadoo model: ";
        const float PanelWidth = 340, PanelHeight = 44;

        sealed class State
        {
            public string Last;           // the text the panel and the setting last agreed on
            public bool Init, Pending;
            public string Verifying;      // the text being checked right now
            public string Error;          // why the last typed model was refused, shown on the component until the next edit
        }

        readonly GH_Component owner;
        readonly Dictionary<ModelKind, State> states = new Dictionary<ModelKind, State>();

        /// <summary>When set, the owner shows an orange warning while the speech engine chosen in the settings is not ready.</summary>
        public bool ChecksEngine;

        string shownProblem;

        public ModelPanelSync(GH_Component owner) { this.owner = owner; }

        public void Attach() => Settings.Changed += OnSettingsChanged;

        public void Detach() => Settings.Changed -= OnSettingsChanged;

        static bool TryKind(IGH_Param p, out ModelKind kind)
        {
            kind = ModelKind.Whisper;
            // the shared speech model input follows the engine chosen in the settings
            if (p.Name == KakadooComponent.WhisperInputName) { kind = ModelSync.SpeechKind; return true; }
            if (p.Name == KakadooComponent.VoskInputName) { kind = ModelKind.Vosk; return true; }
            if (p.Name == KakadooComponent.LlamaInputName) { kind = ModelKind.Llama; return true; }
            if (p.Name == KakadooComponent.ParakeetInputName) { kind = ModelKind.Parakeet; return true; }
            if (p.Name == KakadooComponent.CustomInputName) { kind = ModelKind.Custom; return true; }
            return false;
        }

        static string Label(ModelKind kind) => Tag + kind;

        State StateOf(ModelKind kind)
        {
            if (!states.TryGetValue(kind, out var s)) states[kind] = s = new State();
            return s;
        }

        static GH_Panel OurPanel(IGH_Param input) =>
            input.SourceCount == 1 && input.Sources[0] is GH_Panel panel && panel.NickName != null && panel.NickName.StartsWith(Tag) ? panel : null;

        void OnSettingsChanged()
        {
            Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                var doc = owner.OnPingDocument();
                if (doc == null) { Detach(); return; }
                if (NeedsSync()) doc.ScheduleSolution(5, d => owner.ExpireSolution(false));
            }));
        }

        bool NeedsSync()
        {
            if (ChecksEngine && EngineCheck.Problem() != shownProblem) return true;
            foreach (var input in owner.Params.Input)
            {
                if (!TryKind(input, out var kind)) continue;
                string cur = ModelSync.Get(kind);
                if (input.SourceCount == 0) { if (cur.Length > 0) return true; continue; }
                var panel = OurPanel(input);
                if (panel == null) continue;
                var state = StateOf(kind);
                if (state.Verifying != null) continue; // a typed model is being checked; its result decides
                if (!state.Init || cur != state.Last) return true;
            }
            return false;
        }

        /// <summary>Call before each solve of the owner.</summary>
        public void BeforeSolve()
        {
            shownProblem = ChecksEngine ? EngineCheck.Problem() : null;
            if (shownProblem != null) owner.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, shownProblem);

            var doc = owner.OnPingDocument();
            if (doc == null) return;

            foreach (var input in owner.Params.Input.ToList())
            {
                if (!TryKind(input, out var kind)) continue;
                var state = StateOf(kind);
                string cur = ModelSync.Get(kind);

                if (state.Error != null) owner.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, state.Error);

                if (input.SourceCount == 0)
                {
                    if (cur.Length > 0 && !state.Pending)
                    {
                        state.Pending = true;
                        doc.ScheduleSolution(5, d => { state.Pending = false; CreatePanel(d, input, kind, state); });
                    }
                    continue;
                }

                var panel = OurPanel(input);
                if (panel == null || state.Verifying != null) continue;
                string text = panel.UserText ?? "";

                if (!state.Init || panel.NickName != Label(kind))
                {
                    // First look at this panel (a file was just opened, or Always-On swapped the input): the settings on this PC win.
                    state.Init = true;
                    state.Last = cur;
                    if (text != cur || panel.NickName != Label(kind)) Rewrite(doc, panel, kind, cur);
                }
                else if (text != state.Last)
                {
                    // The user edited the panel. A model that does not run is refused; one that does becomes the setting for every component.
                    state.Last = text;
                    state.Error = null;
                    if (text.Trim().Length == 0) ModelSync.Set(kind, "");
                    else StartVerify(kind, text, panel, state);
                }
                else if (cur != state.Last)
                {
                    state.Last = cur;
                    Rewrite(doc, panel, kind, cur);
                }
            }
        }

        void StartVerify(ModelKind kind, string text, GH_Panel panel, State state)
        {
            state.Verifying = text;
            ProgressAttributes.ShowNote((GH_Component)owner, "Checking that the model runs...", NoteKind.Hint, 30);
            Task.Run(() => ModelCheck.Verify(kind, text)).ContinueWith(t => Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                state.Verifying = null;
                var doc = owner.OnPingDocument();
                if (doc == null) return;
                if ((panel.UserText ?? "") != text) { owner.ExpireSolution(false); return; } // edited again meanwhile: that edit gets its own check

                string error = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result;
                if (error == null)
                {
                    ProgressAttributes.ShowNote(owner, "Model accepted.", NoteKind.Hint, 3);
                    ModelSync.Set(kind, text);
                    return;
                }
                state.Error = "The " + kind + " model was not accepted: " + error;
                ProgressAttributes.ShowNote(owner, state.Error, NoteKind.Error, 20);
                state.Last = ModelSync.Get(kind);
                Rewrite(doc, panel, kind, state.Last);
            })));
        }

        static void Rewrite(GH_Document doc, GH_Panel panel, ModelKind kind, string text) =>
            doc.ScheduleSolution(5, d =>
            {
                panel.NickName = Label(kind);
                panel.UserText = text;
                panel.ExpireSolution(false);
            });

        void CreatePanel(GH_Document doc, IGH_Param input, ModelKind kind, State state)
        {
            string text = ModelSync.Get(kind);
            if (input.SourceCount > 0 || text.Length == 0) return;

            var panel = new GH_Panel();
            panel.CreateAttributes();
            panel.NickName = Label(kind);
            panel.UserText = text;

            var spot = FreeSpot(doc, Wanted(input));
            panel.Attributes.Pivot = spot.Location;
            panel.Attributes.Bounds = spot;

            doc.AddObject(panel, false);
            input.AddSource(panel);
            state.Init = true;
            state.Last = text;
            input.ExpireSolution(false);
        }

        /// <summary>Where a panel would sit: just left of the input, level with it.</summary>
        RectangleF Wanted(IGH_Param input)
        {
            RectangleF b = input.Attributes != null ? input.Attributes.Bounds : RectangleF.Empty;
            if (b.Width < 1 || b.Height < 1) b = owner.Attributes.Bounds; // not laid out yet: use the component
            return new RectangleF(b.Left - PanelWidth - 40, b.Top + b.Height / 2 - PanelHeight / 2, PanelWidth, PanelHeight);
        }

        /// <summary>Moves the spot up, down and further left until it does not overlap anything already on the canvas.</summary>
        static RectangleF FreeSpot(GH_Document doc, RectangleF wanted)
        {
            var others = doc.Objects.Where(o => o.Attributes != null).Select(o => o.Attributes.Bounds).Where(r => r.Width > 0).ToList();
            float step = wanted.Height + 10;
            for (int i = 0; i < 14; i++)
            {
                int dy = (i + 1) / 2 * (i % 2 == 1 ? -1 : 1); // 0, -1, +1, -2, +2, ...
                for (int dx = 0; dx < 3; dx++)
                {
                    var r = new RectangleF(wanted.X - dx * (wanted.Width + 30), wanted.Y + dy * step, wanted.Width, wanted.Height);
                    var padded = RectangleF.Inflate(r, 8, 8);
                    if (!others.Any(o => o.IntersectsWith(padded))) return r;
                }
            }
            return wanted;
        }
    }
}
