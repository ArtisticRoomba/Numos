using System.Collections.Immutable;
using System.Diagnostics;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.Replay;
using Numos.Maths;

namespace Numos.Workbench;

public sealed partial class WorkbenchHost
{
    private const double RealtimeWindowSeconds = 0.5;

    private string? _busyLabel;
    private double? _busyProgress;
    private FrameCache _cache = new();
    private long _frameSequence;
    private double _realtimeFactor;
    private long _windowTick;
    private long _windowTimestamp;

    private void PublishIfRequested()
    {
        if (Interlocked.Exchange(ref _frameRequested, 0) == 0)
            return;

        var fields = (AtmosChunkSnapshotFields)Volatile.Read(ref _requestedFields);
        Interlocked.Exchange(ref _frame, BuildFrame(fields));
    }

    private void BeginBusy(string label)
    {
        _busyLabel = label;
        _busyProgress = null;
        PublishBusyIfRequested();
    }

    private void EndBusy()
    {
        _busyLabel = null;
        _busyProgress = null;
    }

    private void ReportProgress(double fraction)
    {
        // Only the host thread owns the busy state. A job that reports from a worker thread just loses the update.
        if (Thread.CurrentThread != _thread || _busyLabel == null)
            return;

        _busyProgress = double.IsFinite(fraction) ? Math.Clamp(fraction, 0d, 1d) : null;
        PublishBusyIfRequested();
    }

    /// <summary>
    ///     Republishes the last frame with the busy state, because a full frame cannot be built while a job holds the
    ///     session.
    /// </summary>
    private void PublishBusyIfRequested()
    {
        if (Interlocked.Exchange(ref _frameRequested, 0) == 0)
            return;

        var busy = Frame with
        {
            Sequence = ++_frameSequence,
            RunState = WorkbenchRunState.Busy,
            BusyLabel = _busyLabel,
            BusyProgress = _busyProgress
        };

        Interlocked.Exchange(ref _frame, busy);
    }

    private void ResetRealtimeWindow()
    {
        _realtimeFactor = 0d;
        _windowTimestamp = Stopwatch.GetTimestamp();
        _windowTick = _session.World?.TickCount ?? 0;
    }

    private void MeasureRealtime(AtmosWorld world, long now)
    {
        double wall = Stopwatch.GetElapsedTime(_windowTimestamp, now).TotalSeconds;
        if (wall < RealtimeWindowSeconds)
            return;

        // A playback seek can move backwards, which reads as no progress.
        double simulated = Math.Max(0L, world.TickCount - _windowTick) / (double)world.TickRate;
        _realtimeFactor = simulated / wall;
        _windowTimestamp = now;
        _windowTick = world.TickCount;
    }

    private WorkbenchFrame BuildFrame(AtmosChunkSnapshotFields fields)
    {
        var session = _session;
        var world = session.World;
        (WorkbenchRunState state, string? label, double? progress) = GetRunState();
        if (world == null)
        {
            return new WorkbenchFrame
            {
                Sequence = ++_frameSequence,
                ProjectEpoch = ProjectEpoch,
                HasProject = false,
                ProjectName = null,
                RunState = state,
                BusyLabel = label,
                BusyProgress = progress,
                TickCount = 0,
                SimulatedTime = 0d,
                TickRate = 0,
                RealtimeFactor = 0d,
                ActiveSimulationId = null,
                SimulationCollectionRevision = -1,
                LinkSetRevision = -1,
                TopologyVersion = 0,
                ActiveLinkCount = 0,
                TimelineRevision = session.TimelineRevision,
                DefinitionRevision = session.DefinitionRevision,
                SnapshotFields = fields,
                Simulations = [],
                LinkSets = [],
                SolverSteps = [],
                Gases = [],
                ConfigSnapshot = null,
                Config = null,
                Timeline = null
            };
        }

        var cache = _cache;
        if (cache.TimelineRevision != session.TimelineRevision)
        {
            // A timeline move restores chunks, so a version the cache holds no longer proves the data matches.
            cache.TimelineRevision = session.TimelineRevision;
            cache.ResetWorldState();
        }

        if (cache.DefinitionRevision != session.DefinitionRevision)
        {
            cache.DefinitionRevision = session.DefinitionRevision;
            cache.SolverSteps = world.Solvers.Steps;
        }

        var configSnapshot = world.Config;
        if (!ReferenceEquals(configSnapshot, cache.ConfigSnapshot))
        {
            cache.ConfigSnapshot = configSnapshot;
            cache.Config = new AtmosConfig(configSnapshot);
            cache.Gases = configSnapshot.GasRegistry.ToArray();
        }

        if (world.TryGetLinkSets(cache.LinkSetRevision, out long linkRevision, out AtmosWorldLinkSetSnapshot[] links))
        {
            cache.LinkSetRevision = linkRevision;
            cache.LinkSets = links;
        }

        return new WorkbenchFrame
        {
            Sequence = ++_frameSequence,
            ProjectEpoch = ProjectEpoch,
            HasProject = true,
            ProjectName = session.ProjectName,
            RunState = state,
            BusyLabel = label,
            BusyProgress = progress,
            TickCount = world.TickCount,
            SimulatedTime = world.SimulatedTime,
            TickRate = world.TickRate,
            RealtimeFactor = _running ? _realtimeFactor : 0d,
            ActiveSimulationId = session.ActiveSimulationId,
            SimulationCollectionRevision = BuildSimulations(world, fields),
            LinkSetRevision = cache.LinkSetRevision,
            TopologyVersion = world.TopologyVersion,
            ActiveLinkCount = world.ActiveLinkCount,
            TimelineRevision = session.TimelineRevision,
            DefinitionRevision = session.DefinitionRevision,
            SnapshotFields = fields,
            Simulations = cache.SimulationFrames,
            LinkSets = cache.LinkSets,
            SolverSteps = cache.SolverSteps,
            Gases = cache.Gases,
            ConfigSnapshot = configSnapshot,
            Config = cache.Config,
            Timeline = BuildTimeline()
        };
    }

    private (WorkbenchRunState State, string? Label, double? Progress) GetRunState()
    {
        if (_busyLabel != null)
            return (WorkbenchRunState.Busy, _busyLabel, _busyProgress);

        if (_advance is { } job)
            return (WorkbenchRunState.Busy, AdvanceJobLabel, (double)job.Done / job.Total);

        return (_running ? WorkbenchRunState.Running : WorkbenchRunState.Paused, null, null);
    }

    /// <summary>
    ///     Refreshes <see cref="FrameCache.SimulationFrames" /> and returns the simulation collection revision.
    /// </summary>
    private long BuildSimulations(AtmosWorld world, AtmosChunkSnapshotFields fields)
    {
        var cache = _cache;
        if (world.TryGetSimulations(cache.SimulationRevision, out long revision, out AtmosSimulation[] simulations))
        {
            cache.SimulationRevision = revision;
            cache.SimulationObjects = simulations;
            var live = simulations.Select(static simulation => simulation.Id).ToHashSet();
            foreach (var stale in cache.Simulations.Keys.Where(id => !live.Contains(id)).ToArray())
                cache.Simulations.Remove(stale);
        }

        var previous = cache.SimulationFrames;
        var frames = new WorkbenchSimulationFrame[cache.SimulationObjects.Length];
        bool reused = previous.Count == frames.Length;
        for (int index = 0; index < frames.Length; index++)
        {
            frames[index] = BuildSimulation(cache.SimulationObjects[index], fields);
            reused = reused && ReferenceEquals(frames[index], previous[index]);
        }

        if (!reused)
            cache.SimulationFrames = frames;

        return revision;
    }

    private WorkbenchSimulationFrame BuildSimulation(AtmosSimulation simulation, AtmosChunkSnapshotFields fields)
    {
        if (!_cache.Simulations.TryGetValue(simulation.Id, out var cache) ||
            !ReferenceEquals(cache.Simulation, simulation))
        {
            cache = new SimulationCache(simulation);
            _cache.Simulations[simulation.Id] = cache;
        }

        bool handlesChanged = simulation.TryGetChunkHandles(
            cache.ChunkRevision,
            out long chunkRevision,
            out AtmosChunkHandle[] handles);

        if (handlesChanged)
        {
            cache.ChunkRevision = chunkRevision;
            cache.Handles = handles;
        }

        var map = cache.Snapshots;
        var requests = new AtmosChunkSnapshotRequest[cache.Handles.Length];
        for (int index = 0; index < requests.Length; index++)
        {
            Int3 position = cache.Handles[index].Position;
            bool usable = map.TryGetValue(position, out var cached) && cached.HasFields(fields);
            requests[index] = new AtmosChunkSnapshotRequest(position, usable ? cached.Version : default, fields);
        }

        var batch = simulation.GetChangedChunkSnapshots(requests);
        if (batch.ChangedChunks.Length > 0)
        {
            var builder = map.ToBuilder();
            foreach (var snapshot in batch.ChangedChunks)
                builder[snapshot.GridPosition] = snapshot;

            map = builder.ToImmutable();
        }

        if (handlesChanged)
        {
            var live = cache.Handles.Select(static handle => handle.Position).ToHashSet();
            Int3[] stale = map.Keys.Where(position => !live.Contains(position)).ToArray();
            if (stale.Length > 0)
                map = map.RemoveRange(stale);
        }

        cache.Snapshots = map;
        var frame = new WorkbenchSimulationFrame
        {
            Id = simulation.Id,
            Name = _session.GetSimulationName(simulation.Id),
            ChunkDimensions = simulation.ChunkDimensions,
            ChunkRevision = cache.ChunkRevision,
            ChunkHandles = cache.Handles,
            Snapshots = map,
            SnapshotTickCount = batch.TickCount,
            FlowDiagnostics = simulation.LastFlowDiagnostics
        };

        if (cache.Frame != null && cache.Frame.Equals(frame))
            return cache.Frame;

        cache.Frame = frame;
        return frame;
    }

    private WorkbenchTimelineFrame BuildTimeline()
    {
        var cache = _cache;
        var timeline = _session.Timeline!;
        var branches = _session.Branches!;
        var checkpoints = timeline.Checkpoints;
        if (checkpoints.Count != cache.Checkpoints.Count)
            cache.Checkpoints = checkpoints.ToArray();

        var head = timeline.Head;
        if (cache.BranchKey is not { } key ||
            key.Count != branches.BranchCount ||
            key.Selected != branches.SelectedBranchId ||
            key.Head != head)
        {
            cache.BranchKey = (branches.BranchCount, branches.SelectedBranchId, head);
            cache.Branches = branches.Branches;
            cache.MaximumHeadTick = branches.MaximumHeadTick;
        }

        var frame = new WorkbenchTimelineFrame
        {
            IsInspecting = timeline.IsInspecting,
            IsImported = timeline.IsImported,
            Position = timeline.Position,
            Start = timeline.Start,
            Head = head,
            MaximumHeadTick = cache.MaximumHeadTick,
            CanContinueSelectedBranch = branches.CanContinueSelectedBranch,
            LastReplay = timeline.LastReplay,
            IsVerified = timeline.IsVerified,
            Checkpoints = cache.Checkpoints,
            // The timeline returns the same detached list until an operation is recorded or the branch changes.
            Operations = timeline.Operations,
            Branches = cache.Branches,
            SelectedBranchId = branches.SelectedBranchId
        };

        if (cache.Timeline != null && cache.Timeline.Equals(frame))
            return cache.Timeline;

        cache.Timeline = frame;
        return frame;
    }

    /// <summary>
    ///     What the previous frame was built from, so an unchanged section is reused by reference.
    /// </summary>
    /// <remarks>
    ///     The host replaces the whole cache when the project changes.
    /// </remarks>
    private sealed class FrameCache
    {
        public long TimelineRevision { get; set; } = long.MinValue;
        public long DefinitionRevision { get; set; } = long.MinValue;
        public IReadOnlyList<AtmosWorldSolverStep> SolverSteps { get; set; } = [];
        public AtmosConfigSnapshot? ConfigSnapshot { get; set; }
        public AtmosConfig? Config { get; set; }
        public IReadOnlyList<GasProperties> Gases { get; set; } = [];
        public long LinkSetRevision { get; set; } = long.MinValue;
        public IReadOnlyList<AtmosWorldLinkSetSnapshot> LinkSets { get; set; } = [];
        public long SimulationRevision { get; set; } = long.MinValue;
        public AtmosSimulation[] SimulationObjects { get; set; } = [];
        public Dictionary<AtmosSimulationId, SimulationCache> Simulations { get; } = [];
        public IReadOnlyList<WorkbenchSimulationFrame> SimulationFrames { get; set; } = [];
        public IReadOnlyList<AtmosWorldReplayVerificationPoint> Checkpoints { get; set; } = [];
        public (int Count, int Selected, AtmosTimelinePosition Head)? BranchKey { get; set; }
        public IReadOnlyList<ReplayBranchInfo> Branches { get; set; } = [];
        public ulong MaximumHeadTick { get; set; }
        public WorkbenchTimelineFrame? Timeline { get; set; }

        /// <summary>
        ///     Forgets the simulations, chunks, link sets, and checkpoints, which a timeline move can replace.
        /// </summary>
        public void ResetWorldState()
        {
            LinkSetRevision = long.MinValue;
            SimulationRevision = long.MinValue;
            SimulationObjects = [];
            Simulations.Clear();
            Checkpoints = [];
            BranchKey = null;
        }
    }

    private sealed class SimulationCache(AtmosSimulation simulation)
    {
        public AtmosSimulation Simulation { get; } = simulation;
        public long ChunkRevision { get; set; } = long.MinValue;
        public AtmosChunkHandle[] Handles { get; set; } = [];
        public ImmutableDictionary<Int3, AtmosChunkSnapshot> Snapshots { get; set; } =
            ImmutableDictionary<Int3, AtmosChunkSnapshot>.Empty;
        public WorkbenchSimulationFrame? Frame { get; set; }
    }
}
