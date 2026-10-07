using Numos.Maths;

namespace Numos.CoreSim.Datatypes.Primitives;

/// <summary>
///     Fixed boundary-condition mixture presented by a <see cref="VoxelClassification.RoomEnvironment" /> voxel.
/// </summary>
/// <remarks>
///     Unlike a simulated voxel's gas storage, an environmental mixture is not a mole-based mixture: it has no
///     volume and does not accumulate or deplete. It only describes the composition (as a mole fraction of each
///     gas), pressure, and temperature that the environment always presents at its face. Solvers read this
///     record to decide how much of each gas crosses the boundary; they never write moles into it.
/// </remarks>
/// <param name="Pressure">The fixed pressure the environment presents, in pascals (Pa).</param>
/// <param name="Temperature">The fixed temperature the environment presents, in kelvins (K).</param>
/// <param name="GasFractions">
///     The mole fraction of each gas in the mixture, keyed by gas ID, sorted by gas ID. Values should sum to at
///     most 1; any remainder is treated as an inert, untracked filler.
/// </param>
public readonly record struct EnvironmentalMixture(
    Pascal Pressure,
    Kelvin Temperature,
    KeyValuePair<int, Scalar>[] GasFractions)
{
    /// <summary>
    ///     An inert environmental mixture: zero pressure and no gases, at
    ///     <see cref="AtmosConfigDefaults.SpaceTemperature" />. Used as the library default so environmental
    ///     voxels are a harmless no-op until a caller opts in with real values.
    /// </summary>
    public static EnvironmentalMixture Vacuum { get; } = new(0f, AtmosConfigDefaults.SpaceTemperature, []);

    /// <summary>Creates an inert, empty environmental mixture. Equivalent to <see cref="Vacuum" />.</summary>
    public EnvironmentalMixture() : this(0f, AtmosConfigDefaults.SpaceTemperature, [])
    {
    }

    /// <summary>
    ///     Gets the mole fraction of a gas in this mixture, or zero if it is not present.
    /// </summary>
    public Scalar GetFraction(int gasId)
    {
        foreach (var (id, fraction) in GasFractions)
        {
            if (id == gasId)
                return fraction;
        }

        return 0f;
    }

    /// <summary>
    ///     Returns a copy with pressure and temperature normalized to finite, non-negative values and gas
    ///     fractions clamped to [0, 1] and sorted by gas ID.
    /// </summary>
    /// <remarks>Mirrors how other config values are validated when captured into an <see cref="AtmosConfigSnapshot" />.</remarks>
    internal static EnvironmentalMixture Validate(EnvironmentalMixture source)
    {
        var fractions = source.GasFractions ?? [];
        var validated = new KeyValuePair<int, Scalar>[fractions.Length];
        for (int i = 0; i < fractions.Length; i++)
        {
            validated[i] = new KeyValuePair<int, Scalar>(
                fractions[i].Key,
                FloatMath.ClampUnitInterval(fractions[i].Value));
        }

        Array.Sort(validated, static (left, right) => left.Key.CompareTo(right.Key));

        Pascal pressure = FloatMath.GetNonnegativeFinite(source.Pressure);
        return new EnvironmentalMixture(
            pressure,
            FloatMath.IsFinitePositive(source.Temperature) ? source.Temperature : AtmosConfigDefaults.SpaceTemperature,
            validated);
    }
}
