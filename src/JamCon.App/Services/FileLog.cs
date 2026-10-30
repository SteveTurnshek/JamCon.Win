using System.Diagnostics;
using System.IO;

namespace JamCon.App.Services;

/// <summary>
/// Timestamped, line-oriented log in <see cref="AppPaths.LogDirectory"/>, one file per day.
/// </summary>
/// <remarks>
/// The connection loop logs every transition, which is the raw material for answering the open
/// reconnect question, so this favours completeness over brevity. Frames are never logged per
/// frame; only state changes and diagnostics are.
/// </remarks>
internal sealed class FileLog : IDisposable
{
    private const int RecentCapacity = 500;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly Queue<string> _recent = new();

    public FileLog(string directory)
    {
        Directory.CreateDirectory(directory);
        DeleteOldLogs(directory);

        DirectoryPath = directory;
        FilePath = Path.Combine(directory, $"jamcon-{DateTime.Now:yyyyMMdd}.log");
        _writer = new StreamWriter(
            new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };
    }

    public string DirectoryPath { get; }
    public string FilePath { get; }

    /// <summary>Raised after each line is written, on the writing thread.</summary>
    public event Action<string>? LineWritten;

    public void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";
        lock (_gate)
        {
            _writer.WriteLine(line);
            _recent.Enqueue(line);
            while (_recent.Count > RecentCapacity) _recent.Dequeue();
        }

        Debug.WriteLine(line);
        LineWritten?.Invoke(line);
    }

    public string[] Recent()
    {
        lock (_gate) return _recent.ToArray();
    }

    private static void DeleteOldLogs(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "jamcon-*.log"))
        {
            try
            {
                if (DateTime.Now - File.GetLastWriteTime(file) > Retention) File.Delete(file);
            }
            catch (IOException)
            {
                // A log another instance still holds open is not worth failing startup over.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }
}
