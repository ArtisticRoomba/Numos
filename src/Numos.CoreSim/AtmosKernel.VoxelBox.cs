using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim;

/// <summary>
///     What a box edit did, counted over the chunks that exist.
/// </summary>
/// <param name="VoxelsEdited">Voxels of the box that lie inside an existing chunk. Solid voxels count.</param>
/// <param name="VoxelsSkipped">Voxels of the box that lie outside every chunk.</param>
/// <param name="ChunksTouched">Existing chunks that intersect the box.</param>
/// <param name="VelocityVoxels">Voxels that received momentum.</param>
internal readonly record struct VoxelBoxEditCounts(
    long VoxelsEdited,
    long VoxelsSkipped,
    int ChunksTouched,
    long VelocityVoxels);

internal sealed partial class AtmosKernel
{
    /// <summary>
    ///     Edits a box of voxels in global coordinates and records one operation.
    /// </summary>
    /// <param name="op">The edit. The kernel validates all of it before it changes any state.</param>
    /// <returns>The voxel and chunk counts of the edit.</returns>
    /// <remarks>
    ///     <para>
    ///         Each existing chunk gets the same sequence that the per-voxel calls would give. The sequence is
    ///         classification, gas removal, temperature, gas injection, velocity. The kernel wakes a chunk once, and
    ///         only when a per-voxel call would have woken it.
    ///     </para>
    ///     <para>
    ///         Gas removal and injection skip solid and void voxels. The per-voxel removal call throws for them.
    ///         Velocity skips a voxel without gas, and a voxel whose momentum would not be finite.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The box, a gas, a temperature, or a velocity is invalid.</exception>
    internal VoxelBoxEditCounts EditVoxelBox(EditVoxelBoxOperation op)
    {
        ArgumentNullException.ThrowIfNull(op);
        lock (StateGate)
        {
            ValidateVoxelBoxEdit(op);
            var chunks = CollectBoxChunks(op.Min, op.Max);
            long edited = 0;
            long velocityVoxels = 0;
            foreach (var chunk in chunks)
                edited += ApplyVoxelBoxToChunk(chunk, op, ref velocityVoxels);

            Int128 total = (Int128)((long)op.Max.X - op.Min.X + 1) *
                           ((long)op.Max.Y - op.Min.Y + 1) *
                           ((long)op.Max.Z - op.Min.Z + 1);

            Int128 skipped = total - edited;
            var counts = new VoxelBoxEditCounts(
                edited,
                skipped > long.MaxValue ? long.MaxValue : (long)skipped,
                chunks.Length,
                velocityVoxels);

            if (ShouldRecord) RecordOperation(op);
            return counts;
        }
    }

    private void ValidateVoxelBoxEdit(EditVoxelBoxOperation op)
    {
        ArgumentNullException.ThrowIfNull(op.Gases);
        const AtmosVoxelBoxParts known = AtmosVoxelBoxParts.Classification | AtmosVoxelBoxParts.ClearGas |
                                         AtmosVoxelBoxParts.Temperature | AtmosVoxelBoxParts.Velocity;

        if ((op.Parts & ~known) != 0)
            throw new ArgumentException("The voxel box selects an unknown part.", nameof(op));

        if (op.Min.X > op.Max.X || op.Min.Y > op.Max.Y || op.Min.Z > op.Max.Z)
            throw new ArgumentException("The voxel box minimum must not exceed its maximum.", nameof(op));

        if (op.Parts.HasFlag(AtmosVoxelBoxParts.Temperature) &&
            (!float.IsFinite(op.VoxelTemperature) || op.VoxelTemperature < 0f))
        {
            throw new ArgumentException("The voxel temperature must be finite and at least 0 K.", nameof(op));
        }

        if (op.Gases.Count > 0 && (!float.IsFinite(op.GasTemperature) || op.GasTemperature < 0f))
            throw new ArgumentException("The gas temperature must be finite and at least 0 K.", nameof(op));

        foreach (var gas in op.Gases)
        {
            if ((uint)gas.GasId >= (uint)_config.GasRegistry.Count)
                throw new ArgumentException($"No gas is registered with ID {gas.GasId}.", nameof(op));

            if (!float.IsFinite(gas.Moles) || gas.Moles <= 0f)
                throw new ArgumentException($"Gas {gas.GasId} moles must be positive and finite.", nameof(op));
        }

        if (op.Parts.HasFlag(AtmosVoxelBoxParts.Velocity) &&
            (!float.IsFinite(op.VelocityX) || !float.IsFinite(op.VelocityY) || !float.IsFinite(op.VelocityZ)))
        {
            throw new ArgumentException("The velocity components must be finite.", nameof(op));
        }
    }

    private static int FloorDivide(int value, int divisor)
    {
        int quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }

    /// <summary>
    ///     Returns the existing chunks that intersect a global box, in z, y, x order of grid position.
    /// </summary>
    /// <remarks>
    ///     The method looks up each position of the box when that is cheaper than a scan of the chunk map. A huge
    ///     box over a small world scans the map instead.
    /// </remarks>
    private AtmosChunk[] CollectBoxChunks(Int3 min, Int3 max)
    {
        var minChunk = new Int3(
            FloorDivide(min.X, _dimensions.X),
            FloorDivide(min.Y, _dimensions.Y),
            FloorDivide(min.Z, _dimensions.Z));

        var maxChunk = new Int3(
            FloorDivide(max.X, _dimensions.X),
            FloorDivide(max.Y, _dimensions.Y),
            FloorDivide(max.Z, _dimensions.Z));

        Int128 range = (Int128)((long)maxChunk.X - minChunk.X + 1) *
                       ((long)maxChunk.Y - minChunk.Y + 1) *
                       ((long)maxChunk.Z - minChunk.Z + 1);

        var found = new List<AtmosChunk>();
        if (range <= _chunkMap.Count)
        {
            for (int z = minChunk.Z; z <= maxChunk.Z; z++)
            for (int y = minChunk.Y; y <= maxChunk.Y; y++)
            for (int x = minChunk.X; x <= maxChunk.X; x++)
            {
                if (_chunkMap.TryGetValue(new Int3(x, y, z), out var chunk))
                    found.Add(chunk);
            }

            return found.ToArray();
        }

        foreach (var chunk in _chunkMap.Values)
        {
            var position = chunk.GridPosition;
            if (position.X >= minChunk.X && position.X <= maxChunk.X &&
                position.Y >= minChunk.Y && position.Y <= maxChunk.Y &&
                position.Z >= minChunk.Z && position.Z <= maxChunk.Z)
            {
                found.Add(chunk);
            }
        }

        var chunks = found.ToArray();
        Array.Sort(
            chunks,
            static (first, second) =>
            {
                int order = first.GridPosition.Z.CompareTo(second.GridPosition.Z);
                if (order == 0)
                    order = first.GridPosition.Y.CompareTo(second.GridPosition.Y);

                return order != 0 ? order : first.GridPosition.X.CompareTo(second.GridPosition.X);
            });

        return chunks;
    }

    private long ApplyVoxelBoxToChunk(AtmosChunk chunk, EditVoxelBoxOperation op, ref long velocityVoxels)
    {
        var dimensions = chunk.Dimensions;
        long originX = (long)chunk.GridPosition.X * dimensions.X;
        long originY = (long)chunk.GridPosition.Y * dimensions.Y;
        long originZ = (long)chunk.GridPosition.Z * dimensions.Z;
        int x0 = (int)(Math.Max(op.Min.X, originX) - originX);
        int x1 = (int)(Math.Min(op.Max.X, originX + dimensions.X - 1) - originX);
        int y0 = (int)(Math.Max(op.Min.Y, originY) - originY);
        int y1 = (int)(Math.Min(op.Max.Y, originY + dimensions.Y - 1) - originY);
        int z0 = (int)(Math.Max(op.Min.Z, originZ) - originZ);
        int z1 = (int)(Math.Min(op.Max.Z, originZ + dimensions.Z - 1) - originZ);
        long voxels = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
        bool awake = false;

        if (op.Parts.HasFlag(AtmosVoxelBoxParts.Classification))
        {
            bool wholeChunk = x0 == 0 && x1 == dimensions.X - 1 &&
                              y0 == 0 && y1 == dimensions.Y - 1 &&
                              z0 == 0 && z1 == dimensions.Z - 1;

            if (wholeChunk)
            {
                chunk.SetChunkClassification(op.Classification);
            }
            else
            {
                for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                {
                    int row = dimensions.X * (y + dimensions.Y * z);
                    for (int x = x0; x <= x1; x++)
                        chunk.SetVoxelClassification((ushort)(row + x), op.Classification);
                }
            }

            RebuildActiveTopology(chunk);
            chunk.MarkChanged();
        }

        if (op.Parts.HasFlag(AtmosVoxelBoxParts.ClearGas))
        {
            for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
            {
                int row = dimensions.X * (y + dimensions.Y * z);
                for (int x = x0; x <= x1; x++)
                {
                    var index = (ushort)(row + x);
                    if (IsSolidOrVoid(chunk, index))
                        continue;

                    WakeOnce(chunk, ref awake);
                    for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
                        chunk.ActiveGases[gas].Moles[index] = 0f;

                    ApplyVoxelMixtureTotals(chunk, index, default);
                }
            }
        }

        if (op.Parts.HasFlag(AtmosVoxelBoxParts.Temperature))
        {
            for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
            {
                int row = dimensions.X * (y + dimensions.Y * z);
                for (int x = x0; x <= x1; x++)
                {
                    var index = (ushort)(row + x);
                    chunk.Temperature[index] = op.VoxelTemperature;
                    chunk.TotalPressure[index] = AtmosSolverMath.CalculatePressureAtVoxel(_config, chunk, index);
                }
            }

            chunk.MarkChanged();
        }

        foreach (var gas in op.Gases)
        {
            for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
            {
                int row = dimensions.X * (y + dimensions.Y * z);
                for (int x = x0; x <= x1; x++)
                {
                    var index = (ushort)(row + x);
                    if (IsSolidOrVoid(chunk, index))
                        continue;

                    WakeOnce(chunk, ref awake);
                    GasInjectionSolver.Inject(chunk, index, gas.GasId, gas.Moles, op.GasTemperature, _config);
                }
            }
        }

        if (op.Parts.HasFlag(AtmosVoxelBoxParts.Velocity))
        {
            CubicMetre volume = _config.GetVoxelVolume();
            Span<KilogramPerMole> molarMasses = chunk.ActiveGasCount <= AtmosSolverMath.MolarMassStackLimit
                ? stackalloc KilogramPerMole[AtmosSolverMath.MolarMassStackLimit]
                : new KilogramPerMole[chunk.ActiveGasCount];

            AtmosSolverMath.ResolveMolarMasses(_config, chunk, molarMasses);
            for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
            {
                int row = dimensions.X * (y + dimensions.Y * z);
                for (int x = x0; x <= x1; x++)
                {
                    var index = (ushort)(row + x);
                    if (IsSolidOrVoid(chunk, index) || chunk.TotalPressure[index] <= 0f)
                        continue;

                    // Same expression as FlowFieldMath.GetDensity times the voxel volume, so the result matches the
                    // per-voxel path bit for bit.
                    Kilogram gasMass = 0f;
                    for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
                    {
                        Mole moles = chunk.ActiveGases[gas].Moles[index];
                        if (float.IsFinite(moles) && moles > 0f)
                            gasMass += moles * molarMasses[gas];
                    }

                    KilogramPerCubicMetre density = gasMass / volume;
                    Kilogram mass = density * volume;
                    if (!(mass > 0f))
                        continue;

                    KilogramMetrePerSecond momentumX = mass * op.VelocityX;
                    KilogramMetrePerSecond momentumY = mass * op.VelocityY;
                    KilogramMetrePerSecond momentumZ = mass * op.VelocityZ;
                    if (!float.IsFinite(momentumX) || !float.IsFinite(momentumY) || !float.IsFinite(momentumZ))
                        continue;

                    WakeOnce(chunk, ref awake);
                    chunk.MomentumX[index] = momentumX;
                    chunk.MomentumY[index] = momentumY;
                    chunk.MomentumZ[index] = momentumZ;
                    velocityVoxels++;
                }
            }
        }

        chunk.MarkChanged();
        return voxels;
    }

    private static bool IsSolidOrVoid(AtmosChunk chunk, ushort index)
    {
        int classification = chunk.VoxelRoomMap[index];
        return classification == VoxelClassification.RoomSolid || classification == VoxelClassification.RoomVoid;
    }

    private static void WakeOnce(AtmosChunk chunk, ref bool awake)
    {
        if (awake)
            return;

        chunk.Wake();
        awake = true;
    }
}
