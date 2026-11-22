using System.Numerics;
using ImGuiNET;
using NativeFileDialogNET;
using Numos.Viewer.Ui;
using Numos.Workbench;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private const string SolverAssemblySource = "Solvers";

    /// <summary>
    ///     Loads the solver assemblies that the Load Solvers dialog names. It is off in a NativeAOT build.
    /// </summary>
    private readonly SolverAssemblyLoader _solverLoader = new();

    private bool _requestSolverAssemblyModal;
    private bool _solverAssemblyLoadPending;
    private string _solverAssemblyPath = string.Empty;
    private FeedbackLines? _solverAssemblyFeedback;

    /// <summary>
    ///     Writes the reports of the `--solvers` paths that loaded before the window opened to Messages.
    /// </summary>
    internal void LogSolverAssemblies(IReadOnlyList<SolverAssemblyLoadReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        foreach (var report in reports)
            WriteSolverAssemblyReport(report);
    }

    /// <summary>
    ///     Draws the menu item that opens the Load Solvers dialog. A NativeAOT build shows it disabled, with the reason
    ///     in its tooltip.
    /// </summary>
    private void RenderLoadSolversMenuItem()
    {
        ImGui.BeginDisabled(!_solverLoader.IsSupported);
        if (ImGui.MenuItem($"{Icons.Plug} {Locale.GetString("load-solvers-menu")}"))
            RequestLoadSolvers();

        ImGui.EndDisabled();
        if (!_solverLoader.IsSupported)
            ImGuiExtensions.ItemTooltip(Locale.GetString("load-solvers-unavailable"));
    }

    /// <summary>
    ///     Draws the button that opens the Load Solvers dialog, for the solver pipeline panel.
    /// </summary>
    private void RenderLoadSolversButton()
    {
        ImGui.BeginDisabled(!_solverLoader.IsSupported);
        if (ImGui.Button(Locale.GetString("load-solvers-menu") + "##solver-load-assemblies"))
            RequestLoadSolvers();

        ImGui.EndDisabled();
        ImGuiExtensions.ItemTooltip(
            _solverLoader.IsSupported
                ? Locale.GetString("load-solvers-help")
                : Locale.GetString("load-solvers-unavailable"));
    }

    private void RequestLoadSolvers()
    {
        _solverAssemblyFeedback = null;
        _requestSolverAssemblyModal = true;
    }

    private void DrawSolverAssemblyModal()
    {
        string popupId = $"{Locale.GetString("load-solvers-title")}###load-solvers";
        ImGuiExtensions.OpenPopupWhenRequested(popupId, ref _requestSolverAssemblyModal);
        CenterNextReplayModal();
        bool open = true;
        using var modal = ImGuiExtensions.BeginPopupModal(popupId, ref open);
        if (!modal.IsVisible)
            return;

        ImGui.PushTextWrapPos(560f);
        ImGui.TextWrapped(Locale.GetString("load-solvers-description"));
        ImGui.PopTextWrapPos();
        ImGui.SetNextItemWidth(400f);
        ImGui.InputText(Locale.GetString("replay-file-path-label") + "##solver-assembly-path", ref _solverAssemblyPath, 4096);
        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("load-solvers-browse-file")))
            PickSolverAssemblyPath(folder: false);

        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("load-solvers-browse-folder")))
            PickSolverAssemblyPath(folder: true);

        if (_solverAssemblyFeedback is { } feedback)
        {
            ImGui.PushTextWrapPos(560f);
            foreach (string line in feedback.Lines)
                ImGuiExtensions.Feedback(line, feedback.IsError);

            ImGui.PopTextWrapPos();
        }

        ImGui.BeginDisabled(
            string.IsNullOrWhiteSpace(_solverAssemblyPath) || _solverAssemblyLoadPending || !_solverLoader.IsSupported);

        if (ImGui.Button(Locale.GetString("load-solvers-button"), new Vector2(120f, 0f)))
            StartSolverAssemblyLoad(_solverAssemblyPath);

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("common-close"), new Vector2(120f, 0f)))
            ImGui.CloseCurrentPopup();
    }

    private void PickSolverAssemblyPath(bool folder)
    {
        try
        {
            using var dialog = folder
                ? new NativeFileDialog().SelectFolder()
                : new NativeFileDialog().SelectFile().AddFilter(Locale.GetString("load-solvers-filter-name"), "dll");

            string? initialDirectory = null;
            if (!string.IsNullOrWhiteSpace(_solverAssemblyPath))
            {
                string fullPath = Path.GetFullPath(_solverAssemblyPath);
                initialDirectory = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
            }

            var result = dialog.Open(out string? path, initialDirectory);
            if (result == DialogResult.Cancel)
                return;

            if (result == DialogResult.Error || string.IsNullOrWhiteSpace(path))
            {
                _solverAssemblyFeedback = new FeedbackLines([Locale.GetString("replay-file-open-dialog-error")], true);
                return;
            }

            _solverAssemblyPath = path;
            _solverAssemblyFeedback = null;
        }
        catch (Exception exception)
        {
            _solverAssemblyFeedback = new FeedbackLines(
                [$"{Locale.GetString("replay-file-open-dialog-error")} {exception.Message}"],
                true);
        }
    }

    /// <summary>
    ///     Loads on the host thread, so no module initializer runs while a tick reads the catalogs.
    /// </summary>
    private void StartSolverAssemblyLoad(string path)
    {
        var loader = _solverLoader;
        _solverAssemblyLoadPending = true;
        _solverAssemblyFeedback = null;
        Send(
            _ => loader.Load(path),
            result =>
            {
                _solverAssemblyLoadPending = false;
                if (result.Exception is { } exception)
                {
                    _solverAssemblyFeedback = new FeedbackLines([DescribeFailure(exception)], true);
                    WriteException(Locale.GetString("load-solvers-title"), exception);
                    return;
                }

                var report = result.Value!;
                WriteSolverAssemblyReport(report);
                var lines = new List<string>
                {
                    report.Succeeded
                        ? Locale.GetString("load-solvers-loaded")
                        : Locale.GetString("load-solvers-failed")
                };

                lines.AddRange(report.Describe());
                _solverAssemblyFeedback = new FeedbackLines(lines, !report.Succeeded);
                RequestNextFrame();
            });
    }

    private void WriteSolverAssemblyReport(SolverAssemblyLoadReport report)
    {
        var level = report.Succeeded ? WorkbenchLogLevel.Info : WorkbenchLogLevel.Error;
        foreach (string line in report.Describe())
            WriteMessage(level, SolverAssemblySource, line);
    }

    /// <summary>
    ///     A dialog result of several lines in one color.
    /// </summary>
    private sealed record FeedbackLines(IReadOnlyList<string> Lines, bool IsError);
}
