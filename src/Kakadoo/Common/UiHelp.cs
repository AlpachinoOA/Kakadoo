using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;

using Grasshopper.Kernel;

namespace Kakadoo
{
    /// <summary>Turns exceptions into a sentence a user can act on.</summary>
    static class ErrorHelp
    {
        public static string Explain(Exception e)
        {
            if (e is SpeechSetupException)
                return e.Message;
            if (e is UnauthorizedAccessException)
                return Hardware.IsMac
                    ? "Access was denied. Allow Rhino in System Settings > Privacy & Security > Microphone (and Files and Folders if asked), then restart Rhino."
                    : "Access was denied. Allow microphone access for desktop apps in your system privacy settings, then run again.";
            if (e is HttpRequestException || e is WebException)
                return "The model could not be downloaded. Check your internet connection, or put the model file in " + WhisperModelFolder + " (or set its path in the right-click menu).";
            if (e is OutOfMemoryException)
                return "Not enough memory for this model. Set Model to \"fast\" in Kakadoo Settings.";
            if (e is IOException && e.Message.IndexOf("space", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Not enough disk space to save the model in " + WhisperModelFolder + ".";
            if (e is DllNotFoundException || e is BadImageFormatException)
                return Hardware.IsMac
                    ? "A required library was blocked by macOS (" + e.Message + "). In Terminal run: xattr -dr com.apple.quarantine <Kakadoo plugin folder> and restart Rhino. See the README for the full commands."
                    : "A required library could not be loaded (" + e.Message + "). Reinstall Kakadoo and the Microsoft Visual C++ Redistributable (x64).";
            return e.Message;
        }

        static string WhisperModelFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Kakadoo", "models");

        /// <summary>Explains the error, shows it under the component on the canvas, and returns the text for the log.</summary>
        public static string Report(GH_Component owner, Exception e)
        {
            string text = Explain(e);
            ProgressAttributes.ShowNote(owner, text, NoteKind.Error, 20);
            return text;
        }
    }

    /// <summary>One per component: remembers that a refresh is already queued.</summary>
    sealed class RefreshGate
    {
        internal int Pending;
    }

    /// <summary>Asks a component to recompute from any thread without interrupting a running solution.</summary>
    static class UiRefresh
    {
        /// <summary>
        /// Repeated requests are merged, and the update is scheduled with the document, so in a big definition it waits for the
        /// current solution instead of cutting into it.
        /// </summary>
        public static void Request(GH_Component owner, RefreshGate gate)
        {
            if (Interlocked.Exchange(ref gate.Pending, 1) == 1) return;
            Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                Interlocked.Exchange(ref gate.Pending, 0);
                owner.OnPingDocument()?.ScheduleSolution(5, d => owner.ExpireSolution(false));
            }));
        }
    }
}
