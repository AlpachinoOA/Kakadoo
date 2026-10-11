using System;
using System.Drawing;
using Grasshopper;
using Grasshopper.Kernel;

namespace Kakadoo
{
    public class KakadooInfo : GH_AssemblyInfo
    {
        public override string Name => "Kakadoo";

        //Return a 24x24 pixel bitmap to represent this GHA library.
        public override Bitmap Icon => Icons.Load("Kakadoo");

        //Return a short string describing the purpose of this GHA library.
        public override string Description => "Manipulate sliders with your voice!";

        public override Guid Id => new Guid("9d46dcdc-b757-4d7a-9c08-781f56b98e56");

        //Return a string identifying you or your company.
        public override string AuthorName => "Alp Okan Atakan";

        //Return a string representing your preferred contact details.
        public override string AuthorContact => "atakanalpokan@gmail.com";

        //Return a string representing the version.  This returns the same version as the assembly.
        public override string AssemblyVersion => GetType().Assembly.GetName().Version.ToString();
    }
}