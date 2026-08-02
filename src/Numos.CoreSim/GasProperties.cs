namespace Numos;

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
    ///     Specific heat capacity of the gas. Used in latent heat calculations during condensation.
    /// </summary>
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
    ///     Energy released per mole during condensation.
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