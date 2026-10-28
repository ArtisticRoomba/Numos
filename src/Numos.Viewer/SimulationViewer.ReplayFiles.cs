using System.Numerics;
using ImGuiNET;
using NativeFileDialogNET;
using Numos.API;
using Numos.CoreSim;
using Numos.Serialization;
using Numos.Serialization.FileSystem;
using Numos.SimDrawer;
using Numos.Viewer.Ui;
using Raylib_cs;
using Numos.Workbench;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private const string ReplayExtension = ReplayPaths.Extension;
    private CancellationTokenSource? _replayFileCancellation;
    private string? _replayFileError;
    private float _replayFileProgress;
    private Task<LoadedReplay>? _replayLoadTask;
    private string _replayOpenPath = string.Empty;
    private string _replaySavePath = string.Empty;
    private int _replaySaveRange;
    private Task? _replaySaveTask;
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
        if (_session.World == null || _session.Timeline == null || _session.Branches == null) return;

        if (string.IsNullOrWhiteSpace(_replaySavePath))
            _replaySavePath = ReplayPaths.SanitizeName(_session.ProjectName ?? "simulation") + ReplayExtension;

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
        if (ImGui.Button(Locale.GetString("replay-file-browse-button"))) PickReplayFile();
        if (_session.World != null)
        {
            ImGui.TextColored(
                ViewerTheme.Caution,
                _session.Branches?.BranchCount > 1
                    ? Locale.GetString("replay-file-load-discard-branches-warning", ("branchCount", _session.Branches.BranchCount))
                    : Locale.GetString("replay-file-load-close-project-warning"));
        }

        DrawReplayFileError();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_replayOpenPath) || _replayLoadTask != null);
        if (ImGui.Button(_simulation == null ? Locale.GetString("replay-file-open-button") : Locale.GetString("replay-file-load-replace-button"), new Vector2(150f, 0f)))
        {
            StartReplayLoad(_replayOpenPath);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("replay-file-cancel-button"), new Vector2(120f, 0f))) ImGui.CloseCurrentPopup();
        ImGui.SetItemDefaultFocus();
    }

    private void DrawSaveReplayModal()
    {
        const string popupId = "Save Replay##replay-file";
        ImGuiExtensions.OpenPopupWhenRequested(popupId, ref _requestSaveReplayModal);
        CenterNextReplayModal();
        bool open = true;
        using var modal = ImGuiExtensions.BeginPopupModal(popupId, ref open);
        if (!modal.IsVisible || _session.Timeline == null) return;

        ImGui.SetNextItemWidth(460f);
        ImGui.InputText(Locale.GetString("replay-file-path-label"), ref _replaySavePath, 4096);
        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("replay-file-browse-save-button") + "##save-replay")) PickReplaySaveFile();
        if (_session.Timeline.IsInspecting)
        {
            ImGui.RadioButton(Locale.GetString("replay-file-save-full-branch-option"), ref _replaySaveRange, 0);
            ImGui.RadioButton(Locale.GetString("replay-file-save-branch-until-here-option"), ref _replaySaveRange, 1);
        }
        else
        {
            _replaySaveRange = 0;
            ImGui.TextDisabled(Locale.GetString("replay-file-save-live-branch-description"));
        }

        if (_session.Branches?.BranchCount > 1)
        {
            ImGui.TextColored(
                ViewerTheme.Caution,
                Locale.GetString("replay-file-save-selected-branch-warning", ("branchName", _session.Branches.SelectedBranch.Name)));
        }

        string normalized = ReplayPaths.Normalize(_replaySavePath);
        bool overwrite = File.Exists(normalized);
        if (overwrite)
            ImGui.TextColored(ViewerTheme.Caution, Locale.GetString("replay-file-overwrite-warning"));

        DrawReplayFileError();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_replaySavePath) || _replaySaveTask != null);
        if (ImGui.Button(overwrite ? Locale.GetString("replay-file-overwrite-button") : Locale.GetString("replay-file-save-button"), new Vector2(120f, 0f)))
        {
            StartReplaySave(normalized, overwrite);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("replay-file-cancel-button"), new Vector2(120f, 0f))) ImGui.CloseCurrentPopup();
        ImGui.SetItemDefaultFocus();
    }

    private void DrawReplayProgressModal()
    {
        if (_replayLoadTask == null && _replaySaveTask == null) return;

        RequestNextFrame();
        ImGui.OpenPopup("Replay File Progress##replay-file");
        bool open = true;
        using var modal = ImGuiExtensions.BeginPopupModal("Replay File Progress##replay-file", ref open);
        if (!modal.IsVisible) return;

        if (_replayLoadTask != null)
        {
            ImGui.TextUnformatted(Locale.GetString("replay-file-loading-progress"));
            ImGui.ProgressBar(Math.Clamp(_replayFileProgress, 0f, 1f), new Vector2(420f, 0f));
            if (ImGui.Button(Locale.GetString("replay-file-cancel-button"), new Vector2(120f, 0f))) _replayFileCancellation?.Cancel();
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

    private void StartReplayLoad(string path)
    {
        _replayFileCancellation?.Dispose();
        _replayFileCancellation = new CancellationTokenSource();
        var cancellation = _replayFileCancellation.Token;
        _replayFileProgress = 0f;
        _replayFileError = null;
        string fullPath = Path.GetFullPath(path);
        _replayLoadTask = Task.Run(() => LoadReplay(fullPath, cancellation), cancellation);
    }

    private LoadedReplay LoadReplay(string path, CancellationToken cancellation)
    {
        var progress = new InlineProgress<AtmosWorldReplayIndexProgress>(value =>
            _replayFileProgress = value.TotalTicks == 0 ? 1f : (float)value.CompletedTicks / value.TotalTicks);

        return _session.LoadReplay(path, progress, cancellation);
    }

    private void StartReplaySave(string path, bool overwrite)
    {
        try
        {
            _isPaused = true;
            bool throughCurrentPosition = _replaySaveRange == 1;
            var job = _session.PrepareReplaySave(path, throughCurrentPosition, overwrite);
            _replaySaveTask = Task.Run(() => _session.WriteReplay(job, throughCurrentPosition));
            _replayFileError = null;
        }
        catch (Exception exception)
        {
            _replayFileError = Locale.GetString("replay-file-prepare-save-error", ("message", exception.Message));
            _requestSaveReplayModal = true;
        }
    }

    private void UpdateReplayFileOperations()
    {
        if (_replayLoadTask?.IsCompleted == true)
        {
            Task<LoadedReplay> task = _replayLoadTask;
            _replayLoadTask = null;
            LoadedReplay? loaded = null;
            try
            {
                loaded = task.GetAwaiter().GetResult();
                InstallLoadedReplay(loaded);
                loaded = null;
            }
            catch (OperationCanceledException)
            {
                _replayFileError = Locale.GetString("replay-file-loading-cancelled-error");
                RequestNextFrame();
            }
            catch (Exception exception)
            {
                // The session already logged the failure.
                _replayFileError = Locale.GetString("replay-file-load-error", ("message", exception.Message));
                _requestOpenReplayModal = true;
                RequestNextFrame();
            }
            finally
            {
                loaded?.World.Dispose();
            }

            _replayFileCancellation?.Dispose();
            _replayFileCancellation = null;
        }

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
                // The session already logged the failure.
                _replayFileError = Locale.GetString("replay-file-save-error", ("message", exception.Message));
                _requestSaveReplayModal = true;
                RequestNextFrame();
            }
        }
    }

    private void InstallLoadedReplay(LoadedReplay loaded)
    {
        SimulationFrameBuilder? frameBuilder = null;
        _session.InstallReplay(
            loaded,
            config =>
            {
                var visualizations = VisualizationRegistry.CreateDefault(config);
                _configureVisualizations?.Invoke(visualizations);
                frameBuilder = new SimulationFrameBuilder(config, visualizations);
            });

        _frameBuilder = frameBuilder;
        _knownSimulationRevision = -1;
        ReconcileSimulationSurfaces();
        _isPaused = true;
        _showTimelinePanel = true;
        _timelineFirstTick = checked((int)loaded.Timeline.Start.Tick);
        _frameSceneOnNextPresentation = true;
        RequestNextFrame();
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