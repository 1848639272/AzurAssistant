using System.IO;
using System.Media;
using AzurAssistant.Runtime;

namespace AzurAssistant.Runtime;

/// <summary>
/// Short synthesized chimes for scheduled start/finish. The waveforms are generated in memory, so the assistant does not
/// need to ship or locate any audio asset, and every preset can be previewed on demand.
/// </summary>
public static class TaskSound
{
    private const int SampleRate = 22050;

    private static readonly Dictionary<string, (byte[] Start, byte[] Finish)> Waves = Build();

    private static System.Windows.Media.MediaPlayer? _filePlayer;

    /// <summary>Directory holding user-imported audio; <c>file:</c> specs are resolved against it.</summary>
    public static string? SoundDirectory { get; set; }

    public static void PlayStart(string? spec) => PlaySpec(spec, start: true);
    public static void PlayFinish(string? spec) => PlaySpec(spec, start: false);

    private static void PlaySpec(string? spec, bool start)
    {
        var normalized = ScheduleClock.NormalizeSound(spec);
        if (ScheduleClock.IsFileSpec(normalized))
        {
            PlayFile(normalized.Substring(ScheduleClock.FilePrefix.Length));
            return;
        }
        Play(start ? Waves[normalized].Start : Waves[normalized].Finish);
    }

    /// <summary>Plays a user-imported wav/mp3/wma through the WPF media stack.</summary>
    private static void PlayFile(string name)
    {
        try
        {
            var path = System.IO.Path.IsPathRooted(name)
                ? name
                : SoundDirectory is { } directory ? System.IO.Path.Combine(directory, name) : name;
            if (!System.IO.File.Exists(path)) return;
            _filePlayer?.Close();
            var player = new System.Windows.Media.MediaPlayer { Volume = 1.0 };
            player.MediaEnded += (_, _) => player.Close();
            player.MediaFailed += (_, _) => player.Close();
            player.Open(new Uri(path));
            player.Play();
            _filePlayer = player;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException or System.IO.IOException or NotSupportedException) { }
    }

    private static Dictionary<string, (byte[] Start, byte[] Finish)> Build() => new(StringComparer.Ordinal)
    {
        ["chime"] = (Wave((880, 90), (1320, 130)), Wave((1320, 90), (880, 110), (660, 170))),
        ["soft"] = (Wave((660, 150), (990, 190)), Wave((990, 150), (740, 200), (550, 240))),
        ["deep"] = (Wave((330, 170), (440, 210)), Wave((440, 170), (330, 220), (220, 260))),
        ["double"] = (Wave((880, 70), (0, 60), (880, 70), (0, 60)), Wave((880, 70), (0, 60), (880, 70), (0, 60), (660, 150))),
    };

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

    private static byte[] Wave(params (double Hertz, int Milliseconds)[] notes)
    {
        var samples = new List<short>(notes.Sum(note => SampleRate * note.Milliseconds / 1000));
        foreach (var (hertz, milliseconds) in notes)
        {
            var count = SampleRate * milliseconds / 1000;
            for (var index = 0; index < count; index++)
            {
                if (hertz <= 0) { samples.Add(0); continue; }
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
