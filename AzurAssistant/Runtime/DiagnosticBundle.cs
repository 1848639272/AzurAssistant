using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using AzurAssistant.Platform;

namespace AzurAssistant.Runtime;

/// <summary>Packs logs, settings, templates, failure frames and environment details into one zip for support.</summary>
public sealed class DiagnosticBundle(string dataRoot, DailyLogStore log)
{
    private static readonly string[] SettingsFiles = ["settings.json", "schedule.json", "actions", "wheelchair"];

    public string Create(string targetZipPath, int maxFailureImages = 10, int maxLogFiles = 3)
    {
        var staging = Path.Combine(Path.GetTempPath(), "azur-diag-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            File.WriteAllText(Path.Combine(staging, "environment.txt"), EnvironmentText(), Encoding.UTF8);

            foreach (var name in SettingsFiles)
            {
                var source = Path.Combine(dataRoot, name);
                if (File.Exists(source)) File.Copy(source, Path.Combine(staging, name), overwrite: true);
                else if (Directory.Exists(source)) CopyDirectory(source, Path.Combine(staging, name));
            }

            var templates = Path.Combine(dataRoot, "templates");
            if (Directory.Exists(templates))
            {
                CopyDirectory(templates, Path.Combine(staging, "templates"), ".json");
                var manifest = Path.Combine(templates, "recognition.json");
                if (File.Exists(manifest)) File.Copy(manifest, Path.Combine(staging, "templates", "recognition.json"), true);
            }

            var runs = Path.Combine(dataRoot, "runs.jsonl");
            if (File.Exists(runs)) File.Copy(runs, Path.Combine(staging, "runs.jsonl"), true);

            var logDirectory = Path.Combine(dataRoot, "logs");
            if (Directory.Exists(logDirectory))
            {
                var logs = new DirectoryInfo(logDirectory).GetFiles("*.log").OrderByDescending(file => file.LastWriteTime)
                    .Take(maxLogFiles).ToList();
                Directory.CreateDirectory(Path.Combine(staging, "logs"));
                foreach (var file in logs) file.CopyTo(Path.Combine(staging, "logs", file.Name), overwrite: true);

                // Failure frames come in .png + .json pairs; keep the newest ones only.
                var frames = new DirectoryInfo(logDirectory).GetFiles("AzurAssistant-failure-*.png")
                    .OrderByDescending(file => file.LastWriteTime).Take(maxFailureImages).ToList();
                if (frames.Count > 0)
                {
                    Directory.CreateDirectory(Path.Combine(staging, "failure-frames"));
                    foreach (var frame in frames)
                    {
                        frame.CopyTo(Path.Combine(staging, "failure-frames", frame.Name), overwrite: true);
                        var meta = Path.ChangeExtension(frame.FullName, ".json");
                        if (File.Exists(meta)) File.Copy(meta, Path.Combine(staging, "failure-frames", Path.GetFileName(meta)), true);
                    }
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetZipPath)!);
            if (File.Exists(targetZipPath)) File.Delete(targetZipPath);
            ZipFile.CreateFromDirectory(staging, targetZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            log.Write($"已导出诊断包：{targetZipPath}");
            return targetZipPath;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }
    }

    private string EnvironmentText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        builder.AppendLine("操作系统：" + Environment.OSVersion.VersionString);
        builder.AppendLine("64 位进程：" + Environment.Is64BitProcess);
        builder.AppendLine(".NET：" + Environment.Version);
        builder.AppendLine("进程 ID：" + Environment.ProcessId);
        builder.AppendLine("管理员：" + ProcessPrivileges.IsElevated(Environment.ProcessId));
        builder.AppendLine("程序目录：" + AppContext.BaseDirectory);
        builder.AppendLine("数据目录：" + dataRoot);
        try
        {
            using var process = Process.GetCurrentProcess();
            builder.AppendLine("内存：" + (process.WorkingSet64 / 1024 / 1024) + " MB");
        }
        catch (InvalidOperationException) { }
        builder.AppendLine("渲染层：" + (System.Windows.Media.RenderCapability.Tier >> 16));
        foreach (var screen in Screens()) builder.AppendLine("显示器：" + screen);
        return builder.ToString();
    }

    private static IEnumerable<string> Screens()
    {
        var handles = new List<IntPtr>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (monitor, _, _, _) => { handles.Add(monitor); return true; }, IntPtr.Zero);
        foreach (var monitor in handles)
        {
            var info = NativeMethods.MONITORINFO.Create();
            if (!NativeMethods.GetMonitorInfo(monitor, ref info)) continue;
            var work = info.rcWork;
            var full = info.rcMonitor;
            yield return $"{full.Left},{full.Top} {full.Right - full.Left}x{full.Bottom - full.Top}"
                + (info.dwFlags == 1 ? "（主显示器）" : "") + $" 工作区 {work.Right - work.Left}x{work.Bottom - work.Top}";
        }
    }

    private static void CopyDirectory(string source, string target, string? filter = null)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, filter ?? "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
