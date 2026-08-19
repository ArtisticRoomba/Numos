using Numos.Headless.Diagnostics;

namespace Numos.Headless.Protocol;

/// <summary>One compact JSON object emitted for an input request.</summary>
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

internal sealed class SessionState
{
    public required string Name { get; init; }
    public required Coordinate Dimensions { get; init; }
    public required int Tick { get; init; }
    public required int ChunkCount { get; init; }
    public required int GasCount { get; init; }
}

internal sealed class CommandResult
{
    public int? TicksExecuted { get; init; }
    public int? GasId { get; init; }
    public Coordinate? Position { get; init; }
    public bool? Changed { get; init; }
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
