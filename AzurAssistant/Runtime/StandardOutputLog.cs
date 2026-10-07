using System.IO;
using System.Text;

namespace AzurAssistant.Runtime;

/// <summary>Uses an inherited stdout handle without allocating or attaching a console.</summary>
public sealed class StandardOutputLog(Func<Stream>? openStream = null)
{
    public static StandardOutputLog Shared { get; } = new();
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private bool _unavailable;

    public void WriteLine(string line)
    {
        lock (_gate)
        {
            if (_unavailable) return;
            try
            {
                _writer ??= new StreamWriter((openStream ?? Console.OpenStandardOutput)(),
                    new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                _writer.WriteLine(line);
                _writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
            {
                // A missing/closed handle is optional. Never retry buffered data or log recursively.
                _unavailable = true;
                _writer = null;
            }
        }
    }
}
