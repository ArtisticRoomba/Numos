namespace Numos.CoreSim.Solvers;

/// <summary>
///     Applies one gas injection while keeping the voxel's heat capacity, temperature, and pressure coherent.
/// </summary>
/// <remarks>
///     Callers validate the target and wake its chunk before entry. <see cref="AtmosChunk.InjectGasToVoxel" />
///     remains the single invariant guard at the storage boundary.
/// </remarks>
internal static class GasInjectionSolver
{
    /// <summary>
    ///     Injects gas outside a tick (direct API mutations such as explosions or tools).
    ///     The method first recomputes the voxel's heat capacity from its current composition.
    /// </summary>
    /// <remarks>
    ///     Solver stages use the
    ///     <see cref="Inject(AtmosChunk, ushort, int, Mole, Kelvin, AtmosSolverConfigSnapshot, JoulePerKelvin)" />
    ///     overload against the tick's config snapshot instead.
    /// </remarks>
    internal static void Inject(
        AtmosChunk chunk, ushort localVoxelIndex, int gasId, Mole moles,
        Kelvin temperature, IAtmosConfig config)
    {
        JoulePerKelvin currentHeatCapacity = AtmosSolverMath.CalculateHeatCapacityAtVoxel(config, chunk, localVoxelIndex);

        InjectCore(
            chunk,
            localVoxelIndex,
            gasId,
            moles,
            temperature,
            config.GetMolarHeatCapacityAtConstantVolume(gasId),
            currentHeatCapacity,
            config.GetValidatedTemp(chunk.Temperature[localVoxelIndex]),
            config.PressurePerMoleKelvin);
    }

    /// <summary>
    ///     Injects gas that a vent pushes in, outside a tick. The voxel's sensible energy grows by the enthalpy
    ///     <c>n * (cv + R) * T_in</c> because the vent also does flow work <c>p * V = n * R * T_in</c>.
    /// </summary>
    /// <remarks>
    ///     The heat capacity still grows by <c>n * cv</c>. The method therefore injects at the effective temperature
    ///     <c>T_in * (cv + R) / cv</c>, where <c>R</c> is <see cref="AtmosPhysicalConstants.MolarGasConstant" />.
    ///     An empty voxel lands at <c>gamma * T_in</c>, the textbook result for filling an evacuated rigid tank.
    /// </remarks>
    internal static void Vent(
        AtmosChunk chunk, ushort localVoxelIndex, int gasId, Mole moles,
        Kelvin temperature, IAtmosConfig config)
    {
        JoulePerMoleKelvin cv = config.GetMolarHeatCapacityAtConstantVolume(gasId);
        Kelvin effectiveTemperature = temperature * ((cv + AtmosPhysicalConstants.MolarGasConstant) / cv);
        Inject(chunk, localVoxelIndex, gasId, moles, effectiveTemperature, config);
    }

    /// <summary>
    ///     Injects against a tick config snapshot using an already-known heat capacity.
    ///     A batched caller (for example queued boundary flow injections) can then skip re-deriving composition
    ///     for a voxel it already resynced earlier in the same batch.
    /// </summary>
    internal static void Inject(
        AtmosChunk chunk, ushort localVoxelIndex, int gasId, Mole moles,
        Kelvin temperature, AtmosSolverConfigSnapshot config, JoulePerKelvin currentHeatCapacity)
    {
        InjectCore(
            chunk,
            localVoxelIndex,
            gasId,
            moles,
            temperature,
            config.GetMolarHeatCapacityAtConstantVolume(gasId),
            currentHeatCapacity,
            config.GetValidatedTemp(chunk.Temperature[localVoxelIndex]),
            config.PressurePerMoleKelvin);
    }

    private static void InjectCore(
        AtmosChunk chunk, ushort localVoxelIndex, int gasId, Mole moles,
        Kelvin temperature, JoulePerMoleKelvin molarHeatCapacity, JoulePerKelvin currentHeatCapacity,
        Kelvin effectiveCurrentTemperature, PascalPerMoleKelvin pressurePerMoleKelvin)
    {
        chunk.TotalHeatCapacity[localVoxelIndex] = currentHeatCapacity;
        if (currentHeatCapacity > 0f && !AtmosSolverMath.IsFinitePositive(chunk.Temperature[localVoxelIndex]))
            chunk.Temperature[localVoxelIndex] = effectiveCurrentTemperature;

        chunk.InjectGasToVoxel(
            localVoxelIndex,
            gasId,
            moles,
            temperature,
            molarHeatCapacity,
            pressurePerMoleKelvin);
    }
}

internal readonly record struct InjectionEvent(ushort LocalVoxelIndex, int GasId, Mole Moles, Kelvin Temperature);