using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Replay;

/// <summary>
///     Binary codec for simulation operations, simulation checkpoints and configuration, plus the little-endian
///     primitives every replay payload is built from.
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
    ///     Writes one simulation's grid checkpoint nested in a world checkpoint.
    /// </summary>
    internal static void WriteSimulationCheckpoint(BinaryWriter writer, AtmosSimulationCheckpoint checkpoint)
    {
        writer.Write(checkpoint.FormatVersion);
        writer.Write(checkpoint.CompatibilityVersion);
        writer.Write(checkpoint.CompatibilityFingerprint);
        WriteInt3(writer, checkpoint.Dimensions);
        writer.Write(checkpoint.Chunks.Count);
        foreach (var chunk in checkpoint.Chunks) WriteChunk(writer, chunk);
    }

    /// <summary>
    ///     Reads one simulation checkpoint written by <see cref="WriteSimulationCheckpoint" />.
    /// </summary>
    internal static AtmosSimulationCheckpoint ReadSimulationCheckpoint(BinaryReader reader)
    {
        int format = reader.ReadInt32();
        if (format != AtmosSimulationCheckpoint.CurrentFormatVersion)
            throw new InvalidDataException("The simulation checkpoint schema is unsupported.");

        int compatibility = reader.ReadInt32();
        if (compatibility != AtmosSimulationCheckpoint.CurrentCompatibilityVersion)
            throw new InvalidDataException("The simulation checkpoint compatibility version is unsupported.");

        ulong fingerprint = reader.ReadUInt64();
        var dimensions = ReadInt3(reader);
        int chunkCount = ReadCount(reader, 1_000_000, "chunk");
        var chunks = new AtmosChunkCheckpoint[chunkCount];
        for (int index = 0; index < chunkCount; index++) chunks[index] = ReadChunk(reader);
        var checkpoint = new AtmosSimulationCheckpoint(dimensions, chunks);
        if (fingerprint != checkpoint.CompatibilityFingerprint)
            throw new InvalidDataException("The simulation checkpoint schema or compatibility fingerprint is invalid.");

        return checkpoint;
    }

    internal static void WriteConfig(BinaryWriter writer, AtmosConfigSnapshot config)
    {
        GeneratedCheckpointFields.WriteConfigFields(writer, config);
        writer.Write(config.GasRegistry.Count);
        foreach (var gas in config.GasRegistry) GeneratedCheckpointFields.WriteGasFields(writer, gas);
        writer.Write(config.SolverConfigurations.Count);
    }

    internal static AtmosConfigSnapshot ReadConfig(BinaryReader reader, int maxStringBytes)
    {
        var config = new AtmosConfig();
        GeneratedCheckpointFields.ReadConfigFields(reader, config, maxStringBytes);

        int gasCount = ReadCount(reader, 1_000_000, "gas");
        for (int index = 0; index < gasCount; index++)
            config.GasRegistry.Add(GeneratedCheckpointFields.ReadGasFields(reader, maxStringBytes));

        int customConfigurationCount = ReadCount(reader, 1_000_000, "solver configuration");
        if (customConfigurationCount != 0)
            throw new NotSupportedException("Portable replay files cannot decode custom solver configurations.");

        return config.CreateSnapshot();
    }

    private static void WriteChunk(BinaryWriter writer, AtmosChunkCheckpoint chunk)
    {
        WriteInt3(writer, chunk.Position);
        WriteInt3(writer, chunk.Dimensions);
        writer.Write(chunk.IsAwake);
        writer.Write(chunk.SleepTimer);
        writer.Write(chunk.Classifications.Count);
        GeneratedCheckpointFields.WriteChunkFields(writer, chunk);
        writer.Write(chunk.ActiveAirIndices.Count);
        WriteArray(writer, chunk.ActiveAirIndices);
        writer.Write(chunk.Gases.Count);
        foreach (var gas in chunk.Gases)
        {
            writer.Write(gas.GasId);
            WriteArray(writer, gas.Moles);
        }

        writer.Write(chunk.SolverArrays.Count);
    }

    private static AtmosChunkCheckpoint ReadChunk(BinaryReader reader)
    {
        var position = ReadInt3(reader);
        var dimensions = ReadInt3(reader);
        if (dimensions.X <= 0 || dimensions.Y <= 0 || dimensions.Z <= 0)
            throw new InvalidDataException("Chunk dimensions must be positive.");

        bool awake = reader.ReadBoolean();
        int sleepTimer = reader.ReadInt32();
        int voxels = ReadCount(reader, ushort.MaxValue, "voxel");
        int expected;
        try
        {
            expected = checked(dimensions.X * dimensions.Y * dimensions.Z);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Chunk dimensions overflow.", exception);
        }

        if (voxels != expected || voxels <= 0) throw new InvalidDataException("Chunk voxel data does not match its dimensions.");

        var (classifications, temperatures, pressures, capacities, momentumX, momentumY, momentumZ) =
            GeneratedCheckpointFields.ReadChunkFields(reader, voxels);
        ushort[] air = ReadArray<ushort>(reader, ReadCount(reader, voxels, "active air voxel"));
        int gasCount = ReadCount(reader, 1_000_000, "gas channel");
        var gases = new AtmosGasChannelCheckpoint[gasCount];
        for (int index = 0; index < gasCount; index++)
            gases[index] = new AtmosGasChannelCheckpoint(reader.ReadInt32(), ReadArray<float>(reader, voxels));

        int solverArrays = ReadCount(reader, 1_000_000, "solver array");
        if (solverArrays != 0) throw new NotSupportedException("Portable replay files cannot decode custom solver arrays.");

        return new AtmosChunkCheckpoint(
            position,
            dimensions,
            awake,
            sleepTimer,
            classifications,
            temperatures,
            pressures,
            capacities,
            momentumX,
            momentumY,
            momentumZ,
            air,
            gases);
    }

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
        // Three reserved floats keep the layout of the velocity part. This build writes zeros.
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0f);
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
                       AtmosVoxelBoxParts.Temperature)) != 0)
        {
            throw new InvalidDataException("The voxel box operation selects an unknown part.");
        }

        var classification = ReadVoxelClassification(reader);
        Kelvin voxelTemperature = reader.ReadSingle();
        Kelvin gasTemperature = reader.ReadSingle();
        _ = reader.ReadSingle();
        _ = reader.ReadSingle();
        _ = reader.ReadSingle();
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

    /// <summary>
    ///     Reads <paramref name="count" /> little-endian primitives in one bulk read.
    /// </summary>
    internal static T[] ReadArray<T>(BinaryReader reader, int count)
        where T : unmanaged
    {
        var values = new T[count];
        var bytes = MemoryMarshal.AsBytes(values.AsSpan());
        reader.BaseStream.ReadExactly(bytes);
        if (!BitConverter.IsLittleEndian) ReverseElementBytes(bytes, Unsafe.SizeOf<T>());
        return values;
    }

    /// <summary>
    ///     Writes every element as a little-endian primitive in one bulk write, without a length prefix.
    /// </summary>
    internal static void WriteArray<T>(BinaryWriter writer, IReadOnlyList<T> values)
        where T : unmanaged
    {
        T[] buffer = ArrayPool<T>.Shared.Rent(values.Count);
        try
        {
            var span = buffer.AsSpan(0, values.Count);
            if (values is ICollection<T> collection)
                collection.CopyTo(buffer, 0);
            else
                for (int index = 0; index < span.Length; index++)
                    span[index] = values[index];

            var bytes = MemoryMarshal.AsBytes(span);
            if (!BitConverter.IsLittleEndian) ReverseElementBytes(bytes, Unsafe.SizeOf<T>());
            writer.Write(bytes);
        }
        finally
        {
            ArrayPool<T>.Shared.Return(buffer);
        }
    }

    private static void ReverseElementBytes(Span<byte> bytes, int elementSize)
    {
        for (int offset = 0; offset < bytes.Length; offset += elementSize) bytes.Slice(offset, elementSize).Reverse();
    }
}