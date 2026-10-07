using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AzurAssistant.Vision;
using AzurAssistant.Features.StartJourney;
using System.Text.Json;
using System.Security.Cryptography;
using AzurAssistant.Contracts;
using AzurAssistant.Game.Ui;

namespace AzurAssistant.Composition;

public static class JourneyAssets
{
    public static StartJourneyRecognizer CreateRecognizer(PpOcrReader ocr, Func<FrameSnapshot, CancellationToken, Task<bool>>? isWorld = null)
    {
        var settings = JsonSerializer.Deserialize<JourneySettings>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "start-journey", "recognition.json")))
            ?? throw new InvalidOperationException("开始旅程识别配置为空。");
        settings.Validate();
        var directory = Path.Combine(AppContext.BaseDirectory, "assets", "start-journey");
        using (var stream = File.OpenRead(Path.Combine(directory, "age-rating.png")))
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(settings.AgeSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("适龄标识模板校验失败。");
        if (isWorld is null)
        {
            var daily = new DailyUiRecognizer(new TemplateCatalog(Path.Combine(AppContext.BaseDirectory, "assets", "daily")), new WorldHudReader(ocr));
            isWorld = async (frame, token) => (await daily.ObserveAsync(frame, token)).Page is DailyPage.World or DailyPage.Menu;
        }
        return new StartJourneyRecognizer(Load("age-rating.png"), settings, isWorld,
            new JourneyRewardRecognizer(directory), new JourneyUpdateRecognizer(new TemplateCatalog(directory, "update.json")));
    }
    public static ImageTemplate Load(string name)
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "assets", "start-journey", name));
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return OpenCvTemplateMatcher.Create(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }
}
