using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Applies one gas injection while keeping mixture SHC, temperature, and pressure coherent.
/// </summary>
/// <remarks>
///     Callers validate the target and wake its room before entry. <see cref="AtmosChunk.InjectGasToVoxel" />
///     remains the single invariant guard at the storage boundary.
/// </remarks>
internal static class GasInjectionSolver
{
    /// <summary>
    ///     Public entry point for injecting gas outside the tick/solver flow (e.g. explosions, tools).
    ///     Distinct from the <see cref="Inject(AtmosChunk, ushort, int, Mole, Kelvin, AtmosSolverConfigSnapshot, JoulePerKelvin)" />
    ///     overload, which is used during ticked solving against a config snapshot.
    /// </summary>
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
    ///     Injects against a tick config snapshot using an already-known heat capacity. Lets a batched
    ///     caller (e.g. queued boundary flow injections) skip re-deriving composition for a voxel it has
    ///     already resynced earlier in the same batch.
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

internal record InjectionEvent(ushort LocalVoxelIndex, int GasId, Mole Moles, Kelvin Temperature);