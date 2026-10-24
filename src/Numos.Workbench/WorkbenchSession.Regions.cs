using System.Globalization;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.Workbench;

/// <summary>
///     One gas to add to every voxel of a region.
/// </summary>
/// <remarks>
///     Set exactly one of <paramref name="Moles" /> and <paramref name="PartialPressurePa" />.
/// </remarks>
/// <param name="GasId">A registered gas id.</param>
/// <param name="Moles">Moles added to each voxel.</param>
/// <param name="PartialPressurePa">
///     The partial pressure in pascals that the gas has in each voxel. The session converts it to moles with the ideal-gas
///     law `n = pV / (RT)`, using the configured voxel volume and the gas temperature.
/// </param>
public readonly record struct RegionGas(int GasId, float? Moles = null, float? PartialPressurePa = null);

/// <summary>
///     The changes <see cref="WorkbenchSession.EditRegion" /> applies to a box of voxels.
/// </summary>
/// <remarks>
///     The session applies the changes in this order: classification, gas removal, temperature, gas injection.
/// </remarks>
/// <param name="CreateChunks">Whether to create missing chunks inside the box, with classification 0, first.</param>
/// <param name="Classification">The classification for every voxel, or <see langword="null" /> to keep it.</param>
/// <param name="ClearGas">Whether to remove the gas from every voxel before anything is added.</param>
/// <param name="TemperatureK">The stored temperature for every voxel, or <see langword="null" /> to keep it.</param>
/// <param name="Gases">The gases to add to every voxel.</param>
/// <param name="GasTemperatureK">
///     The temperature of the added gas. It defaults to <paramref name="TemperatureK" />, then to
///     <see cref="AtmosConfig.DefaultTemperatureFallback" />.
/// </param>
public sealed record RegionEdit(
    bool CreateChunks = false,
    int? Classification = null,
    bool ClearGas = false,
    float? TemperatureK = null,
    IReadOnlyList<RegionGas>? Gases = null,
    float? GasTemperatureK = null);

/// <summary>
///     What <see cref="WorkbenchSession.EditRegion" /> did.
/// </summary>
/// <param name="VoxelsEdited">Voxels inside the box and inside a chunk. Solid voxels ignore gas but still count.</param>
/// <param name="VoxelsSkipped">Voxels inside the box but outside every chunk.</param>
/// <param name="ChunksTouched">Chunks that hold edited voxels.</param>
/// <param name="ChunksCreated">Chunks the edit created.</param>
/// <param name="MolesPerVoxel">The moles added to each voxel for each entry of <see cref="RegionEdit.Gases" />.</param>
public sealed record RegionEditResult(
    long VoxelsEdited,
    long VoxelsSkipped,
    int ChunksTouched,
    int ChunksCreated,
    IReadOnlyList<float> MolesPerVoxel);

public sealed partial class WorkbenchSession
{
    private const int MaximumCreatedChunksPerEdit = 1_000_000;

    /// <summary>
    ///     Converts a partial pressure to moles for one voxel with the ideal-gas law.
    /// </summary>
    /// <remarks>
    ///     The result is `n = pV / (RT)`. The session reads `V` from <see cref="AtmosConfig.VoxelVolume" /> through
    ///     <see cref="AtmosConfig.PressurePerMoleKelvin" />, which equals `R / V`.
    /// </remarks>
    /// <param name="partialPressurePa">The partial pressure in pascals. It must be finite and not negative.</param>
    /// <param name="temperatureK">The gas temperature in kelvins. It must be finite and positive.</param>
    /// <returns>The moles in one voxel.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is outside its range.</exception>
    public float PartialPressureToMoles(float partialPressurePa, float temperatureK)
    {
        if (!float.IsFinite(partialPressurePa) || partialPressurePa < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(partialPressurePa),
                "The partial pressure must be finite and at least 0 Pa.");
        }

        if (!float.IsFinite(temperatureK) || temperatureK <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(temperatureK),
                "The gas temperature must be finite and above 0 K.");
        }

        return partialPressurePa / (RequireConfig().PressurePerMoleKelvin * temperatureK);
    }

    /// <summary>
    ///     Gets the global box that covers every chunk of a simulation.
    /// </summary>
    /// <param name="simulation">The simulation, or <see langword="null" /> for the active one.</param>
    /// <returns>The box, or <see langword="null" /> when the simulation has no chunks.</returns>
    public VoxelBox? GetChunkBounds(AtmosSimulationId? simulation = null)
    {
        var target = RequireSimulation(simulation);
        var handles = target.GetChunkHandles();
        if (handles.Length == 0)
            return null;

        var dimensions = target.ChunkDimensions;
        var min = handles[0].Position;
        var max = min;
        foreach (var handle in handles)
        {
            var position = handle.Position;
            min = new Int3(Math.Min(min.X, position.X), Math.Min(min.Y, position.Y), Math.Min(min.Z, position.Z));
            max = new Int3(Math.Max(max.X, position.X), Math.Max(max.Y, position.Y), Math.Max(max.Z, position.Z));
        }

        return new VoxelBox(
            new Int3(min.X * dimensions.X, min.Y * dimensions.Y, min.Z * dimensions.Z),
            new Int3(
                (max.X + 1) * dimensions.X - 1,
                (max.Y + 1) * dimensions.Y - 1,
                (max.Z + 1) * dimensions.Z - 1));
    }

    /// <summary>
    ///     Edits every voxel of a global box.
    /// </summary>
    /// <param name="box">The inclusive box in global voxel coordinates.</param>
    /// <param name="edit">The changes to apply.</param>
    /// <param name="simulation">The target simulation, or <see langword="null" /> for the active one.</param>
    /// <returns>Counts of edited and skipped voxels.</returns>
    /// <remarks>
    ///     <para>
    ///         The edit visits one chunk at a time. A classification that covers a whole chunk uses one chunk-wide
    ///         call, because a per-voxel classification call rebuilds the chunk's active topology each time.
    ///     </para>
    ///     <para>
    ///         Every voxel edit is a recorded operation, so a large region adds one operation per voxel and field to the
    ///         replay. The edit writes one summary line to the log instead of one line per voxel.
    ///     </para>
    ///     <para>
    ///         The edit stops at the first failure. Chunks edited before it keep the new values.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The box, a gas, a temperature, or a pressure is invalid.</exception>
    /// <exception cref="InvalidOperationException">Edits are blocked, or the edit would create too many chunks.</exception>
    public RegionEditResult EditRegion(VoxelBox box, RegionEdit edit, AtmosSimulationId? simulation = null)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return Guard(
            "Could not edit the region",
            () =>
            {
                ThrowIfInspecting();
                var target = RequireSimulation(simulation);
                var config = RequireConfig();
                box.ThrowIfInvalid();
                var plan = PlanRegionGases(edit, config);
                var dimensions = target.ChunkDimensions;
                var minChunk = VoxelBox.SplitGlobal(box.Min, dimensions, out _);
                var maxChunk = VoxelBox.SplitGlobal(box.Max, dimensions, out _);
                long chunkCount = (long)(maxChunk.X - minChunk.X + 1) *
                                  (maxChunk.Y - minChunk.Y + 1) *
                                  (maxChunk.Z - minChunk.Z + 1);

                if (edit.CreateChunks && chunkCount > MaximumCreatedChunksPerEdit)
                {
                    throw new InvalidOperationException(
                        $"The region spans {chunkCount} chunks. Creating more than {MaximumCreatedChunksPerEdit} " +
                        "chunks in one edit is not allowed.");
                }

                var existing = new HashSet<Int3>();
                foreach (var handle in target.GetChunkHandles())
                    existing.Add(handle.Position);

                long edited = 0;
                long skipped = 0;
                int touched = 0;
                int created = 0;
                for (int cz = minChunk.Z; cz <= maxChunk.Z; cz++)
                for (int cy = minChunk.Y; cy <= maxChunk.Y; cy++)
                for (int cx = minChunk.X; cx <= maxChunk.X; cx++)
                {
                    var position = new Int3(cx, cy, cz);
                    int x0 = Math.Max(box.Min.X, cx * dimensions.X) - cx * dimensions.X;
                    int x1 = Math.Min(box.Max.X, (cx + 1) * dimensions.X - 1) - cx * dimensions.X;
                    int y0 = Math.Max(box.Min.Y, cy * dimensions.Y) - cy * dimensions.Y;
                    int y1 = Math.Min(box.Max.Y, (cy + 1) * dimensions.Y - 1) - cy * dimensions.Y;
                    int z0 = Math.Max(box.Min.Z, cz * dimensions.Z) - cz * dimensions.Z;
                    int z1 = Math.Min(box.Max.Z, (cz + 1) * dimensions.Z - 1) - cz * dimensions.Z;
                    long voxels = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
                    bool isNew = false;
                    if (!existing.Contains(position))
                    {
                        if (!edit.CreateChunks)
                        {
                            skipped += voxels;
                            continue;
                        }

                        var newChunk = target.CreateAndRegisterChunk(position);
                        target.SetChunkClassification(newChunk, new VoxelClassification(0));
                        existing.Add(position);
                        created++;
                        isNew = true;
                    }

                    var chunk = new AtmosChunkHandle(position);
                    bool wholeChunk = x0 == 0 && x1 == dimensions.X - 1 &&
                                      y0 == 0 && y1 == dimensions.Y - 1 &&
                                      z0 == 0 && z1 == dimensions.Z - 1;

                    if (edit.Classification is { } classification)
                    {
                        var value = new VoxelClassification(classification);
                        if (wholeChunk)
                        {
                            target.SetChunkClassification(chunk, value);
                        }
                        else
                        {
                            ForEachVoxel(dimensions, x0, x1, y0, y1, z0, z1,
                                index => target.SetVoxelClassification(chunk, index, value));
                        }
                    }

                    if (edit.ClearGas && !isNew)
                    {
                        ForEachVoxel(dimensions, x0, x1, y0, y1, z0, z1,
                            index => target.GetVoxelGasMixture(chunk, index).Clear());
                    }

                    if (edit.TemperatureK is { } temperature)
                    {
                        ForEachVoxel(dimensions, x0, x1, y0, y1, z0, z1,
                            index => target.SetVoxelTemperature(chunk, index, temperature));
                    }

                    foreach (var (gasId, moles) in plan.Gases)
                    {
                        if (moles <= 0f)
                            continue;

                        float gasTemperature = plan.GasTemperature;
                        ForEachVoxel(dimensions, x0, x1, y0, y1, z0, z1,
                            index => target.AddGasToVoxel(chunk, index, gasId, moles, gasTemperature));
                    }

                    edited += voxels;
                    touched++;
                }

                var result = new RegionEditResult(
                    edited,
                    skipped,
                    touched,
                    created,
                    plan.Gases.Select(static gas => gas.Moles).ToArray());

                Info(DescribeRegionEdit(box, edit, result));
                return result;
            });
    }

    private static void ForEachVoxel(
        Int3 dimensions,
        int x0,
        int x1,
        int y0,
        int y1,
        int z0,
        int z1,
        Action<ushort> action)
    {
        for (int z = z0; z <= z1; z++)
        for (int y = y0; y <= y1; y++)
        {
            int row = dimensions.X * (y + dimensions.Y * z);
            for (int x = x0; x <= x1; x++)
                action(checked((ushort)(row + x)));
        }
    }

    private readonly record struct RegionGasPlan((int GasId, float Moles)[] Gases, float GasTemperature);

    private RegionGasPlan PlanRegionGases(RegionEdit edit, AtmosConfig config)
    {
        if (edit.TemperatureK is { } temperature && (!float.IsFinite(temperature) || temperature < 0f))
            throw new ArgumentException("temperatureK must be finite and at least 0.");

        float gasTemperature = edit.GasTemperatureK ?? edit.TemperatureK ?? config.DefaultTemperatureFallback;
        var gases = edit.Gases ?? [];
        var planned = new (int GasId, float Moles)[gases.Count];
        for (int index = 0; index < planned.Length; index++)
        {
            var gas = gases[index];
            if (gas.GasId < 0 || gas.GasId >= config.GasRegistry.Count)
                throw new ArgumentException($"No gas is registered with ID {gas.GasId}.");

            if (gas.Moles.HasValue == gas.PartialPressurePa.HasValue)
            {
                throw new ArgumentException(
                    $"Gas {gas.GasId} needs exactly one of moles and partialPressurePa.");
            }

            float moles;
            if (gas.Moles is { } explicitMoles)
            {
                if (!float.IsFinite(explicitMoles) || explicitMoles < 0f)
                    throw new ArgumentException($"Gas {gas.GasId} moles must be finite and at least 0.");

                moles = explicitMoles;
            }
            else
            {
                moles = PartialPressureToMoles(gas.PartialPressurePa!.Value, gasTemperature);
            }

            planned[index] = (gas.GasId, moles);
        }

        if (planned.Length > 0 && (!float.IsFinite(gasTemperature) || gasTemperature < 0f))
            throw new ArgumentException("gasTemperatureK must be finite and at least 0.");

        return new RegionGasPlan(planned, gasTemperature);
    }

    private static string DescribeRegionEdit(VoxelBox box, RegionEdit edit, RegionEditResult result)
    {
        var parts = new List<string>();
        if (edit.Classification is { } classification)
            parts.Add($"classification {classification}");

        if (edit.ClearGas)
            parts.Add("cleared gas");

        if (edit.TemperatureK is { } temperature)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"temperature {temperature:G6} K"));

        if (edit.Gases is { Count: > 0 } gases)
            parts.Add($"{gases.Count} gas{(gases.Count == 1 ? string.Empty : "es")}");

        string what = parts.Count == 0 ? "no changes" : string.Join(", ", parts);
        string text = string.Create(
            CultureInfo.InvariantCulture,
            $"Edited region ({box.Min.X}, {box.Min.Y}, {box.Min.Z}) to ({box.Max.X}, {box.Max.Y}, {box.Max.Z}): " +
            $"{what}, {result.VoxelsEdited} voxels in {result.ChunksTouched} chunks");

        if (result.ChunksCreated > 0)
            text += $", {result.ChunksCreated} chunks created";

        if (result.VoxelsSkipped > 0)
            text += $", {result.VoxelsSkipped} voxels skipped outside chunks";

        return text + ".";
    }
}
