namespace Numos.API;

/// <summary>
///     Transient scratch for one mixture transaction.
/// </summary>
/// <remarks>
///     A transaction captures the mixtures it touches into instances, computes the result, validates it, and writes it
///     back as an atomic replacement. Neither the pool nor the kernel stores this type.
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
    ///     Gets the total that the captured mixture's own <see cref="IGasMixture.TotalMoles" /> reported.
    /// </summary>
    /// <remarks>
    ///     The storage sums its channels in page order, and <see cref="TotalMoles" /> sums in gas-ID order. The two can
    ///     differ in the last bit. A ratio taken from a caller's amount must use this value, so that an amount equal to
    ///     the mixture's <see cref="IGasMixture.TotalMoles" /> removes everything. Zero for a state that was not
    ///     captured.
    /// </remarks>
    internal float CapturedTotalMoles { get; init; }

    /// <summary>
    ///     Gets the pressure that the captured mixture's own <see cref="IGasMixture.Pressure" /> reported.
    /// </summary>
    internal float CapturedPressure { get; init; }

    /// <summary>
    ///     Gets the sum of the amounts of all gas species, in gas-ID order.
    /// </summary>
    internal float TotalMoles
    {
        get
        {
            float total = 0f;
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
        foreach ((int gasId, float moles) in Moles)
            clone.Moles.Add(gasId, moles);

        return clone;
    }

    /// <summary>
    ///     Materializes the ordered gas map for transfer to the kernel's voxel representation.
    /// </summary>
    internal KeyValuePair<int, float>[] ToGasArray()
    {
        var gases = new KeyValuePair<int, float>[Moles.Count];
        int index = 0;
        foreach (KeyValuePair<int, float> gas in Moles)
            gases[index++] = gas;

        return gases;
    }
}