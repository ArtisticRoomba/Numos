using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;
using Numos.Maths;
using Numos.Replay.SourceGen;

namespace Numos.Serialization;

/// <summary>
///     Owns the <c>.numos</c> container framing and the codecs shared by world replay content: metadata, simulation
///     checkpoints, configuration, and simulation operations.
/// </summary>
/// <remarks>
///     <see cref="NumosWorldReplaySerializer" /> is the public entry point. The <c>[WireOperation]</c> attributes below
///     register every <see cref="AtmosOperationCode" /> with Numos.Replay.SourceGen, which generates this class's
///     <c>TryWriteGeneratedOperation</c>/<c>TryReadGeneratedOperation</c> partial-class members. Field order for the
///     mechanical ones is exactly constructor-declaration order; see
///     <c>tests/Numos.Serialization.Tests/ReplayWireFormatGoldenTests.cs</c> for the byte-level pin that codec must not
///     silently drift from.
///     <para>
///         <see cref="SetVoxelMixtureOperation" /> (variable-length gas list) doesn't fit the generator's mechanical
///         field-order mapping, so it registers as <c>Custom</c>: the generator plugs calls to the hand-written
///         <c>Write{Name}</c>/<c>Read{Name}</c> methods below into the same generated dispatch table, rather than
///         generating their bodies.
///     </para>
///     <para>
///         Every <see cref="AtmosOperationCode" /> member needs one of these registrations. A missing one is a
///         NUMOSREPLAYGEN008 build error from <c>ReplayOpcodeConsistencyAnalyzer</c> in Numos.Replay.Analyzers.
///     </para>
/// </remarks>
[WireOperation(typeof(CreateChunkOperation), AtmosOperationCode.CreateChunk)]
[WireOperation(typeof(RemoveChunkOperation), AtmosOperationCode.RemoveChunk)]
[WireOperation(typeof(SetChunkClassificationOperation), AtmosOperationCode.SetChunkClassification)]
[WireOperation(typeof(SetChunkBoundaryClassificationOperation), AtmosOperationCode.SetChunkBoundaryClassification)]
[WireOperation(typeof(SetVoxelClassificationOperation), AtmosOperationCode.SetVoxelClassification)]
[WireOperation(typeof(SetVoxelTemperatureOperation), AtmosOperationCode.SetVoxelTemperature)]
[WireOperation(typeof(AddGasToVoxelOperation), AtmosOperationCode.AddGasToVoxel)]
[WireOperation(typeof(WakeChunkOperation), AtmosOperationCode.WakeChunk)]
[WireOperation(typeof(SleepChunkOperation), AtmosOperationCode.SleepChunk)]
[WireOperation(typeof(SetVoxelMixtureOperation), AtmosOperationCode.SetVoxelMixture, Custom = true)]
internal static partial class NumosReplaySerializer
{
    internal const ushort ContainerVersion = 1;
    // Kind 1 was the single-simulation replay. Readers recognize it only to reject it with a clear message.
    internal const ushort SimulationReplayContentKind = 1;
    internal const ushort WorldReplayContentKind = 2;
    internal const ushort MetadataSection = 1;
    internal const ushort CheckpointSection = 2;
    internal const ushort OperationsSection = 3;
    internal const ushort RequiredSection = 1;
    internal readonly static byte[] Magic = [0x4e, 0x55, 0x4d, 0x4f, 0x53, 0x0d, 0x0a, 0x1a];
    internal readonly static Encoding Utf8 = new UTF8Encoding(false, true);

    /// <summary>
    ///     Writes the container header and its three required sections.
    /// </summary>
    internal static void WriteContainer(
        Stream destination,
        Action<BinaryWriter> writeMetadata,
        Action<BinaryWriter> writeCheckpoint,
        Action<BinaryWriter> writeOperations)
    {
        using var writer = new BinaryWriter(destination, Utf8, true);
        writer.Write(Magic);
        writer.Write(ContainerVersion);
        writer.Write(WorldReplayContentKind);
        writer.Write(3u);

        using var scratch = destination.CanSeek ? null : new MemoryStream();
        WriteSection(destination, writer, scratch, MetadataSection, 0, writeMetadata);
        WriteSection(destination, writer, scratch, CheckpointSection, RequiredSection, writeCheckpoint);
        WriteSection(destination, writer, scratch, OperationsSection, RequiredSection, writeOperations);
    }

    /// <summary>
    ///     Writes one section header and payload, encoding the payload exactly once.
    /// </summary>
    /// <remarks>
    ///     A seekable destination gets a placeholder length that is patched after the payload is written. Anything else
    ///     has the payload staged in <paramref name="scratch" /> first, because the length precedes the payload on the
    ///     wire.
    /// </remarks>
    private static void WriteSection(
        Stream destination,
        BinaryWriter writer,
        MemoryStream? scratch,
        ushort id,
        ushort flags,
        Action<BinaryWriter> write)
    {
        writer.Write(id);
        writer.Write(flags);
        if (scratch == null)
        {
            long lengthPosition = destination.Position;
            writer.Write(0UL);
            write(writer);
            long end = destination.Position;
            destination.Position = lengthPosition;
            writer.Write(checked((ulong)(end - lengthPosition - sizeof(ulong))));
            destination.Position = end;
            return;
        }

        scratch.SetLength(0);
        using (var sectionWriter = new BinaryWriter(scratch, Utf8, true))
        {
            write(sectionWriter);
        }

        writer.Write(checked((ulong)scratch.Length));
        destination.Write(scratch.GetBuffer(), 0, (int)scratch.Length);
    }

    /// <summary>
    ///     Validates the container header and decodes the metadata, checkpoint, and operation sections. Unknown optional
    ///     sections are skipped.
    /// </summary>
    internal static (MetadataPayload Metadata, NumosWorldReplaySerializer.WorldCheckpointPayload Checkpoint, AtmosWorldRecording Recording)
        ReadContainer(Stream source, NumosReplayReadOptions? options)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("The source stream must be readable.", nameof(source));

        options ??= new NumosReplayReadOptions();
        ValidateOptions(options);
        using var reader = new BinaryReader(source, Utf8, true);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException("The stream is not a Numos file.");
        if (reader.ReadUInt16() != ContainerVersion) throw new InvalidDataException("The Numos container version is unsupported.");
        ushort kind = reader.ReadUInt16();
        if (kind == SimulationReplayContentKind)
        {
            throw new InvalidDataException(
                "Single-simulation replay files (content kind 1) are no longer supported. Record the simulation's world instead.");
        }

        if (kind != WorldReplayContentKind) throw new InvalidDataException($"Numos content kind {kind} is unsupported.");

        uint sectionCount = reader.ReadUInt32();
        if (sectionCount > 1024) throw new InvalidDataException("The Numos file declares too many sections.");

        MetadataPayload? metadata = null;
        NumosWorldReplaySerializer.WorldCheckpointPayload? checkpoint = null;
        AtmosWorldRecording? recording = null;
        long totalPayload = 0;
        for (uint index = 0; index < sectionCount; index++)
        {
            ushort id = reader.ReadUInt16();
            ushort flags = reader.ReadUInt16();
            ulong rawLength = reader.ReadUInt64();
            if (rawLength > long.MaxValue || checked(totalPayload + (long)rawLength) > options.MaxPayloadBytes)
                throw new InvalidDataException("The Numos file exceeds the configured payload limit.");

            totalPayload += (long)rawLength;
            if (id == MetadataSection && rawLength > (ulong)options.MaxMetadataBytes)
                throw new InvalidDataException("The replay metadata exceeds the configured limit.");

            var limited = new LimitedReadStream(source, (long)rawLength);
            using var section = new BinaryReader(limited, Utf8, true);
            switch (id)
            {
                case MetadataSection:
                    if (metadata != null) throw new InvalidDataException("The replay contains duplicate metadata sections.");

                    metadata = ReadMetadata(section, options);
                    break;
                case CheckpointSection:
                    if (checkpoint != null) throw new InvalidDataException("The replay contains duplicate checkpoint sections.");

                    checkpoint = NumosWorldReplaySerializer.ReadCheckpoint(section, options);
                    break;
                case OperationsSection:
                    if (recording != null) throw new InvalidDataException("The replay contains duplicate operation sections.");

                    recording = NumosWorldReplaySerializer.ReadOperations(section, options);
                    break;
                default:
                    if ((flags & RequiredSection) != 0) throw new InvalidDataException($"Required Numos section {id} is unsupported.");

                    Drain(limited);
                    break;
            }

            if (limited.Remaining != 0) throw new InvalidDataException($"Numos section {id} has unexpected trailing data.");
        }

        if (metadata == null || checkpoint == null || recording == null)
            throw new InvalidDataException("The world replay is missing a required section.");

        return (metadata, checkpoint, recording);
    }

    internal static void EnsureEndOfContainer(Stream source)
    {
        if (source.ReadByte() != -1) throw new InvalidDataException("The Numos file has trailing data.");
    }

    /// <summary>
    ///     Writes the metadata section, including redundant bounds and counts the reader checks against the payload.
    /// </summary>
    internal static void WriteMetadata(
        BinaryWriter writer,
        NumosReplayMetadata metadata,
        AtmosTimelinePosition start,
        AtmosTimelinePosition head,
        int operationCount,
        int simulationCount,
        int linkSetCount)
    {
        WriteString(writer, metadata.ProjectName);
        writer.Write(metadata.CreatedUtc.UtcTicks);
        WriteString(writer, metadata.ProducerName);
        WriteString(writer, metadata.ProducerVersion);
        WriteString(writer, metadata.CoreSimVersion);
        WriteNullableString(writer, metadata.SourceReference);
        WritePosition(writer, start);
        WritePosition(writer, head);
        writer.Write(checked((ulong)operationCount));
        writer.Write(checked((ulong)simulationCount));
        writer.Write(checked((ulong)linkSetCount));
    }

    private static MetadataPayload ReadMetadata(BinaryReader reader, NumosReplayReadOptions options)
    {
        string projectName = ReadString(reader, options);
        long utcTicks = reader.ReadInt64();
        string producerName = ReadString(reader, options);
        string producerVersion = ReadString(reader, options);
        string coreVersion = ReadString(reader, options);
        string? sourceReference = ReadNullableString(reader, options);
        var start = ReadPosition(reader);
        var head = ReadPosition(reader);
        ulong operationCount = reader.ReadUInt64();
        ulong simulationCount = reader.ReadUInt64();
        ulong linkSetCount = reader.ReadUInt64();
        DateTimeOffset created;
        try
        {
            created = new DateTimeOffset(utcTicks, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("The replay creation time is invalid.", exception);
        }

        return new MetadataPayload(
            new NumosReplayMetadata(projectName, created, producerName, producerVersion, coreVersion, sourceReference),
            start,
            head,
            operationCount,
            simulationCount,
            linkSetCount);
    }

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
        WriteGeneratedConfigFields(writer, config);
        writer.Write(config.GasRegistry.Count);
        foreach (var gas in config.GasRegistry) WriteGas(writer, gas);
        writer.Write(config.SolverConfigurations.Count);
    }

    internal static AtmosConfigSnapshot ReadConfig(BinaryReader reader, NumosReplayReadOptions options)
    {
        var config = new AtmosConfig();
        ReadGeneratedConfigFields(reader, config);

        int gasCount = ReadCount(reader, 1_000_000, "gas");
        for (int index = 0; index < gasCount; index++) config.GasRegistry.Add(ReadGas(reader, options));
        int customConfigurationCount = ReadCount(reader, 1_000_000, "solver configuration");
        if (customConfigurationCount != 0)
            throw new NotSupportedException("Portable replay files cannot decode custom solver configurations.");

        return config.CreateSnapshot();
    }

    private static void WriteGas(BinaryWriter writer, GasProperties gas)
    {
        WriteGeneratedGasFields(writer, gas);
    }

    private static GasProperties ReadGas(BinaryReader reader, NumosReplayReadOptions options)
    {
        return ReadGeneratedGasFields(reader, options);
    }

    internal static void WriteChunk(BinaryWriter writer, AtmosChunkCheckpoint chunk)
    {
        WriteInt3(writer, chunk.Position);
        WriteInt3(writer, chunk.Dimensions);
        writer.Write(chunk.IsAwake);
        writer.Write(chunk.SleepTimer);
        writer.Write(chunk.Classifications.Count);
        WriteGeneratedChunkFields(writer, chunk);
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

    internal static AtmosChunkCheckpoint ReadChunk(BinaryReader reader)
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

        var (classifications, temperatures, pressures, capacities) = ReadGeneratedChunkFields(reader, voxels);
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
            air,
            gases);
    }

    internal static void WriteOperation(BinaryWriter writer, AtmosOperation operation)
    {
        if (!TryWriteGeneratedOperation(writer, operation))
            throw new NotSupportedException($"Replay opcode {operation.Code} is not serializable.");
    }

    internal static AtmosOperation ReadOperation(BinaryReader reader, ushort rawCode, NumosReplayReadOptions options)
    {
        if (!Enum.IsDefined((AtmosOperationCode)rawCode)) throw new InvalidDataException($"Replay opcode {rawCode} is unsupported.");

        if (TryReadGeneratedOperation(reader, rawCode, options, out var generated)) return generated!;

        throw new InvalidDataException($"Replay opcode {rawCode} is unsupported.");
    }

    private static void WriteSetVoxelMixtureOperation(BinaryWriter writer, SetVoxelMixtureOperation operation)
    {
        WriteInt3(writer, operation.Position);
        writer.Write(operation.LocalVoxelIndex);
        writer.Write(operation.Temperature);
        writer.Write(operation.Pressure);
        writer.Write(operation.HeatCapacity);
        writer.Write(operation.Gases.Count);
        foreach (var gas in operation.Gases)
        {
            writer.Write(gas.GasId);
            writer.Write(gas.Moles);
        }
    }

    private static SetVoxelMixtureOperation ReadSetVoxelMixtureOperation(BinaryReader reader, NumosReplayReadOptions options)
    {
        var position = ReadInt3(reader);
        ushort voxel = reader.ReadUInt16();
        float temperature = reader.ReadSingle();
        float pressure = reader.ReadSingle();
        float capacity = reader.ReadSingle();
        int count = ReadCount(reader, 1_000_000, "mixture gas");
        var gases = new AtmosGasAmount[count];
        for (int index = 0; index < count; index++) gases[index] = new AtmosGasAmount(reader.ReadInt32(), reader.ReadSingle());
        return new SetVoxelMixtureOperation(position, voxel, temperature, pressure, capacity, gases);
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

    internal static string ReadString(BinaryReader reader, NumosReplayReadOptions options)
    {
        int length = ReadCount(reader, options.MaxStringBytes, "string byte");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();

        return Utf8.GetString(bytes);
    }

    internal static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value != null);
        if (value != null) WriteString(writer, value);
    }

    internal static string? ReadNullableString(BinaryReader reader, NumosReplayReadOptions options)
    {
        return reader.ReadBoolean() ? ReadString(reader, options) : null;
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
                for (int index = 0; index < span.Length; index++) span[index] = values[index];

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

    internal static void Drain(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4096];
        while (stream.Read(buffer) != 0)
        {
        }
    }

    internal static void ValidateOptions(NumosReplayReadOptions options)
    {
        if (options.MaxPayloadBytes <= 0 || options.MaxOperations < 0 || options.MaxStringBytes < 0 || options.MaxMetadataBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    internal sealed record MetadataPayload(
        NumosReplayMetadata Metadata,
        AtmosTimelinePosition Start,
        AtmosTimelinePosition Head,
        ulong OperationCount,
        ulong SimulationCount,
        ulong LinkSetCount)
    {
        /// <summary>
        ///     Checks the metadata's redundant bounds and counts against the decoded payload.
        /// </summary>
        public bool Matches(
            AtmosTimelinePosition start,
            AtmosTimelinePosition head,
            int operationCount,
            int simulationCount,
            int linkSetCount)
        {
            return Start == start &&
                   Head == head &&
                   OperationCount == (ulong)operationCount &&
                   SimulationCount == (ulong)simulationCount &&
                   LinkSetCount == (ulong)linkSetCount;
        }
    }
}