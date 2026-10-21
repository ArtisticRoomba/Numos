using Numos.CoreSim.State;
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

    internal void AddBytes(ReadOnlySpan<byte> bytes)
    {
        ulong value = Value;
        foreach (byte part in bytes)
            value = unchecked((value ^ part) * 1099511628211UL);

        Value = value;
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

    internal static ulong HashDefinition(AtmosSimulationCheckpoint checkpoint)
    {
        var hash = new AtmosStateHasher();
        hash.Add(checkpoint.CompatibilityVersion);
        hash.Add(checkpoint.Dimensions);
        return hash.Value;
    }

    // Mirrors AtmosReplayCodec.WriteSimulationCheckpoint, minus the chunk payload lengths.
    internal static ulong Hash(AtmosSimulationCheckpoint checkpoint)
    {
        var sink = new StateHashSink();
        sink.WriteInt32(checkpoint.FormatVersion);
        sink.WriteInt32(checkpoint.CompatibilityVersion);
        sink.WriteUInt64(checkpoint.CompatibilityFingerprint);
        StateCodecs.Int3.Write(ref sink, checkpoint.Dimensions);
        sink.WriteCount(checkpoint.Chunks.Count);
        foreach (var chunk in checkpoint.Chunks)
            NumosState.Write(ref sink, chunk);

        return sink.Value;
    }
}