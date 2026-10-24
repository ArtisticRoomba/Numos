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

    internal override void Apply(AtmosKernel kernel) => kernel.Apply(this);
}

/// <summary>
///     Removes a chunk and releases its storage.
/// </summary>
/// <param name="Position">Chunk-grid address to remove.</param>
public sealed record RemoveChunkOperation(Int3 Position) : AtmosOperation
{
    /// <inheritdoc />
    public override AtmosOperationCode Code => AtmosOperationCode.RemoveChunk;

    internal override void Apply(AtmosKernel kernel) => kernel.Apply(this);
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

    internal override void Apply(AtmosKernel kernel) => kernel.Apply(this);
}

/// <summary>
///     Classifies outer chunk faces. Single-layer chunks use only the X/Y perimeter.
/// </summary>
/// <param name="Position">Target chunk-grid address.</param>
/// <param name="Classification">Air classification value or reserved voxel classification.</param>
public sealed record SetChunkBoundaryClassificationOperation(Int3 Position, VoxelClassification Classification) : AtmosOperation
{
    /// <inheritdoc />
    public override AtmosOperationCode Code => AtmosOperationCode.SetChunkBoundaryClassification;

    internal override void Apply(AtmosKernel kernel) => kernel.Apply(this);
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

    internal override void Apply(AtmosKernel kernel) => kernel.Apply(this);
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

    internal override void Apply(AtmosKernel kernel) => kernel.Apply(this);
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

    internal override void Apply(AtmosKernel kernel) => kernel.Apply(this);
}

/// <summary>
///     Wakes a chunk and resets its sleep timer.
/// </summary>
/// <param name="Position">Target chunk-grid address.</param>
public sealed record WakeChunkOperation(Int3 Position) : AtmosOperation
{
    /// <inheritdoc />
    public override AtmosOperationCode Code => AtmosOperationCode.WakeChunk;

    internal override void Apply(AtmosKernel kernel) => kernel.Apply(this);
}

/// <summary>
///     Makes a chunk ineligible for subsequent solver processing until woken.
/// </summary>
/// <param name="Position">Target chunk-grid address.</param>
public sealed record SleepChunkOperation(Int3 Position) : AtmosOperation
{
    /// <inheritdoc />
    public override AtmosOperationCode Code => AtmosOperationCode.SleepChunk;

    internal override void Apply(AtmosKernel kernel) => kernel.Apply(this);
}

/// <summary>
///     The optional parts of an <see cref="EditVoxelBoxOperation" />.
/// </summary>
/// <remarks>
///     <para>
///         The kernel applies the parts in the order of this list: classification, gas removal, temperature, gas
///         injection. Gas injection has no flag, because a nonempty gas list selects it.
///     </para>
///     <para>
///         The flag value 8 is reserved for a velocity part. This build rejects it.
///     </para>
/// </remarks>
[Flags]
public enum AtmosVoxelBoxParts : byte
{
    /// <summary>
    ///     No optional part.
    /// </summary>
    None = 0,

    /// <summary>
    ///     Assign the classification to every voxel.
    /// </summary>
    Classification = 1,

    /// <summary>
    ///     Remove the gas from every air voxel before anything is added.
    /// </summary>
    ClearGas = 2,

    /// <summary>
    ///     Store the voxel temperature in every voxel.
    /// </summary>
    Temperature = 4
}

/// <summary>
///     Edits every voxel of a box in global voxel coordinates with one recorded operation.
/// </summary>
/// <remarks>
///     <para>
///         Voxels in chunks that do not exist are skipped. The kernel visits the existing chunks in z, y, x order of
///         their grid positions. Inside a chunk it applies the parts in the order of <see cref="AtmosVoxelBoxParts" />.
///         The result equals the same edit made with one per-voxel call for each part and voxel.
///     </para>
///     <para>
///         A field is read only when its part is selected. The other fields hold zero.
///     </para>
/// </remarks>
/// <param name="Min">Inclusive minimum corner, in global voxel coordinates.</param>
/// <param name="Max">Inclusive maximum corner, in global voxel coordinates.</param>
/// <param name="Parts">The selected parts.</param>
/// <param name="Classification">The classification to assign when <see cref="AtmosVoxelBoxParts.Classification" /> is set.</param>
/// <param name="VoxelTemperature">The stored temperature, in kelvins, when <see cref="AtmosVoxelBoxParts.Temperature" /> is set.</param>
/// <param name="GasTemperature">The temperature of the injected gas, in kelvins. It matters when <paramref name="Gases" /> is not empty.</param>
/// <param name="Gases">The gases to add to every air voxel, in injection order. Each amount is positive and finite.</param>
public sealed record EditVoxelBoxOperation(
    Int3 Min,
    Int3 Max,
    AtmosVoxelBoxParts Parts,
    VoxelClassification Classification,
    Kelvin VoxelTemperature,
    Kelvin GasTemperature,
    IReadOnlyList<AtmosGasAmount> Gases) : AtmosOperation
{
    /// <inheritdoc />
    public override AtmosOperationCode Code => AtmosOperationCode.EditVoxelBox;

    internal override void Apply(AtmosKernel kernel) => kernel.Apply(this);
}

/// <summary>
///     Sets a voxel's resolved gas and thermal state, including caches and channel order, independent of detached
///     mixture identity.
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
        var gases = new AtmosGasAmount[chunk.ActiveGasCount];
        for (int gas = 0; gas < gases.Length; gas++)
            gases[gas] = new AtmosGasAmount(chunk.ActiveGases[gas].GasId, chunk.ActiveGases[gas].Moles[index]);

        Gases = Array.AsReadOnly(gases);
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

    internal override void Apply(AtmosKernel kernel) => kernel.Apply(this);
}

/// <summary>
///     One resolved species amount in a voxel operation payload.
/// </summary>
/// <param name="GasId">Nonnegative simulation gas ID.</param>
/// <param name="Moles">Stored amount in moles, including zero for retained channels.</param>
public readonly record struct AtmosGasAmount(int GasId, Mole Moles);