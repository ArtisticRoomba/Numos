using System.Diagnostics;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     Conserved variables of every voxel in one chunk, structure-of-arrays, with the primitive state derived
///     from them cached per cell.
/// </summary>
/// <remarks>
///     <para>
///         Species rows are indexed by gas id rather than by the chunk's channel index, so two chunks that
///         happen to hold the same gases in different channel order describe a shared face identically once
///         cross-chunk faces exist. A <see langword="null" /> row is a gas this chunk does not hold; present gases
///         are listed ascending in <see cref="EulerChunkWorkspace.PresentGasIds" />, and every mixture sum (mass, moles, heat
///         capacity) runs in that order so both sides of a face agree bit for bit.
///     </para>
///     <para>
///         Energy is double. A warm voxel's total energy is dominated by <c>C·T</c>, and the face-flux differences
///         that move it each substep are many orders of magnitude smaller, so a float accumulator would drop them.
///     </para>
/// </remarks>
internal sealed class EulerChunkState
{
    /// <summary>
    ///     Primitive state per cell: velocities, pressure, sound speed, and signal speed. All zeros for a vacuum
    ///     or trace cell.
    /// </summary>
    public EulerCellState[] Cells = [];

    public Joule64[] Energy = [];
    public JoulePerKelvin[] HeatCapacity = [];
    public Kilogram[] Mass = [];

    /// <summary>
    ///     Fastest signal speed over the cells this buffer holds. Written as a max reduction, so the order cells
    ///     are visited in cannot change it.
    /// </summary>
    public MetrePerSecond MaxSignalSpeed;

    public KilogramMetrePerSecond[] MomentumX = [];
    public KilogramMetrePerSecond[] MomentumY = [];
    public KilogramMetrePerSecond[] MomentumZ = [];

    /// <summary>
    ///     Moles per voxel indexed by gas id; <see langword="null" /> for a gas the chunk does not hold.
    /// </summary>
    public Mole[]?[] Species = [];

    public Mole[] TotalMoles = [];

    /// <summary>
    ///     Makes every array at least <paramref name="voxelCount" /> long and the species table at least
    ///     <paramref name="gasSlotCount" /> wide. Existing rows are kept; the caller clears what it reuses.
    /// </summary>
    internal void EnsureCapacity(int voxelCount, int gasSlotCount)
    {
        if (Cells.Length < voxelCount)
        {
            Cells = new EulerCellState[voxelCount];
            Energy = new Joule64[voxelCount];
            HeatCapacity = new JoulePerKelvin[voxelCount];
            Mass = new Kilogram[voxelCount];
            MomentumX = new KilogramMetrePerSecond[voxelCount];
            MomentumY = new KilogramMetrePerSecond[voxelCount];
            MomentumZ = new KilogramMetrePerSecond[voxelCount];
            TotalMoles = new Mole[voxelCount];
            Species = new Mole[]?[Math.Max(gasSlotCount, Species.Length)];
            return;
        }

        if (Species.Length < gasSlotCount)
            Array.Resize(ref Species, gasSlotCount);
    }

    /// <summary>
    ///     Allocates (or zeroes) the row for one gas.
    /// </summary>
    internal Mole[] ClearRow(int gasId, int voxelCount)
    {
        Mole[]? row = Species[gasId];
        if (row == null || row.Length < voxelCount)
        {
            row = new Mole[voxelCount];
            Species[gasId] = row;
        }
        else
        {
            Array.Clear(row, 0, voxelCount);
        }

        return row;
    }
}

/// <summary>
///     Pooled per-tick scratch for one active chunk of the Euler stage: face masks, the two conserved buffers the
///     sweeps ping-pong between, and the present-gas table shared by both.
/// </summary>
/// <remarks>
///     <para>
///         Instances live in a pool on <see cref="EulerFlowSolver" /> and are rebuilt from the chunk's fields at the
///         start of every tick, so nothing here is checkpointed and nothing survives a tick except the arrays
///         themselves. Every chunk of a kernel has the same dimensions, so a pooled instance fits any of them.
///     </para>
///     <para>
///         The per-gas molar mass and heat capacity are copied out of the configuration snapshot once per tick
///         into arrays parallel to <see cref="PresentGasIds" />, which keeps the per-cell mixture sums down to two
///         array reads per gas.
///     </para>
/// </remarks>
internal sealed class EulerChunkWorkspace
{
    private readonly EulerChunkState[] _buffers = [new EulerChunkState(), new EulerChunkState()];
    private int _readIndex;

    public AtmosChunk Chunk = null!;
    public byte[] OpenMask = [];
    public JoulePerMoleKelvin[] PresentHeatCapacity = [];
    public int PresentGasCount;
    public int[] PresentGasIds = [];
    public KilogramPerMole[] PresentMolarMass = [];
    public byte[] VoidMask = [];

    /// <summary>
    ///     The buffer the current sweep reads. Immutable until <see cref="Swap" />.
    /// </summary>
    public EulerChunkState Read => _buffers[_readIndex];

    /// <summary>
    ///     The buffer the current sweep writes every active cell of.
    /// </summary>
    public EulerChunkState Write => _buffers[_readIndex ^ 1];

    /// <summary>
    ///     Makes the buffer the last sweep wrote the one the next sweep reads.
    /// </summary>
    public void Swap()
    {
        _readIndex ^= 1;
    }

    /// <summary>
    ///     Binds the workspace to a chunk for this tick, sizes the storage, and lists the chunk's gases ascending.
    /// </summary>
    /// <param name="chunk">The awake chunk to solve.</param>
    /// <param name="config">The tick's configuration snapshot.</param>
    internal void Bind(AtmosChunk chunk, AtmosSolverConfigSnapshot config)
    {
        Chunk = chunk;
        _readIndex = 0;
        int voxelCount = chunk.VoxelCount;
        if (OpenMask.Length < voxelCount)
        {
            OpenMask = new byte[voxelCount];
            VoidMask = new byte[voxelCount];
        }

        int gasSlotCount = config.GasPropertyCount;
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            gasSlotCount = Math.Max(gasSlotCount, chunk.ActiveGases[gas].GasId + 1);

        if (PresentGasIds.Length < chunk.ActiveGasCount)
        {
            PresentGasIds = new int[chunk.ActiveGasCount];
            PresentMolarMass = new KilogramPerMole[chunk.ActiveGasCount];
            PresentHeatCapacity = new JoulePerMoleKelvin[chunk.ActiveGasCount];
        }

        PresentGasCount = chunk.ActiveGasCount;
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            PresentGasIds[gas] = chunk.ActiveGases[gas].GasId;

        Array.Sort(PresentGasIds, 0, PresentGasCount);
        for (int present = 0; present < PresentGasCount; present++)
        {
            int gasId = PresentGasIds[present];
            PresentMolarMass[present] = config.GetMolarMass(gasId);
            PresentHeatCapacity[present] = config.GetMolarHeatCapacityAtConstantVolume(gasId);
        }

        foreach (EulerChunkState buffer in _buffers)
        {
            buffer.EnsureCapacity(voxelCount, gasSlotCount);
            buffer.MaxSignalSpeed = 0f;
            // Rows of gases a previous chunk held stay allocated but are never read, because every loop walks
            // PresentGasIds rather than the row table.
            for (int present = 0; present < PresentGasCount; present++)
                buffer.ClearRow(PresentGasIds[present], voxelCount);
        }
    }

    /// <summary>
    ///     Copies the chunk's channels, momentum, and temperature into the read buffer and derives every
    ///     cell's primitive state.
    /// </summary>
    /// <param name="config">The tick's configuration snapshot.</param>
    /// <remarks>
    ///     A voxel with no mass or fewer than <see cref="AtmosSolverConstants.MinimumTrackedMoles" /> moles is a
    ///     trace cell: its momentum is dropped, its energy is <c>C·T</c>, and its primitive state is the vacuum,
    ///     so neighbouring faces see it as empty and only ever push gas into it. It keeps the moles it has.
    /// </remarks>
    internal void Gather(AtmosSolverConfigSnapshot config)
    {
        AtmosChunk chunk = Chunk;
        EulerChunkState buffer = Read;

        for (int present = 0; present < PresentGasCount; present++)
        {
            int channel = chunk.FindGasChannel(PresentGasIds[present]);
            Debug.Assert(channel >= 0);
            Mole[] source = chunk.ActiveGases[channel].Moles;
            Mole[] row = buffer.Species[PresentGasIds[present]]!;
            for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
            {
                ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
                // Same guard as the legacy heat-capacity refresh: a negative or NaN channel entry counts as empty.
                Mole moles = source[voxelIndex];
                row[voxelIndex] = moles > 0f ? moles : 0f;
            }
        }

        MetrePerSecond maxSignalSpeed = 0f;
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            SumMixture(
                buffer, voxelIndex,
                out Kilogram mass, out Mole totalMoles, out JoulePerKelvin heatCapacity);

            Kelvin temperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
            KilogramMetrePerSecond momentumX = chunk.MomentumX[voxelIndex];
            KilogramMetrePerSecond momentumY = chunk.MomentumY[voxelIndex];
            KilogramMetrePerSecond momentumZ = chunk.MomentumZ[voxelIndex];

            if (IsTrace(mass, totalMoles))
            {
                momentumX = 0f;
                momentumY = 0f;
                momentumZ = 0f;
                buffer.Energy[voxelIndex] = (double)heatCapacity * temperature;
                buffer.Cells[voxelIndex] = default;
            }
            else
            {
                Joule64 energy = EulerMixture.TotalEnergy(
                    heatCapacity, temperature, mass,
                    momentumX, momentumY, momentumZ);

                buffer.Energy[voxelIndex] = energy;
                if (EulerMixture.ToPrimitive(
                        mass, totalMoles, heatCapacity,
                        momentumX, momentumY, momentumZ,
                        energy, config.VoxelVolume, config.PressurePerMoleKelvin,
                        out buffer.Cells[voxelIndex]))
                {
                    maxSignalSpeed = MathF.Max(maxSignalSpeed, buffer.Cells[voxelIndex].SignalSpeed);
                }
                else
                {
                    momentumX = 0f;
                    momentumY = 0f;
                    momentumZ = 0f;
                }
            }

            buffer.MomentumX[voxelIndex] = momentumX;
            buffer.MomentumY[voxelIndex] = momentumY;
            buffer.MomentumZ[voxelIndex] = momentumZ;
            buffer.Mass[voxelIndex] = mass;
            buffer.TotalMoles[voxelIndex] = totalMoles;
            buffer.HeatCapacity[voxelIndex] = heatCapacity;
        }

        buffer.MaxSignalSpeed = maxSignalSpeed;
    }

    /// <summary>
    ///     Mixture totals of one cell in ascending gas id order.
    /// </summary>
    internal void SumMixture(
        EulerChunkState buffer, int voxelIndex,
        out Kilogram mass, out Mole totalMoles, out JoulePerKelvin heatCapacity)
    {
        mass = 0f;
        totalMoles = 0f;
        heatCapacity = 0f;
        for (int present = 0; present < PresentGasCount; present++)
        {
            Mole moles = buffer.Species[PresentGasIds[present]]![voxelIndex];
            mass += moles * PresentMolarMass[present];
            totalMoles += moles;
            heatCapacity += moles * PresentHeatCapacity[present];
        }
    }

    /// <summary>
    ///     Whether a cell holds too little gas to carry momentum or a primitive state of its own.
    /// </summary>
    internal static bool IsTrace(Kilogram mass, Mole totalMoles)
    {
        return mass <= 0f || totalMoles < AtmosSolverConstants.MinimumTrackedMoles;
    }

    /// <summary>
    ///     Writes the read buffer back to the chunk: channels, momentum, temperature, heat capacity, pressure, and
    ///     the vacuum flag.
    /// </summary>
    /// <param name="config">The tick's configuration snapshot.</param>
    /// <param name="negativeInternalEnergyCount">Incremented for every voxel written at 0 K because <c>E &lt; K</c>.</param>
    /// <remarks>
    ///     <para>
    ///         The sweeps summed the mixture in ascending gas id, but the heat capacity and total moles written
    ///         here are summed in the chunk's channel order, because that is the order every other stage and
    ///         <see cref="AdvectionSolver.RefreshPressureAndHeatCapacity" /> use, and the cached values must match
    ///         what a later refresh would produce bit for bit.
    ///     </para>
    ///     <para>
    ///         The temperature is <c>(E − K)/C</c> with <c>K</c> from the momentum as it stands in the buffer, so
    ///         whatever the caller did to the momentum first (velocity floor, damping) ends up as heat rather than
    ///         disappearing. Pressure goes through
    ///         <see cref="AtmosSolverMath.CalculatePressureAtVoxel(IAtmosConfig, AtmosChunk, ushort, Mole)" /> so
    ///         <see cref="AtmosChunk.IsVacuum" /> stays consistent with the rest of the kernel.
    ///     </para>
    /// </remarks>
    internal void WriteBack(AtmosSolverConfigSnapshot config, ref int negativeInternalEnergyCount)
    {
        AtmosChunk chunk = Chunk;
        EulerChunkState buffer = Read;

        for (int present = 0; present < PresentGasCount; present++)
        {
            int gasId = PresentGasIds[present];
            Mole[] row = buffer.Species[gasId]!;
            int channel = chunk.FindGasChannel(gasId);
            if (channel < 0)
            {
                // Only worth a channel once a voxel holds a tracked amount; a chunk that is handed a few
                // molecules of something new should not grow a 4 KB row for them.
                if (!HoldsTrackedAmount(chunk, row))
                    continue;

                channel = chunk.GetOrCreateGasChannel(gasId);
            }

            Mole[] target = chunk.ActiveGases[channel].Moles;
            for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
            {
                ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
                target[voxelIndex] = row[voxelIndex];
            }
        }

        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Mole totalMoles = 0f;
            JoulePerKelvin heatCapacity = 0f;
            for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            {
                Mole moles = chunk.ActiveGases[gas].Moles[voxelIndex];
                if (moles <= 0f)
                    continue;

                totalMoles += moles;
                heatCapacity += moles * config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);
            }

            if (totalMoles <= 0f)
            {
                // Clears the channels, the heat capacity, and the momentum, and raises IsVacuum.
                chunk.TotalPressure[voxelIndex] = AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex, 0f);
                continue;
            }

            KilogramMetrePerSecond momentumX = buffer.MomentumX[voxelIndex];
            KilogramMetrePerSecond momentumY = buffer.MomentumY[voxelIndex];
            KilogramMetrePerSecond momentumZ = buffer.MomentumZ[voxelIndex];
            Kilogram mass = buffer.Mass[voxelIndex];
            Joule64 internalEnergy = buffer.Energy[voxelIndex];
            if (mass > 0f)
                internalEnergy -= EulerMixture.KineticEnergy(mass, momentumX, momentumY, momentumZ);

            if (internalEnergy < 0d)
            {
                negativeInternalEnergyCount++;
                internalEnergy = 0d;
            }

            Kelvin rawTemperature = heatCapacity > 0f
                ? (float)(internalEnergy / heatCapacity)
                : chunk.Temperature[voxelIndex];

            Kelvin temperature = config.GetValidatedTemp(rawTemperature);

            Debug.Assert(float.IsFinite(temperature));
            Debug.Assert(
                float.IsFinite(momentumX) && float.IsFinite(momentumY) && float.IsFinite(momentumZ),
                "The Euler stage must never write non-finite momentum.");

            chunk.Temperature[voxelIndex] = temperature;
            chunk.TotalHeatCapacity[voxelIndex] = heatCapacity;
            chunk.TotalPressure[voxelIndex] =
                AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex, totalMoles);

            chunk.MomentumX[voxelIndex] = momentumX;
            chunk.MomentumY[voxelIndex] = momentumY;
            chunk.MomentumZ[voxelIndex] = momentumZ;
        }
    }

    private static bool HoldsTrackedAmount(AtmosChunk chunk, Mole[] row)
    {
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            if (row[chunk.ActiveAirIndices[activeIndex]] >= AtmosSolverConstants.MinimumTrackedMoles)
                return true;
        }

        return false;
    }
}
