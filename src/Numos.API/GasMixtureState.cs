namespace Numos.API;

/// <summary>
///     Mutable, simulation-owned state for a standalone <see cref="GasMixture" />.
/// </summary>
/// <remarks>
///     Voxel mixtures do not use this type as backing storage. They are captured into an instance for a
///     transaction, then written back to the kernel as an atomic replacement.
/// </remarks>
internal sealed class GasMixtureState
{
    /// <summary>
    ///     Initializes an empty mixture with the supplied container volume and temperature.
    /// </summary>
    internal GasMixtureState(float volume, float temperature)
    {
        Volume = volume;
        Temperature = temperature;
    }

    /// <summary>
    ///     Gets or sets the container volume in cubic metres.
    /// </summary>
    internal float Volume { get; set; }

    /// <summary>
    ///     Gets or sets the bulk temperature in kelvins.
    /// </summary>
    internal float Temperature { get; set; }

    /// <summary>
    ///     Maps each gas identifier to its non-zero amount in moles, in deterministic identifier order.
    /// </summary>
    internal SortedDictionary<int, float> Moles { get; } = [];

    /// <summary>
    ///     Gets the number of gas species that currently have an entry in <see cref="Moles" />.
    /// </summary>
    internal int ActiveGasCount => Moles.Count;

    /// <summary>
    ///     Gets the sum of the amounts of all gas species.
    /// </summary>
    internal float TotalMoles
    {
        get
        {
            var total = 0f;
            foreach (float moles in Moles.Values)
                total += moles;
            return total;
        }
    }

    /// <summary>
    ///     Creates a deep copy suitable for transactional mutation without changing this instance.
    /// </summary>
    internal GasMixtureState Clone()
    {
        var clone = new GasMixtureState(Volume, Temperature);
        foreach (var (gasId, moles) in Moles)
            clone.Moles.Add(gasId, moles);
        return clone;
    }

    /// <summary>
    ///     Materializes the ordered gas map for transfer to the kernel's voxel representation.
    /// </summary>
    internal KeyValuePair<int, float>[] ToGasArray()
    {
        var gases = new KeyValuePair<int, float>[Moles.Count];
        var index = 0;
        foreach (var gas in Moles)
            gases[index++] = gas;
        return gases;
    }
}
