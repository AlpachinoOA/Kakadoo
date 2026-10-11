using System.Drawing;
using System.Reflection;

namespace Kakadoo_Final
{
    static class Icons
    {
        /// <summary>Loads an embedded 24x24 icon from Resources, e.g. Icons.Load("Kakadoo").</summary>
        public static Bitmap Load(string name)
        {
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Kakadoo_Final.Resources." + name + ".png");
            return stream == null ? null : new Bitmap(stream);
        }
    }
}
