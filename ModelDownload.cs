using System;
using System.Diagnostics;

namespace Kakadoo_Final
{
    /// <summary>Progress of the one-time model download, shown as a bar under the running component.</summary>
    static class ModelDownload
    {
        static readonly Stopwatch Throttle = new Stopwatch();

        public static volatile bool Active;
        public static volatile string Title = "";
        public static volatile string Folder = "";
        public static double Fraction { get; private set; }
        public static long DoneBytes { get; private set; }
        public static long TotalBytes { get; private set; }

        /// <summary>Raised on a background thread whenever the progress changes (at most about 5 times a second).</summary>
        public static event Action Changed;

        public static void Start(string title, string folder)
        {
            Title = title;
            Folder = folder;
            Fraction = 0;
            DoneBytes = TotalBytes = 0;
            Active = true;
            Throttle.Restart();
            Changed?.Invoke();
        }

        public static void Report(long done, long total)
        {
            DoneBytes = done;
            TotalBytes = total;
            Fraction = total > 0 ? Math.Min(1.0, (double)done / total) : 0;
            if (Throttle.ElapsedMilliseconds < 200) return;
            Throttle.Restart();
            Changed?.Invoke();
        }

        public static void Finish()
        {
            Active = false;
            Changed?.Invoke();
        }
    }
}
