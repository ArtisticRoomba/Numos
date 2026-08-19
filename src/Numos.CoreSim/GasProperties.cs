namespace Numos.CoreSim;

/// <summary>
///     Defines the physical properties of a gas.
/// </summary>
public struct GasProperties
{
    /// <summary>
    ///     Display name of the gas.
    /// </summary>
    public string Name;

    /// <summary>
    ///     Effective molar heat capacity of the gas, in joules per mole-kelvin (J/(mol·K)).
    /// </summary>
    /// <remarks>
    ///     This value determines the sensible energy carried by gas during injection and flow, the voxel's
    ///     total heat capacity, and the energy removed during condensation. Non-finite values and values less
    ///     than or equal to zero use <see cref="AtmosConfig.DefaultSpecificHeatCapacity" />.
    /// </remarks>
    public float SpecificHeatCapacity;

    /// <summary>
    ///     Temperature in kelvin above which the gas remains gaseous.
    /// </summary>
    public float BoilingPoint;

    /// <summary>
    ///     Temperature in kelvin below which condensation can begin.
    /// </summary>
    /// <remarks>
    ///     In the sim, this is used as a hard gate for condensation,
    ///     it doesn't actually reflect the real life behavior.
    /// </remarks>
    public float CondensationPoint;

    /// <summary>
    ///     Energy released per mole during condensation, in joules per mole (J/mol).
    /// </summary>
    public float LatentHeatOfVaporization;

    /// <summary>
    ///     ID of the liquid this gas condenses to. Currently unused but can be passed to a separate fluid sim.
    /// </summary>
    /// TODO FAR FUTURE fluid sim :godo:
    public int LiquidId;

    /// <summary>
    ///     Fickian diffusion rate, used when calculating gas mixing via partial pressures.
    /// </summary>
    public float DiffusionCoefficient; // Passive Fickian diffusion rate
}