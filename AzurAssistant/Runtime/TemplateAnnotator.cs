using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AzurAssistant.Vision;

namespace AzurAssistant.Runtime;

public sealed record AnnotationResult(bool Ok, string Message, string TemplatePath = "", string ManifestPath = "");

/// <summary>
/// Cuts a template out of a captured frame and registers it in a pack's recognition.json,
/// so a broken or missing template can be re-recorded without touching the game.
/// </summary>
public static class TemplateAnnotator
{
    /// <summary>Extra margin around the selection that becomes the search area.</summary>
    public const int DefaultMargin = 60;

    public static AnnotationResult Create(string packDirectory, string framePath, string templateId,
        PixelRegion selection, double threshold, int margin = DefaultMargin)
    {
        if (string.IsNullOrWhiteSpace(templateId) || templateId.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '_' or '.')))
            return new AnnotationResult(false, "模板名只能包含字母、数字、- _ .");
        var manifestPath = Path.Combine(packDirectory, "recognition.json");
        if (!File.Exists(manifestPath)) return new AnnotationResult(false, "找不到模板清单：" + manifestPath);
        if (!File.Exists(framePath)) return new AnnotationResult(false, "找不到画面文件：" + framePath);

        try
        {
            var frame = TemplateRegression.LoadImage(framePath);
            if (frame is null) return new AnnotationResult(false, "无法读取画面：" + framePath);
            if (selection.X < 0 || selection.Y < 0 || selection.Width <= 0 || selection.Height <= 0
                || (long)selection.X + selection.Width > frame.Width || (long)selection.Y + selection.Height > frame.Height)
                return new AnnotationResult(false, "选区超出画面范围。");

            var templateFile = templateId + ".png";
            var templatePath = Path.Combine(packDirectory, templateFile);
            var cropped = CropPixels(frame, selection);
            // Reject unusable selections (flat colours have no features) before any file is written.
            try { _ = OpenCvTemplateMatcher.Create(selection.Width, selection.Height, cropped); }
            catch (ArgumentException) { return new AnnotationResult(false, "这块区域太平了（没有可匹配的纹理），请框住有细节的部分。"); }
            SavePng(cropped, selection.Width, selection.Height, templatePath);

            var search = Expand(selection, margin, frame.Width, frame.Height);
            var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(templatePath))).ToLowerInvariant();

            var text = File.ReadAllText(manifestPath);
            if (JsonNode.Parse(text) is not JsonObject root) return new AnnotationResult(false, "模板清单格式无法识别。");
            // Real manifests are PascalCase (TemplateManifest record); keep that casing or the runtime ignores the entry.
            var templatesKey = root.Select(pair => pair.Key)
                .FirstOrDefault(key => string.Equals(key, "templates", StringComparison.OrdinalIgnoreCase)) ?? "Templates";
            if (root[templatesKey] is not JsonObject templates)
            {
                templates = new JsonObject();
                root[templatesKey] = templates;
            }
            templates[templateId] = new JsonObject
            {
                ["File"] = templateFile,
                ["Search"] = new JsonObject
                {
                    ["X"] = search.X, ["Y"] = search.Y, ["Width"] = search.Width, ["Height"] = search.Height,
                },
                ["Threshold"] = Math.Round(Math.Clamp(threshold, 0.5, 0.99), 2),
                ["Sha256"] = sha.ToUpperInvariant(),
            };
            File.WriteAllText(manifestPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            // The new crop was already validated; still reload the manifest so a broken *other* entry is reported.
            var warning = "";
            try { _ = new TemplateCatalog(packDirectory); }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException)
            { warning = $"（注意：该资产包里还有其它模板无法加载：{ex.Message}）"; }
            return new AnnotationResult(true,
                $"已保存 {templateFile}（{selection.Width}×{selection.Height}），搜索区 {search.Width}×{search.Height}，阈值 {threshold:F2}{warning}",
                templatePath, manifestPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ArgumentException)
        {
            return new AnnotationResult(false, "保存模板失败：" + ex.Message);
        }
    }

    public static PixelRegion Expand(PixelRegion selection, int margin, int frameWidth, int frameHeight)
    {
        var x = Math.Max(0, selection.X - margin);
        var y = Math.Max(0, selection.Y - margin);
        var right = Math.Min(frameWidth, selection.X + selection.Width + margin);
        var bottom = Math.Min(frameHeight, selection.Y + selection.Height + margin);
        return new PixelRegion(x, y, right - x, bottom - y);
    }

    private static byte[] CropPixels(AzurAssistant.Contracts.FrameSnapshot frame, PixelRegion region)
    {
        var pixels = frame.Pixels.ToArray();
        var cropped = new byte[region.Width * region.Height * 4];
        for (var row = 0; row < region.Height; row++)
        {
            var sourceOffset = ((region.Y + row) * frame.Width + region.X) * 4;
            Buffer.BlockCopy(pixels, sourceOffset, cropped, row * region.Width * 4, region.Width * 4);
        }
        return cropped;
    }

    private static void SavePng(byte[] pixels, int width, int height, string targetPath)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(targetPath);
        encoder.Save(output);
    }
}
