using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using AzurAssistant.Runtime;

namespace AzurAssistant.Platform;

/// <summary>Reads published releases including beta releases, and downloads only validated full installers.</summary>
public sealed class GitHubReleaseClient(HttpClient http, UpdateSource source)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("AzurAssistant/0.1.0");
        if (uri.Host == "api.github.com")
        {
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        }
        return request;
    }

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken token)
    {
        source.Validate();
        if (!source.IsConfigured) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        token = timeout.Token;
        using var request = Request(new Uri($"https://api.github.com/repos/{source.GitHubRepository}/releases?per_page=100"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadLimitedAsync(response, 4_000_000, token));
        foreach (var release in document.RootElement.EnumerateArray()
            .Where(r => !r.GetProperty("draft").GetBoolean())
            .OrderByDescending(r => r.GetProperty("published_at").GetString(), StringComparer.Ordinal))
        {
            var assets = release.GetProperty("assets").EnumerateArray().ToArray();
            var manifestAsset = assets.FirstOrDefault(a => a.GetProperty("name").GetString() == "release-manifest.json");
            if (manifestAsset.ValueKind == JsonValueKind.Undefined) continue;
            var tag = release.GetProperty("tag_name").GetString()!;
            var manifestUri = AssetUri(manifestAsset, tag);
            using var manifestRequest = Request(manifestUri);
            using var manifestResponse = await http.SendAsync(manifestRequest, HttpCompletionOption.ResponseHeadersRead, token);
            manifestResponse.EnsureSuccessStatusCode();
            var manifest = JsonSerializer.Deserialize<AssistantRelease>(await ReadLimitedAsync(manifestResponse, 64_000, token), JsonOptions)
                ?? throw new InvalidDataException("发布清单为空。");
            manifest.Validate(installer: true);
            if (manifest.ReleaseId != tag) throw new InvalidDataException("发布标签与清单不匹配。");
            var installer = assets.FirstOrDefault(a => a.GetProperty("name").GetString() == manifest.InstallerFile);
            if (installer.ValueKind == JsonValueKind.Undefined || installer.GetProperty("size").GetInt64() != manifest.InstallerSize)
                throw new InvalidDataException("发布安装包缺失或大小不匹配。");
            return new(manifest, AssetUri(installer, tag), release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "");
        }
        return null;
    }

    private Uri AssetUri(JsonElement asset, string tag)
    {
        var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!, UriKind.Absolute);
        var expected = $"/{source.GitHubRepository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(asset.GetProperty("name").GetString()!)}";
        if (uri.Scheme != "https" || uri.Host != "github.com" || uri.AbsolutePath != expected || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("发布文件地址不属于已配置的 GitHub 仓库。");
        return uri;
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("更新响应过大。");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[16_384];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("更新响应过大。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public async Task<string> DownloadAsync(AvailableUpdate update, string directory, IProgress<double>? progress, CancellationToken token)
    {
        update.Release.Validate(installer: true);
        var expectedPath = $"/{source.GitHubRepository}/releases/download/{Uri.EscapeDataString(update.Release.ReleaseId)}/{Uri.EscapeDataString(update.Release.InstallerFile)}";
        if (update.DownloadUri.Scheme != "https" || update.DownloadUri.Host != "github.com" || update.DownloadUri.AbsolutePath != expectedPath
            || !string.IsNullOrEmpty(update.DownloadUri.UserInfo) || !string.IsNullOrEmpty(update.DownloadUri.Query) || !string.IsNullOrEmpty(update.DownloadUri.Fragment))
            throw new InvalidDataException("安装包地址不属于已配置的 GitHub 仓库。");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, update.Release.InstallerFile);
        var partial = path + ".part";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            using var request = Request(update.DownloadUri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != update.Release.InstallerSize)
                throw new InvalidDataException("安装包下载大小不匹配。");
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131_072, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[131_072];
                long downloaded = 0;
                while (true)
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var count = await input.ReadAsync(buffer, readTimeout.Token);
                    if (count == 0) break;
                    downloaded += count;
                    if (downloaded > update.Release.InstallerSize) throw new InvalidDataException("安装包下载大小不匹配。");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    progress?.Report(100.0 * downloaded / update.Release.InstallerSize);
                }
                if (downloaded != update.Release.InstallerSize || !Convert.ToHexString(hash.GetHashAndReset())
                    .Equals(update.Release.InstallerSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装包校验失败，请重新检查更新。");
            }
            File.Move(partial, path);
            return path;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
