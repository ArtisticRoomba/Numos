using Numos.API;
using Numos.CoreSim.Replay;

namespace Numos.Viewer;

/// <summary>
///     Keeps the Viewer's in-memory replay branches while one branch is attached to the live world.
/// </summary>
internal sealed class ReplayBranchSession
{
    private readonly List<ReplayBranch> _branches = [];
    private readonly ulong _checkpointInterval;
    private readonly Func<AtmosWorld, AtmosWorldReplayArchive, ulong, AtmosWorldReplayTimeline> _restoreTimeline;
    private readonly AtmosWorld _world;
    private int _nextBranchNumber = 1;
    private ReplayBranch _selectedBranch;

    public ReplayBranchSession(
        AtmosWorld world,
        AtmosWorldReplayTimeline timeline,
        ulong checkpointInterval = 50,
        Func<AtmosWorld, AtmosWorldReplayArchive, ulong, AtmosWorldReplayTimeline>? restoreTimeline = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentOutOfRangeException.ThrowIfZero(checkpointInterval);

        _world = world;
        Timeline = timeline;
        _checkpointInterval = checkpointInterval;
        _restoreTimeline = restoreTimeline ?? RestoreTimeline;
        _selectedBranch = new ReplayBranch(0, "main", null, timeline.Start, null);
        _branches.Add(_selectedBranch);
    }

    public AtmosWorldReplayTimeline Timeline { get; private set; }

    public int SelectedBranchId => _selectedBranch.Id;

    public int BranchCount => _branches.Count;

    public ulong MaximumHeadTick => _branches.Max(branch => GetHead(branch).Tick);

    public bool CanContinueSelectedBranch =>
        Timeline.IsInspecting && Timeline.Position == Timeline.Head;

    public IReadOnlyList<ReplayBranchInfo> Branches => _branches
        .Select(branch => new ReplayBranchInfo(
            branch.Id,
            branch.Name,
            branch.ParentId,
            branch.Fork,
            GetHead(branch),
            branch == _selectedBranch))
        .ToArray();

    public ReplayBranchInfo SelectedBranch => new(
        _selectedBranch.Id,
        _selectedBranch.Name,
        _selectedBranch.ParentId,
        _selectedBranch.Fork,
        Timeline.Head,
        true);

    /// <summary>
    ///     Preserves the selected branch and starts a child before operations stamped at the current tick.
    /// </summary>
    public ReplayBranchInfo CreateBranchFromCurrentTick()
    {
        ulong forkTick = Timeline.Position.Tick;
        Timeline.SeekTick(forkTick);
        _selectedBranch.Archive = Timeline.CaptureReplay();

        var fork = Timeline.Position;
        Timeline.SimulateFromHere();
        Timeline = new AtmosWorldReplayTimeline(_world, _checkpointInterval);

        var child = new ReplayBranch(
            _branches.Count,
            $"branch-{_nextBranchNumber++}",
            _selectedBranch.Id,
            fork,
            null);

        _branches.Add(child);
        _selectedBranch = child;
        return SelectedBranch;
    }

    /// <summary>
    ///     Restores another branch and positions it at the requested tick for inspection.
    /// </summary>
    public void SelectBranch(int branchId, ulong? tick = null)
    {
        var target = _branches.SingleOrDefault(branch => branch.Id == branchId) ??
                     throw new ArgumentOutOfRangeException(nameof(branchId));

        if (target == _selectedBranch && !tick.HasValue)
            return;

        ulong targetTick = tick ?? target.Fork.Tick;
        var targetHead = GetHead(target);
        if (targetTick < target.Fork.Tick || targetTick > targetHead.Tick)
            throw new ArgumentOutOfRangeException(nameof(tick));

        if (target == _selectedBranch)
        {
            Timeline.SeekTick(targetTick);
            return;
        }

        var source = _selectedBranch;
        var sourceArchive = Timeline.CaptureReplay();
        var sourcePosition = Timeline.Position;
        bool sourceWasLive = !Timeline.IsInspecting;
        source.Archive = sourceArchive;
        StopRecording();

        try
        {
            var restored = _restoreTimeline(_world, target.Archive!, _checkpointInterval);

            restored.SeekTick(targetTick);
            Timeline = restored;
            _selectedBranch = target;
        }
        catch (Exception selectionException)
        {
            try
            {
                StopRecording();
                var restored = _restoreTimeline(_world, sourceArchive, _checkpointInterval);

                restored.SeekPosition(sourcePosition);
                if (sourceWasLive)
                    restored.SimulateFromHere();

                Timeline = restored;
                _selectedBranch = source;
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "Could not select the replay branch or restore the previously selected branch.",
                    selectionException,
                    rollbackException);
            }

            throw;
        }
    }

    /// <summary>
    ///     Makes the selected branch writable at its preserved head.
    /// </summary>
    public void ContinueSelectedBranch()
    {
        if (!CanContinueSelectedBranch)
            throw new InvalidOperationException("Return to the selected branch head before continuing it.");

        Timeline.SimulateFromHere();
    }

    public AtmosWorldReplayArchive CaptureSelectedBranch(bool throughCurrentPosition)
    {
        return throughCurrentPosition
            ? Timeline.CaptureReplayThroughCurrentPosition()
            : Timeline.CaptureReplay();
    }

    private AtmosTimelinePosition GetHead(ReplayBranch branch)
    {
        return branch == _selectedBranch
            ? Timeline.Head
            : branch.Archive!.Recording.Head;
    }

    private void StopRecording()
    {
        if (_world.IsRecording)
            _world.StopRecording();
    }

    private static AtmosWorldReplayTimeline RestoreTimeline(
        AtmosWorld world,
        AtmosWorldReplayArchive archive,
        ulong checkpointInterval)
    {
        return AtmosWorldReplayTimeline.Restore(world, archive, checkpointInterval);
    }

    private sealed class ReplayBranch(
        int id,
        string name,
        int? parentId,
        AtmosTimelinePosition fork,
        AtmosWorldReplayArchive? archive)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public int? ParentId { get; } = parentId;
        public AtmosTimelinePosition Fork { get; } = fork;
        public AtmosWorldReplayArchive? Archive { get; set; } = archive;
    }
}

internal readonly record struct ReplayBranchInfo(
    int Id,
    string Name,
    int? ParentId,
    AtmosTimelinePosition Fork,
    AtmosTimelinePosition Head,
    bool IsSelected);