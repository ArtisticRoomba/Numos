using Numos.API;
using Numos.CoreSim.Replay;

namespace Numos.Serialization;

/// <summary>
///     Reads and writes <c>.numos</c> world replay documents without accessing filesystem paths.
/// </summary>
/// <remarks>
///     This owns the container: magic, version, sections, metadata, and read limits. Checkpoint and operation
///     payloads are encoded by the internal codecs that live next to their types, <c>AtmosWorldReplayCodec</c> in
///     Numos.API and <c>AtmosReplayCodec</c> in Numos.CoreSim. See
///     <c>tests/Numos.Serialization.Tests/ReplayWireFormatGoldenTests.cs</c> for the byte-level pin.
/// </remarks>
public static class NumosWorldReplaySerializer
{
    private const ushort ContainerVersion = 1;
    // Kind 1 was the single-simulation replay. Readers recognize it only to reject it with a clear message.
    private const ushort SimulationReplayContentKind = 1;
    private const ushort WorldReplayContentKind = 2;
    private const ushort MetadataSection = 1;
    private const ushort CheckpointSection = 2;
    private const ushort OperationsSection = 3;
    private const ushort RequiredSection = 1;
    private static readonly byte[] Magic = [0x4e, 0x55, 0x4d, 0x4f, 0x53, 0x0d, 0x0a, 0x1a];

    /// <summary>
    ///     Writes a complete-world replay document and leaves the destination open.
    /// </summary>
    /// <param name="destination">Writable destination for one Numos container.</param>
    /// <param name="document">Portable world replay and provenance metadata.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination" /> is not writable.</exception>
    /// <exception cref="NotSupportedException">
    ///     The replay holds state the format cannot encode. Examples are a solver configuration without
    ///     <c>[NumosSerialize]</c> members and a rollback chunk field whose element type is not a primitive or an enum.
    /// </exception>
    /// <remarks>
    ///     Custom world solvers are saved by name, along with any <c>[NumosSerialize]</c> state they hold. Loading such a
    ///     file works anywhere, but replaying it needs a world with the same solvers registered, through
    ///     <see cref="AtmosWorldReplayTimeline.Restore" /> rather than <see cref="AtmosWorldReplayTimeline.Import" />.
    /// </remarks>
    public static void Serialize(Stream destination, NumosWorldReplayDocument document)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(document);
        if (!destination.CanWrite)
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));

        document.Replay.EnsureSerializable();

        var replay = document.Replay;
        using var writer = new BinaryWriter(destination, AtmosReplayCodec.Utf8, true);
        writer.Write(Magic);
        writer.Write(ContainerVersion);
        writer.Write(WorldReplayContentKind);
        writer.Write(3u);

        using var scratch = destination.CanSeek ? null : new MemoryStream();
        WriteSection(destination, writer, scratch, MetadataSection, 0, section => WriteMetadata(section, document.Metadata, replay));
        WriteSection(
            destination,
            writer,
            scratch,
            CheckpointSection,
            RequiredSection,
            section => AtmosWorldReplayCodec.WriteCheckpoint(section, replay));

        WriteSection(
            destination,
            writer,
            scratch,
            OperationsSection,
            RequiredSection,
            section => WriteOperations(section, replay.Recording));
    }

    /// <summary>
    ///     Reads a complete-world replay document and leaves the source open.
    /// </summary>
    /// <param name="source">Readable stream containing exactly one world replay container.</param>
    /// <param name="options">Optional allocation and payload limits for untrusted input.</param>
    /// <returns>Decoded metadata and detached complete-world archive.</returns>
    /// <remarks>
    ///     Rollback chunk fields are looked up in the process-wide <c>ChunkField&lt;T&gt;</c> registry, so register every
    ///     field the file might hold (touch its <c>[ChunkData]</c> property or call <c>ChunkField&lt;T&gt;.Register</c>)
    ///     before loading.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="source" /> is not readable.</exception>
    /// <exception cref="InvalidDataException">
    ///     The container is malformed, inconsistent, or holds unsupported content such as a single-simulation replay
    ///     written by an earlier Numos version, or a chunk field whose saved element type or length doesn't match its
    ///     registration.
    /// </exception>
    /// <exception cref="NotSupportedException">
    ///     The replay contains host-defined state. Examples are a solver configuration whose key is not registered in
    ///     <see cref="NumosReplayReadOptions.SolverConfigurations" /> and a rollback chunk field that the process has
    ///     not registered yet.
    /// </exception>
    public static NumosWorldReplayDocument Deserialize(Stream source, NumosReplayReadOptions? options = null)
    {
        var (metadata, checkpoint, recording) = ReadContainer(source, options);

        if (recording.Start != checkpoint.Checkpoint.Position)
            throw new InvalidDataException("The world recording does not begin at its initial checkpoint.");

        ValidateRecording(recording);
        if (!metadata.Matches(
                recording.Start,
                recording.Head,
                recording.Operations.Count,
                checkpoint.Checkpoint.Simulations.Count,
                checkpoint.Checkpoint.LinkSets.Count))
        {
            throw new InvalidDataException("World replay metadata does not match its payload.");
        }

        var archive = new AtmosWorldReplayArchive(
            checkpoint.Checkpoint,
            recording,
            checkpoint.InitialHash,
            checkpoint.HeadHash);

        archive.EnsureSerializable();
        if (source.ReadByte() != -1) throw new InvalidDataException("The Numos file has trailing data.");

        return new NumosWorldReplayDocument(metadata.Metadata, archive);
    }

    /// <summary>
    ///     Writes one section header and payload, encoding the payload exactly once.
    /// </summary>
    /// <remarks>
    ///     A seekable destination gets a placeholder length, which the writer patches after the payload. Any other
    ///     destination gets the payload staged in <paramref name="scratch" /> first, because the length precedes the
    ///     payload on the wire.
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
        using (var sectionWriter = new BinaryWriter(scratch, AtmosReplayCodec.Utf8, true))
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
    private static (MetadataPayload Metadata,
        (AtmosWorldCheckpoint Checkpoint, AtmosWorldStateHash InitialHash, AtmosWorldStateHash HeadHash) Checkpoint,
        AtmosWorldRecording Recording)
        ReadContainer(Stream source, NumosReplayReadOptions? options)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("The source stream must be readable.", nameof(source));

        options ??= new NumosReplayReadOptions();
        if (options.MaxPayloadBytes <= 0 || options.MaxOperations < 0 || options.MaxStringBytes < 0 || options.MaxMetadataBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(options));

        ArgumentNullException.ThrowIfNull(options.SolverConfigurations, nameof(options));

        using var reader = new BinaryReader(source, AtmosReplayCodec.Utf8, true);
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
        (AtmosWorldCheckpoint, AtmosWorldStateHash, AtmosWorldStateHash)? checkpoint = null;
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
            using var section = new ReplayReader(limited, options.SolverConfigurations);
            switch (id)
            {
                case MetadataSection:
                    if (metadata != null) throw new InvalidDataException("The replay contains duplicate metadata sections.");

                    metadata = ReadMetadata(section, options.MaxStringBytes);
                    break;
                case CheckpointSection:
                    if (checkpoint != null) throw new InvalidDataException("The replay contains duplicate checkpoint sections.");

                    checkpoint = AtmosWorldReplayCodec.ReadCheckpoint(section, options.MaxStringBytes);
                    break;
                case OperationsSection:
                    if (recording != null) throw new InvalidDataException("The replay contains duplicate operation sections.");

                    recording = ReadOperations(section, options);
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

        return (metadata, checkpoint.Value, recording);
    }

    /// <summary>
    ///     Writes the metadata section, including redundant bounds and counts the reader checks against the payload.
    /// </summary>
    private static void WriteMetadata(BinaryWriter writer, NumosReplayMetadata metadata, AtmosWorldReplayArchive replay)
    {
        AtmosReplayCodec.WriteString(writer, metadata.ProjectName);
        writer.Write(metadata.CreatedUtc.UtcTicks);
        AtmosReplayCodec.WriteString(writer, metadata.ProducerName);
        AtmosReplayCodec.WriteString(writer, metadata.ProducerVersion);
        AtmosReplayCodec.WriteString(writer, metadata.CoreSimVersion);
        AtmosReplayCodec.WriteNullableString(writer, metadata.SourceReference);
        AtmosReplayCodec.WritePosition(writer, replay.Recording.Start);
        AtmosReplayCodec.WritePosition(writer, replay.Recording.Head);
        writer.Write(checked((ulong)replay.Recording.Operations.Count));
        writer.Write(checked((ulong)replay.InitialCheckpoint.Simulations.Count));
        writer.Write(checked((ulong)replay.InitialCheckpoint.LinkSets.Count));
    }

    private static MetadataPayload ReadMetadata(BinaryReader reader, int maxStringBytes)
    {
        string projectName = AtmosReplayCodec.ReadString(reader, maxStringBytes);
        long utcTicks = reader.ReadInt64();
        string producerName = AtmosReplayCodec.ReadString(reader, maxStringBytes);
        string producerVersion = AtmosReplayCodec.ReadString(reader, maxStringBytes);
        string coreVersion = AtmosReplayCodec.ReadString(reader, maxStringBytes);
        string? sourceReference = AtmosReplayCodec.ReadNullableString(reader, maxStringBytes);
        var start = AtmosReplayCodec.ReadPosition(reader);
        var head = AtmosReplayCodec.ReadPosition(reader);
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
    ///     Writes the operations section: the recording bounds, then each operation's position, opcode, and
    ///     length-prefixed payload.
    /// </summary>
    /// <remarks>
    ///     Payloads are staged in one reused buffer so each is encoded once, and so an unserializable operation throws
    ///     before any of its bytes reach <paramref name="writer" />.
    /// </remarks>
    private static void WriteOperations(BinaryWriter writer, AtmosWorldRecording recording)
    {
        AtmosReplayCodec.WritePosition(writer, recording.Start);
        AtmosReplayCodec.WritePosition(writer, recording.Head);
        writer.Write(recording.Operations.Count);
        using var payload = new MemoryStream();
        using var payloadWriter = new BinaryWriter(payload, AtmosReplayCodec.Utf8, true);
        foreach (var recorded in recording.Operations)
        {
            AtmosReplayCodec.WritePosition(writer, recorded.Position);
            writer.Write((ushort)recorded.Code);
            payload.SetLength(0);
            AtmosWorldReplayCodec.WriteOperation(payloadWriter, recorded.Operation);
            writer.Write(checked((uint)payload.Length));
            writer.Write(payload.GetBuffer(), 0, (int)payload.Length);
        }
    }

    /// <summary>
    ///     Reads an operations section written by <see cref="WriteOperations" />, bounding each payload to its declared
    ///     length.
    /// </summary>
    private static AtmosWorldRecording ReadOperations(BinaryReader reader, NumosReplayReadOptions options)
    {
        var start = AtmosReplayCodec.ReadPosition(reader);
        var head = AtmosReplayCodec.ReadPosition(reader);
        int count = AtmosReplayCodec.ReadCount(reader, options.MaxOperations, "world operation");
        var operations = new AtmosWorldRecordedOperation[count];
        var limited = new LimitedReadStream(reader.BaseStream, 0);
        using var payload = new ReplayReader(limited, options.SolverConfigurations);
        for (int index = 0; index < count; index++)
        {
            var position = AtmosReplayCodec.ReadPosition(reader);
            ushort code = reader.ReadUInt16();
            limited.Reset(reader.ReadUInt32());
            operations[index] = new AtmosWorldRecordedOperation(
                position,
                AtmosWorldReplayCodec.ReadOperation(payload, code, options.MaxStringBytes));

            if (limited.Remaining != 0) throw new InvalidDataException($"World replay opcode {code} has unexpected trailing data.");
        }

        return AtmosWorldRecording.Adopt(start, head, operations);
    }

    private static void Drain(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4096];
        while (stream.Read(buffer) != 0)
        {
        }
    }

    private static void ValidateRecording(AtmosWorldRecording recording)
    {
        switch (ReplayHistory.Validate(recording.Start, recording.Operations, recording.Head, true))
        {
            case ReplayHistoryError.None:
                return;
            case ReplayHistoryError.TargetBeforeStart:
                throw new InvalidDataException("The world replay bounds are invalid.");
            case ReplayHistoryError.MissingTarget:
                throw new InvalidDataException("World operation history does not reach its declared head.");
            default:
                throw new InvalidDataException("World operations are not contiguous and ordered within the recording bounds.");
        }
    }

    private sealed record MetadataPayload(
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