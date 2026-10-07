using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AzurAssistant.Contracts;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using SkiaSharp;

namespace AzurAssistant.Vision;

public enum OcrPreprocessing { Original, WhiteText }

/// <summary>Local Chinese PP-OCRv5 inference; one serialized, lazily loaded CPU session set per reader.</summary>
public sealed class PpOcrReader(string? modelDirectory = null) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RapidOcr? _engine;
    private bool _disposed;
    private readonly string _modelDirectory = modelDirectory ?? Path.Combine(AppContext.BaseDirectory, "models", "ppocr-v5");
    private static readonly RapidOcrOptions Options = RapidOcrOptions.Default with
    {
        ImgResize = 1920, Padding = 20, MaxSideLen = 2400, DoAngle = false,
        TextScore = 0.60f, RecMaxDegreeOfParallelism = 1
    };

    public async Task<IReadOnlyList<TextRegion>> ReadAsync(FrameSnapshot frame, CancellationToken token,
        PixelRegion? region = null, int scale = 1, OcrPreprocessing preprocessing = OcrPreprocessing.Original)
    {
        token.ThrowIfCancellationRequested();
        var roi = region ?? new PixelRegion(0, 0, frame.Width, frame.Height);
        roi.Validate(frame.Width, frame.Height);
        if (scale is < 1 or > 3 || (long)roi.Width * scale > 6000 || (long)roi.Height * scale > 6000)
            throw new ArgumentOutOfRangeException(nameof(scale));
        if (!Enum.IsDefined(preprocessing)) throw new ArgumentOutOfRangeException(nameof(preprocessing));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() => Read(frame, roi, scale, preprocessing, token), token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private IReadOnlyList<TextRegion> Read(FrameSnapshot frame, PixelRegion roi, int scale, OcrPreprocessing preprocessing, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _engine ??= LoadEngine(token);
        var width = checked(roi.Width * scale);
        var height = checked(roi.Height * scale);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var row = new byte[width * 4];
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                frame.Pixels.Span.Slice(((y / scale + roi.Y) * frame.Width + x / scale + roi.X) * 4, 4)
                    .CopyTo(row.AsSpan(x * 4, 4));
                if (preprocessing == OcrPreprocessing.WhiteText)
                {
                    var b = row[x * 4]; var g = row[x * 4 + 1]; var r = row[x * 4 + 2];
                    var minimum = Math.Min(r, Math.Min(g, b)); var maximum = Math.Max(r, Math.Max(g, b));
                    // White HUD glyphs remain bright and nearly neutral over changing saturated scenery.
                    var value = (byte)(minimum >= 175 && maximum - minimum <= 60 ? 0 : 255);
                    row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = value;
                }
                row[x * 4 + 3] = 255;
            }
            Marshal.Copy(row, 0, bitmap.GetPixels() + y * bitmap.RowBytes, row.Length);
        }
        var result = _engine.Detect(bitmap, Options, token);
        token.ThrowIfCancellationRequested();
        return result.TextBlocks.Where(block => block.BoxPoints.Length > 0).Select(block =>
        {
            var left = block.BoxPoints.Min(p => p.X);
            var top = block.BoxPoints.Min(p => p.Y);
            var right = block.BoxPoints.Max(p => p.X);
            var bottom = block.BoxPoints.Max(p => p.Y);
            return new TextRegion(block.Text, roi.X + left / (double)scale, roi.Y + top / (double)scale,
                (right - left) / (double)scale, (bottom - top) / (double)scale,
                block.CharScores is { Length: > 0 } scores ? scores.Average() : 0)
            {
                CharacterConfidences = block.CharScores is { } characters && characters.Length == block.Text.Length
                    ? Array.AsReadOnly(characters.ToArray()) : null
            };
        }).ToArray();
    }

    private RapidOcr LoadEngine(CancellationToken token)
    {
        var manifest = JsonSerializer.Deserialize<ModelManifest>(File.ReadAllText(Path.Combine(_modelDirectory, "manifest.json")))
            ?? throw new InvalidOperationException("PP-OCR 模型清单为空。");
        if (manifest.SchemaVersion != 1 || manifest.Files.Length != 4)
            throw new InvalidOperationException("PP-OCR 模型清单版本或文件数量不受支持。");
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            if (Path.GetFileName(file.File) != file.File) throw new InvalidOperationException("PP-OCR 模型文件名无效。");
            using var stream = File.OpenRead(Path.Combine(_modelDirectory, file.File));
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"PP-OCR 模型校验失败：{file.File}，请恢复完整模型包。");
        }
        string Asset(string role) => Path.Combine(_modelDirectory, manifest.Files.Single(f => f.Role == role).File);
        using var options = new SessionOptions
        {
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };
        var engine = new RapidOcr();
        try
        {
            engine.InitModels(Asset("Detection"), Asset("Classification"), Asset("Recognition"), Asset("Dictionary"), options);
            token.ThrowIfCancellationRequested();
            return engine;
        }
        catch { engine.Dispose(); throw; }
    }

    public void Dispose()
    {
        _gate.Wait();
        try { if (_disposed) return; _disposed = true; _engine?.Dispose(); _engine = null; }
        finally { _gate.Release(); }
    }

    private sealed record ModelManifest(int SchemaVersion, ModelFile[] Files);
    private sealed record ModelFile(string Role, string File, string Sha256);
}
