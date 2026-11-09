using System.Collections.Immutable;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.EulerFlow;
using Numos.CoreSim.Replay;
using Numos.Maths;

namespace Numos.Workbench;

/// <summary>
///     What a <see cref="WorkbenchHost" /> is doing.
/// </summary>
public enum WorkbenchRunState
{
    /// <summary>
    ///     The world does not advance until a command asks it to.
    /// </summary>
    Paused,

    /// <summary>
    ///     The world advances with wall-clock time. While the timeline inspects history, the host plays the recording
    ///     back instead.
    /// </summary>
    Running,

    /// <summary>
    ///     The host runs a long job, such as a step of many ticks, a seek, or a replay load.
    ///     <see cref="WorkbenchFrame.BusyLabel" /> names it.
    /// </summary>
    Busy
}

/// <summary>
///     An immutable view of a <see cref="WorkbenchSession" />, published by <see cref="WorkbenchHost" />.
/// </summary>
/// <remarks>
///     <para>
///         A front end reads <see cref="WorkbenchHost.Frame" /> once per UI frame and draws from it. Nothing in a
///         frame changes after the host publishes it. Do not write to the arrays inside it, such as the chunk
///         snapshots, because later frames share them.
///     </para>
///     <para>
///         The host builds each section only when its source changed and otherwise reuses the previous frame's object.
///         A front end can compare sections by reference to skip work. Each section names the revision it follows.
///     </para>
/// </remarks>
public sealed record WorkbenchFrame
{
    /// <summary>
    ///     Counts published frames, starting at 1 for the frame the host publishes on construction.
    /// </summary>
    public required long Sequence { get; init; }

    /// <summary>
    ///     Counts project replacements. It increases when a project is created, opened, or closed.
    /// </summary>
    /// <remarks>
    ///     A front end resets its per-project state when this changes. Comparing world references is not enough,
    ///     because a frame holds no world.
    /// </remarks>
    public required long ProjectEpoch { get; init; }

    /// <summary>
    ///     Whether a project is open. Every project section is empty or <see langword="null" /> when none is.
    /// </summary>
    public required bool HasProject { get; init; }

    /// <summary>
    ///     The project name, or <see langword="null" /> when no project is open.
    /// </summary>
    public required string? ProjectName { get; init; }

    /// <summary>
    ///     What the host is doing.
    /// </summary>
    public required WorkbenchRunState RunState { get; init; }

    /// <summary>
    ///     The identifier of the job while <see cref="RunState" /> is <see cref="WorkbenchRunState.Busy" />, or
    ///     <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     The host uses <see cref="WorkbenchHost.AdvanceJobLabel" /> and <see cref="WorkbenchHost.SeekJobLabel" />.
    ///     A job from <see cref="WorkbenchHost.SubmitJob{T}" /> uses the label its caller passed. A label is an
    ///     identifier for a front end to translate, not display text.
    /// </remarks>
    public required string? BusyLabel { get; init; }

    /// <summary>
    ///     The finished fraction of the busy job, from 0 to 1, or <see langword="null" /> when the job reports none.
    /// </summary>
    public required double? BusyProgress { get; init; }

    /// <summary>
    ///     The world tick count, or 0 when no project is open.
    /// </summary>
    public required long TickCount { get; init; }

    /// <summary>
    ///     The atmospheric time of <see cref="TickCount" />, in seconds.
    /// </summary>
    public required double SimulatedTime { get; init; }

    /// <summary>
    ///     The world tick rate, in ticks per second, or 0 when no project is open.
    /// </summary>
    public required int TickRate { get; init; }

    /// <summary>
    ///     Simulated seconds per wall-clock second while the host runs, or 0 while it does not.
    /// </summary>
    /// <remarks>
    ///     The host measures it over about half a second, so it stays 0 for that long after
    ///     <see cref="WorkbenchHost.Run" />. A value below 1 means ticks take longer than real time and the simulation
    ///     falls behind.
    /// </remarks>
    public required double RealtimeFactor { get; init; }

    /// <summary>
    ///     The session's active simulation, or <see langword="null" />.
    /// </summary>
    public required AtmosSimulationId? ActiveSimulationId { get; init; }

    /// <summary>
    ///     The world's <see cref="AtmosWorld.SimulationCollectionRevision" />, or -1 when no project is open.
    /// </summary>
    public required long SimulationCollectionRevision { get; init; }

    /// <summary>
    ///     The world's <see cref="AtmosWorld.LinkSetCollectionRevision" />, or -1 when no project is open.
    /// </summary>
    public required long LinkSetRevision { get; init; }

    /// <summary>
    ///     The world's <see cref="AtmosWorld.TopologyVersion" />, or 0 when no project is open.
    /// </summary>
    public required ulong TopologyVersion { get; init; }

    /// <summary>
    ///     The world's <see cref="AtmosWorld.ActiveLinkCount" />, or 0 when no project is open.
    /// </summary>
    public required int ActiveLinkCount { get; init; }

    /// <summary>
    ///     The session's <see cref="WorkbenchSession.TimelineRevision" />.
    /// </summary>
    public required long TimelineRevision { get; init; }

    /// <summary>
    ///     The session's <see cref="WorkbenchSession.DefinitionRevision" />.
    /// </summary>
    public required long DefinitionRevision { get; init; }

    /// <summary>
    ///     The snapshot fields the chunk snapshots hold at least. It is the value of the latest
    ///     <see cref="WorkbenchHost.RequestFrame" /> call.
    /// </summary>
    public required AtmosChunkSnapshotFields SnapshotFields { get; init; }

    /// <summary>
    ///     One entry per simulation, in id order.
    /// </summary>
    public required IReadOnlyList<WorkbenchSimulationFrame> Simulations { get; init; }

    /// <summary>
    ///     Every current and pending link set. The host rereads them when <see cref="LinkSetRevision" /> changes.
    /// </summary>
    public required IReadOnlyList<AtmosWorldLinkSetSnapshot> LinkSets { get; init; }

    /// <summary>
    ///     The solver stages in execution order. The host rereads them when <see cref="DefinitionRevision" /> changes.
    /// </summary>
    public required IReadOnlyList<AtmosWorldSolverStep> SolverSteps { get; init; }

    /// <summary>
    ///     The registered gases, indexed by gas id. The host rebuilds the list when the world's configuration changes.
    /// </summary>
    public required IReadOnlyList<GasProperties> Gases { get; init; }

    /// <summary>
    ///     The configuration the world runs with, or <see langword="null" /> when no project is open.
    /// </summary>
    /// <remarks>
    ///     Create a draft for editing with <see cref="AtmosConfig(AtmosConfigSnapshot)" /> and pass it to
    ///     <see cref="WorkbenchSession.TryApplyConfiguration(AtmosConfig, out string)" />.
    /// </remarks>
    public required AtmosConfigSnapshot? ConfigSnapshot { get; init; }

    /// <summary>
    ///     An editable copy of <see cref="ConfigSnapshot" /> for code that takes an <see cref="AtmosConfig" />, such as
    ///     a frame builder. Do not edit it, because later frames share it. It is <see langword="null" /> when no
    ///     project is open.
    /// </summary>
    public required AtmosConfig? Config { get; init; }

    /// <summary>
    ///     The replay timeline, or <see langword="null" /> when no project is open.
    /// </summary>
    public required WorkbenchTimelineFrame? Timeline { get; init; }
}

/// <summary>
///     One simulation in a <see cref="WorkbenchFrame" />.
/// </summary>
/// <remarks>
///     The host reuses this object when nothing in it changed since the previous frame.
/// </remarks>
public sealed record WorkbenchSimulationFrame
{
    /// <summary>
    ///     The simulation id.
    /// </summary>
    public required AtmosSimulationId Id { get; init; }

    /// <summary>
    ///     The display name from <see cref="WorkbenchSession.GetSimulationName" />.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Voxels per chunk along each axis.
    /// </summary>
    public required Int3 ChunkDimensions { get; init; }

    /// <summary>
    ///     The chunk collection revision that <see cref="ChunkHandles" /> belongs to.
    /// </summary>
    public required long ChunkRevision { get; init; }

    /// <summary>
    ///     The chunk handles in position order. The array is reused by reference until a chunk is added or removed.
    /// </summary>
    public required IReadOnlyList<AtmosChunkHandle> ChunkHandles { get; init; }

    /// <summary>
    ///     The latest snapshot of every chunk, keyed by chunk position.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Each frame holds the full set, built as the previous frame's map plus the chunks that changed since.
    ///         A front end that skips frames therefore misses nothing. An unchanged chunk keeps the same snapshot and
    ///         arrays as in the previous frame.
    ///     </para>
    ///     <para>
    ///         A snapshot holds at least <see cref="WorkbenchFrame.SnapshotFields" />. It can hold more when an earlier
    ///         request asked for more and the chunk has not changed since.
    ///     </para>
    /// </remarks>
    public required ImmutableDictionary<Int3, AtmosChunkSnapshot> Snapshots { get; init; }

    /// <summary>
    ///     The tick count the snapshots were captured at.
    /// </summary>
    public required long SnapshotTickCount { get; init; }

    /// <summary>
    ///     A copy of <see cref="AtmosSimulation.LastFlowDiagnostics" />.
    /// </summary>
    public required AtmosFlowDiagnostics FlowDiagnostics { get; init; }
}

/// <summary>
///     The replay timeline in a <see cref="WorkbenchFrame" />.
/// </summary>
/// <remarks>
///     The lists are detached. Each one is reused by reference until its source changes.
/// </remarks>
public sealed record WorkbenchTimelineFrame
{
    /// <summary>
    ///     Whether the timeline shows history instead of the live head.
    /// </summary>
    public required bool IsInspecting { get; init; }

    /// <summary>
    ///     Whether the timeline came from a loaded replay file.
    /// </summary>
    public required bool IsImported { get; init; }

    /// <summary>
    ///     The position the world shows.
    /// </summary>
    public required AtmosTimelinePosition Position { get; init; }

    /// <summary>
    ///     The first position the timeline can show.
    /// </summary>
    public required AtmosTimelinePosition Start { get; init; }

    /// <summary>
    ///     The newest position of the selected branch.
    /// </summary>
    public required AtmosTimelinePosition Head { get; init; }

    /// <summary>
    ///     The newest head tick across every branch.
    /// </summary>
    public required ulong MaximumHeadTick { get; init; }

    /// <summary>
    ///     Whether the selected branch can continue, which needs the timeline at the branch head.
    /// </summary>
    public required bool CanContinueSelectedBranch { get; init; }

    /// <summary>
    ///     What the last successful seek reconstructed, or <see langword="null" /> before the first seek.
    /// </summary>
    public required AtmosWorldReplayResult? LastReplay { get; init; }

    /// <summary>
    ///     Whether the last seek matched its reference hash, or <see langword="null" /> when it had no reference.
    /// </summary>
    public required bool? IsVerified { get; init; }

    /// <summary>
    ///     The retained checkpoints, oldest first. The host copies them when one is added or the timeline moves.
    /// </summary>
    public required IReadOnlyList<AtmosWorldReplayVerificationPoint> Checkpoints { get; init; }

    /// <summary>
    ///     Every recorded operation of the selected branch, in order.
    /// </summary>
    public required IReadOnlyList<AtmosWorldRecordedOperation> Operations { get; init; }

    /// <summary>
    ///     Every branch, in creation order.
    /// </summary>
    public required IReadOnlyList<ReplayBranchInfo> Branches { get; init; }

    /// <summary>
    ///     The id of the selected branch.
    /// </summary>
    public required int SelectedBranchId { get; init; }
}
