using System.IO;
using System.Text;
using AzurAssistant.Contracts;

namespace AzurAssistant.Runtime;

public sealed record SavedActionRecording(string Path, ActionRecording Recording)
{
    public string Name => Recording.Name;
    public string Summary => Recording.Summary;
    public string CreatedText => Recording.CreatedAt.ToString("yyyy-MM-dd HH:mm");
    public string FileSizeText => new FileInfo(Path).Length is var size && size >= 1024
        ? $"{size / 1024.0:0.0} KB" : $"{size} B";
}

/// <summary>Reads and writes action recordings as JSON files under one directory.</summary>
public sealed class ActionRecordingStore(string directory)
{
    public string DirectoryPath => directory;

    public IReadOnlyList<SavedActionRecording> List()
    {
        if (!Directory.Exists(directory)) return [];
        var saved = new List<SavedActionRecording>();
        foreach (var path in Directory.GetFiles(directory, "*.json").OrderByDescending(p => p))
        {
            try { saved.Add(new SavedActionRecording(path, Read(path))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or System.Text.Json.JsonException or ArgumentException or NotSupportedException) { }
        }
        return saved;
    }

    public ActionRecording Read(string path) => ActionRecording.FromJson(File.ReadAllText(path, Encoding.UTF8));

    public string Save(ActionRecording recording)
    {
        recording.Validate();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{recording.CreatedAt:yyyyMMdd-HHmmss}-{Slug(recording.Name)}.json");
        for (var index = 2; File.Exists(path); index++)
            path = Path.Combine(directory, $"{recording.CreatedAt:yyyyMMdd-HHmmss}-{Slug(recording.Name)}-{index}.json");
        Write(path, recording);
        return path;
    }

    public string Rename(string path, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var recording = Read(path) with { Name = name.Trim() };
        recording.Validate();
        var target = Path.Combine(Path.GetDirectoryName(path)!, $"{recording.CreatedAt:yyyyMMdd-HHmmss}-{Slug(recording.Name)}.json");
        for (var index = 2; target != path && File.Exists(target); index++)
            target = Path.Combine(Path.GetDirectoryName(path)!, $"{recording.CreatedAt:yyyyMMdd-HHmmss}-{Slug(recording.Name)}-{index}.json");
        Write(target, recording);
        if (target != path) File.Delete(path);
        return target;
    }

    public void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void Write(string path, ActionRecording recording)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, recording.ToJson(), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static string Slug(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name.Trim())
        {
            if (char.IsLetterOrDigit(character) || character is '_' or '-') builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        var slug = builder.ToString().Trim('-');
        if (slug.Length > 40) slug = slug[..40].Trim('-');
        return slug.Length == 0 ? "action" : slug;
    }
}
