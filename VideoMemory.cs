using System;
using System.Runtime.InteropServices;

namespace Kakadoo_Final
{
    static class VideoMemory
    {
        /// <summary>Dedicated video memory of the largest graphics card, in bytes (0 if unknown). Windows only.</summary>
        public static long DetectBytes()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return 0;

            long best = 0;
            try
            {
                const string path = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
                using (var classKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path))
                {
                    if (classKey == null) return 0;
                    foreach (string name in classKey.GetSubKeyNames())
                    {
                        try // some subkeys (e.g. "Properties") deny access
                        {
                            using (var adapter = classKey.OpenSubKey(name))
                            {
                                object value = adapter?.GetValue("HardwareInformation.qwMemorySize");
                                long bytes = value is long l ? l
                                           : value is byte[] b && b.Length >= 8 ? BitConverter.ToInt64(b, 0)
                                           : 0;
                                best = Math.Max(best, bytes);
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
            catch (Exception)
            {
                return 0;
            }
            return best;
        }
    }
}
