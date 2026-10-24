namespace Numos.Workbench;

/// <summary>
///     Display wording for one <see cref="Numos.CoreSim.AtmosConfig" /> field.
/// </summary>
/// <param name="Label">Sentence-case label without the unit.</param>
/// <param name="Unit">Unit symbol, or an empty string when the field is dimensionless or a count.</param>
/// <param name="Tooltip">One-sentence explanation of what the field controls.</param>
public readonly record struct AtmosConfigFieldInfo(string Label, string Unit, string Tooltip)
{
    public string LabelWithUnit => Unit.Length == 0 ? Label : $"{Label} ({Unit})";
}

/// <summary>
///     Shared labels, units, and tooltips for <see cref="Numos.CoreSim.AtmosConfig" /> fields, so the Configuration
///     panel and replay inspection describe a setting the same way.
/// </summary>
/// <remarks>
///     Units follow the quantity types on <see cref="Numos.CoreSim.AtmosConfig" />. Keep them in step if a field's
///     type changes.
/// </remarks>
public static class AtmosConfigFields
{
    public readonly static AtmosConfigFieldInfo GlobalTemperature = new(
        "Global temperature",
        "K",
        "Reference ambient temperature.");

    public readonly static AtmosConfigFieldInfo DefaultTemperatureFallback = new(
        "Default temperature fallback",
        "K",
        "Temperature used for a gas-bearing voxel whose stored temperature is zero, negative, or not a number.");

    public readonly static AtmosConfigFieldInfo DefaultMolarHeatCapacityAtConstantVolume = new(
        "Default molar Cv",
        "J/(mol·K)",
        "Molar heat capacity at constant volume used for unregistered gases or gases without a valid value.");

    public readonly static AtmosConfigFieldInfo VoxelVolume = new(
        "Voxel volume",
        "m³",
        "Physical volume represented by each voxel. Pressure uses P = nRT/V.");

    public readonly static AtmosConfigFieldInfo SaturationReferencePressure = new(
        "Saturation reference pressure",
        "kPa",
        "Pressure at which each gas's configured boiling point applies.");

    public readonly static AtmosConfigFieldInfo DefaultDiffusionCoefficient = new(
        "Default diffusion coefficient",
        "m²/s",
        "Effective diffusivity for gases missing from the registry. Each tick a voxel sends D·n·dt/dx² moles to " +
        "each neighbor.");

    public readonly static AtmosConfigFieldInfo SpaceTemperature = new(
        "Space temperature",
        "K",
        "Default temperature of space.");

    public readonly static AtmosConfigFieldInfo BulkFlowCoefficient = new(
        "Bulk flow coefficient",
        "",
        "Fraction of a pressure difference requested as bulk flow per tick.");

    public readonly static AtmosConfigFieldInfo VacuumThreshold = new(
        "Vacuum threshold",
        "kPa",
        "Below this pressure a voxel's gas is removed, provided every adjacent air voxel is also below it.");

    public readonly static AtmosConfigFieldInfo SleepThreshold = new(
        "Sleep threshold",
        "ticks",
        "Consecutive ticks below the sleep epsilon before a chunk goes to sleep.");

    public readonly static AtmosConfigFieldInfo SleepEpsilon = new(
        "Sleep epsilon",
        "%",
        "Largest neighboring pressure difference, as a percentage of the higher pressure, still treated as at rest.");

    public readonly static AtmosConfigFieldInfo ThermalConductance = new(
        "Thermal conductance",
        "J/K",
        "Per-face energy conductance between adjacent voxels, applied once per thermodynamics tick.");

    public readonly static AtmosConfigFieldInfo CondensationRateFactor = new(
        "Condensation rate factor",
        "",
        "Fraction of the equilibrium condensation amount applied per thermodynamics tick.");

    public readonly static AtmosConfigFieldInfo MaxPressureTransferFractionPerNeighbor = new(
        "Max pressure transfer per neighbor",
        "",
        "Largest fraction of a voxel's pressure requested as bulk flow to one neighbor per tick.");

    public readonly static AtmosConfigFieldInfo AccumulatorWakeThreshold = new(
        "Accumulator wake threshold",
        "kPa",
        "Accumulated pressure activity needed to wake a sleeping chunk.");

    public readonly static AtmosConfigFieldInfo AccumulatorMaxAliveTicks = new(
        "Accumulator max alive ticks",
        "ticks",
        "Number of ticks an accumulated activity value stays alive before it expires.");

    public readonly static AtmosConfigFieldInfo ReferenceMolarMass = new(
        "Reference molar mass",
        "kg/mol",
        "Molar mass assumed for gases that don't declare one.");

    public readonly static AtmosConfigFieldInfo EulerCourantNumber = new(
        "Courant number",
        "",
        "Largest fraction of a voxel a signal may cross per substep. 0.5 keeps the first-order scheme positive.");

    public readonly static AtmosConfigFieldInfo EulerMaxSubstepsPerTick = new(
        "Max substeps per tick",
        "",
        "Cap on CFL substeps per tick; 0 means no cap. When it engages the tick advances less gas time.");

    public readonly static AtmosConfigFieldInfo EulerVelocityFloor = new(
        "Velocity floor",
        "m/s",
        "Speeds below this are zeroed at the end of the tick so settled rooms stop sloshing.");

    public readonly static AtmosConfigFieldInfo EulerMaxVelocity = new(
        "Max velocity",
        "m/s",
        "Speeds are clamped to this; a safety net that only engages when a state has gone wrong.");

    public readonly static AtmosConfigFieldInfo EulerWakePressureDelta = new(
        "Wake pressure delta",
        "Pa",
        "Pressure difference across a chunk boundary face needed to wake the sleeping side.");

    public readonly static AtmosConfigFieldInfo EulerAllMachCorrection = new(
        "All-Mach correction",
        "",
        "Scales the flux's pressure dissipation by the local Mach number so slow flows aren't smeared.");

    public readonly static AtmosConfigFieldInfo EulerReconstruction = new(
        "Reconstruction",
        "",
        "How face states are built. Only first order exists today.");

    public readonly static AtmosConfigFieldInfo EulerMomentumDamping = new(
        "Momentum damping",
        "1/s",
        "Exponential decay rate applied to momentum each tick; a stand-in for wall friction. 0 disables it.");

    public readonly static AtmosConfigFieldInfo EulerDefaultLinkAreaFraction = new(
        "Default link area fraction",
        "",
        "Open fraction of a voxel face assumed for portals and docks that declare no area.");
}