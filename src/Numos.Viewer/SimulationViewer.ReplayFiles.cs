using System.Numerics;
using ImGuiNET;
using NativeFileDialogNET;
using Numos.API;
using Numos.CoreSim;
using Numos.Serialization;
using Numos.Serialization.FileSystem;
using Numos.Viewer.Ui;
using Numos.Workbench;
using Raylib_cs;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private const string ReplayExtension = ReplayPaths.Extension;
    private CancellationTokenSource? _replayFileCancellation;
    private string? _replayFileError;

    /// <summary>
    ///     Whether a replay load job is queued or running on the host. The progress modal reads its progress from the
    ///     busy frame.
    /// </summary>
    private bool _replayLoadPending;

    private string _replayOpenPath = string.Empty;
    private string _replaySavePath = string.Empty;
    private int _replaySaveRange;
    private Task? _replaySaveTask;

    /// <summary>
    ///     Whether the host is still capturing the replay for <see cref="_replaySaveTask" />.
    /// </summary>
    private bool _replaySavePreparing;
    private bool _requestOpenReplayModal;
    private bool _requestSaveReplayModal;

    private void RequestOpenReplay(string? path = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            PickReplayFile();
            return;
        }

        _replayOpenPath = path;
        _replayFileError = null;
        _requestOpenReplayModal = true;
    }

    private void PickReplayFile()
    {
        try
        {
            using var dialog = new NativeFileDialog()
                .SelectFile()
                .AddFilter(Locale.GetString("replay-file-filter-name"), "numos");

            string? initialDirectory = GetReplayOpenDirectory();
            var result = dialog.Open(out string? path, initialDirectory);
            if (result == DialogResult.Cancel)
                return;

            if (result == DialogResult.Error || string.IsNullOrWhiteSpace(path))
            {
                SetReplayFileError(Locale.GetString("replay-file-open-dialog-select-error"));
                _requestOpenReplayModal = true;
                return;
            }

            if (!string.Equals(Path.GetExtension(path), ReplayExtension, StringComparison.OrdinalIgnoreCase))
            {
                SetReplayFileError(Locale.GetString("replay-file-invalid-extension-error"));
                _requestOpenReplayModal = true;
                return;
            }

            RequestOpenReplay(path);
        }
        catch (Exception exception)
        {
            SetReplayFileError(Locale.GetString("replay-file-open-dialog-error"), exception);
            _requestOpenReplayModal = true;
        }
    }

    private string? GetReplayOpenDirectory()
    {
        if (string.IsNullOrWhiteSpace(_replayOpenPath))
            return null;

        string fullPath = Path.GetFullPath(_replayOpenPath);
        return Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
    }

    private void RequestSaveReplay()
    {
        if (!_frame.HasProject || _frame.Timeline == null) return;

        if (string.IsNullOrWhiteSpace(_replaySavePath))
            _replaySavePath = ReplayPaths.SanitizeName(_frame.ProjectName ?? "simulation") + ReplayExtension;

        PickReplaySaveFile();
    }

    private void PickReplaySaveFile()
    {
        try
        {
            using var dialog = new NativeFileDialog()
                .SaveFile()
                .AddFilter(Locale.GetString("replay-file-filter-name"), "numos");

            string fullPath = Path.GetFullPath(_replaySavePath);
            string? initialDirectory = Path.GetDirectoryName(fullPath);
            string suggestedName = Path.GetFileName(fullPath);
            var result = dialog.Open(out string? path, initialDirectory, suggestedName);
            if (result == DialogResult.Cancel)
                return;

            if (result == DialogResult.Error || string.IsNullOrWhiteSpace(path))
            {
                SetReplayFileError(Locale.GetString("replay-file-save-dialog-select-error"));
                _requestSaveReplayModal = true;
                return;
            }

            _replaySavePath = ReplayPaths.Normalize(path);
            _replayFileError = null;
            _requestSaveReplayModal = true;
        }
        catch (Exception exception)
        {
            SetReplayFileError(Locale.GetString("replay-file-open-dialog-error"), exception);
            _requestSaveReplayModal = true;
        }
    }

    private void DrawReplayFileModals()
    {
        DrawOpenReplayModal();
        DrawSaveReplayModal();
        DrawReplayProgressModal();
    }

    private void DrawOpenReplayModal()
    {
        const string popupId = "Open Replay##replay-file";
        ImGuiExtensions.OpenPopupWhenRequested(popupId, ref _requestOpenReplayModal);
        CenterNextReplayModal();
        bool open = true;
        using var modal = ImGuiExtensions.BeginPopupModal(popupId, ref open);
        if (!modal.IsVisible) return;

        ImGui.TextWrapped(Locale.GetString("replay-file-open-description"));
        ImGui.SetNextItemWidth(460f);
        ImGui.InputText(Locale.GetString("replay-file-path-label"), ref _replayOpenPath, 4096);
        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("common-browse"))) PickReplayFile();
        if (_frame.HasProject)
        {
            int branchCount = _frame.Timeline?.Branches.Count ?? 0;
            ImGui.TextColored(
                ViewerTheme.Caution,
                branchCount > 1
                    ? Locale.GetString("replay-file-load-discard-branches-warning", ("branchCount", branchCount))
                    : Locale.GetString("replay-file-load-close-project-warning"));
        }

        DrawReplayFileError();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_replayOpenPath) || _replayLoadPending || IsBusy);
        if (ImGui.Button(_simulation == null ? Locale.GetString("replay-file-open-button") : Locale.GetString("replay-file-load-replace-button"), new Vector2(150f, 0f)))
        {
            StartReplayLoad(_replayOpenPath);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("common-cancel"), new Vector2(120f, 0f))) ImGui.CloseCurrentPopup();
        ImGui.SetItemDefaultFocus();
    }

    private void DrawSaveReplayModal()
    {
        const string popupId = "Save Replay##replay-file";
        ImGuiExtensions.OpenPopupWhenRequested(popupId, ref _requestSaveReplayModal);
        CenterNextReplayModal();
        bool open = true;
        using var modal = ImGuiExtensions.BeginPopupModal(popupId, ref open);
        if (!modal.IsVisible || _frame.Timeline is not { } timeline) return;

        ImGui.SetNextItemWidth(460f);
        ImGui.InputText(Locale.GetString("replay-file-path-label"), ref _replaySavePath, 4096);
        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("common-browse") + "##save-replay")) PickReplaySaveFile();
        if (timeline.IsInspecting)
        {
            ImGui.RadioButton(Locale.GetString("replay-file-save-full-branch-option"), ref _replaySaveRange, 0);
            ImGui.RadioButton(Locale.GetString("replay-file-save-branch-until-here-option"), ref _replaySaveRange, 1);
        }
        else
        {
            _replaySaveRange = 0;
            ImGui.TextDisabled(Locale.GetString("replay-file-save-live-branch-description"));
        }

        if (timeline.Branches.Count > 1)
        {
            ImGui.TextColored(
                ViewerTheme.Caution,
                Locale.GetString("replay-file-save-selected-branch-warning", ("branchName", GetSelectedBranch()?.Name ?? string.Empty)));
        }

        string normalized = ReplayPaths.Normalize(_replaySavePath);
        bool overwrite = File.Exists(normalized);
        if (overwrite)
            ImGui.TextColored(ViewerTheme.Caution, Locale.GetString("replay-file-overwrite-warning"));

        DrawReplayFileError();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_replaySavePath) || IsReplaySaving() || IsBusy);
        if (ImGui.Button(overwrite ? Locale.GetString("replay-file-overwrite-button") : Locale.GetString("replay-file-save-button"), new Vector2(120f, 0f)))
        {
            StartReplaySave(normalized, overwrite);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("common-cancel"), new Vector2(120f, 0f))) ImGui.CloseCurrentPopup();
        ImGui.SetItemDefaultFocus();
    }

    private bool IsReplaySaving()
    {
        return _replaySavePreparing || _replaySaveTask != null;
    }

    private void DrawReplayProgressModal()
    {
        if (!_replayLoadPending && !IsReplaySaving()) return;

        RequestNextFrame();
        ImGui.OpenPopup("Replay File Progress##replay-file");
        bool open = true;
        using var modal = ImGuiExtensions.BeginPopupModal("Replay File Progress##replay-file", ref open);
        if (!modal.IsVisible) return;

        if (_replayLoadPending)
        {
            // The load runs as a host job, so its progress arrives in the busy frame.
            float progress = _frame.BusyLabel == HostJobLabels.LoadReplay ? (float)(_frame.BusyProgress ?? 0d) : 0f;
            ImGui.TextUnformatted(Locale.GetString("replay-file-loading-progress"));
            ImGui.ProgressBar(Math.Clamp(progress, 0f, 1f), new Vector2(420f, 0f));
            if (ImGui.Button(Locale.GetString("common-cancel"), new Vector2(120f, 0f))) _replayFileCancellation?.Cancel();
        }
        else
        {
            ImGui.TextUnformatted(Locale.GetString("replay-file-saving-progress"));
            ImGui.ProgressBar(-1f * (float)ImGui.GetTime(), new Vector2(420f, 0f));
        }
    }

    private void DrawReplayFileError()
    {
        ImGuiExtensions.Feedback(_replayFileError, true);
    }

    private static void CenterNextReplayModal()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(
            viewport.Pos + viewport.Size * 0.5f,
            ImGuiCond.Always,
            new Vector2(0.5f, 0.5f));
    }

    /// <summary>
    ///     Loads a replay as one busy host job, which reads and indexes the file and then opens it as the project.
    /// </summary>
    /// <remarks>
    ///     The job returns a failure instead of throwing it. The session already logged the failure, and a thrown
    ///     exception would make the host log it a second time.
    /// </remarks>
    private void StartReplayLoad(string path)
    {
        _replayFileCancellation?.Dispose();
        _replayFileCancellation = new CancellationTokenSource();
        var cancellation = _replayFileCancellation.Token;
        _replayFileError = null;
        _replayLoadPending = true;
        string fullPath = Path.GetFullPath(path);
        PauseHost();
        SendJob<Exception?>(
            HostJobLabels.LoadReplay,
            (session, reportProgress) =>
            {
                var progress = new InlineProgress<AtmosWorldReplayIndexProgress>(value =>
                    reportProgress(value.TotalTicks == 0 ? 1d : (double)value.CompletedTicks / value.TotalTicks));

                LoadedReplay loaded;
                try
                {
                    loaded = session.LoadReplay(fullPath, progress, cancellation);
                }
                catch (Exception exception)
                {
                    return exception;
                }

                try
                {
                    session.InstallReplay(loaded);
                    return null;
                }
                catch (Exception exception)
                {
                    loaded.Dispose();
                    return exception;
                }
            },
            CompleteReplayLoad);
    }

    private void CompleteReplayLoad(HostResult<Exception?> result)
    {
        _replayLoadPending = false;
        _replayFileCancellation?.Dispose();
        _replayFileCancellation = null;
        switch (result.Exception ?? result.Value)
        {
            case null:
                _showTimelinePanel = true;
                break;
            case OperationCanceledException:
                _replayFileError = Locale.GetString("replay-file-loading-cancelled-error");
                break;
            case var exception:
                _replayFileError = Locale.GetString("replay-file-load-error", ("message", DescribeFailure(exception)));
                _requestOpenReplayModal = true;
                break;
        }

        RequestNextFrame();
    }

    /// <summary>
    ///     Captures the replay on the host thread, then writes the file on a worker thread.
    /// </summary>
    private void StartReplaySave(string path, bool overwrite)
    {
        PauseHost();
        bool throughCurrentPosition = _replaySaveRange == 1;
        _replaySavePreparing = true;
        _replayFileError = null;
        var log = _host.Log;
        Send(
            session => session.PrepareReplaySave(path, throughCurrentPosition, overwrite),
            result =>
            {
                _replaySavePreparing = false;
                if (result.Exception is { } exception)
                {
                    _replayFileError = Locale.GetString(
                        "replay-file-prepare-save-error",
                        ("message", DescribeFailure(exception)));

                    _requestSaveReplayModal = true;
                    return;
                }

                var job = result.Value!;
                _replaySaveTask = Task.Run(() => WriteReplay(job, throughCurrentPosition, log));
            });
    }

    /// <summary>
    ///     Writes a captured replay and logs the result. It runs on a worker thread and touches only the job and the
    ///     thread-safe log.
    /// </summary>
    private static void WriteReplay(ReplaySaveJob job, bool throughCurrentPosition, WorkbenchLog log)
    {
        try
        {
            job.Execute();
            string range = throughCurrentPosition
                ? "selected branch up to the inspected position"
                : "full selected branch";

            log.Write(WorkbenchLogLevel.Info, "Replay", $"Saved replay to '{job.Path}' ({range}).");
        }
        catch (Exception exception)
        {
            log.Write(
                WorkbenchLogLevel.Error,
                "Replay",
                $"Could not save the replay: {exception.Message}",
                exception.ToString());

            throw;
        }
    }

    private void UpdateReplayFileOperations()
    {
        if (_replaySaveTask?.IsCompleted == true)
        {
            var task = _replaySaveTask;
            _replaySaveTask = null;
            try
            {
                task.GetAwaiter().GetResult();
                RequestNextFrame();
            }
            catch (Exception exception)
            {
                // WriteReplay already logged the failure.
                _replayFileError = Locale.GetString("replay-file-save-error", ("message", exception.Message));
                _requestSaveReplayModal = true;
                RequestNextFrame();
            }
        }
    }

    private void HandleReplayFileDrop()
    {
        if (!Raylib.IsFileDropped()) return;
#pragma warning disable CS0618
        string[] paths = Raylib.GetDroppedFiles();
#pragma warning restore CS0618
        if (paths.Length == 1 && string.Equals(Path.GetExtension(paths[0]), ReplayExtension, StringComparison.OrdinalIgnoreCase))
            RequestOpenReplay(paths[0]);
        else
        {
            SetReplayFileError(Locale.GetString("replay-file-drop-single-file-error"));
            _requestOpenReplayModal = true;
        }
    }

    private void SetReplayFileError(string message, Exception? exception = null)
    {
        _replayFileError = exception == null ? message : $"{message} {exception.Message}";
        if (exception == null)
            WriteMessage(WorkbenchLogLevel.Error, "Replay", message);
        else
            WriteException(message, exception);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }
}