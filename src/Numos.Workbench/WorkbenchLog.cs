using System.Text;

namespace Numos.Workbench;

/// <summary>
///     Severity of a log entry. `Warn` is the warning level.
/// </summary>
public enum WorkbenchLogLevel
{
    Debug,
    Info,
    Warn,
    Error,
    Fatal
}

/// <summary>
///     One log line.
/// </summary>
/// <param name="Sequence">Position in the log, starting at 1.</param>
/// <param name="Timestamp">Local wall-clock time of the write.</param>
/// <param name="Level">Severity.</param>
/// <param name="Source">The subsystem that wrote the entry.</param>
/// <param name="Message">One-line text.</param>
/// <param name="Details">Extra text such as a stack trace, or <see langword="null" />.</param>
/// <param name="Tick">The world tick when the entry is about a project, or <see langword="null" />.</param>
public sealed record WorkbenchLogEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    WorkbenchLogLevel Level,
    string Source,
    string Message,
    string? Details,
    long? Tick = null);

/// <summary>
///     Bounded, thread-safe log. It works like a server output shell: sessions write entries, and front ends
///     render the history or stream new entries through <see cref="EntryWritten" />.
/// </summary>
public sealed class WorkbenchLog
{
    private const int MaximumEntries = 5_000;
    private readonly List<WorkbenchLogEntry> _entries = [];
    private readonly Lock _sync = new();
    private long _nextSequence;
    private int _unseenErrorCount;
    private int _unseenWarningCount;

    /// <summary>
    ///     Errors and fatal entries written since the last <see cref="MarkSeen" />. Cheap enough to poll every frame.
    /// </summary>
    public int UnseenErrorCount => Volatile.Read(ref _unseenErrorCount);

    /// <summary>
    ///     Warnings written since the last <see cref="MarkSeen" />.
    /// </summary>
    public int UnseenWarningCount => Volatile.Read(ref _unseenWarningCount);

    /// <summary>
    ///     Raised for each entry after it is stored.
    /// </summary>
    /// <remarks>
    ///     Handlers run on the writing thread, outside the log's lock. A handler must be quick and must not throw.
    /// </remarks>
    public event Action<WorkbenchLogEntry>? EntryWritten;

    /// <summary>
    ///     Appends an entry. Blank messages are ignored.
    /// </summary>
    public void Write(
        WorkbenchLogLevel level,
        string source,
        string message,
        string? details = null,
        long? tick = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var entry = new WorkbenchLogEntry(
            Interlocked.Increment(ref _nextSequence),
            DateTimeOffset.Now,
            level,
            source,
            message.TrimEnd(),
            details,
            tick);

        lock (_sync)
        {
            _entries.Add(entry);
            if (_entries.Count > MaximumEntries)
                _entries.RemoveRange(0, _entries.Count - MaximumEntries);
        }

        if (level >= WorkbenchLogLevel.Error)
            Interlocked.Increment(ref _unseenErrorCount);
        else if (level == WorkbenchLogLevel.Warn)
            Interlocked.Increment(ref _unseenWarningCount);

        EntryWritten?.Invoke(entry);
    }

    /// <summary>
    ///     Resets the unseen counters once the log is actually on screen.
    /// </summary>
    public void MarkSeen()
    {
        Interlocked.Exchange(ref _unseenErrorCount, 0);
        Interlocked.Exchange(ref _unseenWarningCount, 0);
    }

    public WorkbenchLogEntry[] Snapshot()
    {
        lock (_sync)
        {
            return [.. _entries];
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }
}

public sealed class WorkbenchConsoleWriter : TextWriter
{
    private readonly StringBuilder _buffer = new();
    private readonly WorkbenchLogLevel _level;
    private readonly WorkbenchLog _log;
    private readonly TextWriter _original;
    private readonly Lock _sync = new();

    public WorkbenchConsoleWriter(TextWriter original, WorkbenchLog log, WorkbenchLogLevel level)
    {
        _original = original;
        _log = log;
        _level = level;
    }

    public override Encoding Encoding => _original.Encoding;

    public override void Write(char value)
    {
        lock (_sync)
        {
            _original.Write(value);
            if (value == '\n')
                CommitLine();
            else if (value != '\r')
                _buffer.Append(value);
        }
    }

    public override void Write(string? value)
    {
        if (value == null)
            return;

        lock (_sync)
        {
            _original.Write(value);
            foreach (char character in value)
            {
                if (character == '\n')
                    CommitLine();
                else if (character != '\r')
                    _buffer.Append(character);
            }
        }
    }

    public override void Flush()
    {
        lock (_sync)
        {
            _original.Flush();
            CommitLine();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Flush();

        base.Dispose(disposing);
    }

    private void CommitLine()
    {
        if (_buffer.Length == 0)
            return;

        _log.Write(_level, "Console", _buffer.ToString());
        _buffer.Clear();
    }
}