namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     The gases every active workspace of one tick carries a species row for: the union, in ascending gas id,
///     of the gases held by every chunk that is or becomes active, with the per-gas constants the mixture sums
///     need copied out of the configuration snapshot.
/// </summary>
/// <remarks>
///     <para>
///         One table for the whole active set, rather than one per chunk, is what lets a shared face move a gas
///         into a chunk that has never held it. The species flux through a face is the upwind cell's moles of
///         each gas times a common weight, so the receiving side needs a row for every gas the upwind side might
///         hold; and because gas can travel across several chunks within one tick, "might hold" is transitive
///         over the whole connected active set. The union over every active chunk is the simple superset of that,
///         and it costs only zero-filled rows, which contribute nothing to any sum: <c>x + 0·M</c> is <c>x</c>
///         bitwise, so a chunk's results do not depend on which absent gases are tabled alongside its own.
///     </para>
///     <para>
///         The table only grows during a tick, on the tick thread, and every growth is followed by a row clear in
///         each bound workspace before any worker runs again. Keeping it sorted is what makes the mixture sums
///         of two chunks with different channel orders agree bit for bit on a shared face.
///     </para>
/// </remarks>
internal sealed class EulerGasTable
{
    /// <summary>
    ///     Number of gases in the table.
    /// </summary>
    public int Count;

    /// <summary>
    ///     Gas ids, ascending.
    /// </summary>
    public int[] GasIds = [];

    /// <summary>
    ///     <c>Cv</c> of each gas, parallel to <see cref="GasIds" />.
    /// </summary>
    public JoulePerMoleKelvin[] HeatCapacity = [];

    /// <summary>
    ///     Molar mass of each gas, parallel to <see cref="GasIds" />.
    /// </summary>
    public KilogramPerMole[] MolarMass = [];

    /// <summary>
    ///     One more than the largest gas id a species row table has to index.
    /// </summary>
    public int SlotCount;

    /// <summary>
    ///     Empties the table at the start of a tick.
    /// </summary>
    internal void Clear(AtmosSolverConfigSnapshot config)
    {
        Count = 0;
        SlotCount = config.GasPropertyCount;
    }

    /// <summary>
    ///     Adds a gas, keeping the ids sorted.
    /// </summary>
    /// <returns><see langword="true" /> if the gas was not tabled yet, so workspaces need a row for it.</returns>
    internal bool Add(int gasId, AtmosSolverConfigSnapshot config)
    {
        int position = Array.BinarySearch(GasIds, 0, Count, gasId);
        if (position >= 0)
            return false;

        position = ~position;
        if (Count == GasIds.Length)
        {
            int capacity = Math.Max(8, Count * 2);
            Array.Resize(ref GasIds, capacity);
            Array.Resize(ref MolarMass, capacity);
            Array.Resize(ref HeatCapacity, capacity);
        }

        int tail = Count - position;
        Array.Copy(GasIds, position, GasIds, position + 1, tail);
        Array.Copy(MolarMass, position, MolarMass, position + 1, tail);
        Array.Copy(HeatCapacity, position, HeatCapacity, position + 1, tail);
        GasIds[position] = gasId;
        MolarMass[position] = config.GetMolarMass(gasId);
        HeatCapacity[position] = config.GetMolarHeatCapacityAtConstantVolume(gasId);
        Count++;
        SlotCount = Math.Max(SlotCount, gasId + 1);
        return true;
    }
}
