using Numos.CoreSim;

namespace Numos.Workbench;

/// <summary>
///     Validation domain for an <see cref="AtmosConfig" /> field, matching what <see cref="AtmosConfigSnapshot" />
///     keeps as-is.
///     The snapshot silently replaces or clamps values outside the domain. Front ends reject them instead, so a
///     recording never holds a value that means something else.
/// </summary>
public enum ConfigDomain
{
    Positive,
    NonNegative,
    UnitInterval
}

/// <summary>
///     Helpers for editing <see cref="AtmosConfig" /> scalar settings.
/// </summary>
public static class AtmosConfigSettings
{
    /// <summary>
    ///     Copies every scalar <see cref="AtmosConfig" /> setting, leaving the gas registry and solver configurations
    ///     alone.
    /// </summary>
    /// <param name="source">The configuration to read.</param>
    /// <param name="destination">The configuration to overwrite.</param>
    public static void CopyScalarSettings(AtmosConfig source, AtmosConfig destination)
    {
        destination.GlobalTemperature = source.GlobalTemperature;
        destination.DefaultTemperatureFallback = source.DefaultTemperatureFallback;
        destination.DefaultMolarHeatCapacityAtConstantVolume = source.DefaultMolarHeatCapacityAtConstantVolume;
        destination.VoxelVolume = source.VoxelVolume;
        destination.SaturationReferencePressure = source.SaturationReferencePressure;
        destination.DefaultDiffusionCoefficient = source.DefaultDiffusionCoefficient;
        destination.DefaultThermalConductivity = source.DefaultThermalConductivity;
        destination.SpaceTemperature = source.SpaceTemperature;
        destination.BulkFlowCoefficient = source.BulkFlowCoefficient;
        destination.VacuumThreshold = source.VacuumThreshold;
        destination.SleepThreshold = source.SleepThreshold;
        destination.SleepEpsilon = source.SleepEpsilon;
        destination.ThermalConductance = source.ThermalConductance;
        destination.CondensationRateFactor = source.CondensationRateFactor;
        destination.MaxPressureTransferFractionPerNeighbor = source.MaxPressureTransferFractionPerNeighbor;
        destination.AccumulatorWakeThreshold = source.AccumulatorWakeThreshold;
        destination.AccumulatorMaxAliveTicks = source.AccumulatorMaxAliveTicks;
        destination.FlameSpeedScale = source.FlameSpeedScale;
        destination.MinimumFlameSpeed = source.MinimumFlameSpeed;
        destination.IgnitionTransferEnabled = source.IgnitionTransferEnabled;
        destination.TransferSeedFraction = source.TransferSeedFraction;
    }

    /// <summary>
    ///     Checks a proposed value against the domain of its field.
    /// </summary>
    /// <param name="field">Display wording for the field, used in the message.</param>
    /// <param name="value">The proposed value.</param>
    /// <param name="domain">The values the field accepts.</param>
    /// <returns>A message that explains the rejection, or <see langword="null" /> when the value is valid.</returns>
    public static string? ValidateConfigValue(AtmosConfigFieldInfo field, float value, ConfigDomain domain)
    {
        string unit = field.Unit.Length == 0 ? string.Empty : " " + field.Unit;
        return domain switch
        {
            ConfigDomain.Positive when !float.IsFinite(value) || value <= 0f =>
                $"{field.Label} must be finite and above 0{unit}. The previous value is still applied.",
            ConfigDomain.NonNegative when !float.IsFinite(value) || value < 0f =>
                $"{field.Label} must be finite and at least 0{unit}. The previous value is still applied.",
            ConfigDomain.UnitInterval when !float.IsFinite(value) || value < 0f || value > 1f =>
                $"{field.Label} must be between 0 and 1. The previous value is still applied.",
            _ => null
        };
    }
}
