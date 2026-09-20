namespace Numos.CoreSim;

/// <summary>
///     Canonical default dimensions and implementation limits for atmospheric chunks.
/// </summary>
public static class AtmosChunkConstants
{
    /// <summary>Initial number of distinct gas-channel slots allocated by a chunk.</summary>
    /// <remarks>The channel table grows when a mixture introduces additional gas IDs.</remarks>
    public const int InitialGasChannelCapacity = 16;
}