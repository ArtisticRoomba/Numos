using Numos.Headless.Diagnostics;

namespace Numos.Headless.Protocol;

/// <summary>
///     One compact JSON object emitted for an input request.
/// </summary>
internal sealed class HeadlessResponse
{
    public int ProtocolVersion { get; init; } = HeadlessCommandHost.ProtocolVersion;
    public string? Id { get; init; }
    public string? Op { get; init; }
    public bool Ok { get; init; }
    public SessionState? State { get; init; }
    public CommandResult? Result { get; init; }
    public SimulationStateReport? Observation { get; init; }
    public HeadlessError? Error { get; init; }
}

/// <summary>
///     The project and active simulation after a request.
/// </summary>
internal sealed class SessionState
{
    public required string Project { get; init; }
    public required string? Simulation { get; init; }
    public required int? SimulationId { get; init; }
    public required int SimulationCount { get; init; }
    public required int Tick { get; init; }
    public required int ChunkCount { get; init; }
    public required int GasCount { get; init; }
    public required bool Inspecting { get; init; }
}

/// <summary>
///     The union of operation results. Each operation sets only the members it documents.
/// </summary>
internal sealed class CommandResult
{
    public HelloInfo? Hello { get; init; }
    public int? TicksExecuted { get; init; }
    public int? GasId { get; init; }
    public Coordinate? Position { get; init; }
    public bool? Changed { get; init; }
    public int? SimulationId { get; init; }
    public string? SimulationName { get; init; }
    public SimulationInfo[]? Simulations { get; init; }
    public GasConfigurationReport[]? Gases { get; init; }
    public SimulationConfigurationReport? Config { get; init; }
    public SolverStepReport[]? Solvers { get; init; }
    public LinkSetInfo[]? LinkSets { get; init; }
    public LinkSetHandleInfo? LinkSet { get; init; }
    public string? Moved { get; init; }
    public TimelineInfo? Timeline { get; init; }
    public BranchInfo[]? Branches { get; init; }
    public BranchInfo? Branch { get; init; }
    public string? Path { get; init; }
    public string? StateHash { get; init; }
    public RegionEditInfo? Region { get; init; }
    public SampleInfo? Samples { get; init; }
    public ExportInfo? Export { get; init; }
    public RenderInfo? Render { get; init; }
    public ProbeInfo? Probes { get; init; }
    public LogInfo? Log { get; init; }
}

internal sealed class LogInfo
{
    public required LogEntryInfo[] Entries { get; init; }
    public required long LatestSequence { get; init; }
    public required bool More { get; init; }
}

internal sealed class LogEntryInfo
{
    public required long Sequence { get; init; }
    public required string Timestamp { get; init; }
    public required string Level { get; init; }
    public long? Tick { get; init; }
    public required string Source { get; init; }
    public required string Message { get; init; }
}

internal sealed class HelloInfo
{
    public required int Protocol { get; init; }
    public required string Host { get; init; }
    public required string HostVersion { get; init; }
    public required string CoreSimVersion { get; init; }
}

internal sealed class SimulationInfo
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required bool Active { get; init; }
    public required Coordinate ChunkDimensions { get; init; }
    public required int ChunkCount { get; init; }
}

internal sealed class LinkSetHandleInfo
{
    public required int Index { get; init; }
    public required uint Generation { get; init; }
}

internal sealed class LinkSetInfo
{
    public required LinkSetHandleInfo Handle { get; init; }
    public required string Kind { get; init; }
    public required string State { get; init; }
    public required int LinkCount { get; init; }
    public required string Flags { get; init; }
}

internal sealed class BranchInfo
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public int? ParentId { get; init; }
    public required ulong ForkTick { get; init; }
    public required ulong HeadTick { get; init; }
    public required bool Selected { get; init; }
}

internal sealed class CheckpointInfo
{
    public required ulong Tick { get; init; }
    public required string Hash { get; init; }
}

internal sealed class TimelineInfo
{
    public required ulong StartTick { get; init; }
    public required ulong HeadTick { get; init; }
    public required ulong PositionTick { get; init; }
    public required bool Inspecting { get; init; }
    public bool? Verified { get; init; }
    public required int SelectedBranchId { get; init; }
    public required BranchInfo[] Branches { get; init; }
    public required CheckpointInfo[] Checkpoints { get; init; }
    public required string StateHash { get; init; }
}

internal sealed class RegionEditInfo
{
    public required long VoxelsEdited { get; init; }
    public required long VoxelsSkipped { get; init; }
    public required int ChunksTouched { get; init; }
    public required int ChunksCreated { get; init; }
    public required float[] MolesPerVoxel { get; init; }
    public required long VelocityVoxels { get; init; }
}

internal sealed class SampleInfo
{
    public required string[] Fields { get; init; }
    public required Coordinate[] Points { get; init; }
    public required float[][] Values { get; init; }
}

internal sealed class ExportInfo
{
    public required string Format { get; init; }
    public required ExportFileInfo[] Files { get; init; }
}

internal sealed class ExportFileInfo
{
    public required string Field { get; init; }
    public required string Unit { get; init; }
    public string? Path { get; init; }
    public required int[] Shape { get; init; }
    public float? Min { get; init; }
    public float? Max { get; init; }
    public float[]? Values { get; init; }
}

internal sealed class RenderInfo
{
    public required string Path { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required float RangeMin { get; init; }
    public required float RangeMax { get; init; }
}

internal sealed class ProbeInfo
{
    public required string Path { get; init; }
    public required int Rows { get; init; }
    public required int Columns { get; init; }
}

internal sealed class HeadlessError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? ExceptionType { get; init; }
    public int? Line { get; init; }
}

internal sealed class HeadlessRequestException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}
