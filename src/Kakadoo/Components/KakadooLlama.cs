using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace Kakadoo
{
    /// <summary>
    /// Kakadoo Llama: speak a command, a local Llama (GGUF) model picks the slider and its new value.
    /// </summary>
    public class KakadooLlama : KakadooComponent
    {
        const string PluginContext =
            "Given a set of sliders with their values, names and an optimization goal, pick which slider is correct slider that should be adjusted to meet the goal. " +
            "Don't explain your reasoning to me. Return nothing but the name of the correct, relevant sliders and its new value. " +
            "I don't want a code, I just want the answer! Pick the correct one otherwise I'll be mad! Pay attention to what user says!";
        const string TaskDescription = "Your task is to modify the correct slider values";

        readonly ISpeechListener listener = SpeechListeners.Create();
        readonly object gate = new object();
        LLamaWeights weights;
        string loadedModelPath;
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        volatile bool running;
        volatile bool removed;
        bool lastRun;

        public KakadooLlama()
          : base("Kakadoo Llama", "KkLl",
              "Speak naturally, e.g. \"make the building higher\". A local Llama model picks the slider and value, fully offline",
              "Kakadoo", " Voice")
        {
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter("Sliders", "S", "Connect the sliders you want to manipulate", GH_ParamAccess.list);
            pManager.AddTextParameter("Llama Model Directory Path", "Llama Model Directory Path", "Path to the Llama GGUF model file", GH_ParamAccess.item);
            pManager.AddBooleanParameter("Run", "R", "Connect a button", GH_ParamAccess.item, false);
            pManager.AddTextParameter("Device", "D", "cpu or gpu (not case sensitive), optionally followed by ; and the number of CPU cores to use, e.g. gpu;6. The Kakadoo Checker shows how many cores you have and how many it recommends.", GH_ParamAccess.item, DeviceSetting.Default);
            pManager[3].Optional = true;
            AddWhisperInput(pManager);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Output", "O", "Status messages", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string modelPath = null, device = null;
            bool run = false;
            DA.GetData(1, ref modelPath);
            if (RunIndex >= 0) DA.GetData(RunIndex, ref run);
            ApplyWhisperInput(DA, listener);
            int deviceIndex = Params.Input.FindIndex(p => p.Name == "Device");
            if (deviceIndex >= 0) DA.GetData(deviceIndex, ref device);

            // Only trigger on the rising edge so re-solves (e.g. after a slider change) don't relisten.
            bool woken = ConsumeTether(!running);
            bool trigger = (run && !lastRun) || woken;
            lastRun = run;

            if (trigger && !running)
            {
                if (!DeviceSetting.TryParse(device, out bool gpu, out int threads, out string error))
                {
                    Log(error, clear: true);
                }
                else
                {
                    UseGpu = gpu;
                    Threads = threads;
                    var sliders = SliderUtil.GetSliders(Params.Input[0]);
                    running = true;
                    Log("Listening...", clear: true);
                    if (SliderUtil.Notice(Params.Input[0]) is string notice) Log(notice);
                    Task.Run(() => RunCommandAsync(sliders, modelPath));
                }
            }
            else if (!run && !running)
            {
                if (messages.IsEmpty) Log("Not Running");
            }

            lock (gate) DA.SetDataList(0, messages.ToList());
        }

        async Task RunCommandAsync(List<GH_NumberSlider> sliders, string modelPath)
        {
            try
            {
                if (sliders.Count == 0) { Log("No sliders connected."); return; }

                string command;
                SpeechListeners.SetHint(listener, SliderUtil.Names(sliders));
                try { command = await HearAsync(() => SpeechListeners.ListenAsync(listener, m => Log(m))); }
                catch (Exception e) { Log("Speech error: " + ErrorHelp.Report(this, e)); return; }

                if (string.IsNullOrWhiteSpace(command)) { Log("No command recognized."); return; }
                command = command.ToLowerInvariant();
                Log("Recognized speech: " + command);

                if (!EnsureModel(modelPath)) return;

                var values = SliderUtil.PromptValues(sliders);

                // The model reads every slider name and value before answering, so a long list is slow without a GPU.
                int promptTokens = (int)((values.Length + 700) / 2.5);
                if (promptTokens > 600 && loadedGpuLayers == 0)
                    Log("Note: " + sliders.Select(s => s.NickName.ToLowerInvariant()).Distinct().Count() + " slider names is about " + promptTokens
                        + " tokens for the model to read. On the CPU that can take minutes per command (about 4 minutes for 200 sliders on an 8-core PC). Use Device = gpu, or connect fewer sliders.");
                string result = await QueryModelAsync(command, values);
                if (string.IsNullOrWhiteSpace(result)) { Log("No result from language model."); return; }

                Log(SliderUtil.ApplyReply(sliders, result));
            }
            catch (Exception e)
            {
                Log("An error occurred: " + ErrorHelp.Report(this, e));
            }
            finally
            {
                running = false;
                FinishTethered();
                if (removed) DisposeModel(); // component was deleted mid-run
                RequestRefresh();
            }
        }

        /// <summary>Run the model on the GPU (Vulkan on Windows, Metal on Mac) instead of the CPU. Set from the Device input.</summary>
        public bool UseGpu { get; set; } = Hardware.IsMac;

        /// <summary>CPU cores the model may use. Set from the Device input.</summary>
        public int Threads { get; set; } = Hardware.RecommendedThreads;

        /// <summary>How many model layers go to the GPU. 0 means as many as fit.</summary>
        public int GpuLayers { get; set; }

        bool loadedGpu;
        int loadedLayers;

        bool EnsureModel(string modelPath)
        {
            modelPath = modelPath?.Trim().Trim('"'); // "Copy as path" in Explorer adds quotes
            if (string.IsNullOrWhiteSpace(modelPath)) { Log("No model path provided."); return false; }
            if (!File.Exists(modelPath)) { Log("Model not found at " + modelPath + ". Please provide the correct path."); return false; }
            if (weights != null && modelPath == loadedModelPath && loadedGpu == UseGpu && loadedLayers == GpuLayers) return true;

            // Loading a model bigger than the memory of the PC freezes or crashes the computer, so refuse it up front.
            long sizeMb = new FileInfo(modelPath).Length / (1024 * 1024);
            long ramMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
            if (sizeMb > ramMb * 0.9)
            {
                Log("This model is " + sizeMb + " MB but the PC has only " + ramMb + " MB of memory. Use a smaller model (for example a 7B or Q4 file).");
                return false;
            }

            bool gpu = UseGpu;
            if (!LlamaNative.Register(gpu) && !LlamaNative.LibrariesFound)
            {
                Log("The llama.cpp libraries were not found in the plugin folder. Reinstall the Kakadoo package.");
                return false;
            }
            else if (!LlamaNative.Register(gpu))
            {
                // llama.cpp can't be swapped once loaded, so the other version needs a fresh Rhino session.
                gpu = LlamaNative.IsGpuBuild;
                Log("The " + (gpu ? "GPU" : "CPU") + " version of llama.cpp is already loaded in this Rhino session. Restart Rhino to switch.");
            }
            else if (gpu && !LlamaNative.IsGpuBuild)
            {
                gpu = false;
                Log("GPU support is not available on this system, using the CPU.");
            }

            DisposeModel();
            Log("Loading model on the " + (gpu ? "GPU..." : "CPU..."));

            // "As many as fit" (0): load once on the CPU to read the model's size, then work out how many layers
            // the GPU can hold. Offloading too many makes llama.cpp crash when it runs out of video memory.
            int layers = !gpu ? 0 : GpuLayers > 0 ? GpuLayers : -1;

            try
            {
                if (layers < 0)
                {
                    layers = AutoGpuLayers(modelPath);
                    if (layers == 0) Log("Could not size the GPU memory for this model, running on the CPU. Set 'GPU layers' in the right-click menu to force it.");
                }

                loadedGpuLayers = layers;
                weights = LLamaWeights.LoadFromFile(MakeParams(modelPath, 2048));
                loadedModelPath = modelPath;
                loadedGpu = UseGpu;
                loadedLayers = GpuLayers;
                if (gpu && layers > 0) Log("Model layers on GPU: " + (layers >= 99 ? "all" : layers.ToString()));
                return true;
            }
            catch (Exception e)
            {
                Log("Could not load the model: " + ErrorHelp.Report(this, e) + (e is DllNotFoundException || e is TypeInitializationException ? " (the llama.cpp libraries could not be loaded; reinstall the Kakadoo package and make sure the Microsoft Visual C++ Redistributable is installed)" : ""));
                return false;
            }
        }

        /// <summary>The largest prompt (in tokens) the model is sized for. Llama 2 models are trained for 4096.</summary>
        const int MaxContext = 4096;

        int loadedGpuLayers;

        ModelParams MakeParams(string modelPath, int contextSize) => new ModelParams(modelPath)
        {
            ContextSize = (uint)contextSize,
            Threads = Threads,
            BatchThreads = Threads,
            GpuLayerCount = loadedGpuLayers
        };

        /// <summary>How many layers fit in video memory, or 0 if that can't be worked out. 99 means all of them.</summary>
        static int AutoGpuLayers(string modelPath)
        {
            // Apple Silicon shares memory with the CPU, so everything goes to the GPU.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return 99;

            long vram = VideoMemory.DetectBytes();
            if (vram <= 0) return 0;

            // Read the layer count and size from the model file without putting anything on the GPU.
            using (var probe = LLamaWeights.LoadFromFile(new ModelParams(modelPath) { ContextSize = 2048, GpuLayerCount = 0 }))
            {
                if (!probe.Metadata.TryGetValue("llama.block_count", out string blocks) || !int.TryParse(blocks, out int total) || total <= 0)
                    return 0;

                // Per layer: its weights plus its share of the context (KV cache, f16, without assuming grouped-query attention).
                double perLayer = (double)probe.SizeInBytes / total + 2.0 * 2 * MaxContext * probe.EmbeddingSize;
                // Leave room for the compute buffers (~0.6 GB), the desktop and Rhino itself (~1 GB), and a safety margin.
                double budget = vram * 0.9 - 1.6e9;
                int fit = (int)Math.Floor(budget / perLayer);

                if (fit <= 0) return 0;
                return fit >= total ? 99 : fit;
            }
        }

        async Task<string> QueryModelAsync(string instruction, string sliderValues)
        {
            // With many sliders the values are listed once; for a few, a second time as the original prompt did.
            bool repeatValues = sliderValues.Length < 600;
            var prompt = new StringBuilder()
                .AppendLine(PluginContext)
                .AppendLine(TaskDescription)
                .AppendLine("Current Slider Values: " + sliderValues)
                .AppendLine("### Instruction:")
                .AppendLine(instruction)
                .AppendLine("### Example:")
                .AppendLine("If the instruction is 'Make it bigger', the response should be ALWAYS in this format 'change_slider_value(\"<appropriate slider name>\", str(<12>))'.")
                .AppendLine("### Input:")
                .AppendLine(repeatValues ? sliderValues : "(see the current slider values above)")
                .AppendLine("### Response:")
                .ToString();

            // About 2.5 characters per token to be safe with names and numbers, plus room for the answer.
            int needed = (int)(prompt.Length / 2.5) + 96;
            if (needed > MaxContext)
                throw new InvalidOperationException("The slider names and values are too long for the model (" + needed + " tokens, limit " + MaxContext + "). Use fewer or shorter slider names.");
            int contextSize = needed <= 2048 ? 2048 : MaxContext;

            var executor = new StatelessExecutor(weights, MakeParams(loadedModelPath, contextSize));
            var inference = new InferenceParams
            {
                MaxTokens = 64,
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.1f }, AntiPrompts = new List<string> { "###", "\n\n" } };

            var sb = new StringBuilder();
            await foreach (var token in executor.InferAsync(prompt, inference, cts.Token))
                sb.Append(token);
            return sb.ToString();
        }

        void Log(string message, bool clear = false)
        {
            lock (gate)
            {
                if (clear) messages.Clear();
                messages.Add(message);
            }
            if (running)
                RequestRefresh();
        }

        protected override void AppendAdditionalComponentMenuItems(System.Windows.Forms.ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "GPU layers (0 = as many as fit):", null, false);
            Menu_AppendTextItem(menu, GpuLayers.ToString(), null, (box, text) =>
            {
                if (int.TryParse(text, out int layers) && layers >= 0 && layers <= 200 && layers != GpuLayers)
                {
                    GpuLayers = layers;
                    ExpireSolution(true);
                }
            }, true);
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetInt32("GpuLayers", GpuLayers);
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            int layers = 0;
            if (reader.TryGetInt32("GpuLayers", ref layers)) GpuLayers = layers;
            return base.Read(reader);
        }

        void DisposeModel()
        {
            weights?.Dispose();
            weights = null;
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            removed = true;
            cts.Cancel();
            if (!running) DisposeModel(); // otherwise the running task disposes it when it stops
            base.RemovedFromDocument(document);
        }

        protected override Bitmap Icon => Icons.Load("KakadooLlama");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        public override Guid ComponentGuid => new Guid("5b0f3c1e-8a64-4d7e-9b52-2f6a1d0c7e93");
    }
}
