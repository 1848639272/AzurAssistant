using System.IO;
using System.Media;

namespace AzurAssistant.Runtime;

/// <summary>
/// Short synthesized chimes for schedule start/finish. The waveforms are generated in memory, so the assistant does not
/// need to ship or locate any audio asset.
/// </summary>
public static class TaskSound
{
    private const int SampleRate = 22050;

    private static readonly Lazy<byte[]> StartWave = new(() => Build([(880, 90), (1320, 130)]));
    private static readonly Lazy<byte[]> FinishWave = new(() => Build([(1320, 90), (880, 110), (660, 150)]));

    public static void PlayStart() => Play(StartWave.Value);
    public static void PlayFinish() => Play(FinishWave.Value);

    private static void Play(byte[] wave)
    {
        // PlaySync blocks its own worker thread only; the UI thread stays responsive.
        _ = Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream(wave, writable: false);
                using var player = new SoundPlayer(stream);
                player.PlaySync();
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or PlatformNotSupportedException) { }
        });
    }

    private static byte[] Build((double Hertz, int Milliseconds)[] notes)
    {
        var samples = new List<short>(notes.Sum(note => SampleRate * note.Milliseconds / 1000));
        foreach (var (hertz, milliseconds) in notes)
        {
            var count = SampleRate * milliseconds / 1000;
            for (var index = 0; index < count; index++)
            {
                var progress = (double)index / count;
                // Smooth attack/release plus a gentle decay keeps the chime from clicking.
                var envelope = Math.Min(1, progress * 12) * Math.Min(1, (1 - progress) * 4) * (1 - progress * 0.35);
                var sample = Math.Sin(2 * Math.PI * hertz * index / SampleRate) * envelope * 0.35;
                samples.Add((short)(sample * short.MaxValue));
            }
        }
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        var dataBytes = samples.Count * 2;
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);          // PCM
        writer.Write((short)1);          // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);    // byte rate
        writer.Write((short)2);          // block align
        writer.Write((short)16);         // bits per sample
        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);
        foreach (var sample in samples) writer.Write(sample);
        writer.Flush();
        return output.ToArray();
    }
}
