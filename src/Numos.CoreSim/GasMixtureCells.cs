using System.Diagnostics;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Solvers;

namespace Numos.CoreSim;

/// <summary>
///     Cached totals that accompany one mixture write: summed moles, heat capacity, and pressure.
/// </summary>
internal readonly record struct GasMixtureCellTotals(
    Mole TotalMoles,
    JoulePerKelvin HeatCapacity,
    Pascal Pressure);

/// <summary>
///     Mixture math for one voxel of a chunk, shared by the kernel's voxel mixtures and the standalone mixture pool.
/// </summary>
/// <remarks>
///     Every pressure comes from the caller's <c>R / V</c> coefficient. A grid voxel passes the configured
///     <see cref="IAtmosConfig.PressurePerMoleKelvin" />. A pool cell passes the coefficient of its own volume.
///     The methods take no locks and do not wake chunks. Callers own both.
/// </remarks>
internal static class GasMixtureCells
{
    /// <summary>
    ///     Sums all currently allocated gas channels at a voxel.
    /// </summary>
    internal static Mole GetTotalMoles(AtmosChunk chunk, ushort localVoxelIndex)
    {
        return AtmosSolverMath.GetTotalMoles(chunk, localVoxelIndex);
    }

    /// <summary>
    ///     Finds a gas channel by identifier, treating an unallocated channel as zero moles.
    /// </summary>
    internal static Mole GetGasMoles(AtmosChunk chunk, ushort localVoxelIndex, int gasId)
    {
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            if (chunk.ActiveGases[gas].GasId == gasId)
                return chunk.ActiveGases[gas].Moles[localVoxelIndex];
        }

        return 0f;
    }

    /// <summary>
    ///     Sets an existing channel or allocates one only when a positive amount must be stored.
    /// </summary>
    internal static void SetGasMoles(AtmosChunk chunk, ushort localVoxelIndex, int gasId, Mole moles)
    {
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            if (chunk.ActiveGases[gas].GasId != gasId)
                continue;

            chunk.ActiveGases[gas].Moles[localVoxelIndex] = moles;
            return;
        }

        if (moles <= 0f)
            return;

        int channel = chunk.GetOrCreateGasChannel(gasId);
        chunk.ActiveGases[channel].Moles[localVoxelIndex] = moles;
    }

    /// <summary>
    ///     Counts gas channels with a positive amount at a voxel.
    /// </summary>
    internal static int CountActiveGases(AtmosChunk chunk, ushort localVoxelIndex)
    {
        int count = 0;
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            if (chunk.ActiveGases[gas].Moles[localVoxelIndex] > 0f)
                count++;
        }

        return count;
    }

    /// <summary>
    ///     Verifies that a voxel can contain a gas mixture.
    /// </summary>
    internal static void ValidateGasVoxel(AtmosChunk chunk, ushort localVoxelIndex)
    {
        int classification = chunk.VoxelRoomMap[localVoxelIndex];
        if (classification == VoxelClassification.RoomSolid || classification == VoxelClassification.RoomVoid)
            throw new InvalidOperationException("Solid and void voxels cannot contain a gas mixture.");
    }

    /// <summary>
    ///     Captures an ordered, detached list of a voxel's positive gas species, sorted by gas ID.
    /// </summary>
    internal static KeyValuePair<int, Mole>[] CaptureGases(AtmosChunk chunk, ushort localVoxelIndex)
    {
        var gases = new KeyValuePair<int, Mole>[chunk.ActiveGasCount];
        int gasCount = 0;
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            Mole moles = chunk.ActiveGases[gas].Moles[localVoxelIndex];
            if (moles <= 0f)
                continue;

            gases[gasCount++] = new KeyValuePair<int, Mole>(chunk.ActiveGases[gas].GasId, moles);
        }

        if (gasCount != gases.Length)
            Array.Resize(ref gases, gasCount);

        Array.Sort(gases, static (left, right) => left.Key.CompareTo(right.Key));
        return gases;
    }

    /// <summary>
    ///     Computes the caches that must accompany a mixture mutation, optionally substituting one species amount.
    /// </summary>
    /// <exception cref="InvalidOperationException">A total overflows the float range.</exception>
    internal static GasMixtureCellTotals CalculateTotals(
        IAtmosConfig config,
        PascalPerMoleKelvin pressurePerMoleKelvin,
        AtmosChunk chunk,
        ushort localVoxelIndex,
        Kelvin temperature,
        int overrideGasId = -1,
        Mole overrideMoles = 0f)
    {
        Mole totalMoles = 0f;
        JoulePerKelvin totalHeatCapacity = 0f;
        bool foundOverride = false;
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            int gasId = chunk.ActiveGases[gas].GasId;
            Mole moles = gasId == overrideGasId
                ? overrideMoles
                : chunk.ActiveGases[gas].Moles[localVoxelIndex];

            foundOverride |= gasId == overrideGasId;
            if (moles <= 0f)
                continue;

            totalMoles += moles;
            totalHeatCapacity += moles * config.GetMolarHeatCapacityAtConstantVolume(gasId);
        }

        if (!foundOverride && overrideGasId >= 0 && overrideMoles > 0f)
        {
            totalMoles += overrideMoles;
            totalHeatCapacity += overrideMoles * config.GetMolarHeatCapacityAtConstantVolume(overrideGasId);
        }

        if (!float.IsFinite(totalMoles))
            throw new InvalidOperationException("The mixture's total moles exceed the supported range.");

        if (!float.IsFinite(totalHeatCapacity))
            throw new InvalidOperationException("The mixture's heat capacity exceeds the supported range.");

        Pascal pressure = AtmosSolverMath.CalculatePressure(config, totalMoles, temperature, pressurePerMoleKelvin);
        if (!float.IsFinite(pressure))
            throw new InvalidOperationException("The mixture's pressure exceeds the supported range.");

        return new GasMixtureCellTotals(
            totalMoles,
            totalHeatCapacity,
            pressure);
    }

    /// <summary>
    ///     Writes calculated mixture caches and marks the containing chunk changed. A write that leaves the voxel
    ///     without gas also zeroes its momentum. The mixture API is the only external path that can empty a voxel
    ///     without going through <see cref="AtmosChunk.SetVoxelToVacuum" />.
    /// </summary>
    internal static void ApplyTotals(AtmosChunk chunk, ushort localVoxelIndex, GasMixtureCellTotals totals)
    {
        chunk.TotalHeatCapacity[localVoxelIndex] = totals.HeatCapacity;
        chunk.TotalPressure[localVoxelIndex] = totals.Pressure;
        chunk.IsVacuum[localVoxelIndex] = totals.Pressure <= 0f;
        if (totals.TotalMoles <= 0f)
            chunk.ZeroMomentum(localVoxelIndex);

        chunk.MarkChanged();
    }

    /// <summary>
    ///     Replaces one species amount and refreshes the cached heat capacity and pressure.
    /// </summary>
    /// <exception cref="InvalidOperationException">The voxel is solid or void, or a total overflows.</exception>
    internal static void SetMoles(
        IAtmosConfig config,
        PascalPerMoleKelvin pressurePerMoleKelvin,
        AtmosChunk chunk,
        ushort localVoxelIndex,
        int gasId,
        Mole moles)
    {
        ValidateGasVoxel(chunk, localVoxelIndex);
        var totals = CalculateTotals(
            config,
            pressurePerMoleKelvin,
            chunk,
            localVoxelIndex,
            chunk.Temperature[localVoxelIndex],
            gasId,
            moles);

        SetGasMoles(chunk, localVoxelIndex, gasId, moles);
        ApplyTotals(chunk, localVoxelIndex, totals);
    }

    /// <summary>
    ///     Applies a signed mole adjustment, clamping the resulting amount at zero.
    /// </summary>
    /// <exception cref="InvalidOperationException">The voxel is solid or void, or a total overflows.</exception>
    internal static void AdjustMoles(
        IAtmosConfig config,
        PascalPerMoleKelvin pressurePerMoleKelvin,
        AtmosChunk chunk,
        ushort localVoxelIndex,
        int gasId,
        Mole deltaMoles)
    {
        Mole currentMoles = GetGasMoles(chunk, localVoxelIndex, gasId);
        Mole adjusted = currentMoles + deltaMoles;
        if (!float.IsFinite(adjusted))
            throw new InvalidOperationException("The adjusted gas amount exceeds the supported range.");

        Mole moles = MathF.Max(0f, adjusted);
        ValidateGasVoxel(chunk, localVoxelIndex);
        var totals = CalculateTotals(
            config,
            pressurePerMoleKelvin,
            chunk,
            localVoxelIndex,
            chunk.Temperature[localVoxelIndex],
            gasId,
            moles);

        SetGasMoles(chunk, localVoxelIndex, gasId, moles);
        ApplyTotals(chunk, localVoxelIndex, totals);
    }

    /// <summary>
    ///     Adds gas at a temperature and heat-capacity-weights the resulting mixture temperature.
    /// </summary>
    /// <remarks>
    ///     Every total is computed and validated before the first write, so a failed call leaves the voxel unchanged.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The voxel is solid or void, or a total overflows.</exception>
    internal static void AddGas(
        IAtmosConfig config,
        PascalPerMoleKelvin pressurePerMoleKelvin,
        AtmosChunk chunk,
        ushort localVoxelIndex,
        int gasId,
        Mole moles,
        Kelvin temperature)
    {
        Mole currentGasMoles = GetGasMoles(chunk, localVoxelIndex, gasId);
        Mole combinedGasMoles = currentGasMoles + moles;
        if (!float.IsFinite(combinedGasMoles))
            throw new InvalidOperationException("A merged gas amount exceeds the supported range.");

        var currentTotals = CalculateTotals(
            config,
            pressurePerMoleKelvin,
            chunk,
            localVoxelIndex,
            chunk.Temperature[localVoxelIndex]);

        JoulePerKelvin currentHeatCapacity = currentTotals.HeatCapacity;
        JoulePerKelvin incomingHeatCapacity = moles * config.GetMolarHeatCapacityAtConstantVolume(gasId);
        JoulePerKelvin combinedHeatCapacity = currentHeatCapacity + incomingHeatCapacity;
        if (!float.IsFinite(combinedHeatCapacity))
            throw new InvalidOperationException("The mixture's heat capacity exceeds the supported range.");

        Kelvin currentTemperature = config.GetValidatedTemp(chunk.Temperature[localVoxelIndex]);
        Kelvin incomingTemperature = config.GetValidatedTemp(temperature);
        Kelvin mixedTemperature = combinedHeatCapacity > 0f
            ? currentTemperature +
              (incomingTemperature - currentTemperature) * incomingHeatCapacity / combinedHeatCapacity
            : temperature;

        ValidateGasVoxel(chunk, localVoxelIndex);
        var totals = CalculateTotals(
            config,
            pressurePerMoleKelvin,
            chunk,
            localVoxelIndex,
            mixedTemperature,
            gasId,
            combinedGasMoles);

        SetGasMoles(chunk, localVoxelIndex, gasId, combinedGasMoles);
        chunk.Temperature[localVoxelIndex] = mixedTemperature;
        ApplyTotals(chunk, localVoxelIndex, totals);
    }

    /// <summary>
    ///     Removes every gas amount from a voxel and clears its cached mixture totals.
    /// </summary>
    /// <exception cref="InvalidOperationException">The voxel is solid or void.</exception>
    internal static void Clear(AtmosChunk chunk, ushort localVoxelIndex)
    {
        ValidateGasVoxel(chunk, localVoxelIndex);
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            chunk.ActiveGases[gas].Moles[localVoxelIndex] = 0f;

        ApplyTotals(chunk, localVoxelIndex, default);
    }

    /// <summary>
    ///     Replaces the complete mixture of an eligible voxel from a detached list of gas amounts.
    /// </summary>
    /// <remarks>
    ///     The caller has validated the amounts. This method only asserts that they are finite.
    /// </remarks>
    internal static void Replace(
        IAtmosConfig config,
        PascalPerMoleKelvin pressurePerMoleKelvin,
        AtmosChunk chunk,
        ushort localVoxelIndex,
        Kelvin temperature,
        KeyValuePair<int, Mole>[] gases)
    {
        Mole totalMoles = 0f;
        JoulePerKelvin totalHeatCapacity = 0f;
        foreach ((int gasId, Mole moles) in gases)
        {
            totalMoles += moles;
            totalHeatCapacity += moles * config.GetMolarHeatCapacityAtConstantVolume(gasId);
        }

        Debug.Assert(float.IsFinite(totalMoles));
        Debug.Assert(float.IsFinite(totalHeatCapacity));

        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            chunk.ActiveGases[gas].Moles[localVoxelIndex] = 0f;

        foreach ((int gasId, Mole moles) in gases)
        {
            int gasChannel = chunk.GetOrCreateGasChannel(gasId);
            chunk.ActiveGases[gasChannel].Moles[localVoxelIndex] = moles;
        }

        chunk.Temperature[localVoxelIndex] = temperature;
        ApplyTotals(
            chunk,
            localVoxelIndex,
            new GasMixtureCellTotals(
                totalMoles,
                totalHeatCapacity,
                AtmosSolverMath.CalculatePressure(config, totalMoles, temperature, pressurePerMoleKelvin)));
    }

    /// <summary>
    ///     Recomputes the cached heat capacity, pressure, and vacuum flag from a voxel's moles and temperature.
    /// </summary>
    /// <remarks>
    ///     A voxel without gas is cleared through <see cref="AtmosChunk.SetVoxelToVacuum" />.
    /// </remarks>
    internal static void RefreshCaches(
        IAtmosConfig config,
        PascalPerMoleKelvin pressurePerMoleKelvin,
        AtmosChunk chunk,
        ushort localVoxelIndex)
    {
        Pascal pressure = AtmosSolverMath.CalculatePressureAtVoxel(
            config, pressurePerMoleKelvin, chunk, localVoxelIndex);

        chunk.TotalPressure[localVoxelIndex] = pressure;
        chunk.TotalHeatCapacity[localVoxelIndex] =
            AtmosSolverMath.CalculateHeatCapacityAtVoxel(config, chunk, localVoxelIndex);

        chunk.IsVacuum[localVoxelIndex] = pressure <= 0f;
    }
}
