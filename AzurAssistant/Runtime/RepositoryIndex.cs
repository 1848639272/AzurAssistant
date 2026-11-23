using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzurAssistant.Runtime;

/// <summary>One shareable item in a community index: a flow, a task chain or a template pack.</summary>
public sealed record RepositoryItem
{
    public string Kind { get; init; } = "flow";
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public string Url { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string Version { get; init; } = "";

    public string KindLabel => RepositoryTarget.LabelFor(Kind);
    public string Subtitle => string.Join(" · ", new[] { KindLabel, Author.Length > 0 ? "by " + Author : "", Version }
        .Where(part => part.Length > 0));
    public override string ToString() => Name;
}

/// <summary>The list of items a repository URL returns. Parsing is lenient: bad entries are dropped and reported.</summary>
public sealed record RepositoryIndex
{
    public const int CurrentSchema = 1;
    public const int MaxItems = 200;

    public int SchemaVersion { get; init; } = CurrentSchema;
    public string Version { get; init; } = "";
    public IReadOnlyList<RepositoryItem> Items { get; init; } = [];
    [JsonIgnore] public string? Error { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static RepositoryIndex Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new RepositoryIndex { Error = "索引为空。" };
        try
        {
            var parsed = JsonSerializer.Deserialize<RepositoryIndex>(json, Json);
            if (parsed is null) return new RepositoryIndex { Error = "索引不是有效的 JSON。" };
            if (parsed.SchemaVersion != CurrentSchema)
                return new RepositoryIndex { Error = $"索引版本 {parsed.SchemaVersion} 不受支持（需要 {CurrentSchema}）。" };
            var items = parsed.Items
                .Where(item => RepositoryTarget.IsKnownKind(item.Kind))
                .Where(item => Uri.TryCreate(item.Url, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .Take(MaxItems)
                .ToList();
            return parsed with { Items = items };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return new RepositoryIndex { Error = "索引解析失败：" + ex.Message };
        }
    }
}

/// <summary>Maps an item kind to the folder it is installed into.</summary>
public static class RepositoryTarget
{
    public const string Flow = "flow";
    public const string Chain = "chain";
    public const string Assets = "assets";

    public static bool IsKnownKind(string? kind) => kind is Flow or Chain or Assets;

    public static string FolderFor(string kind) => kind switch
    {
        Chain => "chains",
        Assets => Path.Combine("assets", "packages"),
        _ => "flows",
    };

    public static string LabelFor(string kind) => kind switch
    {
        Chain => "任务队列",
        Assets => "模板包",
        _ => "流程",
    };
}

/// <summary>Downloads index entries and installs them into the assistant's folders.</summary>
public sealed class RepositoryService(string dataRoot)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public string DataRoot => dataRoot;

    /// <summary>Fetches and parses the index at <paramref name="url"/>.</summary>
    public async Task<RepositoryIndex> FetchIndexAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            return new RepositoryIndex { Error = "仓库地址需要是 http(s) 链接。" };
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var json = await client.GetStringAsync(uri, token).ConfigureAwait(false);
            return RepositoryIndex.Parse(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new RepositoryIndex { Error = "获取索引失败：" + ex.Message };
        }
    }

    /// <summary>Downloads one item into its folder, verifying the optional SHA-256.</summary>
    public async Task<(bool Success, string Message, string? Path)> InstallAsync(RepositoryItem item, CancellationToken token)
    {
        if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            return (false, "下载地址无效。", null);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var bytes = await client.GetByteArrayAsync(uri, token).ConfigureAwait(false);
            if (bytes.Length == 0) return (false, "下载到的文件是空的。", null);
            if (item.Sha256.Length > 0)
            {
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (!hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                    return (false, $"SHA-256 不匹配（期望 {item.Sha256[..Math.Min(12, item.Sha256.Length)]}…，实际 {hash[..12]}…）。", null);
            }

            var folder = Path.Combine(dataRoot, RepositoryTarget.FolderFor(item.Kind));
            Directory.CreateDirectory(folder);
            var name = SafeName(item.Name.Length > 0 ? item.Name : "下载项");
            var extension = item.Kind == RepositoryTarget.Assets ? ".zip" : ".json";
            var path = Path.Combine(folder, name + extension);
            for (var index = 2; File.Exists(path); index++) path = Path.Combine(folder, $"{name}-{index}{extension}");
            await File.WriteAllBytesAsync(path, bytes, token).ConfigureAwait(false);
            return (true, $"已安装到 {Path.GetRelativePath(dataRoot, path)}", path);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
            or UnauthorizedAccessException)
        {
            return (false, "下载失败：" + ex.Message, null);
        }
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
            builder.Append(invalid.Contains(character) ? '_' : character);
        var text = builder.ToString().Trim().TrimEnd('.');
        return text.Length == 0 ? "下载项" : text.Length > 60 ? text[..60] : text;
    }
}
