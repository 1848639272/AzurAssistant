using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AzurAssistant.Runtime;

public sealed record UpdateSource(int SchemaVersion = 1, string GitHubRepository = "")
{
    public bool IsConfigured => !string.IsNullOrEmpty(GitHubRepository);
    public void Validate()
    {
        if (SchemaVersion != 1 || GitHubRepository is null ||
            (IsConfigured && !Regex.IsMatch(GitHubRepository, @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z")))
            throw new InvalidDataException("GitHub 更新源配置无效。");
    }
}

public sealed record AssistantRelease
{
    public int SchemaVersion { get; init; } = 1;
    public string Product { get; init; } = "AzurAssistant";
    public string ProductVersion { get; init; } = "0.1.0-beta";
    public long BuildNumber { get; init; }
    public string ReleaseId { get; init; } = "development";
    public string Architecture { get; init; } = "win-x64";
    public string InstallerFile { get; init; } = "";
    public string InstallerSha256 { get; init; } = "";
    public long InstallerSize { get; init; }
    public void Validate(bool installer = false)
    {
        if (SchemaVersion != 1 || Product != "AzurAssistant" || Architecture != "win-x64" || BuildNumber < 0
            || string.IsNullOrWhiteSpace(ProductVersion) || string.IsNullOrWhiteSpace(ReleaseId))
            throw new InvalidDataException("发布信息不兼容。");
        if (installer && (BuildNumber == 0 || string.IsNullOrEmpty(InstallerFile)
            || !Regex.IsMatch(InstallerFile, @"\AAzurAssistant-[A-Za-z0-9_.-]+-setup\.exe\z")
            || !Regex.IsMatch(InstallerSha256 ?? "", @"\A[0-9a-fA-F]{64}\z")
            || InstallerSize is <= 0 or > 2_000_000_000))
            throw new InvalidDataException("发布安装包信息无效。");
    }
}

public sealed record AvailableUpdate(AssistantRelease Release, Uri DownloadUri, string Notes);
public enum UpdateCheckStatus { NotConfigured, Skipped, Current, Available, Failed }
public sealed record UpdateCheckResult(UpdateCheckStatus Status, string Message, AvailableUpdate? Update = null);

/// <summary>Daily reminder persistence is separate from the user's preferences; manual checks always bypass it.</summary>
public sealed class AssistantUpdate(UpdateSource source, AssistantRelease installed,
    Func<CancellationToken, Task<AvailableUpdate?>> check, string statePath, Action<string> log,
    Func<DateOnly>? today = null)
{
    private bool _checking;
    public async Task<UpdateCheckResult> CheckAsync(bool automatic, bool enabled, CancellationToken token = default)
    {
        if (_checking) return new(UpdateCheckStatus.Skipped, "正在检查更新。");
        _checking = true;
        try
        {
            source.Validate(); installed.Validate();
            if (!source.IsConfigured) return new(UpdateCheckStatus.NotConfigured, "尚未配置 GitHub 更新仓库。");
            if (automatic)
            {
                if (!enabled) return new(UpdateCheckStatus.Skipped, "自动更新已关闭。");
                var day = (today?.Invoke() ?? DateOnly.FromDateTime(DateTime.Now)).ToString("yyyy-MM-dd");
                if (File.Exists(statePath))
                {
                    var previous = JsonSerializer.Deserialize<DailyUpdateState>(File.ReadAllText(statePath))
                        ?? throw new InvalidDataException("自动检查记录为空。");
                    if (previous.SchemaVersion != 1) throw new InvalidDataException("自动检查记录版本不兼容。");
                    if (previous.Repository == source.GitHubRepository && previous.LocalDate == day)
                        return new(UpdateCheckStatus.Skipped, "今天已经自动检查过更新。");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
                File.WriteAllText(statePath + ".tmp", JsonSerializer.Serialize(new DailyUpdateState(1, source.GitHubRepository, day)));
                File.Move(statePath + ".tmp", statePath, true);
            }
            var update = await check(token);
            if (update is null || update.Release.BuildNumber <= installed.BuildNumber)
                return new(UpdateCheckStatus.Current, "当前已是最新发布版本。");
            return new(UpdateCheckStatus.Available, "发现新版本。", update);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            log($"连接 GitHub 失败：{ex.Message}");
            return new(UpdateCheckStatus.Failed, "连接 GitHub 失败，请检查网络后重试。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException
            or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            log($"检查更新失败：{ex.Message}");
            return new(UpdateCheckStatus.Failed, $"检查更新失败：{ex.Message}");
        }
        finally { _checking = false; }
    }
    private sealed record DailyUpdateState(int SchemaVersion, string Repository, string LocalDate);
}
