using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AzurAssistant.Contracts;

namespace AzurAssistant.Vision;

public sealed record TemplateDefinition(string File, PixelRegion Search, double Threshold, string Sha256,
    string? Source = null, string? Version = null, string? Channel = null);
public sealed record TemplateManifest(int SchemaVersion, int ReferenceWidth, int ReferenceHeight,
    Dictionary<string, TemplateDefinition> Templates);

public sealed class TemplateCatalog
{
    private readonly TemplateManifest _manifest;
    private readonly Dictionary<string, ImageTemplate> _images = new(StringComparer.Ordinal);

    public TemplateCatalog(string directory, string manifestFile = "recognition.json")
    {
        if (Path.GetFileName(manifestFile) != manifestFile) throw new ArgumentException("模板清单必须位于素材目录内。", nameof(manifestFile));
        _manifest = JsonSerializer.Deserialize<TemplateManifest>(File.ReadAllText(Path.Combine(directory, manifestFile)))
            ?? throw new InvalidDataException("模板清单为空。");
        if (_manifest.SchemaVersion is not (1 or 2) || _manifest.ReferenceWidth <= 0 || _manifest.ReferenceHeight <= 0
            || _manifest.Templates is null || _manifest.Templates.Count == 0)
            throw new InvalidDataException("模板清单格式不受支持。");
        foreach (var (id, definition) in _manifest.Templates)
        {
            if (_manifest.SchemaVersion == 1 && definition.Channel is not null)
                throw new InvalidDataException($"模板颜色通道要求 Schema 2：{id}");
            if (Path.GetFileName(definition.File) != definition.File || string.IsNullOrWhiteSpace(definition.File))
                throw new InvalidDataException($"模板文件名无效：{id}");
            definition.Search.Validate(_manifest.ReferenceWidth, _manifest.ReferenceHeight);
            if (!double.IsFinite(definition.Threshold) || definition.Threshold is <= 0 or > 1)
                throw new InvalidDataException($"模板阈值无效：{id}");
            using var stream = File.OpenRead(Path.Combine(directory, definition.File));
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(definition.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"模板校验失败：{id}");
            stream.Position = 0;
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            var channel = definition.Channel switch
            {
                null or "Gray" => TemplateChannel.Gray,
                "RedMinusBlue" => TemplateChannel.RedMinusBlue,
                _ => throw new InvalidDataException($"模板颜色通道不受支持：{id}/{definition.Channel}")
            };
            var image = OpenCvTemplateMatcher.Create(bitmap.PixelWidth, bitmap.PixelHeight, pixels, channel);
            if (image.Width > definition.Search.Width || image.Height > definition.Search.Height)
                throw new InvalidDataException($"模板大于搜索区域：{id}");
            _images.Add(id, image);
        }
    }

    public void ValidateFrame(FrameSnapshot frame)
    {
        if (frame.Width != _manifest.ReferenceWidth || frame.Height != _manifest.ReferenceHeight)
            throw new InvalidOperationException($"此功能当前仅支持 {_manifest.ReferenceWidth}×{_manifest.ReferenceHeight} 客户区。");
    }

    public TemplateMatch? Match(FrameSnapshot frame, string id, PixelRegion? search = null)
        => Match(frame, id, out _, search);

    public TemplateMatch? Match(FrameSnapshot frame, string id, out double score, PixelRegion? search = null)
    {
        ValidateFrame(frame);
        if (!_manifest.Templates.TryGetValue(id, out var definition))
            throw new InvalidDataException($"模板清单缺少 {id}，请更新完整识别资源包。");
        var region = search ?? definition.Search;
        region.Validate(_manifest.ReferenceWidth, _manifest.ReferenceHeight);
        var result = OpenCvTemplateMatcher.Match(frame, _images[id], region);
        score = result.Score;
        return result.Score >= definition.Threshold ? result : null;
    }
}
