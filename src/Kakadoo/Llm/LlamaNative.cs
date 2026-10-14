using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Kakadoo
{
    /// <summary>
    /// Makes LLamaSharp find llama.cpp inside the plugin folder (runtimes/{rid}/native).
    /// The build of LLamaSharp that Rhino plugins use has no path setting and only probes next to Rhino.exe.
    /// </summary>
    static class LlamaNative
    {
        static bool registered;

        /// <summary>True if the loaded llama.cpp build uses the GPU (Vulkan on Windows, Metal on Mac).</summary>
        public static bool IsGpuBuild { get; private set; }

        /// <summary>False if the llama.cpp libraries are missing from the plugin folder.</summary>
        public static bool LibrariesFound { get; private set; } = true;

        /// <summary>
        /// Loads llama.cpp. Native libraries can't be swapped inside a running process, so the first call decides
        /// between the CPU and GPU build. Returns false if a later call asks for the other one.
        /// </summary>
        public static bool Register(bool wantGpu)
        {
            if (registered) return IsGpuBuild == wantGpu || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            registered = true;
            MacTrust.Prepare();

            string dir = FindNativeDirectory(wantGpu);
            LibrariesFound = dir != null;
            if (dir == null) return false;

            bool mac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            string FileFor(string name) => Path.Combine(dir, mac ? "lib" + name + ".dylib" : name + ".dll");

            // llama.cpp loads its CPU/GPU backends from the same folder at startup, so load them first.
            // The Vulkan build ships without a CPU backend, so borrow it from the CPU build.
            foreach (var dep in new[] { "ggml-base", "ggml-cpu", "ggml-blas", "ggml-metal", "ggml-vulkan", "ggml" })
            {
                string file = FileFor(dep);
                if (!File.Exists(file) && dep == "ggml-cpu" && FindCpuDirectory() is string cpu)
                    file = Path.Combine(cpu, Path.GetFileName(file));
                if (File.Exists(file)) Preload(file);
            }
            IsGpuBuild = mac || dir.EndsWith("vulkan");

#if !NETFRAMEWORK
            NativeLibrary.SetDllImportResolver(typeof(LLama.LLamaWeights).Assembly, (name, assembly, path) =>
            {
                if (name != "llama" && name != "mtmd" && name != "ggml" && name != "ggml-base") return IntPtr.Zero;
                string file = FileFor(name);
                return File.Exists(file) && NativeLibrary.TryLoad(file, out var handle) ? handle : IntPtr.Zero;
            });
#else
            foreach (var name in new[] { "llama", "mtmd" })
                if (File.Exists(FileFor(name))) Preload(FileFor(name));
#endif
            return true;
        }

        static string FindCpuDirectory() => FindNativeDirectory(false);

        static string FindNativeDirectory(bool gpu)
        {
            string root = Path.Combine(Path.GetDirectoryName(typeof(LlamaNative).Assembly.Location), "runtimes");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
                return Existing(Path.Combine(root, rid, "native"));
            }

            string win = Path.Combine(root, RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64", "native");
            if (gpu && Existing(Path.Combine(win, "vulkan")) is string vulkan) return vulkan;
#if NET7_0_OR_GREATER
            string level = System.Runtime.Intrinsics.X86.Avx2.IsSupported ? "avx2"
                         : System.Runtime.Intrinsics.X86.Avx.IsSupported ? "avx" : "noavx";
#else
            string level = "avx";
#endif
            return Existing(Path.Combine(win, level));
        }

        static string Existing(string dir) => Directory.Exists(dir) ? dir : null;

        static void Preload(string file)
        {
#if !NETFRAMEWORK
            NativeLibrary.TryLoad(file, out _);
#else
            LoadLibraryEx(file, IntPtr.Zero, 0x8); // LOAD_WITH_ALTERED_SEARCH_PATH
#endif
        }

#if NETFRAMEWORK
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibraryEx(string file, IntPtr hFile, uint flags);
#endif
    }
}
