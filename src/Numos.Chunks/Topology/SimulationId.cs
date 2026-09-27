namespace Numos.Chunks.Topology;

/// <summary>
///     Identifies one simulation registration that owns a <see cref="ChunkMap{T}"/>.
/// </summary>
/// <param name="Index">The stable registry slot.</param>
/// <param name="Generation">The slot generation that prevents a stale identifier from naming a replacement simulation.</param>
/// <remarks>
///     The default value is invalid. Identifiers are meaningful only within the world that issued them.
/// </remarks>
public readonly record struct SimulationId(int Index, uint Generation) : IComparable<SimulationId>
{
    /// <summary>
    ///     Gets whether this value could have been issued by a world registry.
    /// </summary>
    public bool IsValid => Index >= 0 && Generation != 0;

    /// <summary>
    ///     Compares stable slot and generation values without depending on object identity or hash iteration.
    /// </summary>
    /// <param name="other">The identifier to compare.</param>
    /// <returns>
    ///     A negative value when this identifier sorts first, zero when the identifiers match, or a positive value when
    ///     <paramref name="other" /> sorts first.
    /// </returns>
    public int CompareTo(SimulationId other)
    {
        int index = Index.CompareTo(other.Index);
        return index != 0 ? index : Generation.CompareTo(other.Generation);
    }
}
