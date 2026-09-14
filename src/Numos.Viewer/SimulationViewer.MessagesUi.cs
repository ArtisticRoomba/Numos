using System.Numerics;
using System.Text;
using ImGuiNET;
using Numos.Viewer.Ui;
using Raylib_cs;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private readonly ViewerLog _messages = new();
    private bool _messageAutoScroll = true;
    private string _messageFilter = string.Empty;
    private int _messageMinimumLevel;
    private long _messagesLastRenderedSequence;
    private TextWriter? _originalConsoleError;
    private TextWriter? _originalConsoleOut;
    private bool _showMessagesPanel;
    private ViewerConsoleWriter? _viewerConsoleError;
    private ViewerConsoleWriter? _viewerConsoleOut;

    private void StartMessageCapture()
    {
        _originalConsoleOut = Console.Out;
        _originalConsoleError = Console.Error;
        _viewerConsoleOut = new ViewerConsoleWriter(_originalConsoleOut, _messages, ViewerLogLevel.Info);
        _viewerConsoleError = new ViewerConsoleWriter(_originalConsoleError, _messages, ViewerLogLevel.Error);
        Console.SetOut(_viewerConsoleOut);
        Console.SetError(_viewerConsoleError);
        AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
        TaskScheduler.UnobservedTaskException += HandleUnobservedTaskException;
        WriteMessage(ViewerLogLevel.Info, "Viewer", $"Numos Viewer {ViewerBuildInfo.PackageVersion} started.");
    }

    private void StopMessageCapture()
    {
        AppDomain.CurrentDomain.UnhandledException -= HandleUnhandledException;
        TaskScheduler.UnobservedTaskException -= HandleUnobservedTaskException;

        if (_originalConsoleOut != null)
            Console.SetOut(_originalConsoleOut);

        if (_originalConsoleError != null)
            Console.SetError(_originalConsoleError);

        _viewerConsoleOut?.Dispose();
        _viewerConsoleError?.Dispose();
        _viewerConsoleOut = null;
        _viewerConsoleError = null;
    }

    private void HandleUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
            WriteFatalException("Unhandled exception", exception);
        else
            WriteMessage(ViewerLogLevel.Fatal, "Runtime", $"Unhandled exception: {args.ExceptionObject}");
    }

    private void HandleUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        WriteException("Unobserved task exception", args.Exception);
    }

    private void WriteMessage(ViewerLogLevel level, string source, string message)
    {
        _messages.Write(level, source, message);
        RequestNextFrame();
    }

    private void WriteException(string context, Exception exception)
    {
        _messages.Write(ViewerLogLevel.Error, "Exception", $"{context}: {exception.Message}", exception.ToString());
        RequestNextFrame();
    }

    private void WriteFatalException(string context, Exception exception)
    {
        _messages.Write(ViewerLogLevel.Fatal, "Exception", $"{context}: {exception.Message}", exception.ToString());
        RequestNextFrame();
    }

    private void RenderMessagesPanel()
    {
        if (!_showMessagesPanel)
            return;

        using var window = ImGuiExtensions.BeginWindow(
            "Messages / Logs##messages",
            ref _showMessagesPanel,
            new Vector2(320, 650),
            new Vector2(900, 240));

        if (!window.IsVisible)
            return;

        ViewerLogEntry[] entries = _messages.Snapshot();
        ImGui.SetNextItemWidth(230f);
        ImGui.InputTextWithHint("##message-filter", "Filter messages...", ref _messageFilter, 256);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        ImGui.Combo("##message-level", ref _messageMinimumLevel, "DEBG\0INFO\0WARN\0ERR\0FATL\0");
        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref _messageAutoScroll);
        ImGui.SameLine();

        ViewerLogEntry[] visibleEntries = entries.Where(IsMessageVisible).ToArray();
        if (ImGui.Button("Copy"))
            Raylib.SetClipboardText(FormatMessages(visibleEntries));

        ImGui.SameLine();
        if (ImGui.Button("Clear"))
        {
            _messages.Clear();
            entries = [];
            visibleEntries = [];
            _messagesLastRenderedSequence = 0;
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"{visibleEntries.Length} shown / {entries.Length} total");
        ImGui.Separator();

        ImGui.BeginChild(
            "MessageLog##messages",
            Vector2.Zero,
            ImGuiChildFlags.None,
            ImGuiWindowFlags.HorizontalScrollbar);

        foreach (var entry in visibleEntries)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, GetMessageColor(entry.Level));
            ImGui.TextUnformatted(FormatMessage(entry));
            if (!string.IsNullOrWhiteSpace(entry.Details))
                ImGui.TextUnformatted(entry.Details);

            ImGui.PopStyleColor();
        }

        long lastSequence = entries.Length == 0 ? 0 : entries[^1].Sequence;
        if (_messageAutoScroll && lastSequence != _messagesLastRenderedSequence)
            ImGui.SetScrollHereY(1f);

        _messagesLastRenderedSequence = lastSequence;
        ImGui.EndChild();
    }

    private bool IsMessageVisible(ViewerLogEntry entry)
    {
        if ((int)entry.Level < _messageMinimumLevel)
            return false;

        return string.IsNullOrWhiteSpace(_messageFilter) ||
               entry.Source.Contains(_messageFilter, StringComparison.OrdinalIgnoreCase) ||
               entry.Message.Contains(_messageFilter, StringComparison.OrdinalIgnoreCase) ||
               (entry.Details?.Contains(_messageFilter, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string FormatMessages(IEnumerable<ViewerLogEntry> entries)
    {
        var text = new StringBuilder();
        foreach (var entry in entries)
        {
            text.AppendLine(FormatMessage(entry));
            if (!string.IsNullOrWhiteSpace(entry.Details))
                text.AppendLine(entry.Details);
        }

        return text.ToString();
    }

    private static string FormatMessage(ViewerLogEntry entry)
    {
        return $"{entry.Timestamp:HH:mm:ss.fff} [{GetMessageLevelLabel(entry.Level),-7}] [{entry.Source}] {entry.Message}";
    }

    private static string GetMessageLevelLabel(ViewerLogLevel level)
    {
        return level switch
        {
            ViewerLogLevel.Debug => "DEBG",
            ViewerLogLevel.Info => "INFO",
            ViewerLogLevel.Warn => "WARN",
            ViewerLogLevel.Error => "ERR",
            ViewerLogLevel.Fatal => "FATL",
            _ => level.ToString().ToUpperInvariant()
        };
    }

    private static Vector4 GetMessageColor(ViewerLogLevel level)
    {
        return level switch
        {
            ViewerLogLevel.Debug => ViewerTheme.SecondaryText,
            ViewerLogLevel.Info => ViewerTheme.PrimaryText,
            ViewerLogLevel.Warn => ViewerTheme.Caution,
            ViewerLogLevel.Error => ViewerTheme.Error,
            ViewerLogLevel.Fatal => new Vector4(1f, 0.18f, 0.68f, 1f),
            _ => ViewerTheme.PrimaryText
        };
    }
}