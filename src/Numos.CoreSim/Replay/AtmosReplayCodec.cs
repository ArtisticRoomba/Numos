using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.EulerFlow;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.Solvers;
using Numos.CoreSim.State;
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

    // Every built-in configuration a portable file can carry, by key. A key missing here is a host type, which the
    // archive reports as unsupported before anything is written.
    private static readonly Dictionary<string, Func<BinaryReader, IAtmosSolverConfiguration>> PortableConfigurationReaders =
        new(StringComparer.Ordinal)
        {
            [EulerFlowConfig.ConfigurationKey] = EulerFlowConfig.ReadWire
        };

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
    internal static AtmosSimulationCheckpoint ReadSimulationCheckpoint(BinaryReader reader, int maxStringBytes)
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
        for (int index = 0; index < chunkCount; index++) chunks[index] = ReadChunk(reader, maxStringBytes);
        var checkpoint = new AtmosSimulationCheckpoint(dimensions, chunks);
        if (fingerprint != checkpoint.CompatibilityFingerprint)
            throw new InvalidDataException("The simulation checkpoint schema or compatibility fingerprint is invalid.");

        return checkpoint;
    }

    /// <summary>
    ///     Writes a configuration: scalar fields, the gas registry, then its solver configurations.
    /// </summary>
    /// <remarks>
    ///     Solver configurations are a count followed by, for each one in the snapshot's ordinal key order, its key
    ///     and a length-prefixed <see cref="StateBinarySink" /> payload. With none, that's a single zero count, which
    ///     is what files written before solver configurations could be saved contain.
    /// </remarks>
    /// <exception cref="NotSupportedException">A solver configuration is neither a <see cref="NumosStateAttribute" /> type nor a built-in portable one.</exception>
    internal static void WriteConfig(BinaryWriter writer, AtmosConfigSnapshot config)
    {
        GeneratedCheckpointFields.WriteConfigFields(writer, config);
        writer.Write(config.GasRegistry.Count);
        foreach (var gas in config.GasRegistry) GeneratedCheckpointFields.WriteGasFields(writer, gas);
        writer.Write(config.SolverConfigurations.Count);
        foreach (var configuration in config.SolverConfigurations)
        {
            if (configuration is IPortableSolverConfiguration portable)
            {
                WriteString(writer, configuration.Key);
                portable.WriteWire(writer);
                continue;
            }

            if (configuration is not INumosState state)
            {
                throw new NotSupportedException(
                    $"Solver configuration '{configuration.Key}' ({configuration.GetType()}) isn't a [NumosState] type, so it can't be saved.");
            }

            var sink = new StateBinarySink();
            state.WriteState(ref sink);
            WriteString(writer, configuration.Key);
            writer.Write(sink.WrittenSpan.Length);
            writer.Write(sink.WrittenSpan);
        }
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
        var config = new AtmosConfig();
        GeneratedCheckpointFields.ReadConfigFields(reader, config, maxStringBytes);

        int gasCount = ReadCount(reader, 1_000_000, "gas");
        for (int index = 0; index < gasCount; index++)
            config.GasRegistry.Add(GeneratedCheckpointFields.ReadGasFields(reader, maxStringBytes));

        var types = (reader as ReplayReader)?.SolverConfigurations ?? SolverConfigurationTypes.BuiltIn;
        int configurationCount = ReadCount(reader, 1_000_000, "solver configuration");
        string? previousKey = null;
        for (int index = 0; index < configurationCount; index++)
        {
            string key = ReadString(reader, maxStringBytes);
            if (previousKey != null && string.CompareOrdinal(previousKey, key) >= 0)
                throw new InvalidDataException("Solver configurations are not in ascending ordinal key order.");

            previousKey = key;
            if (PortableConfigurationReaders.TryGetValue(key, out var readPortable))
            {
                var portable = readPortable(reader);
                if (portable.Key != key)
                    throw new InvalidDataException("The solver configuration key does not match its payload.");

                config.SolverConfigurations.Add(portable);
                continue;
            }

            byte[] payload = ReadPayload(reader, ReadCount(reader, int.MaxValue, "solver configuration byte"));
            var state = new StateReader(payload, maxStringBytes);
            if (!types.TryRead(key, ref state, config.GasRegistry, out var configuration))
            {
                throw new NotSupportedException(
                    $"The file contains solver configuration '{key}', but no type is registered for that key. " +
                    "Register it in NumosReplayReadOptions.SolverConfigurations before loading.");
            }

            if (!state.IsAtEnd)
                throw new InvalidDataException($"Solver configuration '{key}' has trailing data.");

            if (configuration!.Key != key)
            {
                throw new InvalidDataException(
                    $"Solver configuration saved as '{key}' loaded as {configuration.GetType()} with key '{configuration.Key}'.");
            }

            config.SolverConfigurations.Add(configuration);
        }

        return config.CreateSnapshot();
    }

    /// <summary>
    ///     Reads <paramref name="length" /> bytes, growing the buffer as data arrives so a corrupt length can't make
    ///     the reader allocate more than the stream holds.
    /// </summary>
    internal static byte[] ReadPayload(BinaryReader reader, int length)
    {
        const int chunk = 64 * 1024;
        byte[] buffer = new byte[Math.Min(length, chunk)];
        int filled = 0;
        while (filled < length)
        {
            if (filled == buffer.Length)
                Array.Resize(ref buffer, (int)Math.Min(length, (long)buffer.Length * 2));

            int read = reader.Read(buffer, filled, buffer.Length - filled);
            if (read == 0) throw new EndOfStreamException();

            filled += read;
        }

        return buffer;
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

        WriteSolverArrays(writer, chunk.SolverArrays);
    }

    /// <summary>
    ///     Writes a chunk's rollback <c>ChunkField&lt;T&gt;</c> arrays.
    /// </summary>
    /// <remarks>
    ///     A count, then for each array in the checkpoint's ordinal key order (the order the state hash uses): its key,
    ///     the element type's full name, the element size and count as <c>int32</c>, and the elements as one
    ///     little-endian byte run. A chunk with none writes a single zero count, which is what files written before
    ///     these arrays could be saved contain.
    /// </remarks>
    /// <exception cref="NotSupportedException">An array's element type isn't portable.</exception>
    private static void WriteSolverArrays(BinaryWriter writer, IReadOnlyList<AtmosSolverArraySnapshot> arrays)
    {
        writer.Write(arrays.Count);
        foreach (var array in arrays)
        {
            if (!array.IsPortable)
            {
                throw new NotSupportedException(
                    $"Chunk field '{array.Key}' holds {array.ElementType.FullName}, which can't be saved. " +
                    "Only primitives and enums over them can.");
            }

            WriteString(writer, array.Key);
            WriteString(writer, array.ElementType.FullName!);
            writer.Write(array.ElementSize);
            writer.Write(array.Length);
            array.Write(writer);
        }
    }

    /// <summary>
    ///     Reads arrays written by <see cref="WriteSolverArrays" />, mapping each key to its registered field.
    /// </summary>
    /// <exception cref="InvalidDataException">An array doesn't match its registered field, or the data is malformed.</exception>
    /// <exception cref="NotSupportedException">A key has no registered field.</exception>
    private static AtmosSolverArraySnapshot[] ReadSolverArrays(BinaryReader reader, int voxels, int maxStringBytes)
    {
        int count = ReadCount(reader, 1_000_000, "solver array");
        if (count == 0)
            return [];

        var arrays = new List<AtmosSolverArraySnapshot>(Math.Min(count, 64));
        string? previousKey = null;
        for (int index = 0; index < count; index++)
        {
            string key = ReadString(reader, maxStringBytes);
            if (previousKey != null && string.CompareOrdinal(previousKey, key) >= 0)
                throw new InvalidDataException("Chunk field arrays are not in ascending ordinal key order.");

            previousKey = key;
            string typeName = ReadString(reader, maxStringBytes);
            int size = reader.ReadInt32();
            int length = reader.ReadInt32();
            if (!ChunkFieldRegistry.TryGet(key, out var field))
            {
                throw new NotSupportedException(
                    $"The file contains chunk field '{key}', but no field is registered under that key. Register it " +
                    "before loading by touching its [ChunkData] property or calling ChunkField<T>.Register.");
            }

            if (!field.CaptureForRollback)
                throw new InvalidDataException($"Chunk field '{key}' is saved in the file but registered without rollback.");

            if (field.ElementType.FullName != typeName || field.ElementSize != size)
            {
                throw new InvalidDataException(
                    $"Chunk field '{key}' was saved as {typeName} ({size} bytes) but is registered as " +
                    $"{field.ElementType.FullName} ({field.ElementSize} bytes).");
            }

            if (!field.IsPortable)
                throw new InvalidDataException($"Chunk field '{key}' is registered as {typeName}, which isn't a portable element type.");

            // The length has to match the registration, so a corrupt length can't make the reader allocate more than
            // the host already allocates for this field in every chunk.
            if (length != field.GetLength(voxels))
            {
                throw new InvalidDataException(
                    $"Chunk field '{key}' has {length} elements, but the registered field needs {field.GetLength(voxels)}.");
            }

            arrays.Add(AtmosSolverArraySnapshot.Adopt(field.Read(reader, length)));
        }

        return arrays.ToArray();
    }

    private static AtmosChunkCheckpoint ReadChunk(BinaryReader reader, int maxStringBytes)
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

        var solverArrays = ReadSolverArrays(reader, voxels, maxStringBytes);

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
            gases,
            solverArrays);
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

    internal static void ReverseElementBytes(Span<byte> bytes, int elementSize)
    {
        for (int offset = 0; offset < bytes.Length; offset += elementSize) bytes.Slice(offset, elementSize).Reverse();
    }
}