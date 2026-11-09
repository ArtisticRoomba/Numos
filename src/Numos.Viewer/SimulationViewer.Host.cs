using System.Diagnostics;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.SimDrawer;
using Numos.Viewer.Ui;
using Numos.Viewer.Workspace;
using Numos.Workbench;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    /// <summary>
    ///     How long the viewer keeps requesting voxel detail fields after it last drew voxel details. Moving the pointer
    ///     on and off a voxel then does not make the host recapture every chunk each time.
    /// </summary>
    private readonly static TimeSpan VoxelDetailFieldHold = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Runs the session on its own thread. The viewer reaches the world only through its commands and frames.
    /// </summary>
    private readonly WorkbenchHost _host;

    /// <summary>
    ///     A sequence number the viewer waits to see exceeded. A frame with a higher sequence was published after the
    ///     viewer took a command result, so it shows that command's effect.
    /// </summary>
    private long? _awaitedFrameSequence;

    /// <summary>
    ///     The UI state of the open project. <see cref="UpdateHost" /> replaces it when the frame's project epoch
    ///     changes, and an empty document stands in while no project is open.
    /// </summary>
    private ProjectDocument _document;

    /// <summary>
    ///     The frame every panel draws this UI frame. <see cref="UpdateHost" /> replaces it once per UI frame.
    /// </summary>
    private WorkbenchFrame _frame;

    private int _outstandingCommands;
    private long _projectEpoch;
    private long _voxelDetailsWantedUntil;

    /// <summary>
    ///     Whether the timeline shows history, which makes the world read-only.
    /// </summary>
    private bool IsInspecting => _frame.Timeline?.IsInspecting == true;

    /// <summary>
    ///     Whether the host runs a long job, such as a step of many ticks, a seek, or a replay load.
    /// </summary>
    private bool IsBusy => _frame.RunState == WorkbenchRunState.Busy;

    /// <summary>
    ///     Whether editing controls must stay disabled: the timeline inspects history or the host is busy.
    /// </summary>
    private bool IsEditingBlocked => IsInspecting || IsBusy;

    /// <summary>
    ///     Whether the world does not advance in real time. A busy host counts as paused.
    /// </summary>
    private bool IsPaused => _frame.RunState != WorkbenchRunState.Running;

    /// <summary>
    ///     The registered gases, indexed by gas id.
    /// </summary>
    private IReadOnlyList<GasProperties> Gases => _frame.Gases;

    /// <summary>
    ///     Runs host callbacks, takes the latest frame, and asks the host for the next one.
    /// </summary>
    /// <remarks>
    ///     Callbacks run before the frame changes, so a callback sees the frame its panel drew. A project change resets
    ///     the presentation here, on the UI thread, because it disposes GPU resources.
    /// </remarks>
    private void UpdateHost()
    {
        int completed = _host.DrainCompletions();
        var previous = _frame;
        var frame = _host.Frame;
        _frame = frame;
        if (frame.ProjectEpoch != _projectEpoch)
        {
            _projectEpoch = frame.ProjectEpoch;
            ResetProjectPresentation();
            if (frame.HasProject)
                OpenProjectPresentation(frame);
        }

        _document.ConfigDraft.Sync(frame.ConfigSnapshot, frame.DefinitionRevision);

        if (completed > 0)
        {
            _awaitedFrameSequence = frame.Sequence;
            RequestNextFrame();
        }
        else if (_awaitedFrameSequence is { } awaited && frame.Sequence > awaited)
        {
            _awaitedFrameSequence = null;
        }

        if (FrameChanges.HasVisibleChange(previous, frame))
            RequestNextFrame();

        _host.RequestFrame(GetRequestedSnapshotFields());
    }

    /// <summary>
    ///     Sets up the presentation of a project the host just created or opened.
    /// </summary>
    private void OpenProjectPresentation(WorkbenchFrame frame)
    {
        _document.TimelineFirstTick = checked((long)(frame.Timeline?.Start.Tick ?? 0));
        _frameSceneOnNextPresentation = true;
        SyncPresentationConfig();
        RequestNextFrame();
    }

    /// <summary>
    ///     Rebuilds the visualizations and the frame builders when the frame carries another configuration copy.
    /// </summary>
    /// <remarks>
    ///     The builders read the frame's copy, never the configuration the host runs with. A build still running on an
    ///     old builder is dropped when it finishes.
    /// </remarks>
    private void SyncPresentationConfig()
    {
        var config = _frame.Config;
        if (config == null || ReferenceEquals(config, _document.PresentationConfig))
            return;

        _document.PresentationConfig = config;
        var visualizations = VisualizationRegistry.CreateDefault(config);
        _configureVisualizations?.Invoke(visualizations);
        _document.FrameBuilder = new SimulationFrameBuilder(config, visualizations);
        if (!visualizations.Methods.Any(method =>
                string.Equals(method.Id, _currentVisualizationId, StringComparison.OrdinalIgnoreCase)))
        {
            _currentVisualizationId = visualizations.Methods[0].Id;
        }

        foreach (var surface in _document.Surfaces)
            surface.Builder = new SimulationFrameBuilder(config, visualizations);
    }

    /// <summary>
    ///     The fields the drawing needs, plus the voxel detail fields while details are on screen.
    /// </summary>
    private AtmosChunkSnapshotFields GetRequestedSnapshotFields()
    {
        if (!_frame.HasProject)
            return AtmosChunkSnapshotFields.None;

        var fields = GetDrawSnapshotFields();
        if (_selectedCell.HasValue || _hovered3DCell.HasValue || _hoveredSliceCell.HasValue)
        {
            _voxelDetailsWantedUntil = Stopwatch.GetTimestamp() +
                                       (long)(VoxelDetailFieldHold.TotalSeconds * Stopwatch.Frequency);
        }

        if (Stopwatch.GetTimestamp() < _voxelDetailsWantedUntil)
            fields |= VoxelDetails.RequiredFields;

        return fields;
    }

    /// <summary>
    ///     Whether the viewer has to keep drawing to pick up host work it is waiting for.
    /// </summary>
    private bool IsAwaitingHost()
    {
        return _outstandingCommands > 0 ||
               _awaitedFrameSequence.HasValue ||
               _frame.RunState != WorkbenchRunState.Paused;
    }

    /// <summary>
    ///     Sends a command to the host and keeps the viewer drawing until its result arrives.
    /// </summary>
    /// <param name="work">
    ///     Runs on the host thread. It must capture only values copied before this call, never viewer fields.
    /// </param>
    /// <param name="onComplete">Runs on the UI thread at the top of a later <see cref="Update" />.</param>
    private void Send<T>(Func<WorkbenchSession, T> work, Action<HostResult<T>>? onComplete = null)
    {
        _outstandingCommands++;
        _host.Submit(work, result => CompleteCommand(result, onComplete));
    }

    /// <summary>
    ///     Sends a long job that the toolbar shows as busy. The rules of <see cref="Send{T}" /> apply.
    /// </summary>
    private void SendJob<T>(
        string label,
        Func<WorkbenchSession, Action<double>, T> work,
        Action<HostResult<T>>? onComplete = null)
    {
        _outstandingCommands++;
        _host.SubmitJob(label, work, result => CompleteCommand(result, onComplete));
    }

    private void CompleteCommand<T>(HostResult<T> result, Action<HostResult<T>>? onComplete)
    {
        _outstandingCommands = Math.Max(0, _outstandingCommands - 1);
        onComplete?.Invoke(result);
    }

    /// <summary>
    ///     Starts real-time ticking, or timeline playback while the timeline inspects history.
    /// </summary>
    private void RunHost()
    {
        _document.SteppedToTick = null;
        _host.Run();
        RequestNextFrame();
    }

    private void PauseHost()
    {
        _host.Pause();
        RequestNextFrame();
    }

    private WorkbenchSimulationFrame? GetSimulationFrame(AtmosSimulationId id)
    {
        foreach (var simulation in _frame.Simulations)
        {
            if (simulation.Id == id)
                return simulation;
        }

        return null;
    }

    private ReplayBranchInfo? GetSelectedBranch()
    {
        if (_frame.Timeline is not { } timeline)
            return null;

        foreach (var branch in timeline.Branches)
        {
            if (branch.Id == timeline.SelectedBranchId)
                return branch;
        }

        return null;
    }

    /// <summary>
    ///     The message of a failed command, without the wrapper an aggregate adds.
    /// </summary>
    private static string DescribeFailure(Exception exception)
    {
        return exception is AggregateException { InnerException: { } inner } ? inner.Message : exception.Message;
    }
}
