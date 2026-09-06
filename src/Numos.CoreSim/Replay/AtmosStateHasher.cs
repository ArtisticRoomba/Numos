using Numos.Maths;

namespace Numos.CoreSim.Replay;

// Built-in primitive writes use little endian and raw IEEE bits. Solver values retain their native byte layout.
// Never hash CLR identities or pool capacity.
internal struct AtmosStateHasher
{
    public AtmosStateHasher()
    {
        Value = 14695981039346656037UL;
    }

    internal ulong Value { get; private set; }

    internal void AddByte(byte value)
    {
        Value = unchecked((Value ^ value) * 1099511628211UL);
    }

    internal void Add(bool value)
    {
        AddByte(value ? (byte)1 : (byte)0);
    }

    internal void Add(float value)
    {
        Add(BitConverter.SingleToInt32Bits(value));
    }

    internal void Add(int value)
    {
        uint bits = unchecked((uint)value);
        for (int shift = 0; shift < 32; shift += 8)
            AddByte((byte)(bits >> shift));
    }

    internal void Add(ulong value)
    {
        for (int shift = 0; shift < 64; shift += 8)
            AddByte((byte)(value >> shift));
    }

    internal void Add(string? value)
    {
        Add(value?.Length ?? -1);
        if (value == null)
            return;

        foreach (char character in value)
        {
            AddByte((byte)character);
            AddByte((byte)(character >> 8));
        }
    }

    internal void Add(Int3 value)
    {
        Add(value.X);
        Add(value.Y);
        Add(value.Z);
    }

    internal void Add(GasProperties gas)
    {
        Add(gas.Name);
        Add(gas.MolarHeatCapacityAtConstantVolume);
        Add(gas.BoilingPoint);
        Add(gas.CondensationEnabled);
        Add(gas.MolarEnthalpyOfVaporization);
        Add(gas.LiquidId);
        Add(gas.DiffusionCoefficient);
    }

    internal static ulong HashDefinition(AtmosSimulationCheckpoint checkpoint)
    {
        var hash = new AtmosStateHasher();
        hash.Add(checkpoint.CompatibilityVersion);
        hash.Add(checkpoint.Dimensions);
        hash.Add(checkpoint.Solvers.Count);
        foreach (var solver in checkpoint.Solvers)
        {
            hash.Add(solver.Name);
            hash.Add(solver.IsCustom);
        }

        return hash.Value;
    }

    internal static AtmosStateHash Hash(AtmosSimulationCheckpoint checkpoint)
    {
        var hash = new AtmosStateHasher();
        hash.Add(checkpoint.FormatVersion);
        hash.Add(checkpoint.CompatibilityFingerprint);
        hash.Add(checkpoint.Position.Tick);
        hash.Add(checkpoint.Position.OperationSequence);
        hash.Add(checkpoint.ElapsedAccumulator);
        checkpoint.Config.AppendHash(ref hash);
        foreach (var solver in checkpoint.Solvers)
            hash.Add(solver.Enabled);

        hash.Add(checkpoint.Chunks.Count);
        foreach (var chunk in checkpoint.Chunks)
        {
            hash.Add(chunk.Position);
            hash.Add(chunk.Dimensions);
            hash.Add(chunk.MaxActiveRooms);
            hash.Add(chunk.IsAwake);
            hash.Add(chunk.SleepTimer);
            foreach (int value in chunk.Classifications) hash.Add(value);
            foreach (float value in chunk.Temperatures) hash.Add(value);
            foreach (float value in chunk.Pressures) hash.Add(value);
            foreach (float value in chunk.HeatCapacities) hash.Add(value);
            hash.Add(chunk.ActiveRooms.Count);
            foreach (int value in chunk.ActiveRooms) hash.Add(value);
            hash.Add(chunk.ActiveAirIndices.Count);
            foreach (ushort value in chunk.ActiveAirIndices) hash.Add(value);
            hash.Add(chunk.Gases.Count);
            foreach (var gas in chunk.Gases)
            {
                hash.Add(gas.GasId);
                foreach (float value in gas.Moles) hash.Add(value);
            }

            if (checkpoint.FormatVersion >= 2)
            {
                hash.Add(chunk.SolverArrays.Count);
                foreach (var array in chunk.SolverArrays)
                    array.AppendHash(ref hash);
            }
        }

        return new AtmosStateHash(checkpoint.Position, hash.Value);
    }
}