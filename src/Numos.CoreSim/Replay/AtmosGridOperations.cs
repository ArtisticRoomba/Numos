using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Replay;

/// <summary>
///     Creates an empty sleeping chunk using the simulation’s fixed dimensions and wakes sleeping face neighbors.
/// </summary>
/// <param name="Position">Chunk-grid address to create.</param>
public sealed record CreateChunkOperation(Int3 Position) : AtmosOperation
{
    /// <inheritdoc />
    public override AtmosOperationCode Code => AtmosOperationCode.CreateChunk;
}

/// <summary>
///     Removes a chunk and releases its storage.
/// </summary>
/// <param name="Position">Chunk-grid address to remove.</param>
public sealed record RemoveChunkOperation(Int3 Position) : AtmosOperation
{
    /// <inheritdoc />
    public override AtmosOperationCode Code => AtmosOperationCode.RemoveChunk;
}

/// <summary>
///     Classifies every voxel, clearing gas in solid or void voxels and refreshing awake topology.
/// </summary>
/// <param name="Position">Target chunk-grid address.</param>
/// <param name="Classification">Air classification value or reserved voxel classification.</param>
public sealed record SetChunkClassificationOperation(Int3 Position, VoxelClassification Classification) : AtmosOperation
{
    /// <inheritdoc />
    public override AtmosOperationCode Code => AtmosOperationCode.SetChunkClassification;
}

/// <summary>
///     Classifies outer chunk faces; single-layer chunks use only the X/Y perimeter.
/// </summary>
/// <param name="Position">Target chunk-grid address.</param>
/// <param name="Classification">Air classification value or reserved voxel classification.</param>
public sealed record SetChunkBoundaryClassificationOperation(Int3 Position, VoxelClassification Classification) : AtmosOperation
{
    /// <inheritdoc />
    public override AtmosOperationCode Code => AtmosOperationCode.SetChunkBoundaryClassification;
}

/// <summary>
///     Classifies one voxel and refreshes awake topology.
/// </summary>
/// <param name="Position">Target chunk-grid address.</param>
/// <param name="LocalVoxelIndex">Canonical flat local voxel index.</param>
/// <param name="Classification">Air classification value or reserved voxel classification.</param>
public sealed record SetVoxelClassificationOperation(Int3 Position, ushort LocalVoxelIndex, VoxelClassification Classification)
    : AtmosOperation
{
    /// <inheritdoc />
    public override AtmosOperationCode Code => AtmosOperationCode.SetVoxelClassification;
}

/// <summary>
///     Stores raw voxel temperature and refreshes its pressure cache.
/// </summary>
/// <param name="Position">Target chunk-grid address.</param>
/// <param name="LocalVoxelIndex">Canonical flat local voxel index.</param>
/// <param name="Temperature">Raw kelvins, preserved without eager normalization.</param>
public sealed record SetVoxelTemperatureOperation(Int3 Position, ushort LocalVoxelIndex, Kelvin Temperature) : AtmosOperation
{
    /// <inheritdoc />
    public override AtmosOperationCode Code => AtmosOperationCode.SetVoxelTemperature;
}

/// <summary>
///     Injects gas immediately using heat-capacity-weighted temperature mixing and wakes its chunk.
/// </summary>
/// <param name="Position">Target chunk-grid address.</param>
/// <param name="LocalVoxelIndex">Canonical flat local voxel index.</param>
/// <param name="GasId">Nonnegative simulation gas ID.</param>
/// <param name="Moles">Positive finite amount to add, in moles.</param>
/// <param name="Temperature">Nonnegative finite incoming temperature, in kelvins.</param>
public sealed record AddGasToVoxelOperation(Int3 Position, ushort LocalVoxelIndex, int GasId, Mole Moles, Kelvin Temperature)
    : AtmosOperation
{
    /// <inheritdoc />
    public override AtmosOperationCode Code => AtmosOperationCode.AddGasToVoxel;
}

/// <summary>
///     Wakes a chunk and resets its sleep timer.
/// </summary>
/// <param name="Position">Target chunk-grid address.</param>
public sealed record WakeChunkOperation(Int3 Position) : AtmosOperation
{
    /// <inheritdoc />
    public override AtmosOperationCode Code => AtmosOperationCode.WakeChunk;
}

/// <summary>
///     Makes a chunk ineligible for subsequent solver processing until woken.
/// </summary>
/// <param name="Position">Target chunk-grid address.</param>
public sealed record SleepChunkOperation(Int3 Position) : AtmosOperation
{
    /// <inheritdoc />
    public override AtmosOperationCode Code => AtmosOperationCode.SleepChunk;
}

/// <summary>
///     Changes participation of a pre-registered solver without changing its definition or order.
/// </summary>
/// <param name="Name">Ordinal stable solver identity.</param>
/// <param name="Enabled">Whether the solver should run in subsequent ticks.</param>
public sealed record SetSolverEnabledOperation(string Name, bool Enabled) : AtmosOperation
{
    /// <inheritdoc />
    public override AtmosOperationCode Code => AtmosOperationCode.SetSolverEnabled;
}

/// <summary>
///     Resolved voxel state, including caches and channel order, independent of detached mixture identity.
/// </summary>
public sealed record SetVoxelMixtureOperation : AtmosOperation
{
    internal SetVoxelMixtureOperation(AtmosChunk chunk, ushort index)
    {
        Position = chunk.GridPosition;
        LocalVoxelIndex = index;
        Temperature = chunk.Temperature[index];
        Pressure = chunk.TotalPressure[index];
        HeatCapacity = chunk.TotalHeatCapacity[index];
        Gases = Array.AsReadOnly(
            Enumerable.Range(0, chunk.ActiveGasCount)
                .Select(gas => new AtmosGasAmount(chunk.ActiveGases[gas].GasId, chunk.ActiveGases[gas].Moles[index]))
                .ToArray());
    }

    internal SetVoxelMixtureOperation(
        Int3 position,
        ushort localVoxelIndex,
        Kelvin temperature,
        Pascal pressure,
        JoulePerKelvin heatCapacity,
        IEnumerable<AtmosGasAmount> gases)
    {
        ArgumentNullException.ThrowIfNull(gases);
        Position = position;
        LocalVoxelIndex = localVoxelIndex;
        Temperature = temperature;
        Pressure = pressure;
        HeatCapacity = heatCapacity;
        Gases = Array.AsReadOnly(gases.ToArray());
    }

    /// <inheritdoc />
    public override AtmosOperationCode Code => AtmosOperationCode.SetVoxelMixture;

    /// <summary>
    ///     Gets the chunk-grid address receiving the resolved mutation.
    /// </summary>
    public Int3 Position { get; }

    /// <summary>
    ///     Gets the canonical flat local voxel index.
    /// </summary>
    public ushort LocalVoxelIndex { get; }

    /// <summary>
    ///     Gets the exact resulting temperature, in kelvins.
    /// </summary>
    public Kelvin Temperature { get; }

    /// <summary>
    ///     Gets the exact resulting cached pressure, in pascals.
    /// </summary>
    public Pascal Pressure { get; }

    /// <summary>
    ///     Gets the exact resulting cached heat capacity, in joules per kelvin.
    /// </summary>
    public JoulePerKelvin HeatCapacity { get; }

    /// <summary>
    ///     Gets all allocated channels at the affected voxel in reduction order, including zero amounts.
    /// </summary>
    public IReadOnlyList<AtmosGasAmount> Gases { get; }
}

/// <summary>
///     One resolved species amount in a voxel operation payload.
/// </summary>
/// <param name="GasId">Nonnegative simulation gas ID.</param>
/// <param name="Moles">Stored amount in moles, including zero for retained channels.</param>
public readonly record struct AtmosGasAmount(int GasId, Mole Moles);