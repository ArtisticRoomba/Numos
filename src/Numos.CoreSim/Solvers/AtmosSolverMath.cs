using System.Diagnostics;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Shared atmospheric calculations used across solver stages and mixture operations.
/// </summary>
internal static class AtmosSolverMath
{
    /// <summary>
    ///     Ideal-gas pressure <c>P = nRT/V</c>, using the validated temperature.
    /// </summary>
    internal static Pascal CalculatePressure(IAtmosConfig config, Mole moles, Kelvin temperature)
    {
        Debug.Assert(float.IsFinite(moles) && moles >= 0f);
        return moles * config.GetValidatedTemp(temperature) * config.PressurePerMoleKelvin;
    }

    /// <summary>
    ///     Inverse of <see cref="CalculatePressure" />: <c>n = PV/(RT)</c>. Nonpositive or NaN pressure gives zero.
    /// </summary>
    internal static Mole PressureToMoles(IAtmosConfig config, Pascal pressure, Kelvin temperature)
    {
        if (pressure <= 0f || float.IsNaN(pressure))
            return 0f;

        PascalPerMole denominator = config.PressurePerMoleKelvin * config.GetValidatedTemp(temperature);
        return pressure / denominator;
    }

    /// <summary>
    ///     Returns a voxel's ideal-gas pressure from its current gas amounts.
    /// </summary>
    /// <remarks>
    ///     Not a pure function. It updates <see cref="AtmosChunk.IsVacuum" />.
    ///     A voxel with no gas is cleared through <see cref="AtmosChunk.SetVoxelToVacuum" />.
    /// </remarks>
    internal static Pascal CalculatePressureAtVoxel(
        IAtmosConfig config, AtmosChunk chunk,
        ushort localVoxelIndex)
    {
        Mole totalMoles = GetTotalMoles(chunk, localVoxelIndex);
        if (totalMoles <= 0f)
        {
            chunk.SetVoxelToVacuum(localVoxelIndex);
            return 0f;
        }

        chunk.IsVacuum[localVoxelIndex] = false;
        return CalculatePressure(config, totalMoles, chunk.Temperature[localVoxelIndex]);
    }

    /// <summary>
    ///     Same as <see cref="CalculatePressureAtVoxel(IAtmosConfig, AtmosChunk, ushort)" />, with the voxel's total
    ///     moles already summed by the caller.
    /// </summary>
    internal static Pascal CalculatePressureAtVoxel(
        IAtmosConfig config, AtmosChunk chunk,
        ushort localVoxelIndex, Mole totalMoles)
    {
        if (totalMoles <= 0f)
        {
            chunk.SetVoxelToVacuum(localVoxelIndex);
            return 0f;
        }

        chunk.IsVacuum[localVoxelIndex] = false;
        return CalculatePressure(config, totalMoles, chunk.Temperature[localVoxelIndex]);
    }


    /// <summary>
    ///     Sums <c>moles × Cv</c> over a voxel's positive gas amounts, in J/K. Does not read or write
    ///     <see cref="AtmosChunk.TotalHeatCapacity" />.
    /// </summary>
    internal static JoulePerKelvin CalculateHeatCapacityAtVoxel(
        IAtmosConfig config, AtmosChunk chunk,
        ushort localVoxelIndex)
    {
        JoulePerKelvin totalHeatCapacity = 0f;
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            Mole moles = chunk.ActiveGases[gas].Moles[localVoxelIndex];
            if (moles <= 0f)
                continue;

            totalHeatCapacity += moles *
                                 config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);
        }

        return totalHeatCapacity;
    }

    /// <summary>
    ///     Returns the bulk-flow pressure requested for a pressure delta: <c>pressureDelta × BulkFlowCoefficient</c>.
    ///     The coefficient is capped at 0.5, so a transfer never asks for more than half the delta.
    /// </summary>
    internal static Pascal CalculateBulkPressureTransfer(
        AtmosSolverConfigSnapshot config,
        Pascal pressureDelta)
    {
        if (pressureDelta == 0f)
            return 0f;

        float bulkFlowCoefficient = MathF.Min(config.BulkFlowCoefficient, 0.5f);
        return pressureDelta * bulkFlowCoefficient;
    }

    /// <summary>
    ///     Returns the source-relative species imbalance <c>n_s - n_t · (T_t / T_s)</c>.
    /// </summary>
    /// <remarks>No solver currently calls this. Diffusion is driven by the source amount alone.</remarks>
    internal static Mole CalculateMoleImbalance(
        Mole sourceMoles, Kelvin sourceTemperature,
        Mole targetMoles, Kelvin targetTemperature)
    {
        Debug.Assert(sourceMoles >= 0f && targetMoles >= 0f);
        Debug.Assert(IsFinitePositive(sourceTemperature));

        // An empty target contributes zero whatever the temperature ratio.
        // Handling it first stops 0 * infinity from turning a valid outward imbalance into NaN at extreme temperatures.
        if (targetMoles == 0f)
            return sourceMoles;

        Debug.Assert(IsFinitePositive(targetTemperature));
        return sourceMoles - targetMoles * (targetTemperature / sourceTemperature);
    }

    /// <summary>
    ///     Returns the effective conductance between two voxels: the configured conductance capped at
    ///     <c>C1·C2 / (C1 + C2)</c>.
    ///     That cap is the value that equalizes their temperatures in one step, so a transfer cannot overshoot
    ///     equilibrium.
    /// </summary>
    internal static JoulePerKelvin CalculateThermalConductance(
        JoulePerKelvin sourceHeatCapacity,
        JoulePerKelvin targetHeatCapacity, JoulePerKelvin thermalConductance)
    {
        Debug.Assert(IsFinitePositive(sourceHeatCapacity));
        Debug.Assert(IsFinitePositive(targetHeatCapacity));
        Debug.Assert(IsFinitePositive(thermalConductance));

        JoulePerKelvin smallerHeatCapacity = MathF.Min(sourceHeatCapacity, targetHeatCapacity);
        JoulePerKelvin largerHeatCapacity = MathF.Max(sourceHeatCapacity, targetHeatCapacity);
        JoulePerKelvin equilibriumConductance = smallerHeatCapacity /
                                                (1f + smallerHeatCapacity / largerHeatCapacity);

        return MathF.Min(thermalConductance, equilibriumConductance);
    }

    internal static int CompareChunkPositions(Int3 left, Int3 right)
    {
        int comparison = left.X.CompareTo(right.X);
        if (comparison != 0)
            return comparison;

        comparison = left.Y.CompareTo(right.Y);
        return comparison != 0 ? comparison : left.Z.CompareTo(right.Z);
    }

    internal static bool IsFinitePositive(float value)
    {
        return float.IsFinite(value) && value > 0f;
    }

    /// <summary>
    ///     Gas count up to which a per-channel molar-mass table lives on the stack. A larger table goes on the heap.
    /// </summary>
    internal const int MolarMassStackLimit = 64;

    /// <summary>
    ///     Derives a voxel's bulk velocity from its stored momentum: <c>v = p / m</c>, with the gas mass
    ///     <c>m = Σ n_g · M_g</c> summed over the positive gas amounts in channel order.
    /// </summary>
    /// <remarks>
    ///     Every component is zero for a vacuum voxel or one whose gas mass is not positive, so a consumer never
    ///     sees the division blow up on an emptied voxel that still carries stale momentum mid-write. Gases without
    ///     a registered molar mass weigh <see cref="IAtmosConfig.ReferenceMolarMass" />.
    /// </remarks>
    internal static void ComputeVoxelVelocity(
        IAtmosConfig config, AtmosChunk chunk, ushort voxelIndex,
        out MetrePerSecond x, out MetrePerSecond y, out MetrePerSecond z)
    {
        if (chunk.IsVacuum[voxelIndex])
        {
            x = 0f;
            y = 0f;
            z = 0f;
            return;
        }

        Kilogram mass = 0f;
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            Mole moles = chunk.ActiveGases[gas].Moles[voxelIndex];
            if (moles <= 0f)
                continue;

            mass += moles * config.GetMolarMass(chunk.ActiveGases[gas].GasId);
        }

        DivideMomentumByMass(chunk, voxelIndex, mass, out x, out y, out z);
    }

    /// <summary>
    ///     Derives a voxel's bulk velocity from a molar-mass table that the caller resolved once for the whole chunk.
    ///     See <see cref="ResolveMolarMasses" />.
    /// </summary>
    /// <remarks>
    ///     Use this overload in a loop over many voxels. It gives the same result as the configuration overload,
    ///     without an interface call per voxel and gas. The table holds the molar mass of each active gas channel,
    ///     indexed like <see cref="AtmosChunk.ActiveGases" />.
    /// </remarks>
    internal static void ComputeVoxelVelocity(
        ReadOnlySpan<KilogramPerMole> molarMasses, AtmosChunk chunk, ushort voxelIndex,
        out MetrePerSecond x, out MetrePerSecond y, out MetrePerSecond z)
    {
        if (chunk.IsVacuum[voxelIndex])
        {
            x = 0f;
            y = 0f;
            z = 0f;
            return;
        }

        Kilogram mass = 0f;
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            Mole moles = chunk.ActiveGases[gas].Moles[voxelIndex];
            if (moles <= 0f)
                continue;

            mass += moles * molarMasses[gas];
        }

        DivideMomentumByMass(chunk, voxelIndex, mass, out x, out y, out z);
    }

    /// <summary>
    ///     Resolves the molar mass of every active gas channel of a chunk.
    /// </summary>
    /// <param name="config">The configuration that supplies the molar masses.</param>
    /// <param name="chunk">The chunk whose active gas channels get a molar mass.</param>
    /// <param name="destination">
    ///     Receives one value per active channel, in channel order. It must hold at least
    ///     <see cref="AtmosChunk.ActiveGasCount" /> entries.
    /// </param>
    internal static void ResolveMolarMasses(
        IAtmosConfig config, AtmosChunk chunk, Span<KilogramPerMole> destination)
    {
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            destination[gas] = config.GetMolarMass(chunk.ActiveGases[gas].GasId);
    }

    private static void DivideMomentumByMass(
        AtmosChunk chunk, ushort voxelIndex, Kilogram mass,
        out MetrePerSecond x, out MetrePerSecond y, out MetrePerSecond z)
    {
        if (mass <= 0f)
        {
            x = 0f;
            y = 0f;
            z = 0f;
            return;
        }

        x = chunk.MomentumX[voxelIndex] / mass;
        y = chunk.MomentumY[voxelIndex] / mass;
        z = chunk.MomentumZ[voxelIndex] / mass;
    }

    /// <summary>
    ///     Returns the sum of all gas moles in a voxel.
    /// </summary>
    internal static Mole GetTotalMoles(AtmosChunk chunk, ushort voxelIndex)
    {
        Mole totalMoles = 0f;
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            totalMoles += chunk.ActiveGases[gas].Moles[voxelIndex];

        return totalMoles;
    }
}