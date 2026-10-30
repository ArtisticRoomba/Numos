using JetBrains.Annotations;

namespace Numos.API;

/// <summary>
///     Identifies one allocation of a slot in a world's mixture pool.
/// </summary>
/// <param name="Slot">The pool slot.</param>
/// <param name="Generation">The slot generation of this allocation. It is never zero for an allocation.</param>
/// <remarks>
///     <para>
///         The value holds no object reference, so a host can save it with its own entity data. A
///         <see cref="GasMixture" /> handle does not survive a restore into another world, a load, or a replay. The
///         identifier does, because the pool reproduces slots and generations exactly. Pass it to
///         <see cref="AtmosWorld.TryGetGasMixture" /> to get a handle back.
///     </para>
///     <para>
///         An identifier never names a later mixture in the same slot, because a release advances the generation.
///         The default value names nothing. Identifiers are meaningful only within the world, or the timeline of
///         worlds, that issued them.
///     </para>
/// </remarks>
[PublicAPI]
public readonly record struct GasMixtureId(int Slot, uint Generation) : IComparable<GasMixtureId>
{
    /// <summary>
    ///     Gets whether this value could have been issued by a mixture pool.
    /// </summary>
    public bool IsValid => Slot >= 0 && Generation != 0;

    /// <summary>
    ///     Compares slots first, then generations, so a sorted list follows the pool's slot order.
    /// </summary>
    /// <param name="other">The identifier to compare.</param>
    /// <returns>
    ///     A negative value when this identifier sorts first, zero when the identifiers match, or a positive value when
    ///     <paramref name="other" /> sorts first.
    /// </returns>
    public int CompareTo(GasMixtureId other)
    {
        int slot = Slot.CompareTo(other.Slot);
        return slot != 0 ? slot : Generation.CompareTo(other.Generation);
    }
}
