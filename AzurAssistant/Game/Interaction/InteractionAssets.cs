using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Game.Interaction;

internal sealed class InteractionAssets
{
    public int SchemaVersion { get; set; }
    public string Version { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public PixelRegion DialogueTextRegion { get; set; }
    public int IconSearchX { get; set; }
    public int IconSearchWidth { get; set; }
    public int RowHeight { get; set; }
    public int MaxRowDistance { get; set; }
    public int NameOffsetX { get; set; }
    public int NameWidth { get; set; }
    public int FirstRowY { get; set; }
    public int RowSpacing { get; set; }
    public int MaxVisibleRows { get; set; }
    public Asset[] Templates { get; set; } = [];
    private readonly Dictionary<string, (Asset Asset, ImageTemplate Template)> _templates = [];

    public static InteractionAssets Load(string directory)
    {
        var assets = JsonSerializer.Deserialize<InteractionAssets>(File.ReadAllText(Path.Combine(directory, "manifest.json")))
            ?? throw new InvalidOperationException("实时辅助素材清单为空。");
        if (assets.SchemaVersion != 2 || assets.Version != "1.1" || assets.Width != 1920 || assets.Height != 1080
            || assets.Templates.Length != 11 || assets.RowHeight < 32 || assets.RowHeight > 50
            || assets.MaxRowDistance is < 1 or > 10 || assets.MaxVisibleRows is < 1 or > 5
            || assets.RowSpacing < assets.RowHeight || assets.NameOffsetX < 20 || assets.NameWidth < 20)
            throw new InvalidOperationException("实时辅助素材清单版本或参数无效。");
        assets.DialogueTextRegion.Validate(assets.Width, assets.Height);
        for (var index = 0; index < assets.MaxVisibleRows; index++)
            new PixelRegion(assets.IconSearchX, assets.FirstRowY + index * assets.RowSpacing - assets.RowHeight / 2,
                assets.IconSearchWidth, assets.RowHeight).Validate(assets.Width, assets.Height);
        foreach (var item in assets.Templates)
        {
            if (Path.GetFileName(item.File) != item.File || item.Threshold is < 0.75 or > 1)
                throw new InvalidOperationException("实时辅助模板文件名或阈值无效。");
            item.Search.Validate(assets.Width, assets.Height);
            using var stream = File.OpenRead(Path.Combine(directory, item.File));
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"实时辅助模板校验失败：{item.File}");
            stream.Position = 0;
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var image = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            var pixels = new byte[checked(image.PixelWidth * image.PixelHeight * 4)];
            image.CopyPixels(pixels, image.PixelWidth * 4, 0);
            assets._templates.Add(item.Id, (item, OpenCvTemplateMatcher.Create(image.PixelWidth, image.PixelHeight, pixels)));
        }
        foreach (var id in new[] { "dialogue-auto", "dialogue-skip", "world-menu", "world-hud", "interact-key", "pickup-icon", "talk-icon",
                     "dialogue-summary-title", "dialogue-summary-confirm", "dialogue-prompt-question", "dialogue-prompt-confirm" })
            if (!assets._templates.ContainsKey(id)) throw new InvalidOperationException($"缺少实时辅助模板：{id}");
        return assets;
    }

    public TemplateMatch? Find(FrameSnapshot frame, string id, PixelRegion? region = null)
    {
        var (asset, template) = _templates[id];
        var match = OpenCvTemplateMatcher.Match(frame, template, region ?? asset.Search);
        return match.Score >= asset.Threshold ? match : null;
    }
    public sealed record Asset(string Id, string File, string Sha256, double Threshold, PixelRegion Search);
}
