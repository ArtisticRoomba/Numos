using System.Diagnostics.CodeAnalysis;
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
    UnitInterval,
    PositiveUnitInterval
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
        destination.ReferenceMolarMass = source.ReferenceMolarMass;
        destination.FlameSpeedScale = source.FlameSpeedScale;
        destination.MinimumFlameSpeed = source.MinimumFlameSpeed;
        destination.IgnitionTransferEnabled = source.IgnitionTransferEnabled;
        destination.TransferSeedFraction = source.TransferSeedFraction;
    }

    /// <summary>
    ///     Finds the first solver configuration of type <typeparamref name="T" />.
    /// </summary>
    /// <typeparam name="T">The solver configuration type. A derived type also matches.</typeparam>
    /// <param name="config">The configuration to search.</param>
    /// <param name="fallback">The value to return when <paramref name="config" /> holds no match.</param>
    /// <returns>
    ///     The first match, or <paramref name="fallback" /> when there is none. The result is never
    ///     <see langword="null" /> when <paramref name="fallback" /> is not.
    /// </returns>
    [return: NotNullIfNotNull(nameof(fallback))]
    public static T? GetSolverConfiguration<T>(AtmosConfig config, T? fallback = null)
        where T : class, IAtmosSolverConfiguration
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.SolverConfigurations.OfType<T>().FirstOrDefault() ?? fallback;
    }

    /// <summary>
    ///     Replaces every solver configuration of type <typeparamref name="T" /> with one value.
    /// </summary>
    /// <typeparam name="T">The solver configuration type. A derived type also matches.</typeparam>
    /// <param name="config">The configuration to edit.</param>
    /// <param name="value">The configuration to store. It goes last in the list.</param>
    public static void SetSolverConfiguration<T>(AtmosConfig config, T value)
        where T : class, IAtmosSolverConfiguration
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(value);
        config.SolverConfigurations.RemoveAll(static entry => entry is T);
        config.SolverConfigurations.Add(value);
   }

    /// <summary>
    ///     Checks a proposed value against the domain of its field.
    /// </summary>
    /// <param name="field">The field. Its label and unit appear in the message.</param>
    /// <param name="value">The proposed value.</param>
    /// <returns>
    ///     A message that explains the rejection, or <see langword="null" /> when the value is valid or the field has
    ///     no <see cref="AtmosConfigFieldInfo.Domain" />.
    /// </returns>
    public static string? ValidateConfigValue(AtmosConfigFieldInfo field, float value)
    {
        string unit = field.Unit.Length == 0 ? string.Empty : " " + field.Unit;
        return field.Domain switch
        {
            ConfigDomain.Positive when !float.IsFinite(value) || value <= 0f =>
                $"{field.Label} must be finite and above 0{unit}. The previous value is still applied.",
            ConfigDomain.NonNegative when !float.IsFinite(value) || value < 0f =>
                $"{field.Label} must be finite and at least 0{unit}. The previous value is still applied.",
            ConfigDomain.UnitInterval when !float.IsFinite(value) || value < 0f || value > 1f =>
                $"{field.Label} must be between 0 and 1. The previous value is still applied.",
            ConfigDomain.PositiveUnitInterval when !float.IsFinite(value) || value <= 0f || value > 1f =>
                $"{field.Label} must be above 0 and at most 1. The previous value is still applied.",
            _ => null
        };
    }
}
