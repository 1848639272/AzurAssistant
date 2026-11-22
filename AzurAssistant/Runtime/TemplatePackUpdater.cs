using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using AzurAssistant.Vision;

namespace AzurAssistant.Runtime;

public sealed record TemplateUpdateResult(bool Ok, string Message, int FilesUpdated = 0, string? BackupPath = null);

/// <summary>
/// Installs a newer recognition asset pack (zip with assets\*\recognition.json + template files).
/// Every pack is validated with the same loader the runtime uses before anything is replaced.
/// </summary>
public sealed class TemplatePackUpdater(string assetsRoot)
{
    public string AssetsRoot => assetsRoot;

    /// <summary>Reads TemplatePackUrl from update-source.json next to the executable.</summary>
    public static string SourceUrl()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "update-source.json");
            if (!File.Exists(path)) return "";
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var property in document.RootElement.EnumerateObject())
                if (string.Equals(property.Name, "TemplatePackUrl", StringComparison.OrdinalIgnoreCase))
                    return property.Value.GetString() ?? "";
            return "";
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return "";
        }
    }

    public async Task<TemplateUpdateResult> InstallAsync(string source, CancellationToken token)
    {
        string? localZip = null;
        var temporaryDownload = false;
        try
        {
            if (File.Exists(source))
            {
                localZip = source;
            }
            else if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                localZip = Path.Combine(Path.GetTempPath(), "azur-templates-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
                temporaryDownload = true;
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                await using var target = File.Create(localZip);
                await using var stream = await client.GetStreamAsync(uri, token);
                await stream.CopyToAsync(target, token);
            }
            else
            {
                return new TemplateUpdateResult(false, "模板包地址无效：需要一个本地 zip 或 http(s) 链接。");
            }

            var staging = Path.Combine(Path.GetTempPath(), "azur-tpl-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(staging);
            try
            {
                ZipFile.ExtractToDirectory(localZip, staging, overwriteFiles: true);
                var packRoot = ResolveRoot(staging);
                var manifests = Directory.GetFiles(packRoot, "recognition.json", SearchOption.AllDirectories);
                if (manifests.Length == 0)
                    return new TemplateUpdateResult(false, "模板包里没有找到 recognition.json，已取消。");

                // Validate every pack exactly as the runtime would (files, sizes and SHA-256).
                foreach (var manifest in manifests)
                {
                    token.ThrowIfCancellationRequested();
                    try { _ = new TemplateCatalog(Path.GetDirectoryName(manifest)!); }
                    catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException)
                    {
                        return new TemplateUpdateResult(false, $"模板包校验失败（{Path.GetFileName(Path.GetDirectoryName(manifest))}）：{ex.Message}");
                    }
                }

                var backup = assetsRoot + "_backup_" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                if (Directory.Exists(assetsRoot))
                {
                    CopyDirectory(assetsRoot, backup);
                }
                Directory.CreateDirectory(assetsRoot);
                var updated = CopyDirectory(packRoot, assetsRoot);
                var backedUp = Directory.Exists(backup);
                return new TemplateUpdateResult(true,
                    $"已更新 {updated} 个文件（{manifests.Length} 个资产包）"
                    + (backedUp ? $"；原文件备份在 {Path.GetFileName(backup)}" : "；首次安装，没有需要备份的旧文件"),
                    updated, backedUp ? backup : null);
            }
            finally
            {
                try { Directory.Delete(staging, true); } catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or TaskCanceledException)
        {
            return new TemplateUpdateResult(false, "安装模板包失败：" + ex.Message);
        }
        finally
        {
            if (temporaryDownload && localZip is not null)
            {
                try { File.Delete(localZip); } catch (IOException) { }
            }
        }
    }

    private static string ResolveRoot(string staging)
    {
        // Accept both "assets\..." and "recognition.json at the zip root" layouts.
        var assets = Path.Combine(staging, "assets");
        if (Directory.Exists(assets)) return assets;
        var candidates = Directory.GetDirectories(staging);
        if (Directory.GetFiles(staging, "recognition.json", SearchOption.AllDirectories).Length > 0) return staging;
        return candidates.Length == 1 ? candidates[0] : staging;
    }

    private static int CopyDirectory(string source, string target)
    {
        var copied = 0;
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            copied++;
        }
        return copied;
    }
}
