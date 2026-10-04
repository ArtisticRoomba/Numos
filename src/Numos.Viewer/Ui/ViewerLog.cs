using System.Text;

namespace Numos.Viewer.Ui;

internal enum ViewerLogLevel
{
    Debug,
    Info,
    Warn,
    Error,
    Fatal
}

internal sealed record ViewerLogEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    ViewerLogLevel Level,
    string Source,
    string Message,
    string? Details);

internal sealed class ViewerLog
{
    private const int MaximumEntries = 5_000;
    private readonly List<ViewerLogEntry> _entries = [];
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

    public void Write(ViewerLogLevel level, string source, string message, string? details = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var entry = new ViewerLogEntry(
            Interlocked.Increment(ref _nextSequence),
            DateTimeOffset.Now,
            level,
            source,
            message.TrimEnd(),
            details);

        lock (_sync)
        {
            _entries.Add(entry);
            if (_entries.Count > MaximumEntries)
                _entries.RemoveRange(0, _entries.Count - MaximumEntries);
        }

        if (level >= ViewerLogLevel.Error)
            Interlocked.Increment(ref _unseenErrorCount);
        else if (level == ViewerLogLevel.Warn)
            Interlocked.Increment(ref _unseenWarningCount);
    }

    /// <summary>
    ///     Resets the unseen counters once the log is actually on screen.
    /// </summary>
    public void MarkSeen()
    {
        Interlocked.Exchange(ref _unseenErrorCount, 0);
        Interlocked.Exchange(ref _unseenWarningCount, 0);
    }

    public ViewerLogEntry[] Snapshot()
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

internal sealed class ViewerConsoleWriter : TextWriter
{
    private readonly StringBuilder _buffer = new();
    private readonly ViewerLogLevel _level;
    private readonly ViewerLog _log;
    private readonly TextWriter _original;
    private readonly Lock _sync = new();

    public ViewerConsoleWriter(TextWriter original, ViewerLog log, ViewerLogLevel level)
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