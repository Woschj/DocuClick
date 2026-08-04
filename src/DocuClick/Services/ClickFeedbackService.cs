using System;
using System.IO;
using System.Media;
using System.Text;

namespace DocuClick.Services;

/// <summary>
/// Audible confirmation that a click was actually captured — the capture
/// itself is intentionally invisible (no screen flash), so without this
/// there is no way to tell whether the tool is doing anything at all.
/// </summary>
public static class ClickFeedbackService
{
    // Windows has no built-in "camera shutter" SystemSound, and
    // SystemSounds.Beep reads as an error/alert tone rather than
    // confirmation, so the capture sound is synthesized once at startup:
    // two short exponentially-decaying noise bursts approximating a
    // shutter click.
    private static readonly byte[] CapturedClickWav = BuildCameraClickWav();

    public static void PlayCaptured()
    {
        using var stream = new MemoryStream(CapturedClickWav);
        using var player = new SoundPlayer(stream);
        player.Play();
    }

    public static void PlaySkipped() => SystemSounds.Asterisk.Play();

    public static void PlayError() => SystemSounds.Hand.Play();

    private static byte[] BuildCameraClickWav()
    {
        const int sampleRate = 44100;
        const double totalSeconds = 0.09;
        int totalSamples = (int)(sampleRate * totalSeconds);
        var samples = new short[totalSamples];

        AddNoiseBurst(samples, sampleRate, startSeconds: 0.0, durationSeconds: 0.02, amplitude: 0.9);
        AddNoiseBurst(samples, sampleRate, startSeconds: 0.045, durationSeconds: 0.02, amplitude: 0.6);

        return WriteWav(samples, sampleRate);
    }

    private static void AddNoiseBurst(short[] samples, int sampleRate, double startSeconds, double durationSeconds, double amplitude)
    {
        int start = (int)(startSeconds * sampleRate);
        int length = (int)(durationSeconds * sampleRate);
        var rng = new Random(12345);

        for (int i = 0; i < length && start + i < samples.Length; i++)
        {
            double t = (double)i / length;
            double envelope = Math.Exp(-t * 18); // fast decay: mechanical "tick" rather than a tone
            double noise = rng.NextDouble() * 2 - 1;
            double value = noise * envelope * amplitude * short.MaxValue;

            int idx = start + i;
            samples[idx] = (short)Math.Clamp(samples[idx] + value, short.MinValue, short.MaxValue);
        }
    }

    private static byte[] WriteWav(short[] samples, int sampleRate)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        int byteRate = sampleRate * 2;
        int dataSize = samples.Length * 2;

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)2);  // block align
        writer.Write((short)16); // bits per sample
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        foreach (short sample in samples)
        {
            writer.Write(sample);
        }

        return stream.ToArray();
    }
}
