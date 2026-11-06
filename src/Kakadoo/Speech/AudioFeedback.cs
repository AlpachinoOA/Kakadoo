using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Kakadoo
{
    /// <summary>
    /// Around every recording: an optional start sound, system audio muted while Kakadoo listens (so music is not picked up),
    /// and an optional stop sound. Windows only; on other systems it does nothing.
    /// </summary>
    sealed class RecordingSession : IDisposable
    {
        bool mutedByUs;

        public static RecordingSession Begin()
        {
            var session = new RecordingSession();
            if (!SpeechListeners.IsWindows) return session;
            try
            {
                SystemAudio.RecoverAfterCrash();
                if (Settings.AudioFeedback) AudioFeedback.Play(true, true); // before muting, and before the microphone calibrates, so the beep is not recorded
                if (Settings.MuteWhileRecording) session.mutedByUs = SystemAudio.Mute();
            }
            catch (Exception) { /* sounds and muting are conveniences; recording goes on without them */ }
            return session;
        }

        public void Dispose()
        {
            if (!SpeechListeners.IsWindows) return;
            try
            {
                if (mutedByUs) SystemAudio.Restore();
                if (Settings.AudioFeedback) AudioFeedback.Play(false, false);
            }
            catch (Exception) { }
        }
    }

    /// <summary>Mutes the default playback device and gives it back, but only if it was not muted already.</summary>
    static class SystemAudio
    {
        static readonly object Gate = new object();
        static int depth;
        static bool recovered;

        // If Rhino dies while Kakadoo has the sound muted, this file tells the next session to give the sound back.
        static string Flag => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Kakadoo", "muted.flag");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void RecoverAfterCrash()
        {
            if (recovered) return;
            recovered = true;
            try
            {
                if (!System.IO.File.Exists(Flag)) return;
                using (var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator())
                using (var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia))
                    device.AudioEndpointVolume.Mute = false;
                System.IO.File.Delete(Flag);
            }
            catch (Exception) { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Mute()
        {
            lock (Gate)
            {
                using (var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator())
                using (var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia))
                {
                    if (device.AudioEndpointVolume.Mute) return false; // the user muted it: leave it alone afterwards
                    device.AudioEndpointVolume.Mute = true;
                    try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Flag)); System.IO.File.WriteAllText(Flag, "muted"); } catch (Exception) { }
                    depth++;
                    return true;
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Restore()
        {
            lock (Gate)
            {
                if (depth > 0) depth--;
                if (depth > 0) return; // another recording is still running
                using (var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator())
                using (var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia))
                    device.AudioEndpointVolume.Mute = false;
                try { System.IO.File.Delete(Flag); } catch (Exception) { }
            }
        }
    }

    /// <summary>Short start and stop beeps, played on the device and volume chosen in the settings.</summary>
    static class AudioFeedback
    {
        const int Rate = 22050;

        /// <summary>The playback devices of this PC.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string[] OutputDevices()
        {
            var names = new List<string>();
            try
            {
                using (var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator())
                    foreach (var device in enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
                    {
                        names.Add(device.FriendlyName);
                        device.Dispose();
                    }
            }
            catch (Exception) { }
            return names.ToArray();
        }

        /// <summary>A rising pair of tones for start, a falling pair for stop.</summary>
        static byte[] Tone(bool start)
        {
            double[] freqs = start ? new[] { 660.0, 880.0 } : new[] { 880.0, 660.0 };
            int per = Rate / 14; // about 70 ms each
            var pcm = new byte[per * freqs.Length * 2];
            for (int t = 0; t < freqs.Length; t++)
                for (int i = 0; i < per; i++)
                {
                    double envelope = Math.Min(1.0, Math.Min(i / 200.0, (per - i) / 400.0)); // soft edges, no clicks
                    short sample = (short)(Math.Sin(2 * Math.PI * freqs[t] * i / Rate) * envelope * 0.6 * short.MaxValue);
                    BitConverter.GetBytes(sample).CopyTo(pcm, (t * per + i) * 2);
                }
            return pcm;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Play(bool start, bool wait)
        {
            var task = Task.Run(() =>
            {
                try
                {
                    var format = new NAudio.Wave.WaveFormat(Rate, 16, 1);
                    using (var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator())
                    using (var device = PickDevice(enumerator))
                    using (var stream = new MemoryStream(Tone(start)))
                    using (var raw = new NAudio.Wave.RawSourceWaveStream(stream, format))
                    using (var output = new NAudio.Wave.WasapiOut(device, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 100))
                    using (var finished = new ManualResetEventSlim())
                    {
                        var volume = new NAudio.Wave.SampleProviders.VolumeSampleProvider(NAudio.Wave.WaveExtensionMethods.ToSampleProvider(raw)) { Volume = Settings.FeedbackVolume / 100f };
                        output.PlaybackStopped += (s, e) => finished.Set();
                        output.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider16(volume));
                        output.Play();
                        finished.Wait(1500);
                    }
                }
                catch (Exception) { /* no sound device: no sound */ }
            });
            if (wait) task.Wait(2000);
        }

        /// <summary>The chosen output device, or the system default when it is not set or not found.</summary>
        static NAudio.CoreAudioApi.MMDevice PickDevice(NAudio.CoreAudioApi.MMDeviceEnumerator enumerator)
        {
            string wanted = Settings.FeedbackDevice;
            if (!string.IsNullOrWhiteSpace(wanted))
                foreach (var device in enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
                {
                    if (string.Equals(device.FriendlyName, wanted, StringComparison.OrdinalIgnoreCase)) return device;
                    device.Dispose();
                }
            return enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
        }
    }
}
