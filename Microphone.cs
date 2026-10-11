using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Kakadoo_Final
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

                double threshold = Math.Max(0.01, noise * 3.5);

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
                    }
                    else if (DateTime.UtcNow - started > waitForSpeech)
                    {
                        return false;
                    }
                    return true;
                }

                phrase.Write(chunk, 0, chunk.Length);
                quietChunks = level > threshold * 0.7 ? 0 : quietChunks + 1;

                // About 0.7 s of quiet ends the phrase.
                if (quietChunks * ChunkMs >= 700) return false;
                return DateTime.UtcNow - speechStart < maxLength;
            });

            return speaking ? phrase.ToArray() : new byte[0];
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

        /// <summary>Streams ~50 ms chunks of audio to <paramref name="onChunk"/> until it returns false or the token is cancelled.</summary>
        static void Capture(int sampleRate, CancellationToken token, Func<byte[], bool> onChunk)
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
            var device = OpenTK.Audio.OpenAL.ALC.CaptureOpenDevice(null, sampleRate, OpenTK.Audio.OpenAL.ALFormat.Mono16, sampleRate);
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
