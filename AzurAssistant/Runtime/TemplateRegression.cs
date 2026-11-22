using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Runtime;

/// <summary>One template scored against a saved frame.</summary>
public sealed record TemplateProbe(string Pack, string Id, double Score, double Threshold, bool Passed)
{
    public string Text => $"{(Passed ? "✓" : "✗")} {Pack}/{Id}  {Score:F3} / {Threshold:F2}";
}

public sealed record RegressionReport(int Total, int Passed, IReadOnlyList<TemplateProbe> Probes, IReadOnlyList<string> Errors)
{
    public string Summary => Total == 0
        ? "没有可复测的模板。" + string.Join("；", Errors)
        : $"共 {Total} 个模板：通过 {Passed}、未达阈值 {Total - Passed}。"
          + (Errors.Count > 0 ? "　注意：" + string.Join("；", Errors) : "");
}

/// <summary>Scores every template of every asset pack against a recorded frame, without touching the game.</summary>
public static class TemplateRegression
{
    /// <summary>Loads a saved PNG/JPEG failure frame as a reference-space snapshot.</summary>
    public static FrameSnapshot? LoadImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            var pixels = new byte[checked(width * height * 4)];
            converted.CopyPixels(pixels, width * 4, 0);
            return new FrameSnapshot(0, TimeSpan.Zero, DateTimeOffset.Now, width, height, 0, 0, pixels);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Runs every manifest under <paramref name="assetsRoot"/> against the frame.</summary>
    public static RegressionReport Run(string assetsRoot, FrameSnapshot frame)
    {
        var probes = new List<TemplateProbe>();
        var errors = new List<string>();
        if (!Directory.Exists(assetsRoot))
            return new RegressionReport(0, 0, probes, ["素材目录不存在：" + assetsRoot]);

        foreach (var manifest in Directory.GetFiles(assetsRoot, "recognition.json", SearchOption.AllDirectories))
        {
            var directory = Path.GetDirectoryName(manifest)!;
            var pack = Path.GetFileName(directory);
            TemplateCatalog catalog;
            try
            {
                catalog = new TemplateCatalog(directory);
                catalog.ValidateFrame(frame);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException)
            {
                errors.Add($"{pack}: {ex.Message}");
                continue;
            }
            foreach (var id in catalog.Ids)
            {
                try
                {
                    catalog.Match(frame, id, out var score);
                    probes.Add(new TemplateProbe(pack, id, score, catalog.ThresholdFor(id), score >= catalog.ThresholdFor(id)));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
                {
                    errors.Add($"{pack}/{id}: {ex.Message}");
                }
            }
        }
        probes.Sort((left, right) => right.Score.CompareTo(left.Score));
        return new RegressionReport(probes.Count, probes.Count(probe => probe.Passed), probes, errors);
    }
}
