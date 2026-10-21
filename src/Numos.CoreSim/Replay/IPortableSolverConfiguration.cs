namespace Numos.CoreSim.Replay;

/// <summary>
///     A built-in solver configuration that a portable <c>.numos</c> replay file can carry.
/// </summary>
/// <remarks>
///     <see cref="AtmosReplayCodec" /> writes each portable entry as its <see cref="IAtmosSolverConfiguration.Key" />
///     followed by the payload <see cref="WriteWire" /> produces, and resolves the matching reader from a table
///     keyed by that string. The payload is the snapshot's normalized values, so reading it back and capturing it
///     again is a no-op. Only configurations Numos itself defines implement this: a standalone reader has no way to
///     construct a host type, which is why host configurations stay non-portable.
/// </remarks>
internal interface IPortableSolverConfiguration : IAtmosSolverConfiguration
{
    /// <summary>
    ///     Writes every authoritative setting in a fixed order, matching the type's static reader.
    /// </summary>
    void WriteWire(BinaryWriter writer);
}