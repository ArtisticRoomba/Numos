namespace Numos.API;

/// <summary>
///     Configures a world recording started with <see cref="AtmosWorld.StartRecording(AtmosRecordingOptions)" />.
/// </summary>
public sealed class AtmosRecordingOptions
{
    private readonly int _hashInterval;

    /// <summary>
    ///     Gets the number of ticks between recorded state hashes. Zero records none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         With a value of <c>n</c> above zero, the world takes <see cref="AtmosWorld.ComputeStateHash" /> at the end
    ///         of every tick whose distance from the recording start is a multiple of <c>n</c>. The hashes go into
    ///         <see cref="AtmosWorldRecording.Hashes" />.
    ///         <see cref="AtmosWorld.ReplayTo(AtmosWorldCheckpoint, AtmosWorldRecording)" /> compares them and stops at
    ///         the first position that differs.
    ///     </para>
    ///     <para>
    ///         Each recorded hash costs one full checkpoint capture at the end of its tick. That cost grows with the chunk
    ///         count, so a large world needs a longer interval. Taking the hash records no operation.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int HashInterval
    {
        get => _hashInterval;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _hashInterval = value;
        }
    }
}
