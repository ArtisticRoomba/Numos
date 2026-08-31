using Numos.Units;

namespace Numos.CoreSim;

/// <summary>
///     Canonical default values used when constructing an <see cref="AtmosConfig" />.
/// </summary>
/// <remarks>
///     These are model defaults rather than universal physical constants. Consumers can use this class when
///     resetting individual settings or constructing configuration user interfaces without duplicating literals.
/// </remarks>
public static class AtmosConfigDefaults
{
    /// <summary>Default reference ambient temperature, in kelvins (K).</summary>
    [Quantity("temperature")]
    public const Kelvin GlobalTemperature = AtmosPhysicalConstants.RoomTemperature;

    /// <summary>Default fallback temperature, in kelvins (K).</summary>
    [Quantity("temperature")]
    public const Kelvin DefaultTemperatureFallback = AtmosPhysicalConstants.RoomTemperature;

    /// <summary>Default molar heat capacity at constant volume, in joules per mole-kelvin (J/(mol·K)).</summary>
    [Quantity("molarHeatCapacity")]
    public const JoulePerMoleKelvin DefaultMolarHeatCapacityAtConstantVolume =
        AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume;

    /// <summary>Default physical volume represented by one voxel, in cubic metres (m³).</summary>
    [Quantity("volume")]
    public const CubicMetre VoxelVolume = 1f;

    /// <summary>Default saturation-pressure reference, in pascals (Pa).</summary>
    [Quantity("pressure")]
    public const Pascal SaturationReferencePressure = AtmosPhysicalConstants.StandardAtmosphericPressure;

    /// <summary>Default per-tick diffusion fraction for unregistered gas IDs.</summary>
    public const Scalar DefaultDiffusionCoefficient = 0.02f;

    /// <summary>Default modeled temperature of space, in kelvins (K).</summary>
    [Quantity("temperature")]
    public const Kelvin SpaceTemperature = 2.7f;

    /// <summary>Default fraction of a pressure delta requested as bulk flow per tick.</summary>
    public const Scalar BulkFlowCoefficient = 0.125f;

    /// <summary>Default pressure below which a voxel is treated as vacuum, in pascals (Pa).</summary>
    [Quantity("pressure")]
    public const Pascal VacuumThreshold = 1f;

    /// <summary>Default consecutive quiet ticks required before a chunk sleeps.</summary>
    public const int SleepThreshold = 100;

    /// <summary>Default maximum pressure delta considered at rest, in pascals (Pa).</summary>
    [Quantity("pressure")]
    public const Pascal SleepEpsilon = 3.5f;

    /// <summary>Default effective per-face thermal conductance, in joules per kelvin per thermodynamics tick.</summary>
    [Quantity("heatCapacity")]
    public const JoulePerKelvin ThermalConductance = 0.05f;

    /// <summary>Default fraction of the heat-coupled equilibrium condensation amount applied per tick.</summary>
    public const Scalar CondensationRateFactor = 0.5f;

    /// <summary>Default maximum source-pressure fraction transferred to one neighbor per tick.</summary>
    public const Scalar MaxPressureTransferFractionPerNeighbor = 0.16f;

    /// <summary>Default accumulated pressure activity required to wake a sleeping chunk, in pascals (Pa).</summary>
    [Quantity("pressure")]
    public const Pascal AccumulatorWakeThreshold = 15f;

    /// <summary>Default maximum lifetime of accumulated activity, in ticks.</summary>
    public const int AccumulatorMaxAliveTicks = 20;
}