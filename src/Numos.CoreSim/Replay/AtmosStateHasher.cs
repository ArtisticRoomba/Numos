namespace Numos.CoreSim.Replay;

// The FNV-1a core behind StateHashSink, which is what state hashes should go through so they see the wire bytes.
// Integers are little-endian and floats their raw IEEE bits. Never hash CLR identities or pool capacity.
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
}