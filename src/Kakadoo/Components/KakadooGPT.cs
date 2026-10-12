using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Newtonsoft.Json.Linq;

namespace Kakadoo
{
    /// <summary>
    /// Kakadoo GPT: speak a command, OpenAI picks the slider and its new value.
    /// This is the one component that needs internet and an OpenAI API key.
    /// </summary>
    public class KakadooGPT : KakadooComponent
    {
        // gpt-3.5-turbo-16k from the original script has been retired.
        const string Model = "gpt-4o-mini";
        const string Endpoint = "https://api.openai.com/v1/chat/completions";
        const string PluginContext =
            "You are GrasshopperAI, an AI tool working within Grasshopper to manipulate Grasshopper scripts through natural language. " +
            "You will be given a description of the entire script and data about the sliders in the format <SLIDER_NAME> - <SLIDER_VALUE>. " +
            "After understanding the prompt, you have to understand what slider to change and what the new slider value would be. " +
            "To do that, all you have to do is call a function that I have already programmed which is change_slider_value(str(<SLIDER_NAME>), <NEW_SLIDER_VALUE>). " +
            "Remember to respond only with the function and enclose the name in an str() function. " +
            "And always check the current value and iterate over it. Never give the same result as the current value";

        static readonly HttpClient Http = CreateClient();

        readonly ISpeechListener listener = SpeechListeners.Create();
        readonly object gate = new object();
        volatile bool running;
        bool lastRun;

        public KakadooGPT()
          : base("Kakadoo GPT", "KkGPT",
              "Speak naturally, e.g. \"make the building higher\". OpenAI GPT picks the slider and value (needs an API key)",
              "Kakadoo", "Voice")
        {
        }

        static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            // Office networks often need the signed-in Windows user to pass the proxy.
            var handler = new HttpClientHandler { UseProxy = true, DefaultProxyCredentials = CredentialCache.DefaultCredentials };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter("Sliders", "S", "Connect the sliders you want to manipulate", GH_ParamAccess.list);
            pManager.AddTextParameter("API Key", "K", "OpenAI API Key", GH_ParamAccess.item);
            pManager.AddTextParameter("Description", "D", "Brief description about the environment", GH_ParamAccess.item, "");
            pManager.AddBooleanParameter("Run", "R", "Connect a button", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Output", "O", "Status messages", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string apiKey = null, description = "";
            bool run = false;
            DA.GetData(1, ref apiKey);
            DA.GetData(2, ref description);
            DA.GetData(3, ref run);

            // Only trigger on the rising edge so re-solves (e.g. after a slider change) don't relisten.
            bool trigger = run && !lastRun;
            lastRun = run;

            if (trigger && !running)
            {
                var sliders = SliderUtil.GetSliders(Params.Input[0]);
                string notice = SliderUtil.Notice(Params.Input[0]);
                running = true;
                Message = "Listening";
                Log("Listening...", clear: true);
                if (notice != null) Log(notice);
                Task.Run(() => RunCommandAsync(sliders, apiKey, description));
            }
            else if (!run && !running)
            {
                Message = "Not Listening";
            }

            lock (gate) DA.SetDataList(0, messages.ToList());
        }

        async Task RunCommandAsync(List<GH_NumberSlider> sliders, string apiKey, string description)
        {
            try
            {
                if (sliders.Count == 0) { Log("No sliders connected."); return; }
                if (string.IsNullOrWhiteSpace(apiKey)) { Log("No API key provided."); return; }

                string command;
                SpeechListeners.SetHint(listener, SliderUtil.Names(sliders));
                try { command = await SpeechListeners.ListenAsync(listener, m => Log(m)); }
                catch (Exception e) { Log("Speech error: " + ErrorHelp.Report(this, e)); return; }

                if (string.IsNullOrWhiteSpace(command)) { Log("Could not understand audio. Please try again."); return; }
                command = command.ToLowerInvariant();
                Log("Recognized speech: " + command);

                var values = SliderUtil.PromptValues(sliders);
                string reply = await SendRequestAsync(command, description, values, apiKey);
                if (reply == null) { Log("Failed to get a valid response from the OpenAI API."); return; }

                Log(SliderUtil.ApplyReply(sliders, reply));
            }
            catch (HttpRequestException)
            {
                Log("Could not reach OpenAI. Check your internet connection (offices may block api.openai.com), or use Kakadoo Llama which works offline.");
            }
            catch (TaskCanceledException)
            {
                Log("OpenAI did not answer in time. Check your internet connection and try again.");
            }
            catch (Exception e)
            {
                Log("An error occurred: " + ErrorHelp.Report(this, e));
            }
            finally
            {
                running = false;
                Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
                {
                    Message = "Not Listening";
                    ExpireSolution(true);
                }));
            }
        }

        async Task<string> SendRequestAsync(string userPrompt, string description, string sliderValues, string apiKey)
        {
            var body = new JObject
            {
                ["model"] = Model,
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = PluginContext + "\n\n" + description + "\n" + sliderValues },
                    new JObject { ["role"] = "user", ["content"] = userPrompt }
                },
                ["temperature"] = 0.5,
                ["max_tokens"] = 256
            };

            using (var request = new HttpRequestMessage(HttpMethod.Post, Endpoint))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
                request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");

                using (var response = await Http.SendAsync(request))
                {
                    string text = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        Log("OpenAI error " + (int)response.StatusCode + ": " + ErrorText(text, response.ReasonPhrase));
                        return null;
                    }
                    try
                    {
                        return (string)JObject.Parse(text)["choices"]?[0]?["message"]?["content"];
                    }
                    catch (Exception)
                    {
                        Log("OpenAI sent an answer that could not be read. A proxy or firewall may be interfering.");
                        return null;
                    }
                }
            }
        }

        /// <summary>The message from an OpenAI error reply, or the plain HTTP reason if the reply is not JSON (for example a proxy page).</summary>
        static string ErrorText(string body, string fallback)
        {
            try { return (string)JObject.Parse(body)["error"]?["message"] ?? fallback; }
            catch (Exception) { return fallback; }
        }

        void Log(string message, bool clear = false)
        {
            lock (gate)
            {
                if (clear) messages.Clear();
                messages.Add(message);
            }
            RequestRefresh();
        }

        protected override Bitmap Icon => Icons.Load("KakadooGpt");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        public override Guid ComponentGuid => new Guid("c47d9e10-2b85-4a36-8f0e-61d5a3b97c24");
    }
}
