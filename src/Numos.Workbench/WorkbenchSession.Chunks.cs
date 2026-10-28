using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;
using Numos.SimDrawer;

namespace Numos.Workbench;

/// <summary>
///     Names one voxel by its chunk position and local index, without a chunk generation.
/// </summary>
/// <param name="ChunkPosition">The chunk position in the simulation's chunk grid.</param>
/// <param name="LocalIndex">The voxel index inside the chunk, as `x + width * (y + height * z)`.</param>
public readonly record struct VoxelCell(Int3 ChunkPosition, ushort LocalIndex)
{
    /// <summary>
    ///     Drops the chunk generation from a drawing-layer address.
    /// </summary>
    public static VoxelCell From(VoxelAddress address)
    {
        return new VoxelCell(address.Chunk.Position, address.LocalIndex);
    }
}

public sealed partial class WorkbenchSession
{
    /// <summary>
    ///     Creates and registers a chunk, then gives it a uniform classification.
    /// </summary>
    /// <param name="position">The chunk position.</param>
    /// <param name="classification">The room id for every voxel. 0 is unassigned.</param>
    /// <param name="simulation">The target simulation, or <see langword="null" /> for the active one.</param>
    /// <returns>A handle to the new chunk.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The position or classification is invalid.</exception>
    /// <exception cref="InvalidOperationException">The chunk already exists or edits are blocked.</exception>
    public AtmosChunkHandle AddChunk(Int3 position, int classification = 0, AtmosSimulationId? simulation = null)
    {
        return Guard(
            $"Could not add chunk {FormatChunk(position)}",
            () =>
            {
                ThrowIfInspecting();
                var target = RequireSimulation(simulation);
                var chunk = target.CreateAndRegisterChunk(position);
                target.SetChunkClassification(chunk, new VoxelClassification(classification));
                Info($"Added chunk {FormatChunk(position)}.");
                return chunk;
            });
    }

    /// <summary>
    ///     Unregisters a chunk.
    /// </summary>
    /// <returns><see langword="true" /> when the chunk existed.</returns>
    public bool RemoveChunk(Int3 position, AtmosSimulationId? simulation = null)
    {
        return Guard(
            $"Could not remove chunk {FormatChunk(position)}",
            () =>
            {
                ThrowIfInspecting();
                bool removed = RequireSimulation(simulation).UnregisterChunk(new AtmosChunkHandle(position));
                if (removed)
                    Info($"Removed chunk {FormatChunk(position)}.");
                else
                    Warn($"Chunk {FormatChunk(position)} no longer exists.");

                return removed;
            });
    }

    /// <summary>
    ///     Replaces the outer faces of a chunk with solid walls.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The chunk does not exist.</exception>
    public void SealChunk(Int3 position, AtmosSimulationId? simulation = null)
    {
        Guard(
            $"Could not seal chunk {FormatChunk(position)}",
            () =>
            {
                ThrowIfInspecting();
                RequireSimulation(simulation)
                    .SetChunkBoundaryClassification(new AtmosChunkHandle(position), VoxelClassification.RoomSolid);

                Info($"Sealed chunk {FormatChunk(position)} with solid outer faces.");
            });
    }

    /// <summary>
    ///     Wakes a sleeping chunk.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The chunk does not exist.</exception>
    public void WakeChunk(Int3 position, AtmosSimulationId? simulation = null)
    {
        Guard(
            $"Could not wake chunk {FormatChunk(position)}",
            () =>
            {
                ThrowIfInspecting();
                RequireSimulation(simulation).WakeChunk(new AtmosChunkHandle(position));
                Info($"Woke chunk {FormatChunk(position)}.");
            });
    }

    /// <summary>
    ///     Puts a chunk to sleep so the solver skips it until something wakes it.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The chunk does not exist.</exception>
    public void SleepChunk(Int3 position, AtmosSimulationId? simulation = null)
    {
        Guard(
            $"Could not sleep chunk {FormatChunk(position)}",
            () =>
            {
                ThrowIfInspecting();
                RequireSimulation(simulation).SleepChunk(new AtmosChunkHandle(position));
                Info($"Slept chunk {FormatChunk(position)}.");
            });
    }

    /// <summary>
    ///     Sets the classification of every voxel in a chunk.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The classification is invalid.</exception>
    /// <exception cref="KeyNotFoundException">The chunk does not exist.</exception>
    public void FillChunk(Int3 position, int classification, AtmosSimulationId? simulation = null)
    {
        Guard(
            $"Could not fill chunk {FormatChunk(position)}",
            () =>
            {
                ThrowIfInspecting();
                RequireSimulation(simulation)
                    .SetChunkClassification(new AtmosChunkHandle(position), new VoxelClassification(classification));

                Info($"Filled chunk {FormatChunk(position)} with classification {classification}.");
            });
    }

    /// <summary>
    ///     Builds a <see cref="VoxelCell" /> from local coordinates.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The coordinates are outside the chunk.</exception>
    public VoxelCell GetCell(Int3 chunkPosition, int x, int y, int z, AtmosSimulationId? simulation = null)
    {
        var dimensions = RequireSimulation(simulation).ChunkDimensions;
        if (x < 0 || x >= dimensions.X || y < 0 || y >= dimensions.Y || z < 0 || z >= dimensions.Z)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x),
                $"Voxel ({x}, {y}, {z}) is outside a {dimensions.X} x {dimensions.Y} x {dimensions.Z} chunk.");
        }

        return new VoxelCell(chunkPosition, checked((ushort)(x + dimensions.X * (y + dimensions.Y * z))));
    }

    /// <summary>
    ///     Sets the classification of one voxel.
    /// </summary>
    public void SetVoxelClassification(
        VoxelCell cell,
        int classification,
        AtmosSimulationId? simulation = null)
    {
        SetVoxelClassification([cell], classification, simulation);
    }

    /// <summary>
    ///     Sets the classification of each distinct voxel in <paramref name="cells" />.
    /// </summary>
    /// <returns>The number of distinct voxels edited.</returns>
    /// <remarks>
    ///     The edit stops at the first failure. Voxels edited before it keep the new value.
    /// </remarks>
    public int SetVoxelClassification(
        IEnumerable<VoxelCell> cells,
        int classification,
        AtmosSimulationId? simulation = null)
    {
        return EditVoxels(
            cells,
            simulation,
            $"Set classification {classification}",
            (target, cell) => target.SetVoxelClassification(
                new AtmosChunkHandle(cell.ChunkPosition),
                cell.LocalIndex,
                new VoxelClassification(classification)));
    }

    /// <summary>
    ///     Sets the temperature of one voxel.
    /// </summary>
    public void SetVoxelTemperature(VoxelCell cell, float temperature, AtmosSimulationId? simulation = null)
    {
        SetVoxelTemperature([cell], temperature, simulation);
    }

    /// <summary>
    ///     Sets the temperature of each distinct voxel in <paramref name="cells" />, in kelvins.
    /// </summary>
    /// <returns>The number of distinct voxels edited.</returns>
    public int SetVoxelTemperature(
        IEnumerable<VoxelCell> cells,
        float temperature,
        AtmosSimulationId? simulation = null)
    {
        return EditVoxels(
            cells,
            simulation,
            $"Set temperature to {temperature:F1} K for",
            (target, cell) => target.SetVoxelTemperature(
                new AtmosChunkHandle(cell.ChunkPosition),
                cell.LocalIndex,
                temperature));
    }

    /// <summary>
    ///     Sets the total momentum of the gas in one voxel, in kilogram metres per second.
    /// </summary>
    /// <remarks>
    ///     Momentum is ignored for solid, void, and empty voxels. See
    ///     <see cref="AtmosSimulation.SetVoxelMomentum(AtmosChunkHandle, ushort, float, float, float)" />.
    /// </remarks>
    public void SetVoxelMomentum(
        VoxelCell cell,
        float momentumX,
        float momentumY,
        float momentumZ,
        AtmosSimulationId? simulation = null)
    {
        EditVoxels(
            [cell],
            simulation,
            "Set momentum for",
            (target, voxel) => target.SetVoxelMomentum(
                new AtmosChunkHandle(voxel.ChunkPosition),
                voxel.LocalIndex,
                momentumX,
                momentumY,
                momentumZ));
    }

    /// <summary>
    ///     Adds gas to one voxel.
    /// </summary>
    public void AddVoxelGas(
        VoxelCell cell,
        int gasId,
        float moles,
        float temperature,
        AtmosSimulationId? simulation = null)
    {
        AddVoxelGas([cell], gasId, moles, temperature, simulation);
    }

    /// <summary>
    ///     Adds gas to each distinct voxel in <paramref name="cells" />.
    /// </summary>
    /// <param name="cells">The voxels to inject into.</param>
    /// <param name="gasId">A registered gas id.</param>
    /// <param name="moles">Moles added to each voxel. Must be positive and finite.</param>
    /// <param name="temperature">Temperature of the added gas, in kelvins.</param>
    /// <param name="simulation">The target simulation, or <see langword="null" /> for the active one.</param>
    /// <returns>The number of distinct voxels edited.</returns>
    public int AddVoxelGas(
        IEnumerable<VoxelCell> cells,
        int gasId,
        float moles,
        float temperature,
        AtmosSimulationId? simulation = null)
    {
        string gas = Config != null && gasId >= 0 && gasId < Config.GasRegistry.Count
            ? $"{Config.GasRegistry[gasId].Name} ({gasId})"
            : gasId.ToString();

        return EditVoxels(
            cells,
            simulation,
            $"Injected {moles:G4} mol {gas} into",
            (target, cell) => target.AddGasToVoxel(
                new AtmosChunkHandle(cell.ChunkPosition),
                cell.LocalIndex,
                gasId,
                moles,
                temperature));
    }

    /// <summary>
    ///     Removes the gas from one voxel.
    /// </summary>
    public void ClearVoxelGas(VoxelCell cell, AtmosSimulationId? simulation = null)
    {
        ClearVoxelGas([cell], simulation);
    }

    /// <summary>
    ///     Removes the gas from each distinct voxel in <paramref name="cells" />.
    /// </summary>
    /// <returns>The number of distinct voxels edited.</returns>
    public int ClearVoxelGas(IEnumerable<VoxelCell> cells, AtmosSimulationId? simulation = null)
    {
        return EditVoxels(
            cells,
            simulation,
            "Cleared gas from",
            (target, cell) => target.GetVoxelGasMixture(new AtmosChunkHandle(cell.ChunkPosition), cell.LocalIndex)
                .Clear());
    }

    /// <summary>
    ///     Removes the gas from one voxel and makes it unassigned.
    /// </summary>
    public void ClearVoxelCell(VoxelCell cell, AtmosSimulationId? simulation = null)
    {
        ClearVoxelCells([cell], simulation);
    }

    /// <summary>
    ///     Removes the gas from each distinct voxel in <paramref name="cells" /> and makes them unassigned.
    /// </summary>
    /// <returns>The number of distinct voxels edited.</returns>
    public int ClearVoxelCells(IEnumerable<VoxelCell> cells, AtmosSimulationId? simulation = null)
    {
        return EditVoxels(
            cells,
            simulation,
            "Cleared",
            (target, cell) =>
            {
                var handle = new AtmosChunkHandle(cell.ChunkPosition);
                target.GetVoxelGasMixture(handle, cell.LocalIndex).Clear();
                target.SetVoxelClassification(
                    handle,
                    cell.LocalIndex,
                    new VoxelClassification(VoxelClassification.RoomUnassigned));
            });
    }

    private int EditVoxels(
        IEnumerable<VoxelCell> cells,
        AtmosSimulationId? simulation,
        string action,
        Action<AtmosSimulation, VoxelCell> edit)
    {
        return Guard(
            "Could not edit the voxel selection",
            () =>
            {
                ThrowIfInspecting();
                var target = RequireSimulation(simulation);
                VoxelCell[] targets = cells.Distinct().ToArray();
                foreach (var cell in targets)
                    edit(target, cell);

                if (targets.Length > 0)
                    Info($"{action} {targets.Length} voxel{(targets.Length == 1 ? string.Empty : "s")}.");

                return targets.Length;
            });
    }

    private static string FormatChunk(Int3 position)
    {
        return $"({position.X}, {position.Y}, {position.Z})";
    }
}
