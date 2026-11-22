using System.IO;

namespace AzurAssistant.Runtime;

public sealed record ProfileInfo(string Name, string Directory, DateTimeOffset UpdatedAt, int FileCount)
{
    public string Detail => $"{UpdatedAt.ToLocalTime():MM-dd HH:mm} · {FileCount} 个文件";
    public override string ToString() => Name;
}

/// <summary>
/// Named snapshots of the user data (settings, schedule, actions, wheelchair plans).
/// Switching a profile copies the snapshot back into the data directory after backing up the current one.
/// </summary>
public sealed class ProfileStore(string dataDirectory)
{
    private static readonly string[] Managed = ["settings.json", "schedule.json", "actions", "wheelchair"];
    private static readonly string[] ManagedDirectories = ["actions", "wheelchair"];

    public string Root => Path.Combine(dataDirectory, "profiles");
    public string? Error { get; private set; }

    public IReadOnlyList<ProfileInfo> List()
    {
        if (!Directory.Exists(Root)) return [];
        var list = new List<ProfileInfo>();
        foreach (var directory in Directory.GetDirectories(Root))
        {
            var name = Path.GetFileName(directory);
            // _backup_* snapshots are internal; they are not selectable profiles.
            if (name.StartsWith('_') || name.StartsWith('.')) continue;
            var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
            if (files.Length == 0) continue;
            list.Add(new ProfileInfo(name, directory,
                new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory), TimeSpan.Zero), files.Length));
        }
        return list.OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Copies the current user data into profiles\&lt;name&gt; (overwriting a previous snapshot).</summary>
    public bool Save(string name)
    {
        var safe = Sanitize(name);
        if (safe is null) { Error = "配置名无效（不能为空，也不能包含路径字符）。"; return false; }
        try
        {
            var target = Path.Combine(Root, safe);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(target);
            foreach (var entry in Managed)
            {
                var source = Path.Combine(dataDirectory, entry);
                if (File.Exists(source)) File.Copy(source, Path.Combine(target, entry), overwrite: true);
                else if (Directory.Exists(source)) CopyDirectory(source, Path.Combine(target, entry));
            }
            Error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "保存配置档失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>Copies a snapshot back into the data directory; the previous state is copied to profiles\_backup_&lt;stamp&gt;.</summary>
    public bool Apply(string name, out string backupPath)
    {
        backupPath = "";
        var safe = Sanitize(name);
        if (safe is null) { Error = "配置名无效。"; return false; }
        var source = Path.Combine(Root, safe);
        if (!Directory.Exists(source)) { Error = "找不到配置档：" + safe; return false; }
        try
        {
            backupPath = Path.Combine(Root, "_backup_" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(backupPath);
            foreach (var entry in Managed)
            {
                var existing = Path.Combine(dataDirectory, entry);
                if (File.Exists(existing)) File.Copy(existing, Path.Combine(backupPath, entry), overwrite: true);
                else if (Directory.Exists(existing)) CopyDirectory(existing, Path.Combine(backupPath, entry));
            }

            foreach (var entry in Managed)
            {
                var from = Path.Combine(source, entry);
                var to = Path.Combine(dataDirectory, entry);
                if (File.Exists(from))
                {
                    File.Copy(from, to, overwrite: true);
                }
                else if (Directory.Exists(from))
                {
                    if (Directory.Exists(to)) Directory.Delete(to, true);
                    CopyDirectory(from, to);
                }
            }
            Error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "应用配置档失败：" + ex.Message;
            return false;
        }
    }

    public bool Delete(string name)
    {
        var safe = Sanitize(name);
        if (safe is null) { Error = "配置名无效。"; return false; }
        var target = Path.Combine(Root, safe);
        if (!Directory.Exists(target)) { Error = "找不到配置档：" + safe; return false; }
        try
        {
            Directory.Delete(target, true);
            Error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "删除配置档失败：" + ex.Message;
            return false;
        }
    }

    public static string? Sanitize(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 40) return null;
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        if (trimmed.StartsWith('.') || trimmed.StartsWith('_')) return null;
        return trimmed;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
