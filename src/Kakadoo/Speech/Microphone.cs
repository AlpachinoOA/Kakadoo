using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Kakadoo
{
    /// <summary>Records 16-bit mono PCM from the default microphone (NAudio on Windows, OpenAL on Mac).</summary>
    static class Microphone
    {
        const int ChunkMs = 50;

        /// <summary>Records for a fixed time.</summary>
        public static byte[] Record(int sampleRate, TimeSpan duration, CancellationToken token)
        {
            var result = new MemoryStream();
            var end = DateTime.UtcNow + duration;
            Capture(sampleRate, token, chunk =>
            {
                result.Write(chunk, 0, chunk.Length);
                return DateTime.UtcNow < end;
            });
            return result.ToArray();
        }

        /// <summary>
        /// Waits for someone to start speaking, then records until they stop. Returns an empty array if nobody spoke
        /// within <paramref name="waitForSpeech"/>. The silence level adapts to the room, so quiet microphones work too.
        /// </summary>
        public static byte[] RecordPhrase(int sampleRate, TimeSpan waitForSpeech, TimeSpan maxLength, CancellationToken token)
        {
            var phrase = new MemoryStream();
            var preRoll = new Queue<byte[]>(); // keeps the moment before speech was detected so the first word isn't clipped
            var started = DateTime.UtcNow;
            double noise = -1;
            int calibrationChunks = 0, loudRun = 0, quietChunks = 0;
            bool speaking = false;
            long lastLoud = 0; // where the phrase last had speech in it, so the trailing silence can be cut off
            DateTime speechStart = default;

            Capture(sampleRate, token, chunk =>
            {
                double level = Rms(chunk);

                // The first ~300 ms set the noise floor.
                if (calibrationChunks < 6)
                {
                    noise = noise < 0 ? level : Math.Min(noise, level);
                    calibrationChunks++;
                    preRoll.Enqueue(chunk);
                    return true;
                }

                double threshold = Math.Max(0.01, noise * Settings.NoiseFactor);

                if (!speaking)
                {
                    preRoll.Enqueue(chunk);
                    while (preRoll.Count > 8) preRoll.Dequeue();

                    loudRun = level > threshold ? loudRun + 1 : 0;
                    if (loudRun >= 2)
                    {
                        speaking = true;
                        speechStart = DateTime.UtcNow;
                        foreach (var c in preRoll) phrase.Write(c, 0, c.Length);
                        lastLoud = phrase.Length;
                    }
                    else if (DateTime.UtcNow - started > waitForSpeech)
                    {
                        return false;
                    }
                    return true;
                }

                phrase.Write(chunk, 0, chunk.Length);
                quietChunks = level > threshold * 0.7 ? 0 : quietChunks + 1;
                if (quietChunks == 0) lastLoud = phrase.Length;

                // A pause of the preset length (0.7 s by default) ends the phrase.
                if (quietChunks * ChunkMs >= Settings.EndSilenceMs) return false;
                return DateTime.UtcNow - speechStart < maxLength;
            });

            if (!speaking) return new byte[0];

            // Whisper's time grows with the length of the audio, so drop the silence that ended the phrase (keep 0.2 s).
            long keep = Math.Min(phrase.Length, lastLoud + (long)(sampleRate * 0.2) * 2);
            var audio = phrase.ToArray();
            if (keep < audio.Length) Array.Resize(ref audio, (int)keep);
            return audio;
        }

        /// <summary>Loudness of 16-bit PCM audio, 0 to 1.</summary>
        public static double Rms(byte[] pcm16)
        {
            int n = pcm16.Length / 2;
            if (n == 0) return 0;
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                double s = BitConverter.ToInt16(pcm16, i * 2) / 32768.0;
                sum += s * s;
            }
            return Math.Sqrt(sum / n);
        }

        /// <summary>Peak amplitude (0 to 1) of 16-bit PCM audio.</summary>
        public static double Peak(byte[] pcm16)
        {
            int peak = 0;
            for (int i = 0; i + 1 < pcm16.Length; i += 2)
                peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(pcm16, i)));
            return peak / 32768.0;
        }

        /// <summary>Names of the microphones that are plugged in right now (empty if they can't be listed).</summary>
        static string[] cachedDevices;
        static long cachedAt;

        public static string[] Devices()
        {
            // Listing can take a moment and a settings component asks on every solve, so reuse the answer for a few seconds.
            var cached = cachedDevices;
            if (cached != null && Environment.TickCount64 - cachedAt < 3000) return cached;
            cached = ListDevices();
            cachedAt = Environment.TickCount64;
            return cachedDevices = cached;
        }

        static string[] ListDevices()
        {
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT) return WindowsDevices();
#if !NETFRAMEWORK
                return OpenALDevices();
#endif
            }
            catch (Exception)
            {
                // Listing is a convenience; recording still works with the default microphone.
            }
            return new string[0];
        }

#if !NETFRAMEWORK
        // Kept in its own method so OpenTK is only loaded on Mac; the Windows build doesn't ship it.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static string[] OpenALDevices() =>
            OpenTK.Audio.OpenAL.ALC.GetStringList(OpenTK.Audio.OpenAL.GetEnumerationStringList.CaptureDeviceSpecifier).ToArray();
#endif

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string[] WindowsDevices()
        {
            var names = new List<string>();
            for (int i = 0; i < NAudio.Wave.WaveInEvent.DeviceCount; i++)
                names.Add(NAudio.Wave.WaveInEvent.GetCapabilities(i).ProductName);
            return names.ToArray();
        }

        /// <summary>The device number for the microphone chosen in the settings, or the default when it isn't found. Windows truncates names to 31 characters, so it matches by prefix.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static int WindowsDevice()
        {
            string wanted = Settings.Microphone;
            if (string.IsNullOrWhiteSpace(wanted)) return 0;
            var names = WindowsDevices();
            for (int i = 0; i < names.Length; i++)
                if (wanted.StartsWith(names[i], StringComparison.OrdinalIgnoreCase) || names[i].StartsWith(wanted, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

#if !NETFRAMEWORK
        /// <summary>The OpenAL device name for the chosen microphone, or null for the default.</summary>
        static string OpenALDevice()
        {
            string wanted = Settings.Microphone;
            if (string.IsNullOrWhiteSpace(wanted)) return null;
            return Devices().FirstOrDefault(d => string.Equals(d, wanted, StringComparison.OrdinalIgnoreCase));
        }
#endif

        /// <summary>How loud the microphone is right now, 0 to 1 (smoothed). Drives the strength of the listening ripple.</summary>
        public static volatile float Level;

        /// <summary>
        /// Streams ~50 ms chunks to <paramref name="onChunk"/> until it returns false or the token is cancelled. Also tracks the loudness
        /// and stops early when another microphone is chosen in the settings, so the change applies at once.
        /// </summary>
        static void Capture(int sampleRate, CancellationToken token, Func<byte[], bool> onChunk)
        {
            string mic = Settings.Microphone;
            try
            {
                CaptureRaw(sampleRate, token, chunk =>
                {
                    Level = (float)Math.Max(Rms(chunk), Level * 0.85);
                    return onChunk(chunk) && Settings.Microphone == mic;
                });
            }
            finally
            {
                Level = 0;
            }
        }

        static void CaptureRaw(int sampleRate, CancellationToken token, Func<byte[], bool> onChunk)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                CaptureWindows(sampleRate, token, onChunk);
                return;
            }

#if !NETFRAMEWORK
            CaptureOpenAL(sampleRate, token, onChunk);
#else
            throw new PlatformNotSupportedException("Microphone capture is not supported on this platform.");
#endif
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void CaptureWindows(int sampleRate, CancellationToken token, Func<byte[], bool> onChunk)
        {
            var chunks = new BlockingCollection<byte[]>();
            using (var waveIn = new NAudio.Wave.WaveInEvent
            {
                WaveFormat = new NAudio.Wave.WaveFormat(sampleRate, 16, 1),
                BufferMilliseconds = ChunkMs
            })
            {
                waveIn.DataAvailable += (s, e) =>
                {
                    var copy = new byte[e.BytesRecorded];
                    Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
                    chunks.Add(copy);
                };
                try
                {
                    waveIn.StartRecording();
                }
                catch (Exception e) when (e is NAudio.MmException || e is InvalidOperationException)
                {
                    throw new InvalidOperationException("No microphone found, or access is blocked. Check Windows Settings > Privacy & security > Microphone and allow desktop apps.", e);
                }
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (chunks.TryTake(out var chunk, 200) && !onChunk(chunk)) break;
                    }
                }
                finally
                {
                    waveIn.StopRecording();
                }
            }
        }

#if !NETFRAMEWORK
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void CaptureOpenAL(int sampleRate, CancellationToken token, Func<byte[], bool> onChunk)
        {
            var device = OpenTK.Audio.OpenAL.ALC.CaptureOpenDevice(OpenALDevice(), sampleRate, OpenTK.Audio.OpenAL.ALFormat.Mono16, sampleRate);
            if (device == OpenTK.Audio.OpenAL.ALCaptureDevice.Null)
                throw new InvalidOperationException("No microphone found, or microphone access was denied (System Settings > Privacy & Security > Microphone).");

            try
            {
                OpenTK.Audio.OpenAL.ALC.CaptureStart(device);
                var buffer = new byte[sampleRate * 2];
                while (!token.IsCancellationRequested)
                {
                    token.WaitHandle.WaitOne(ChunkMs);
                    int available;
                    OpenTK.Audio.OpenAL.ALC.GetInteger(device, OpenTK.Audio.OpenAL.AlcGetInteger.CaptureSamples, 1, out available);
                    if (available <= 0) continue;

                    int count = Math.Min(available, buffer.Length / 2);
                    OpenTK.Audio.OpenAL.ALC.CaptureSamples(device, buffer, count);
                    var chunk = new byte[count * 2];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, chunk.Length);
                    if (!onChunk(chunk)) break;
                }
                OpenTK.Audio.OpenAL.ALC.CaptureStop(device);
            }
            finally
            {
                OpenTK.Audio.OpenAL.ALC.CaptureCloseDevice(device);
            }
        }
#endif
    }
}
