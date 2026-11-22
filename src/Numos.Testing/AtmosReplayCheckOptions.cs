using Numos.API;

namespace Numos.Testing;

/// <summary>
///     Tunes the checks that <see cref="AtmosReplayCheck.Run" /> performs.
/// </summary>
public sealed class AtmosReplayCheckOptions
{
    private readonly int _hashInterval = 1;
    private readonly int _maxDifferences = 16;
    private readonly int _syncDistance = 10;
    private readonly int _syncInterval = 50;

    /// <summary>
    ///     Gets the number of ticks between the starts of two sync tests. The default is 50.
    /// </summary>
    /// <remarks>
    ///     The first sync test starts before the first tick. A sync test that is longer than this interval delays the
    ///     next one until it ends.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int SyncInterval
    {
        get => _syncInterval;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _syncInterval = value;
        }
    }

    /// <summary>
    ///     Gets the number of ticks that each sync test simulates twice. The default is 10.
    /// </summary>
    /// <remarks>
    ///     The sync test keeps one checkpoint per tick of its first pass, so it can name the first tick that differs.
    ///     The last sync test is shorter when fewer ticks remain.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int SyncDistance
    {
        get => _syncDistance;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _syncDistance = value;
        }
    }

    /// <summary>
    ///     Gets the ticks between recorded state hashes in the record and replay run. The default is 1. Zero records
    ///     none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The recorded run passes this value to <see cref="AtmosRecordingOptions.HashInterval" />. It also keeps a
    ///         full world checkpoint at every hashed tick, so a failed check can list the fields that differ there.
    ///     </para>
    ///     <para>
    ///         The kept checkpoints stay in memory until <see cref="AtmosReplayCheck.Run" /> returns. With the default
    ///         of 1, memory grows by one world checkpoint per tick. Raise the interval for a world with many chunks or a
    ///         long run. The checks then name the first hashed tick after the divergence instead of the exact tick.
    ///     </para>
    ///     <para>
    ///         With 0, the record and replay check, the save and load check, and the second world check compare only
    ///         the final state.
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

    /// <summary>
    ///     Gets whether <see cref="AtmosReplayCheck.Run" /> writes the recording to a replay file, reads it back, and
    ///     replays it in a fresh world. The default is <see langword="true" />.
    /// </summary>
    public bool CheckSaveLoad { get; init; } = true;

    /// <summary>
    ///     Gets whether <see cref="AtmosReplayCheck.Run" /> builds a second world and compares its run with the recorded
    ///     run. The default is <see langword="true" />.
    /// </summary>
    public bool CheckSecondWorld { get; init; } = true;

    /// <summary>
    ///     Gets the most differences that one failure lists. The default is 16.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int MaxDifferences
    {
        get => _maxDifferences;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxDifferences = value;
        }
    }
}
