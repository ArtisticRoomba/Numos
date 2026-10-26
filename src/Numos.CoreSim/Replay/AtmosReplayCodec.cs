using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.Solvers;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;
using Numos.Maths;

namespace Numos.CoreSim.Replay;

/// <summary>
///     Binary codec for simulation operations and the configurations they carry, plus the little-endian primitives
///     every replay payload is built from. Checkpoints are written by their generated state walkers instead.
/// </summary>
/// <remarks>
///     <para>
///         Numos.Replay.Generators emits <c>WriteOperation</c>/<c>ReadOperation</c> for every concrete
///         <see cref="AtmosOperation" />. A record's fields are written in primary-constructor order. A field type
///         that is not a primitive or quantity alias needs a <c>Write{TypeName}</c>/<c>Read{TypeName}</c>
///         pair declared here (or on <c>AtmosWorldReplayCodec</c> for world operations). An operation that needs
///         its own layout gets hand-written <c>Write{OperationName}</c>/<c>Read{OperationName}</c> methods here.
///         The generator calls those methods instead of generating a body.
///     </para>
///     <para>
///         The bytes are pinned by <c>tests/Numos.Serialization.Tests/ReplayWireFormatGoldenTests.cs</c>.
///     </para>
/// </remarks>
internal static partial class AtmosReplayCodec
{
    internal static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    /// <summary>
    ///     Writes a configuration: its schema fields (scalars and the gas registry), then its solver configurations.
    /// </summary>
    /// <remarks>
    ///     The bytes are one <see cref="StateBinarySink" /> walk of <see cref="AtmosConfigSnapshotCodec" />. The outer
    ///     object has no length of its own, so the schema fields and each solver configuration are length-prefixed
    ///     payloads inside it. Solver configurations follow the fields as a count and, for each one in the snapshot's
    ///     ordinal key order, its key and payload. A state hash walks the same sequence without the lengths.
    /// </remarks>
    /// <exception cref="NotSupportedException">A solver configuration has no <see cref="NumosSerializeAttribute" /> members.</exception>
    internal static void WriteConfig(BinaryWriter writer, AtmosConfigSnapshot config)
    {
        var sink = new StateBinarySink();
        sink.BeginObject();
        AtmosConfigSnapshotCodec.Write(ref sink, config);
        sink.EndObject();
        writer.Write(sink.WrittenSpan);
    }

    /// <summary>
    ///     Reads a configuration written by <see cref="WriteConfig" />.
    /// </summary>
    /// <remarks>
    ///     Solver configuration keys are looked up in the <see cref="ReplayReader.SolverConfigurations" /> registry when
    ///     <paramref name="reader" /> carries one, and in the built-in registry otherwise.
    /// </remarks>
    /// <exception cref="InvalidDataException">The data is malformed.</exception>
    /// <exception cref="NotSupportedException">A solver configuration key has no registered type.</exception>
    internal static AtmosConfigSnapshot ReadConfig(BinaryReader reader, int maxStringBytes)
    {
        // Read into a mutable AtmosConfig so the snapshot constructor normalizes it, exactly as for a live config.
        byte[] fieldBytes = ReadPayload(reader, ReadCount(reader, int.MaxValue, "configuration byte"));
        var fields = new StateReader(fieldBytes, maxStringBytes);
        var config = NumosState.Read<AtmosConfig>(ref fields);

        var types = (reader as ReplayReader)?.SolverConfigurations ?? SolverConfigurationTypes.BuiltIn;
        int configurationCount = ReadCount(reader, 1_000_000, "solver configuration");
        string? previousKey = null;
        for (int index = 0; index < configurationCount; index++)
        {
            string key = ReadString(reader, maxStringBytes);
            RequireAscendingKey(previousKey, key, "Solver configurations");
            previousKey = key;
            byte[] payload = ReadPayload(reader, ReadCount(reader, int.MaxValue, "solver configuration byte"));
            var state = new StateReader(payload, maxStringBytes);
            var configuration = types.Read(key, ref state, config.GasRegistry);
            if (!state.IsAtEnd)
                throw new InvalidDataException($"Solver configuration '{key}' has trailing data.");

            config.SolverConfigurations.Add(configuration);
        }

        return config.CreateSnapshot();
    }

    /// <summary>
    ///     Throws unless <paramref name="key" /> sorts after <paramref name="previousKey" /> in ordinal order.
    /// </summary>
    /// <param name="previousKey">The key before this one, or <see langword="null" /> for the first.</param>
    /// <param name="key">The key just read.</param>
    /// <param name="collection">The plural name of the keyed section, for the message.</param>
    /// <exception cref="InvalidDataException">The keys are not strictly ascending.</exception>
    internal static void RequireAscendingKey(string? previousKey, string key, string collection)
    {
        if (previousKey != null && string.CompareOrdinal(previousKey, key) >= 0)
            throw new InvalidDataException($"{collection} are not in ascending ordinal key order.");
    }

    /// <summary>
    ///     Writes <paramref name="payload" /> behind its <c>int32</c> byte length. <see cref="ReadPayload" /> reads it
    ///     back after <see cref="ReadCount" />.
    /// </summary>
    internal static void WritePayload(BinaryWriter writer, ReadOnlySpan<byte> payload)
    {
        writer.Write(payload.Length);
        writer.Write(payload);
    }

    /// <summary>
    ///     Reads <paramref name="length" /> bytes into an array rented from <see cref="ArrayPool{T}.Shared" />, growing
    ///     as data arrives like <see cref="ReadPayload" />.
    /// </summary>
    /// <returns>A rented array whose first <paramref name="length" /> bytes hold the payload. Return it when done.</returns>
    internal static byte[] RentPayload(BinaryReader reader, int length) => ReadGrowing(reader, length, true);

    /// <summary>
    ///     Reads <paramref name="length" /> bytes, growing the buffer as data arrives so a corrupt length can't make
    ///     the reader allocate more than the stream holds.
    /// </summary>
    /// <returns>A new array of exactly <paramref name="length" /> bytes.</returns>
    internal static byte[] ReadPayload(BinaryReader reader, int length) => ReadGrowing(reader, length, false);

    // A pooled buffer can be larger than asked for, so callers of RentPayload must use only the first length bytes.
    private static byte[] ReadGrowing(BinaryReader reader, int length, bool pooled)
    {
        const int chunk = 64 * 1024;
        byte[] buffer = Allocate(Math.Min(length, chunk), pooled);
        try
        {
            int filled = 0;
            while (filled < length)
            {
                if (filled == buffer.Length)
                {
                    byte[] grown = Allocate((int)Math.Min(length, (long)buffer.Length * 2), pooled);
                    buffer.AsSpan(0, filled).CopyTo(grown);
                    if (pooled) ArrayPool<byte>.Shared.Return(buffer);

                    buffer = grown;
                }

                int read = reader.Read(buffer, filled, Math.Min(buffer.Length, length) - filled);
                if (read == 0) throw new EndOfStreamException();

                filled += read;
            }

            return buffer;
        }
        catch
        {
            if (pooled) ArrayPool<byte>.Shared.Return(buffer);

            throw;
        }
    }

    private static byte[] Allocate(int size, bool pooled) => pooled ? ArrayPool<byte>.Shared.Rent(size) : new byte[size];

    private static void WriteSetVoxelMixtureOperation(BinaryWriter writer, SetVoxelMixtureOperation operation)
    {
        WriteInt3(writer, operation.Position);
        writer.Write(operation.LocalVoxelIndex);
        writer.Write(operation.Temperature);
        writer.Write(operation.Pressure);
        writer.Write(operation.HeatCapacity);
        writer.Write(operation.MomentumX);
        writer.Write(operation.MomentumY);
        writer.Write(operation.MomentumZ);
        writer.Write(operation.Gases.Count);
        foreach (var gas in operation.Gases)
        {
            writer.Write(gas.GasId);
            writer.Write(gas.Moles);
        }
    }

    private static SetVoxelMixtureOperation ReadSetVoxelMixtureOperation(BinaryReader reader)
    {
        var position = ReadInt3(reader);
        ushort voxel = reader.ReadUInt16();
        Kelvin temperature = reader.ReadSingle();
        Pascal pressure = reader.ReadSingle();
        JoulePerKelvin capacity = reader.ReadSingle();
        KilogramMetrePerSecond momentumX = reader.ReadSingle();
        KilogramMetrePerSecond momentumY = reader.ReadSingle();
        KilogramMetrePerSecond momentumZ = reader.ReadSingle();
        int count = ReadCount(reader, 1_000_000, "mixture gas");
        var gases = new AtmosGasAmount[count];
        for (int index = 0; index < count; index++)
        {
            int gasId = reader.ReadInt32();
            Mole moles = reader.ReadSingle();
            gases[index] = new AtmosGasAmount(gasId, moles);
        }

        return new SetVoxelMixtureOperation(
            position,
            voxel,
            temperature,
            pressure,
            capacity,
            momentumX,
            momentumY,
            momentumZ,
            gases);
    }

    private static void WriteEditVoxelBoxOperation(BinaryWriter writer, EditVoxelBoxOperation operation)
    {
        WriteInt3(writer, operation.Min);
        WriteInt3(writer, operation.Max);
        writer.Write((byte)operation.Parts);
        WriteVoxelClassification(writer, operation.Classification);
        writer.Write(operation.VoxelTemperature);
        writer.Write(operation.GasTemperature);
        writer.Write(operation.VelocityX);
        writer.Write(operation.VelocityY);
        writer.Write(operation.VelocityZ);
        writer.Write(operation.Gases.Count);
        foreach (var gas in operation.Gases)
        {
            writer.Write(gas.GasId);
            writer.Write(gas.Moles);
        }
    }

    private static EditVoxelBoxOperation ReadEditVoxelBoxOperation(BinaryReader reader)
    {
        var min = ReadInt3(reader);
        var max = ReadInt3(reader);
        var parts = (AtmosVoxelBoxParts)reader.ReadByte();
        if ((parts & ~(AtmosVoxelBoxParts.Classification | AtmosVoxelBoxParts.ClearGas |
                       AtmosVoxelBoxParts.Temperature | AtmosVoxelBoxParts.Velocity)) != 0)
        {
            throw new InvalidDataException("The voxel box operation selects an unknown part.");
        }

        var classification = ReadVoxelClassification(reader);
        Kelvin voxelTemperature = reader.ReadSingle();
        Kelvin gasTemperature = reader.ReadSingle();
        MetrePerSecond velocityX = reader.ReadSingle();
        MetrePerSecond velocityY = reader.ReadSingle();
        MetrePerSecond velocityZ = reader.ReadSingle();
        int count = ReadCount(reader, 1_000_000, "voxel box gas");
        var gases = new AtmosGasAmount[count];
        for (int index = 0; index < count; index++)
        {
            int gasId = reader.ReadInt32();
            Mole moles = reader.ReadSingle();
            gases[index] = new AtmosGasAmount(gasId, moles);
        }

        return new EditVoxelBoxOperation(
            min,
            max,
            parts,
            classification,
            voxelTemperature,
            gasTemperature,
            velocityX,
            velocityY,
            velocityZ,
            gases);
    }

    private static void WriteVoxelClassification(BinaryWriter writer, VoxelClassification value)
    {
        writer.Write(value.RoomId);
    }

    private static VoxelClassification ReadVoxelClassification(BinaryReader reader)
    {
        return new VoxelClassification(reader.ReadInt32());
    }

    internal static void WritePosition(BinaryWriter writer, AtmosTimelinePosition position)
    {
        writer.Write(position.Tick);
        writer.Write(position.OperationSequence);
    }

    internal static AtmosTimelinePosition ReadPosition(BinaryReader reader)
    {
        return new AtmosTimelinePosition(reader.ReadUInt64(), reader.ReadUInt64());
    }

    internal static void WriteInt3(BinaryWriter writer, Int3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    internal static Int3 ReadInt3(BinaryReader reader)
    {
        return new Int3(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
    }

    internal static void WriteString(BinaryWriter writer, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes = Utf8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    internal static string ReadString(BinaryReader reader, int maxStringBytes)
    {
        int length = ReadCount(reader, maxStringBytes, "string byte");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();

        return Utf8.GetString(bytes);
    }

    internal static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value != null);
        if (value != null) WriteString(writer, value);
    }

    internal static string? ReadNullableString(BinaryReader reader, int maxStringBytes)
    {
        return reader.ReadBoolean() ? ReadString(reader, maxStringBytes) : null;
    }

    internal static int ReadCount(BinaryReader reader, int maximum, string name)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum) throw new InvalidDataException($"The {name} count is invalid.");

        return count;
    }
}