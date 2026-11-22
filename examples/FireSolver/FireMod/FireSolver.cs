using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.State;
using Numos.Maths;

namespace FireMod;

/// <summary>
/// Burns voxels that stay hot for long enough.
/// </summary>
/// <remarks>
/// <para>
/// A voxel above <see cref="IgnitionTemperature"/> gains exposure at one second per second. A voxel that cools below it
/// loses all of its exposure. At <see cref="ExposureToBurn"/> seconds the voxel burns. A burn consumes
/// <see cref="OxidizerPerBurn"/> moles of the oxidizer, releases <see cref="HeatOfCombustion"/> joules per mole into the
/// gas that remains, and resets the exposure. A voxel without oxidizer does not burn.
/// </para>
/// <para>
/// A burn never heats the gas past <see cref="FlameTemperature"/>. Near that limit it consumes less oxidizer, and a
/// voxel at or above it does not burn.
/// </para>
/// <para>
/// Each burn spreads to one random face neighbor in the same chunk with probability <see cref="SpreadChance"/>. The
/// neighbor burns at the next run, whatever its temperature. <see cref="Ignite"/> does the same for any cell.
/// </para>
/// </remarks>
[AtmosSolver("fire-mod/fire", After = AtmosBuiltInSolvers.Thermodynamics, RunsPerSecond = 20)]
public sealed partial class FireSolver : IAtmosWorldSolver
{
    /// <summary>
    /// The temperature above which a voxel gains exposure, in kelvins.
    /// </summary>
    [NumosSerialize] public float IgnitionTemperature = 600f;

    /// <summary>
    /// Seconds above <see cref="IgnitionTemperature"/> before a voxel burns.
    /// </summary>
    [NumosSerialize] public float ExposureToBurn = 0.5f;

    /// <summary>
    /// The registered name of the gas that a burn consumes.
    /// </summary>
    [NumosSerialize] public string Oxidizer = "Oxygen";

    /// <summary>
    /// The moles of <see cref="Oxidizer"/> that one burn consumes. A voxel with less burns what it has.
    /// </summary>
    [NumosSerialize] public float OxidizerPerBurn = 0.05f;

    /// <summary>
    /// The heat that a burn releases per mole of <see cref="Oxidizer"/> consumed, in joules per mole.
    /// </summary>
    /// <remarks>
    /// The default is close to methane in air, which releases about 445 kJ per mole of oxygen.
    /// </remarks>
    [NumosSerialize] public float HeatOfCombustion = 445_000f;

    /// <summary>
    /// The temperature that a burn cannot heat gas past, in kelvins.
    /// </summary>
    /// <remarks>
    /// The default is close to methane burning in air, which reaches about 2200 K.
    /// </remarks>
    [NumosSerialize] public float FlameTemperature = 2200f;

    /// <summary>
    /// The chance, from 0 to 1, that a burn spreads to a neighbor.
    /// </summary>
    [NumosSerialize] public float SpreadChance = 0.3f;

    /// <summary>
    /// Cells that burn at the start of the next run, from <see cref="Ignite"/> and from spread.
    /// </summary>
    [NumosSerialize] private readonly List<AtmosCellRef> _pendingIgnitions = [];

    /// <summary>
    /// Spread targets of the current run. They move to <see cref="_pendingIgnitions"/> when the run ends, so a burn
    /// never spreads twice in one run.
    /// </summary>
    [NumosScratch] private readonly List<AtmosCellRef> _spreads = [];

    /// <summary>
    /// Makes a cell burn at the next run of the stage.
    /// </summary>
    /// <param name="cell">A grid voxel. A cell whose chunk does not exist when the stage runs is ignored.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="cell"/> names a standalone gas mixture, or a simulation that the world does not have.
    /// </exception>
    [SolverCommand]
    public partial void Ignite(AtmosCellRef cell);

    // The generator passes the world that holds this instance. A restore can replace the instance with one that the
    // catalog creates, so the solver keeps no reference of its own.
    private void ApplyIgnite(AtmosWorld world, AtmosCellRef cell)
    {
        if (cell.IsGasMixture)
            throw new ArgumentException("A fire needs a grid voxel, not a gas mixture.", nameof(cell));

        if (!world.TryGetSimulation(cell.Simulation, out _))
            throw new ArgumentException("The cell's simulation is not in this world.", nameof(cell));

        _pendingIgnitions.Add(cell);
    }

    /// <inheritdoc/>
    public void Solve(AtmosWorldSolverContext context)
    {
        int oxidizer = context.Config.GasRegistry.TryGetGasIndex(Oxidizer, out int found) ? found : -1;
        if (oxidizer >= 0)
        {
            foreach (AtmosCellRef cell in _pendingIgnitions)
            {
                if (context.TryGetChunk(cell, out AtmosSolverChunk chunk))
                    Burn(context, chunk, chunk.GetFire(), cell.LocalVoxelIndex, oxidizer);
            }
        }

        _pendingIgnitions.Clear();

        foreach (AtmosSimulation simulation in context.Simulations)
        {
            foreach (AtmosSolverChunk chunk in context.Chunks(simulation))
                UpdateExposure(context, chunk, oxidizer);
        }

        _pendingIgnitions.AddRange(_spreads);
        _spreads.Clear();
    }

    private void UpdateExposure(AtmosWorldSolverContext context, AtmosSolverChunk chunk, int oxidizer)
    {
        ReadOnlySpan<float> temperature = chunk.Temperature;
        ReadOnlySpan<float> pressure = chunk.Pressure;

        // A chunk that never got hot keeps no fire storage, so a cold station adds nothing to its checkpoints.
        if (!chunk.TryGetFire(out Fire.View fire))
        {
            if (!AnyAboveIgnition(temperature, pressure))
                return;

            fire = chunk.GetFire();
        }

        for (int voxel = 0; voxel < fire.Exposure.Length; voxel++)
        {
            // A vacuum voxel keeps its last temperature, so the pressure check comes first.
            if (pressure[voxel] <= 0f || temperature[voxel] <= IgnitionTemperature)
            {
                fire.Exposure[voxel] = 0f;
                continue;
            }

            fire.Exposure[voxel] += context.DeltaTime;
            if (oxidizer >= 0 && fire.Exposure[voxel] >= ExposureToBurn)
                Burn(context, chunk, fire, (ushort)voxel, oxidizer);
        }
    }

    private bool AnyAboveIgnition(ReadOnlySpan<float> temperature, ReadOnlySpan<float> pressure)
    {
        for (int voxel = 0; voxel < temperature.Length; voxel++)
        {
            if (pressure[voxel] > 0f && temperature[voxel] > IgnitionTemperature)
                return true;
        }

        return false;
    }

    private void Burn(AtmosWorldSolverContext context, AtmosSolverChunk chunk, Fire.View fire, ushort voxel, int oxidizer)
    {
        ReadOnlySpan<float> available = chunk.Moles(oxidizer);
        if (available.IsEmpty || available[voxel] <= 0f)
            return;

        // Removing n moles of oxidizer takes n * cv of the heat capacity C with it. The released heat goes into the gas
        // that remains, so T' = T + n * H / (C - n * cv). With dT the headroom below the flame temperature, T' reaches
        // that temperature at n = dT * C / (H + dT * cv). There C - n * cv = C * H / (H + dT * cv), so it stays
        // positive. The headroom check comes first, because a dT below -H / cv makes the limit positive again. The
        // cache still holds C from before this stage. Each voxel burns at most once between repairs unless
        // ExposureToBurn is shorter than one run.
        float headroom = FlameTemperature - chunk.Temperature[voxel];
        if (headroom <= 0f)
            return;

        float molarHeatCapacity = context.Config.GasRegistry[oxidizer].MolarHeatCapacityAtConstantVolume;
        float heatCapacity = chunk.HeatCapacity[voxel];
        float limit = headroom * heatCapacity / (HeatOfCombustion + headroom * molarHeatCapacity);
        float consumed = MathF.Min(MathF.Min(OxidizerPerBurn, available[voxel]), limit);
        if (consumed <= 0f)
            return;

        float remainingHeatCapacity = heatCapacity - consumed * molarHeatCapacity;

        AtmosSolverChunkEditor editor = chunk.Edit();
        editor.Moles(oxidizer)[voxel] -= consumed;
        if (remainingHeatCapacity > 0f)
            editor.Temperature[voxel] += consumed * HeatOfCombustion / remainingHeatCapacity;

        fire.Exposure[voxel] = 0f;
        fire.BurnCount[0]++;

        AtmosCellRef cell = chunk.GetCellRef(voxel);
        context.Get<FireLog>(chunk.Simulation).BurntCells.Add(cell);

        bool spread = TrySpread(context, chunk, cell);
        context.Emit(new FireBurned(cell, chunk.Temperature[voxel], spread));
    }

    private bool TrySpread(AtmosWorldSolverContext context, AtmosSolverChunk chunk, AtmosCellRef cell)
    {
        AtmosRandom random = context.Random(SpreadStream(cell));
        if (random.NextSingle() >= SpreadChance)
            return false;

        Int3 position = chunk.GetVoxelPosition(cell.LocalVoxelIndex);
        switch (random.NextInt32(6))
        {
            case 0: position.X--; break;
            case 1: position.X++; break;
            case 2: position.Y--; break;
            case 3: position.Y++; break;
            case 4: position.Z--; break;
            default: position.Z++; break;
        }

        if (!position.IsWithin(chunk.Dimensions))
            return false;

        ushort neighbor = (ushort)chunk.GetVoxelIndex(position);
        int room = chunk.Classification[neighbor];
        if (room == VoxelClassification.RoomSolid || room == VoxelClassification.RoomVoid)
            return false;

        _spreads.Add(chunk.GetCellRef(neighbor));
        return true;
    }

    /// <summary>
    /// Gives each cell its own random stream. Chunk coordinates wrap at 16 bits, so two cells 65536 chunks apart share
    /// a stream.
    /// </summary>
    private static ulong SpreadStream(AtmosCellRef cell)
    {
        Int3 chunk = cell.Chunk.Position;
        return (ulong)(ushort)chunk.X << 48 | (ulong)(ushort)chunk.Y << 32 | (ulong)(ushort)chunk.Z << 16 |
               cell.LocalVoxelIndex;
    }
}
