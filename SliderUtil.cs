using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Kakadoo_Final
{
    static class SliderUtil
    {
        /// <summary>The most sliders one Kakadoo component works with.</summary>
        public const int MaxSliders = 200;

        // Matches change_slider_value(str(<name>), <value>) and looser forms like: name, value
        static readonly Regex CallPattern = new Regex(
            @"(?:change_slider_value\s*\(\s*)?(?:str\s*\(\s*)?[""']?(?<name>[^,""'()]+?)[""']?\s*\)?\s*,\s*(?:str\s*\(\s*)?[""']?(?<value>[-+]?\d+(?:\.\d+)?)(?<pct>\s*%)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Parses a model reply into a slider name and new value ("+20%" means relative to the current value).</summary>
        public static bool TryParseCall(string text, out string name, out double value, out bool isPercent)
        {
            name = null;
            value = 0;
            isPercent = false;
            var match = CallPattern.Match(text ?? string.Empty);
            if (!match.Success) return false;
            name = match.Groups["name"].Value.Trim();
            isPercent = match.Groups["pct"].Success;
            return double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Applies a model reply to every slider with the matching name. Returns a status message either way.</summary>
        public static string ApplyReply(List<GH_NumberSlider> sliders, string reply)
        {
            if (!TryParseCall(reply, out string name, out double value, out bool isPercent))
                return "Error parsing result: '" + reply.Trim() + "'";

            // Exact name first, then a loose match in case the model shortened or padded the name.
            var matches = sliders.Where(s => Same(s.NickName, name)).ToList();
            if (matches.Count == 0)
            {
                var loose = sliders.FirstOrDefault(s => s.NickName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                                                      || name.IndexOf(s.NickName, StringComparison.OrdinalIgnoreCase) >= 0);
                if (loose != null) matches = sliders.Where(s => Same(s.NickName, loose.NickName)).ToList();
            }
            if (matches.Count == 0) return "Slider '" + name + "' not found.";

            string limited = SetValues(matches, s => isPercent ? (double)s.CurrentValue * (1 + value / 100.0) : value);

            string shown = isPercent ? (value >= 0 ? "+" : "") + value.ToString("0.###", CultureInfo.InvariantCulture) + "%"
                                     : value.ToString("0.###", CultureInfo.InvariantCulture);
            return "Changed " + Describe(matches) + " to " + shown + limited;
        }

        /// <summary>
        /// The sliders whose names appear in the spoken command, as whole words. When one name contains another
        /// ("floor" and "floor height") the longer one wins. Every slider sharing a matched name is included.
        /// </summary>
        public static List<GH_NumberSlider> MatchByName(List<GH_NumberSlider> sliders, string command)
        {
            string remaining = command.ToLowerInvariant();
            var matched = new List<GH_NumberSlider>();

            foreach (var group in sliders.GroupBy(s => s.NickName.Trim().ToLowerInvariant()).OrderByDescending(g => g.Key.Length))
            {
                var m = Regex.Match(remaining, @"(?<!\w)" + Regex.Escape(group.Key) + @"(?!\w)");
                if (!m.Success) continue;

                matched.AddRange(group);
                remaining = remaining.Remove(m.Index, m.Length).Insert(m.Index, new string(' ', m.Length)); // don't match inside it again
            }
            return matched;
        }

        /// <summary>"name - value, name - value", with one entry per distinct name (sliders sharing a name move together).</summary>
        public static string PromptValues(List<GH_NumberSlider> sliders)
        {
            return string.Join(", ", sliders
                .GroupBy(s => s.NickName.Trim().ToLowerInvariant())
                .Select(g => g.First().NickName.Trim() + " - " + g.First().CurrentValue.ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>The distinct slider names, for hints to the speech engine.</summary>
        public static IEnumerable<string> Names(List<GH_NumberSlider> sliders) =>
            sliders.Select(s => s.NickName.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Named number sliders wired into the given input parameter, up to <see cref="MaxSliders"/>.
        /// Sliders without a name can't be spoken about, so they are left out.
        /// </summary>
        public static List<GH_NumberSlider> GetSliders(IGH_Param input)
        {
            return input.Sources
                .Select(p => p.Attributes?.GetTopLevel?.DocObject as GH_NumberSlider)
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.NickName))
                .Take(MaxSliders)
                .ToList();
        }

        /// <summary>A note about sliders that were left out (no name, or over the limit), or null if all are used.</summary>
        public static string Notice(IGH_Param input)
        {
            int notSliders = input.Sources.Count(p => !(p.Attributes?.GetTopLevel?.DocObject is GH_NumberSlider));
            var all = input.Sources.Select(p => p.Attributes?.GetTopLevel?.DocObject as GH_NumberSlider).Where(s => s != null).ToList();
            int unnamed = all.Count(s => string.IsNullOrWhiteSpace(s.NickName));
            int named = all.Count - unnamed;

            var notes = new List<string>();
            if (unnamed > 0) notes.Add(unnamed + " slider(s) without a name are ignored. Give them a name to use them.");
            if (notSliders > 0) notes.Add(notSliders + " connected item(s) are not number sliders and are ignored.");
            if (named > MaxSliders) notes.Add("Using the first " + MaxSliders + " of " + named + " named sliders.");
            return notes.Count == 0 ? null : string.Join(" ", notes);
        }

        /// <summary>
        /// Sets slider values on the UI thread and recomputes the solution once. Values outside a slider's range are brought
        /// inside it (and rounded for integer sliders). Returns " (limited to the slider range)" if that happened, else "".
        /// </summary>
        public static string SetValues(IEnumerable<GH_NumberSlider> sliders, Func<GH_NumberSlider, double> value)
        {
            var targets = new List<KeyValuePair<GH_NumberSlider, double>>();
            bool limited = false;
            foreach (var slider in sliders)
            {
                double wanted = value(slider);
                double actual = Fit(slider, wanted);
                if (Math.Abs(actual - wanted) > 1e-9) limited = true;
                targets.Add(new KeyValuePair<GH_NumberSlider, double>(slider, actual));
            }

            Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                foreach (var t in targets)
                {
                    t.Key.SetSliderValue((decimal)t.Value);
                    t.Key.ExpireSolution(false);
                }
                targets.FirstOrDefault().Key?.OnPingDocument()?.NewSolution(false);
            }));
            return limited ? " (limited to the slider range)" : "";
        }

        public static string SetValues(IEnumerable<GH_NumberSlider> sliders, double value) => SetValues(sliders, _ => value);

        /// <summary>The value the slider can actually take: inside its range, whole for integer sliders.</summary>
        public static double Fit(GH_NumberSlider slider, double value)
        {
            double fitted = Math.Min((double)slider.Slider.Maximum, Math.Max((double)slider.Slider.Minimum, value));
            if (slider.Slider.Type == Grasshopper.GUI.Base.GH_SliderAccuracy.Integer) fitted = Math.Round(fitted);
            return fitted;
        }

        /// <summary>"slider 'Height'" or "3 sliders named 'Height'".</summary>
        public static string Describe(List<GH_NumberSlider> sliders) =>
            sliders.Count == 1
                ? "slider '" + sliders[0].NickName + "'"
                : sliders.Count + " sliders (" + string.Join(", ", sliders.Select(s => s.NickName).Distinct(StringComparer.OrdinalIgnoreCase)) + ")";

        static bool Same(string a, string b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
