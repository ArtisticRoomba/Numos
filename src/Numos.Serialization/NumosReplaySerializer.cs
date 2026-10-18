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
///     Reads and writes the portable Numos binary replay container without accessing filesystem paths.
/// </summary>
/// <remarks>
///     The <c>[WireOperation]</c> attributes below register every <see cref="AtmosOperationCode" /> with
///     Numos.Replay.SourceGen, which generates this class's <c>TryWriteGeneratedOperation</c>/
///     <c>TryReadGeneratedOperation</c> partial-class members. Field order for the mechanical ones is exactly
///     constructor-declaration order; see <c>tests/Numos.API.Tests/ReplayWireFormatGoldenTests.cs</c> for the
///     byte-level pin that codec must not silently drift from.
///     <para>
///         <see cref="SetAtmosConfigOperation" /> (nested config snapshot) and
///         <see cref="SetVoxelMixtureOperation" /> (variable-length gas list) don't fit the generator's mechanical
///         field-order mapping, so they register as <c>Custom</c>: the generator plugs calls to the hand-written
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
[WireOperation(typeof(SetSolverEnabledOperation), AtmosOperationCode.SetSolverEnabled)]
[WireOperation(typeof(SetAtmosConfigOperation), AtmosOperationCode.SetAtmosConfig, Custom = true)]
[WireOperation(typeof(SetVoxelMixtureOperation), AtmosOperationCode.SetVoxelMixture, Custom = true)]
public static partial class NumosReplaySerializer
{
    internal const ushort ContainerVersion = 1;
    internal const ushort ReplayContentKind = 1;
    internal const ushort WorldReplayContentKind = 2;
    internal const ushort MetadataSection = 1;
    internal const ushort CheckpointSection = 2;
    internal const ushort OperationsSection = 3;
    internal const ushort RequiredSection = 1;
    internal readonly static byte[] Magic = [0x4e, 0x55, 0x4d, 0x4f, 0x53, 0x0d, 0x0a, 0x1a];
    internal readonly static Encoding Utf8 = new UTF8Encoding(false, true);

    /// <summary>
    ///     Writes any supported replay document to a writable stream and leaves the stream open.
    /// </summary>
    /// <param name="destination">The stream that receives the complete Numos container.</param>
    /// <param name="document">A component or complete-world replay document.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">The stream is not writable.</exception>
    /// <exception cref="NotSupportedException">The document type or host-defined replay state is unsupported.</exception>
    public static void Serialize(Stream destination, INumosReplayDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        switch (document)
        {
            case NumosReplayDocument component:
                Serialize(destination, component);
                break;
            case NumosWorldReplayDocument world:
                NumosWorldReplaySerializer.Serialize(destination, world);
                break;
            default:
                throw new NotSupportedException($"Replay document type '{document.GetType().FullName}' is unsupported.");
        }
    }

    /// <summary>
    ///     Reads either supported replay content kind and leaves the source stream open.
    /// </summary>
    /// <param name="source">The readable stream containing exactly one Numos container.</param>
    /// <param name="options">Optional allocation and payload limits for untrusted input.</param>
    /// <returns>A component or complete-world replay document selected by the content discriminator.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">The stream is not readable.</exception>
    /// <exception cref="InvalidDataException">The container is malformed, unsupported, or inconsistent.</exception>
    /// <exception cref="NotSupportedException">The replay contains host-defined state.</exception>
    public static INumosReplayDocument DeserializeDocument(Stream source, NumosReplayReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("The source stream must be readable.", nameof(source));

        options ??= new NumosReplayReadOptions();
        ValidateOptions(options);

        var readable = source;
        MemoryStream? buffered = null;
        long origin = 0;
        if (source.CanSeek)
        {
            origin = source.Position;
        }
        else
        {
            buffered = new MemoryStream();
            try
            {
                long maximumBufferedBytes = options.MaxPayloadBytes > long.MaxValue - 16L * 1024
                    ? long.MaxValue
                    : options.MaxPayloadBytes + 16L * 1024;

                Span<byte> copyBuffer = stackalloc byte[8192];
                int read;
                while ((read = source.Read(copyBuffer)) != 0)
                {
                    if (buffered.Length + read > maximumBufferedBytes)
                        throw new InvalidDataException("The Numos file exceeds the configured payload limit.");

                    buffered.Write(copyBuffer[..read]);
                }

                buffered.Position = 0;
                readable = buffered;
            }
            catch
            {
                buffered.Dispose();
                throw;
            }
        }

        try
        {
            using var reader = new BinaryReader(readable, Utf8, true);
            if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
                throw new InvalidDataException("The stream is not a Numos file.");

            if (reader.ReadUInt16() != ContainerVersion)
                throw new InvalidDataException("The Numos container version is unsupported.");

            ushort kind = reader.ReadUInt16();
            readable.Position = source.CanSeek ? origin : 0;
            return kind switch
            {
                ReplayContentKind => Deserialize(readable, options),
                WorldReplayContentKind => NumosWorldReplaySerializer.Deserialize(readable, options),
                _ => throw new InvalidDataException($"Numos content kind {kind} is unsupported.")
            };
        }
        finally
        {
            buffered?.Dispose();
        }
    }

    /// <summary>
    ///     Writes a replay document to a writable stream and leaves the stream open.
    /// </summary>
    /// <param name="destination">The stream that receives the complete replay container.</param>
    /// <param name="document">Replay state and provenance metadata to write.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">The stream is not writable.</exception>
    /// <exception cref="NotSupportedException">
    ///     The replay contains host-defined state that the portable format cannot
    ///     represent.
    /// </exception>
    public static void Serialize(Stream destination, NumosReplayDocument document)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(document);
        if (!destination.CanWrite) throw new ArgumentException("The destination stream must be writable.", nameof(destination));

        document.Replay.EnsurePortable();

        var replay = document.Replay;
        WriteContainer(
            destination,
            ReplayContentKind,
            section => WriteMetadata(
                section,
                document.Metadata,
                replay.Recording.Start,
                replay.Recording.Head,
                replay.Recording.Operations.Count,
                replay.InitialCheckpoint.Chunks.Count),
            section => WriteSimulationCheckpoint(section, replay.InitialCheckpoint, (replay.InitialStateHash, replay.HeadStateHash)),
            section => WriteOperations(section, replay.Recording));
    }

    /// <summary>
    ///     Reads a replay document from a readable stream and leaves the stream open.
    /// </summary>
    /// <param name="source">The stream containing exactly one Numos replay container.</param>
    /// <param name="options">Optional allocation and payload limits for untrusted input.</param>
    /// <returns>The decoded metadata and detached replay archive.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">The stream is not readable.</exception>
    /// <exception cref="InvalidDataException">The container is malformed, unsupported, or internally inconsistent.</exception>
    /// <exception cref="NotSupportedException">
    ///     The replay contains host-defined state that the portable format cannot
    ///     reconstruct.
    /// </exception>
    public static NumosReplayDocument Deserialize(Stream source, NumosReplayReadOptions? options = null)
    {
        var (metadata, checkpoint, recording) =
            ReadContainer(source, options, ReplayContentKind, "replay", 1, ReadCheckpoint, ReadOperations);

        if (recording.Start != checkpoint.Checkpoint.Position)
            throw new InvalidDataException("The recording does not begin at the initial checkpoint.");

        ValidateRecording(recording);
        if (!metadata.Matches(recording.Start, recording.Head, recording.Operations.Count, checkpoint.Checkpoint.Chunks.Count))
            throw new InvalidDataException("The replay metadata does not match its payload.");

        var archive = new AtmosReplayArchive(
            checkpoint.Checkpoint,
            recording,
            checkpoint.InitialHash,
            checkpoint.HeadHash);

        archive.EnsurePortable();
        EnsureEndOfContainer(source);
        return new NumosReplayDocument(metadata.Metadata, archive);
    }

    /// <summary>
    ///     Writes the container header and the three required sections shared by both content kinds.
    /// </summary>
    internal static void WriteContainer(
        Stream destination,
        ushort contentKind,
        Action<BinaryWriter> writeMetadata,
        Action<BinaryWriter> writeCheckpoint,
        Action<BinaryWriter> writeOperations)
    {
        using var writer = new BinaryWriter(destination, Utf8, true);
        writer.Write(Magic);
        writer.Write(ContainerVersion);
        writer.Write(contentKind);
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
    ///     Validates the container header and decodes the metadata, checkpoint, and operation sections shared by both
    ///     content kinds. Unknown optional sections are skipped. <paramref name="payloadCountFields" /> is the number of
    ///     content-specific counts at the end of the metadata section.
    /// </summary>
    internal static (MetadataPayload Metadata, TCheckpoint Checkpoint, TRecording Recording) ReadContainer<TCheckpoint, TRecording>(
        Stream source,
        NumosReplayReadOptions? options,
        ushort contentKind,
        string contentName,
        int payloadCountFields,
        Func<BinaryReader, NumosReplayReadOptions, TCheckpoint> readCheckpoint,
        Func<BinaryReader, NumosReplayReadOptions, TRecording> readOperations)
        where TCheckpoint : class
        where TRecording : class
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("The source stream must be readable.", nameof(source));

        options ??= new NumosReplayReadOptions();
        ValidateOptions(options);
        using var reader = new BinaryReader(source, Utf8, true);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException("The stream is not a Numos file.");
        if (reader.ReadUInt16() != ContainerVersion) throw new InvalidDataException("The Numos container version is unsupported.");
        if (reader.ReadUInt16() != contentKind) throw new InvalidDataException($"The Numos file does not contain a {contentName}.");

        uint sectionCount = reader.ReadUInt32();
        if (sectionCount > 1024) throw new InvalidDataException("The Numos file declares too many sections.");

        MetadataPayload? metadata = null;
        TCheckpoint? checkpoint = null;
        TRecording? recording = null;
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

                    metadata = ReadMetadata(section, options, payloadCountFields);
                    break;
                case CheckpointSection:
                    if (checkpoint != null) throw new InvalidDataException("The replay contains duplicate checkpoint sections.");

                    checkpoint = readCheckpoint(section, options);
                    break;
                case OperationsSection:
                    if (recording != null) throw new InvalidDataException("The replay contains duplicate operation sections.");

                    recording = readOperations(section, options);
                    break;
                default:
                    if ((flags & RequiredSection) != 0) throw new InvalidDataException($"Required Numos section {id} is unsupported.");

                    Drain(limited);
                    break;
            }

            if (limited.Remaining != 0) throw new InvalidDataException($"Numos section {id} has unexpected trailing data.");
        }

        if (metadata == null || checkpoint == null || recording == null)
            throw new InvalidDataException($"The {contentName} is missing a required section.");

        return (metadata, checkpoint, recording);
    }

    internal static void EnsureEndOfContainer(Stream source)
    {
        if (source.ReadByte() != -1) throw new InvalidDataException("The Numos file has trailing data.");
    }

    /// <summary>
    ///     Writes the metadata section. <paramref name="payloadCounts" /> are the content kind's own consistency counts
    ///     (chunks for a replay; simulations and link sets for a world replay).
    /// </summary>
    internal static void WriteMetadata(
        BinaryWriter writer,
        NumosReplayMetadata metadata,
        AtmosTimelinePosition start,
        AtmosTimelinePosition head,
        int operationCount,
        params ReadOnlySpan<int> payloadCounts)
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
        foreach (int count in payloadCounts) writer.Write(checked((ulong)count));
    }

    private static MetadataPayload ReadMetadata(BinaryReader reader, NumosReplayReadOptions options, int payloadCountFields)
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
        ulong[] payloadCounts = new ulong[payloadCountFields];
        for (int index = 0; index < payloadCounts.Length; index++) payloadCounts[index] = reader.ReadUInt64();
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
            payloadCounts);
    }

    private static CheckpointPayload ReadCheckpoint(BinaryReader reader, NumosReplayReadOptions options)
    {
        var checkpoint = ReadSimulationCheckpoint(reader, options, true, out var initialHash, out var headHash);
        if (initialHash.Position != checkpoint.Position) throw new InvalidDataException("The initial hash position is invalid.");

        return new CheckpointPayload(checkpoint, initialHash, headHash);
    }

    /// <summary>
    ///     Writes one simulation checkpoint. A single-simulation replay carries its initial and head state hashes
    ///     inline after the position; checkpoints nested in a world checkpoint have none.
    /// </summary>
    internal static void WriteSimulationCheckpoint(
        BinaryWriter writer,
        AtmosSimulationCheckpoint checkpoint,
        (AtmosStateHash Initial, AtmosStateHash Head)? hashes)
    {
        writer.Write(checkpoint.FormatVersion);
        writer.Write(checkpoint.CompatibilityVersion);
        writer.Write(checkpoint.CompatibilityFingerprint);
        WriteInt3(writer, checkpoint.Dimensions);
        WritePosition(writer, checkpoint.Position);
        if (hashes is var (initial, head))
        {
            WriteHash(writer, initial);
            WriteHash(writer, head);
        }

        WriteConfig(writer, checkpoint.Config);
        writer.Write(checkpoint.Solvers.Count);
        foreach (var solver in checkpoint.Solvers)
        {
            WriteString(writer, solver.Name);
            writer.Write(solver.IsCustom);
            writer.Write(solver.Enabled);
            WriteNullableString(writer, solver.NeighborSelectionKey);
        }

        writer.Write(checkpoint.Chunks.Count);
        foreach (var chunk in checkpoint.Chunks) WriteChunk(writer, chunk);
    }

    /// <summary>
    ///     Reads one simulation checkpoint written by <see cref="WriteSimulationCheckpoint" />. The hashes are only
    ///     present on the wire when <paramref name="hasHashes" /> is set; otherwise they come back as default.
    /// </summary>
    internal static AtmosSimulationCheckpoint ReadSimulationCheckpoint(
        BinaryReader reader,
        NumosReplayReadOptions options,
        bool hasHashes,
        out AtmosStateHash initialHash,
        out AtmosStateHash headHash)
    {
        int format = reader.ReadInt32();
        if (format != AtmosSimulationCheckpoint.CurrentFormatVersion)
            throw new InvalidDataException("The simulation checkpoint schema is unsupported.");

        int compatibility = reader.ReadInt32();
        if (compatibility != AtmosSimulationCheckpoint.CurrentCompatibilityVersion)
            throw new InvalidDataException("The simulation checkpoint compatibility version is unsupported.");

        ulong fingerprint = reader.ReadUInt64();
        var dimensions = ReadInt3(reader);
        var position = ReadPosition(reader);
        initialHash = hasHashes ? ReadHash(reader) : default;
        headHash = hasHashes ? ReadHash(reader) : default;
        var config = ReadConfig(reader, options);
        int solverCount = ReadCount(reader, 1024, "solver");
        var solvers = new AtmosSolverCheckpoint[solverCount];
        for (int index = 0; index < solverCount; index++)
        {
            string name = ReadString(reader, options);
            bool isCustom = reader.ReadBoolean();
            bool enabled = reader.ReadBoolean();
            string? selectionKey = ReadNullableString(reader, options);
            if (string.IsNullOrWhiteSpace(name) || selectionKey != null && string.IsNullOrWhiteSpace(selectionKey))
                throw new InvalidDataException("The solver checkpoint metadata is invalid.");

            solvers[index] = new AtmosSolverCheckpoint(name, isCustom, enabled, selectionKey);
        }

        int chunkCount = ReadCount(reader, 1_000_000, "chunk");
        var chunks = new AtmosChunkCheckpoint[chunkCount];
        for (int index = 0; index < chunkCount; index++) chunks[index] = ReadChunk(reader);
        var checkpoint = new AtmosSimulationCheckpoint(dimensions, position, config, solvers, chunks);
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

    private static void WriteOperations(BinaryWriter writer, AtmosRecording recording)
    {
        WriteOperations(
            writer,
            recording.Start,
            recording.Head,
            recording.Operations,
            static recorded => (recorded.Position, (ushort)recorded.Code),
            static (payload, recorded) => WriteOperation(payload, recorded.Operation));
    }

    private static AtmosRecording ReadOperations(BinaryReader reader, NumosReplayReadOptions options)
    {
        var (start, head, operations) = ReadOperations(
            reader,
            options,
            "operation",
            "Replay opcode",
            static (payload, code, options, position) => new AtmosRecordedOperation(position, ReadOperation(payload, code, options)));

        return new AtmosRecording(start, head, operations);
    }

    /// <summary>
    ///     Writes an operations section: the recording bounds, then each operation's position, opcode, and
    ///     length-prefixed payload.
    /// </summary>
    /// <remarks>
    ///     Payloads are staged in one reused buffer so each is encoded once, and so an unserializable operation throws
    ///     before any of its bytes reach <paramref name="writer" />.
    /// </remarks>
    internal static void WriteOperations<TRecorded>(
        BinaryWriter writer,
        AtmosTimelinePosition start,
        AtmosTimelinePosition head,
        IReadOnlyList<TRecorded> operations,
        Func<TRecorded, (AtmosTimelinePosition Position, ushort Code)> envelope,
        Action<BinaryWriter, TRecorded> writePayload)
    {
        WritePosition(writer, start);
        WritePosition(writer, head);
        writer.Write(operations.Count);
        using var payload = new MemoryStream();
        using var payloadWriter = new BinaryWriter(payload, Utf8, true);
        foreach (var recorded in operations)
        {
            var (position, code) = envelope(recorded);
            WritePosition(writer, position);
            writer.Write(code);
            payload.SetLength(0);
            writePayload(payloadWriter, recorded);
            writer.Write(checked((uint)payload.Length));
            writer.Write(payload.GetBuffer(), 0, (int)payload.Length);
        }
    }

    /// <summary>
    ///     Reads an operations section written by <see cref="WriteOperations{TRecorded}" />, bounding each payload to its
    ///     declared length.
    /// </summary>
    internal static (AtmosTimelinePosition Start, AtmosTimelinePosition Head, TRecorded[] Operations) ReadOperations<TRecorded>(
        BinaryReader reader,
        NumosReplayReadOptions options,
        string countName,
        string opcodeLabel,
        Func<BinaryReader, ushort, NumosReplayReadOptions, AtmosTimelinePosition, TRecorded> readOperation)
    {
        var start = ReadPosition(reader);
        var head = ReadPosition(reader);
        int count = ReadCount(reader, options.MaxOperations, countName);
        var operations = new TRecorded[count];
        var limited = new LimitedReadStream(reader.BaseStream, 0);
        using var payload = new BinaryReader(limited, Utf8, true);
        for (int index = 0; index < count; index++)
        {
            var position = ReadPosition(reader);
            ushort code = reader.ReadUInt16();
            limited.Reset(reader.ReadUInt32());
            operations[index] = readOperation(payload, code, options, position);
            if (limited.Remaining != 0) throw new InvalidDataException($"{opcodeLabel} {code} has unexpected trailing data.");
        }

        return (start, head, operations);
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

    private static void WriteSetAtmosConfigOperation(BinaryWriter writer, SetAtmosConfigOperation operation)
    {
        WriteConfig(writer, operation.Config);
    }

    private static SetAtmosConfigOperation ReadSetAtmosConfigOperation(BinaryReader reader, NumosReplayReadOptions options)
    {
        return new SetAtmosConfigOperation(ReadConfig(reader, options));
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

    internal static void WriteHash(BinaryWriter writer, AtmosStateHash hash)
    {
        WritePosition(writer, hash.Position);
        writer.Write(hash.Digest);
    }

    private static AtmosStateHash ReadHash(BinaryReader reader)
    {
        return new AtmosStateHash(ReadPosition(reader), reader.ReadUInt64());
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

    private static void ValidateRecording(AtmosRecording recording)
    {
        switch (ReplayHistoryValidator.Validate(recording.Start, recording.Operations, recording.Head, true))
        {
            case ReplayHistoryError.None:
                return;
            case ReplayHistoryError.TargetBeforeStart:
                throw new InvalidDataException("The replay recording bounds are invalid.");
            case ReplayHistoryError.MissingTarget:
                throw new InvalidDataException("The replay operation history does not reach its declared head.");
            default:
                throw new InvalidDataException("Replay operations are not contiguous and ordered within the recording bounds.");
        }
    }

    private sealed record CheckpointPayload(AtmosSimulationCheckpoint Checkpoint, AtmosStateHash InitialHash, AtmosStateHash HeadHash);

    internal sealed record MetadataPayload(
        NumosReplayMetadata Metadata,
        AtmosTimelinePosition Start,
        AtmosTimelinePosition Head,
        ulong OperationCount,
        ulong[] PayloadCounts)
    {
        /// <summary>
        ///     Checks the metadata's redundant bounds and counts against the decoded payload.
        /// </summary>
        public bool Matches(
            AtmosTimelinePosition start,
            AtmosTimelinePosition head,
            int operationCount,
            params ReadOnlySpan<int> payloadCounts)
        {
            if (Start != start || Head != head || OperationCount != (ulong)operationCount ||
                PayloadCounts.Length != payloadCounts.Length)
                return false;

            for (int index = 0; index < payloadCounts.Length; index++)
            {
                if (PayloadCounts[index] != (ulong)payloadCounts[index])
                    return false;
            }

            return true;
        }
    }
}