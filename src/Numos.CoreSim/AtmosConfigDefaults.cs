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
    public const float GlobalTemperature = AtmosPhysicalConstants.RoomTemperature;

    /// <summary>Default fallback temperature, in kelvins (K).</summary>
    public const float DefaultTemperatureFallback = AtmosPhysicalConstants.RoomTemperature;

    /// <summary>Default molar heat capacity at constant volume, in joules per mole-kelvin (J/(mol·K)).</summary>
    public const float DefaultMolarHeatCapacityAtConstantVolume =
        AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume;

    /// <summary>Default physical volume represented by one voxel, in cubic metres (m³).</summary>
    public const float VoxelVolume = 1f;

    /// <summary>Default saturation-pressure reference, in pascals (Pa).</summary>
    public const float SaturationReferencePressure = AtmosPhysicalConstants.StandardAtmosphericPressure;

    /// <summary>Default per-tick diffusion fraction for unregistered gas IDs.</summary>
    public const float DefaultDiffusionCoefficient = 0.02f;

    /// <summary>Default modeled temperature of space, in kelvins (K).</summary>
    public const float SpaceTemperature = 2.7f;

    /// <summary>Default fraction of a pressure delta requested as bulk flow per tick.</summary>
    public const float BulkFlowCoefficient = 0.25f;

    /// <summary>Default large-delta bulk-flow damping multiplier.</summary>
    public const float BulkFlowDamping = 0.5f;

    /// <summary>Default pressure-delta boundary between low- and large-delta flow, in pascals (Pa).</summary>
    public const float LowPressureDeltaThreshold = 5f;

    /// <summary>Default minimum candidate pressure transfer, in pascals per tick (Pa/tick).</summary>
    public const float MinimumPressureTransfer = 0.1f;

    /// <summary>Default pressure below which a voxel is treated as vacuum, in pascals (Pa).</summary>
    public const float VacuumThreshold = 1f;

    /// <summary>Default consecutive quiet ticks required before a chunk sleeps.</summary>
    public const int SleepThreshold = 15;

    /// <summary>
    ///     Default absolute pressure-correction floor used by voxel snapping, and the legacy neighboring-pressure
    ///     tolerance when snapping is disabled, in pascals (Pa).
    /// </summary>
    public const float SleepEpsilon = 0.5f;

    /// <summary>Default enablement for conservative progressive voxel snapping.</summary>
    public const bool VoxelSnappingEnabled = true;

    /// <summary>Default maximum relative pressure correction made by one voxel snap.</summary>
    public const float VoxelSnapPressureRelativeEpsilon = 0.001f;

    /// <summary>Default maximum temperature correction made by one voxel snap, in kelvins (K).</summary>
    public const float VoxelSnapTemperatureEpsilon = 0.01f;

    /// <summary>Default maximum per-species mole-fraction correction made by one voxel snap.</summary>
    public const float VoxelSnapMoleFractionEpsilon = 0.005f;

    /// <summary>Default effective per-face thermal conductance, in joules per kelvin per thermodynamics tick.</summary>
    public const float ThermalConductance = 0.05f;

    /// <summary>Default fraction of the heat-coupled equilibrium condensation amount applied per tick.</summary>
    public const float CondensationRateFactor = 0.5f;

    /// <summary>Default maximum source-pressure fraction transferred to one neighbor per tick.</summary>
    public const float MaxPressureTransferFractionPerNeighbor = 0.16f;

    /// <summary>Default accumulated pressure activity required to wake a sleeping chunk, in pascals (Pa).</summary>
    public const float AccumulatorWakeThreshold = 15f;

    /// <summary>Default maximum lifetime of accumulated activity, in ticks.</summary>
    public const int AccumulatorMaxAliveTicks = 20;
}
