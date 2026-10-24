using JetBrains.Annotations;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;

namespace Numos.API;

/// <summary>
///     The changes <see cref="AtmosSimulation.EditVoxelBox" /> applies to a box of voxels.
/// </summary>
/// <remarks>
///     <para>
///         Every part is optional. Numos applies the selected parts to each chunk in this order: classification, gas
///         removal, voxel temperature, gas injection.
///     </para>
///     <para>
///         Gas removal and gas injection skip solid and void voxels.
///     </para>
/// </remarks>
/// <example>
///     Fill a box with air at 293.15 K:
///     <code>
///     simulation.EditVoxelBox(min, max, new AtmosVoxelBoxEdit
///     {
///         Gases = [new AtmosGasAmount(airId, 0.04f)],
///         GasTemperatureK = 293.15f
///     });
///     </code>
/// </example>
[PublicAPI]
public readonly record struct AtmosVoxelBoxEdit
{
    /// <summary>
    ///     Gets the classification to assign to every voxel, or <see langword="null" /> to keep the classification.
    /// </summary>
    /// <remarks>
    ///     A solid or void classification removes the gas of the voxel.
    /// </remarks>
    public VoxelClassification? Classification { get; init; }

    /// <summary>
    ///     Gets whether to remove the gas from every air voxel before Numos adds any.
    /// </summary>
    /// <remarks>
    ///     Removal keeps the stored temperature.
    /// </remarks>
    public bool ClearGas { get; init; }

    /// <summary>
    ///     Gets the stored temperature, in kelvins, for every voxel, or <see langword="null" /> to keep it.
    /// </summary>
    /// <remarks>
    ///     The value must be finite and at least 0. Numos refreshes the cached pressure of each voxel.
    /// </remarks>
    public float? TemperatureK { get; init; }

    /// <summary>
    ///     Gets the gases to add to every air voxel, in injection order, or <see langword="null" /> for none.
    /// </summary>
    /// <remarks>
    ///     Each amount must be positive and finite, and each gas ID must be registered. Numos mixes the gas into each
    ///     voxel exactly as <see cref="AtmosSimulation.AddGasToVoxel(AtmosChunkHandle, ushort, int, float, float)" />
    ///     does.
    /// </remarks>
    public IReadOnlyList<AtmosGasAmount>? Gases { get; init; }

    /// <summary>
    ///     Gets the temperature of the added gas, in kelvins.
    /// </summary>
    /// <remarks>
    ///     The default is <see cref="TemperatureK" />, then <see cref="AtmosConfigSnapshot.DefaultTemperatureFallback" />.
    ///     The value must be finite and at least 0.
    /// </remarks>
    public float? GasTemperatureK { get; init; }
}

/// <summary>
///     What <see cref="AtmosSimulation.EditVoxelBox" /> did.
/// </summary>
/// <param name="VoxelsEdited">Voxels of the box inside an existing chunk. Solid voxels count.</param>
/// <param name="VoxelsSkipped">Voxels of the box outside every chunk.</param>
/// <param name="ChunksTouched">Existing chunks that intersect the box.</param>
[PublicAPI]
public readonly record struct AtmosVoxelBoxEditResult(
    long VoxelsEdited,
    long VoxelsSkipped,
    int ChunksTouched);
