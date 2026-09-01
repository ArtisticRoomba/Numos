using Numos.Units;

namespace Numos.CoreSim;

/// <summary>
///     Physical constants used by the Numos ideal-gas and phase-change models.
/// </summary>
public static class AtmosPhysicalConstants
{
    /// <summary>
    ///     Molar gas constant in joules per mole-kelvin (J/(mol·K)).
    /// </summary>
    [Quantity("molarHeatCapacity")]
    public const JoulePerMoleKelvin MolarGasConstant = 8.31446262f;

    /// <summary>
    ///     Ideal-diatomic molar heat capacity at constant volume, <c>5R/2</c>, in joules per
    ///     mole-kelvin (J/(mol·K)).
    /// </summary>
    [Quantity("molarHeatCapacity")]
    public const JoulePerMoleKelvin IdealDiatomicMolarHeatCapacityAtConstantVolume = 2.5f * MolarGasConstant;

    /// <summary>
    ///     Standard atmospheric pressure in pascals (Pa).
    /// </summary>
    [Quantity("pressure")]
    public const Pascal StandardAtmosphericPressure = 101_325f;

    /// <summary>
    ///     Conventional room temperature in kelvins (K).
    /// </summary>
    [Quantity("temperature")]
    public const Kelvin RoomTemperature = 293.15f;

    /// <summary>
    /// Boltzmann constant
    /// </summary>
    [Quantity("boltzmannConstant")]
    public const JoulePerKelvin BoltzmannConstant = 1.380649E-23f;
}