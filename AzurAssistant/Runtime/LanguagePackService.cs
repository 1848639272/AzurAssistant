using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AzurAssistant.Runtime;

/// <summary>One language pack folder under 语言包\ (manifest.json + the asset bundles to install).</summary>
public sealed record LanguagePack(string Code, string Label, string Directory, bool Ready);

/// <summary>Runs the bundled Switch-GameLanguage.ps1 so the app can apply a pack without leaving the UI.</summary>
public sealed class LanguagePackService(string packRoot, string scriptPath)
{
    public string PackRoot => packRoot;
    public string ScriptPath => scriptPath;

    public IReadOnlyList<LanguagePack> List()
    {
        if (!Directory.Exists(packRoot)) return [];
        var packs = new List<LanguagePack>();
        foreach (var directory in Directory.GetDirectories(packRoot))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith('_') || name.StartsWith('.')) continue;
            var manifest = Path.Combine(directory, "manifest.json");
            var ready = File.Exists(manifest) && Directory.Exists(Path.Combine(directory, "files"));
            var label = name;
            if (ready)
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(manifest, Encoding.UTF8));
                    if (document.RootElement.TryGetProperty("label", out var value) && value.GetString() is { Length: > 0 } text)
                        label = text;
                }
                catch (JsonException) { ready = false; }
            }
            packs.Add(new LanguagePack(name, $"{label}（{name}）", directory, ready));
        }
        return packs.OrderBy(pack => pack.Code, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task<(bool Ok, string Output)> ApplyAsync(string code, CancellationToken token) =>
        RunAsync(["-Language", code], token);

    public Task<(bool Ok, string Output)> RestoreAsync(CancellationToken token) => RunAsync(["-Restore"], token);

    private async Task<(bool Ok, string Output)> RunAsync(string[] arguments, CancellationToken token)
    {
        if (!File.Exists(scriptPath)) return (false, $"找不到脚本：{scriptPath}");
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 PowerShell。");
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (false, "切换超时，已终止脚本。");
            }
            var text = (await output) + (await error);
            var ok = process.ExitCode == 0;
            return (ok, string.IsNullOrWhiteSpace(text) ? (ok ? "已完成。" : $"脚本退出码 {process.ExitCode}。") : text.Trim());
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (false, "运行脚本失败：" + ex.Message);
        }
    }
}
