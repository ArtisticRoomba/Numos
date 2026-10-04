using System.Globalization;
using System.Numerics;
using System.Text;
using ImGuiNET;
using Numos.Viewer.Ui;
using Raylib_cs;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private readonly ViewerLog _messages = new();
    private bool _focusMessagesPanelRequested;
    private bool _messageAutoScroll = true;
    private string _messageFilter = string.Empty;
    private int _messageMinimumLevel;
    private long _messagesLastRenderedSequence;

    // Whether the Messages panel was on screen last frame. The menu bar is drawn before the panel, so it reads the
    // previous frame's value; that keeps the unseen indicator from flashing while the log is already visible.
    private bool _messagesPanelVisible;
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
        WriteMessage(ViewerLogLevel.Info, "Viewer", $"Numos.Viewer {ViewerBuildInfo.PackageVersion} started.");
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

    /// <summary>
    ///     Opens the Messages panel and brings it to the front, including when it is docked behind another tab.
    /// </summary>
    private void ShowMessagesPanel()
    {
        _showMessagesPanel = true;
        _focusMessagesPanelRequested = true;
    }

    /// <summary>
    ///     Draws a right-aligned menu-bar item for warnings and errors logged while the Messages panel was not on
    ///     screen. It stays until the panel is shown, since the panel is hidden by default and a failure would
    ///     otherwise go unnoticed.
    /// </summary>
    private void RenderUnseenMessagesIndicator()
    {
        if (_messagesPanelVisible)
            return;

        int errors = _messages.UnseenErrorCount;
        int warnings = _messages.UnseenWarningCount;
        if (errors == 0 && warnings == 0)
            return;

        string label = errors > 0 && warnings > 0
            ? $"{FormatCount(errors, "error")}, {FormatCount(warnings, "warning")}"
            : errors > 0
                ? FormatCount(errors, "error")
                : FormatCount(warnings, "warning");

        // A menu-bar MenuItem is as wide as its label plus one item spacing on either side.
        float itemWidth = ImGui.CalcTextSize(label).X + ImGui.GetStyle().ItemSpacing.X * 2f;
        float cursorX = ImGui.GetCursorPosX();
        float right = cursorX + ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(Math.Max(cursorX, right - itemWidth));

        ImGui.PushStyleColor(ImGuiCol.Text, errors > 0 ? ViewerTheme.Error : ViewerTheme.Caution);
        bool clicked = ImGui.MenuItem($"{label}##unseen-messages");
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
            ImGui.SetTooltip("Open Messages / Logs.");

        if (clicked)
            ShowMessagesPanel();
    }

    private static string FormatCount(int count, string noun)
    {
        string number = count.ToString(CultureInfo.InvariantCulture);
        return count == 1 ? $"{number} {noun}" : $"{number} {noun}s";
    }

    private void RenderMessagesPanel()
    {
        _messagesPanelVisible = false;
        if (!_showMessagesPanel)
            return;

        if (_focusMessagesPanelRequested)
        {
            ImGui.SetNextWindowFocus();
            _focusMessagesPanelRequested = false;
        }

        using var window = ImGuiExtensions.BeginWindow(
            "Messages / Logs##messages",
            ref _showMessagesPanel,
            new Vector2(320, 650),
            new Vector2(900, 240));

        if (!window.IsVisible)
            return;

        _messagesPanelVisible = true;
        _messages.MarkSeen();

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

        long lastSequence = entries.Length == 0 ? 0 : entries[^1].Sequence;
        RenderMessageTable(visibleEntries, _messageAutoScroll && lastSequence != _messagesLastRenderedSequence);
        _messagesLastRenderedSequence = lastSequence;
    }

    private static void RenderMessageTable(ViewerLogEntry[] entries, bool scrollToBottom)
    {
        const ImGuiTableFlags flags =
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.SizingFixedFit |
            ImGuiTableFlags.BordersInnerV |
            ImGuiTableFlags.BordersOuter |
            ImGuiTableFlags.Resizable;

        if (!ImGui.BeginTable("MessageLog##messages", 4, flags))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Message", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var entry in entries)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(entry.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));

            ImGui.TableSetColumnIndex(1);
            ImGui.PushStyleColor(ImGuiCol.Text, GetMessageColor(entry.Level));
            ImGui.TextUnformatted(GetMessageLevelLabel(entry.Level));
            ImGui.PopStyleColor();

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(entry.Source);

            // The scrolling table is its own child window, so the panel's wrap position does not reach in here.
            ImGui.TableSetColumnIndex(3);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(entry.Message);
            if (!string.IsNullOrWhiteSpace(entry.Details))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ViewerTheme.SecondaryText);
                ImGui.TextUnformatted(entry.Details);
                ImGui.PopStyleColor();
            }

            ImGui.PopTextWrapPos();
        }

        if (scrollToBottom && entries.Length > 0)
            ImGui.SetScrollHereY(1f);

        ImGui.EndTable();
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
            ViewerLogLevel.Fatal => ViewerTheme.Error,
            _ => ViewerTheme.PrimaryText
        };
    }
}