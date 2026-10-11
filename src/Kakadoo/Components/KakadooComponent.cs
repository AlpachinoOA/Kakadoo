using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

using GH_IO.Serialization;
using Grasshopper.GUI;
using Grasshopper.Kernel;

namespace Kakadoo
{
    /// <summary>Status messages grouped into runs. Only the newest runs are kept.</summary>
    public class MessageLog : IEnumerable<string>
    {
        readonly List<List<string>> runs = new List<List<string>>(); // oldest first

        public int Limit { get; set; } = 4;

        public bool IsEmpty { get { lock (runs) return runs.Count == 0; } }

        /// <summary>Starts a new run, dropping the oldest ones beyond the limit.</summary>
        public void Clear()
        {
            lock (runs)
            {
                runs.Add(new List<string>());
                Trim();
            }
        }

        public void Add(string message)
        {
            lock (runs)
            {
                if (runs.Count == 0) runs.Add(new List<string>());
                runs[runs.Count - 1].Add(message);
            }
        }

        public void Trim()
        {
            lock (runs) while (runs.Count > Math.Max(1, Limit)) runs.RemoveAt(0);
        }

        /// <summary>All messages, newest run first, with a separator line between runs.</summary>
        public IEnumerator<string> GetEnumerator()
        {
            var lines = new List<string>();
            lock (runs)
            {
                foreach (var run in Enumerable.Reverse(runs))
                {
                    if (lines.Count > 0) lines.Add("---");
                    lines.AddRange(run);
                }
            }
            return lines.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Base for Kakadoo components that report status messages. Adds a "Keep last runs" setting to the right-click menu.</summary>
    public abstract class KakadooComponent : GH_Component
    {
        protected readonly MessageLog messages = new MessageLog();

        protected KakadooComponent(string name, string nickname, string description, string category, string subCategory)
          : base(name, nickname, description, category, subCategory)
        {
        }

        public override void CreateAttributes() => m_attributes = new ProgressAttributes(this);

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendItem(menu, "Keep last runs:", null, false);
            Menu_AppendTextItem(menu, messages.Limit.ToString(), null, (box, text) =>
            {
                if (int.TryParse(text, out int limit) && limit >= 1 && limit <= 100)
                {
                    messages.Limit = limit;
                    messages.Trim();
                    ExpireSolution(true);
                }
            }, true);

            if (UsesWhisper) AppendWhisperMenu(menu);
        }

        /// <summary>Whether this component listens with Whisper, and so shows the model path setting.</summary>
        protected virtual bool UsesWhisper => true;

        /// <summary>The "Whisper model file or folder" entry, shared by every component that listens with Whisper.</summary>
        public static void AppendWhisperMenu(ToolStripDropDown menu)
        {
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "Whisper model file or folder (blank = automatic):", null, false);
            Menu_AppendTextItem(menu, Settings.WhisperModelPath ?? "", null, (box, text) => Settings.WhisperModelPath = text, true);
        }

        public override bool Write(GH_IWriter writer)
        {
            writer.SetInt32("HistoryLimit", messages.Limit);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            int limit = 4;
            if (reader.TryGetInt32("HistoryLimit", ref limit)) messages.Limit = Math.Max(1, limit);
            return base.Read(reader);
        }
    }
}
