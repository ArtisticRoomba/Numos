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

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private const string ReplayExtension = ".numos";
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
                .AddFilter("Numos replay", "numos");

            string? initialDirectory = GetReplayOpenDirectory();
            var result = dialog.Open(out string? path, initialDirectory);
            if (result == DialogResult.Cancel)
                return;

            if (result == DialogResult.Error || string.IsNullOrWhiteSpace(path))
            {
                SetReplayFileError("The operating system file dialog could not select a replay.");
                _requestOpenReplayModal = true;
                return;
            }

            if (!string.Equals(Path.GetExtension(path), ReplayExtension, StringComparison.OrdinalIgnoreCase))
            {
                SetReplayFileError("Select a .numos replay file.");
                _requestOpenReplayModal = true;
                return;
            }

            RequestOpenReplay(path);
        }
        catch (Exception exception)
        {
            SetReplayFileError("The operating system file dialog could not be opened.", exception);
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
        if (_simulation == null) return;

        if (string.IsNullOrWhiteSpace(_replaySavePath))
            _replaySavePath = SanitizeReplayName(_projectName ?? "simulation") + ReplayExtension;

        PickReplaySaveFile();
    }

    private void PickReplaySaveFile()
    {
        try
        {
            using var dialog = new NativeFileDialog()
                .SaveFile()
                .AddFilter("Numos replay", "numos");

            string fullPath = Path.GetFullPath(_replaySavePath);
            string? initialDirectory = Path.GetDirectoryName(fullPath);
            string suggestedName = Path.GetFileName(fullPath);
            var result = dialog.Open(out string? path, initialDirectory, suggestedName);
            if (result == DialogResult.Cancel)
                return;

            if (result == DialogResult.Error || string.IsNullOrWhiteSpace(path))
            {
                SetReplayFileError("The operating system file dialog could not select a save location.");
                _requestSaveReplayModal = true;
                return;
            }

            _replaySavePath = NormalizeReplayPath(path);
            _replayFileError = null;
            _requestSaveReplayModal = true;
        }
        catch (Exception exception)
        {
            SetReplayFileError("The operating system file dialog could not be opened.", exception);
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

        ImGui.TextWrapped("Open a .numos replay. Numos verifies it before replacing the current project.");
        ImGui.SetNextItemWidth(460f);
        ImGui.InputText("Path", ref _replayOpenPath, 4096);
        ImGui.SameLine();
        if (ImGui.Button("Browse...")) PickReplayFile();
        if (_simulation != null)
            ImGui.TextColored(ViewerTheme.Caution, "Loading successfully will close the current in-memory project.");

        DrawReplayFileError();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_replayOpenPath) || _replayLoadTask != null);
        if (ImGui.Button(_simulation == null ? "Open" : "Load and Replace", new Vector2(150f, 0f)))
        {
            StartReplayLoad(_replayOpenPath);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
    }

    private void DrawSaveReplayModal()
    {
        const string popupId = "Save Replay##replay-file";
        ImGuiExtensions.OpenPopupWhenRequested(popupId, ref _requestSaveReplayModal);
        CenterNextReplayModal();
        bool open = true;
        using var modal = ImGuiExtensions.BeginPopupModal(popupId, ref open);
        if (!modal.IsVisible || _replayTimeline == null) return;

        ImGui.SetNextItemWidth(460f);
        ImGui.InputText("Path", ref _replaySavePath, 4096);
        ImGui.SameLine();
        if (ImGui.Button("Browse...##save-replay")) PickReplaySaveFile();
        if (_replayTimeline.IsInspecting)
        {
            ImGui.RadioButton("Save full history", ref _replaySaveRange, 0);
            ImGui.RadioButton("Save up to here", ref _replaySaveRange, 1);
        }
        else
        {
            _replaySaveRange = 0;
            ImGui.TextDisabled("The live replay will be saved through its current head.");
        }

        DrawReplayFileError();
        string normalized = NormalizeReplayPath(_replaySavePath);
        bool overwrite = File.Exists(normalized);
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_replaySavePath) || _replaySaveTask != null);
        if (ImGui.Button(overwrite ? "Overwrite" : "Save", new Vector2(120f, 0f)))
        {
            StartReplaySave(normalized, overwrite);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        if (overwrite)
        {
            ImGui.SameLine();
            ImGui.TextColored(ViewerTheme.Caution, "The existing file will be replaced.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
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
            ImGui.TextUnformatted("Loading, verifying, and indexing replay...");
            ImGui.ProgressBar(Math.Clamp(_replayFileProgress, 0f, 1f), new Vector2(420f, 0f));
            if (ImGui.Button("Cancel")) _replayFileCancellation?.Cancel();
        }
        else
        {
            ImGui.TextUnformatted("Saving replay...");
            ImGui.ProgressBar(-1f * (float)ImGui.GetTime(), new Vector2(420f, 0f));
        }
    }

    private void DrawReplayFileError()
    {
        if (_replayFileError != null) ImGui.TextColored(ViewerTheme.Error, _replayFileError);
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
        var document = NumosReplayFile.Load(path);
        var replay = document.Replay;
        var config = new AtmosConfig(replay.InitialCheckpoint.Config);
        var dimensions = replay.InitialCheckpoint.Dimensions;
        AtmosSimulation? simulation = null;
        try
        {
            simulation = new AtmosSimulation(config, dimensions.X, dimensions.Y, dimensions.Z);
            var progress = new InlineProgress<AtmosReplayIndexProgress>(value =>
                _replayFileProgress = value.TotalTicks == 0 ? 1f : (float)value.CompletedTicks / value.TotalTicks);

            var timeline = AtmosReplayTimeline.Import(simulation, replay, 50, progress, cancellation);
            var result = new LoadedReplay(path, document, simulation, config, timeline);
            simulation = null;
            return result;
        }
        finally
        {
            simulation?.Dispose();
        }
    }

    private void StartReplaySave(string path, bool overwrite)
    {
        try
        {
            _isPaused = true;
            var replay = _replaySaveRange == 1
                ? _replayTimeline!.CaptureReplayThroughCurrentPosition()
                : _replayTimeline!.CaptureReplay();

            replay.EnsurePortable();
            var metadata = new NumosReplayMetadata(
                _projectName ?? Path.GetFileNameWithoutExtension(path),
                DateTimeOffset.UtcNow,
                "Numos.Viewer",
                ViewerBuildInfo.PackageVersion,
                CoreSimBuildInfo.PackageVersion,
                CoreSimBuildInfo.GitCommit ?? CoreSimBuildInfo.SourceReference);

            var document = new NumosReplayDocument(metadata, replay);
            _replaySaveTask = Task.Run(() => NumosReplayFile.Save(path, document, overwrite));
            _replayFileError = null;
        }
        catch (Exception exception)
        {
            SetReplayFileError("Could not prepare the replay for saving.", exception);
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
                _replayFileError = "Replay loading was cancelled.";
                WriteMessage(ViewerLogLevel.Warn, "Replay", _replayFileError);
            }
            catch (Exception exception)
            {
                SetReplayFileError("Could not load the replay.", exception);
                _requestOpenReplayModal = true;
            }
            finally
            {
                loaded?.Simulation.Dispose();
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
                SetProjectMessage($"Saved replay to '{NormalizeReplayPath(_replaySavePath)}'.", false);
            }
            catch (Exception exception)
            {
                SetReplayFileError("Could not save the replay.", exception);
                _requestSaveReplayModal = true;
            }
        }
    }

    private void InstallLoadedReplay(LoadedReplay loaded)
    {
        var visualizations = VisualizationRegistry.CreateDefault(loaded.Config);
        _configureVisualizations?.Invoke(visualizations);
        var frameBuilder = new SimulationFrameBuilder(loaded.Config, visualizations);
        string projectName = string.IsNullOrWhiteSpace(loaded.Document.Metadata.ProjectName)
            ? Path.GetFileNameWithoutExtension(loaded.Path)
            : loaded.Document.Metadata.ProjectName;

        int timelineFirstTick = checked((int)loaded.Timeline.Start.Tick);
        DisposeSimulationProject();
        _simulation = loaded.Simulation;
        _replayTimeline = loaded.Timeline;
        _config = loaded.Config;
        _frameBuilder = frameBuilder;
        _projectName = projectName;
        _chunkDimensions = loaded.Document.Replay.InitialCheckpoint.Dimensions;
        _isPaused = true;
        _showTimelinePanel = true;
        _timelineFirstTick = timelineFirstTick;
        _frameSceneOnNextPresentation = true;
        SetProjectMessage($"Loaded and verified replay '{Path.GetFileName(loaded.Path)}'.", false);
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
            SetReplayFileError("Drop exactly one .numos replay file.");
            _requestOpenReplayModal = true;
        }
    }

    private void SetReplayFileError(string message, Exception? exception = null)
    {
        _replayFileError = exception == null ? message : $"{message} {exception.Message}";
        if (exception == null)
            WriteMessage(ViewerLogLevel.Error, "Replay", message);
        else
            WriteException(message, exception);
    }

    private static string NormalizeReplayPath(string path)
    {
        return Path.GetFullPath(
            string.Equals(Path.GetExtension(path), ReplayExtension, StringComparison.OrdinalIgnoreCase)
                ? path
                : path + ReplayExtension);
    }

    private static string SanitizeReplayName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "simulation" : name;
    }

    private sealed record LoadedReplay(
        string Path,
        NumosReplayDocument Document,
        AtmosSimulation Simulation,
        AtmosConfig Config,
        AtmosReplayTimeline Timeline);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }
}