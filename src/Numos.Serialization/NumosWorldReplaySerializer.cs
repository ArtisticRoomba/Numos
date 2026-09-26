using Numos.API;
using Numos.Chunks;
using Numos.CoreSim.Replay;
using Numos.Replay.SourceGen;

namespace Numos.Serialization;

/// <summary>
///     Reads and writes complete multi-simulation world replay documents.
/// </summary>
/// <remarks>
///     The <c>[WireOperation]</c> attributes below register every <see cref="AtmosWorldOperationCode" /> with
///     Numos.Replay.SourceGen, which generates this class's <c>TryWriteGeneratedOperation</c>/
///     <c>TryReadGeneratedOperation</c> partial-class members. Field order for the mechanical ones is exactly
///     constructor-declaration order; see <c>tests/Numos.API.Tests/ReplayWireFormatGoldenTests.cs</c> for the
///     byte-level pin that codec must not silently drift from.
///     <para>
///         <see cref="AtmosWorldSimulationOperation" />'s <c>Operation</c> field is a nested simulation operation --
///         the generator writes its opcode and then delegates to <see cref="NumosReplaySerializer" />'s own codec
///         for the payload, rather than treating it as an opaque unsupported type.
///     </para>
///     <para>
///         <see cref="SetAtmosWorldConfigOperation" /> (nested config snapshot) and
///         <see cref="CreateAtmosLinkSetOperation" /> (variable-length link list) don't fit the generator's
///         mechanical field-order mapping, so they register as <c>Custom</c>, same as their sim-family
///         counterparts.
///     </para>
///     <para>
///         Nothing yet enforces that every <see cref="AtmosWorldOperationCode" /> member has one of these
///         registrations -- forgetting one fails at replay time, not at build time. That consistency check is
///         Phase 5's analyzer.
///     </para>
/// </remarks>
[WireOperation(typeof(AtmosWorldSimulationOperation), AtmosWorldOperationCode.SimulationOperation)]
[WireOperation(typeof(CreateAtmosSimulationOperation), AtmosWorldOperationCode.CreateSimulation)]
[WireOperation(typeof(DestroyAtmosSimulationOperation), AtmosWorldOperationCode.DestroySimulation)]
[WireOperation(typeof(DestroyAtmosLinkSetOperation), AtmosWorldOperationCode.DestroyLinkSet)]
[WireOperation(typeof(SetAtmosWorldSolverEnabledOperation), AtmosWorldOperationCode.SetSolverEnabled)]
[WireOperation(typeof(SetAtmosWorldConfigOperation), AtmosWorldOperationCode.SetAtmosConfig, Custom = true)]
[WireOperation(typeof(CreateAtmosLinkSetOperation), AtmosWorldOperationCode.CreateLinkSet, Custom = true)]
public static partial class NumosWorldReplaySerializer
{
    /// <summary>
    ///     Writes a complete-world replay document and leaves the destination open.
    /// </summary>
    /// <param name="destination">Writable destination for one Numos container.</param>
    /// <param name="document">Portable world replay and provenance metadata.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination" /> is not writable.</exception>
    /// <exception cref="NotSupportedException">The replay contains host-defined state.</exception>
    public static void Serialize(Stream destination, NumosWorldReplayDocument document)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(document);
        if (!destination.CanWrite)
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));

        document.Replay.EnsurePortable();

        using var writer = new BinaryWriter(destination, NumosReplaySerializer.Utf8, true);
        writer.Write(NumosReplaySerializer.Magic);
        writer.Write(NumosReplaySerializer.ContainerVersion);
        writer.Write(NumosReplaySerializer.WorldReplayContentKind);
        writer.Write(3u);
        NumosReplaySerializer.WriteSection(
            writer,
            NumosReplaySerializer.MetadataSection,
            0,
            section => WriteMetadata(section, document));

        NumosReplaySerializer.WriteSection(
            writer,
            NumosReplaySerializer.CheckpointSection,
            NumosReplaySerializer.RequiredSection,
            section => WriteCheckpoint(section, document.Replay));

        NumosReplaySerializer.WriteSection(
            writer,
            NumosReplaySerializer.OperationsSection,
            NumosReplaySerializer.RequiredSection,
            section => WriteOperations(section, document.Replay.Recording));
    }

    /// <summary>
    ///     Reads a complete-world replay document and leaves the source open.
    /// </summary>
    /// <param name="source">Readable stream containing exactly one world replay container.</param>
    /// <param name="options">Optional allocation and payload limits for untrusted input.</param>
    /// <returns>Decoded metadata and detached complete-world archive.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="source" /> is not readable.</exception>
    /// <exception cref="InvalidDataException">The container is malformed, unsupported, or inconsistent.</exception>
    /// <exception cref="NotSupportedException">The replay contains host-defined state.</exception>
    public static NumosWorldReplayDocument Deserialize(Stream source, NumosReplayReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("The source stream must be readable.", nameof(source));

        options ??= new NumosReplayReadOptions();
        NumosReplaySerializer.ValidateOptions(options);

        using var reader = new BinaryReader(source, NumosReplaySerializer.Utf8, true);
        if (!reader.ReadBytes(NumosReplaySerializer.Magic.Length).SequenceEqual(NumosReplaySerializer.Magic))
            throw new InvalidDataException("The stream is not a Numos file.");

        if (reader.ReadUInt16() != NumosReplaySerializer.ContainerVersion)
            throw new InvalidDataException("The Numos container version is unsupported.");

        if (reader.ReadUInt16() != NumosReplaySerializer.WorldReplayContentKind)
            throw new InvalidDataException("The Numos file does not contain a world replay.");

        uint sectionCount = reader.ReadUInt32();
        if (sectionCount > 1024)
            throw new InvalidDataException("The Numos file declares too many sections.");

        WorldMetadataPayload? metadata = null;
        WorldCheckpointPayload? checkpoint = null;
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
            if (id == NumosReplaySerializer.MetadataSection && rawLength > (ulong)options.MaxMetadataBytes)
                throw new InvalidDataException("The replay metadata exceeds the configured limit.");

            var limited = new LimitedReadStream(source, (long)rawLength);
            using var section = new BinaryReader(limited, NumosReplaySerializer.Utf8, true);
            switch (id)
            {
                case NumosReplaySerializer.MetadataSection:
                    if (metadata != null)
                        throw new InvalidDataException("The replay contains duplicate metadata sections.");

                    metadata = ReadMetadata(section, options);
                    break;
                case NumosReplaySerializer.CheckpointSection:
                    if (checkpoint != null)
                        throw new InvalidDataException("The replay contains duplicate checkpoint sections.");

                    checkpoint = ReadCheckpoint(section, options);
                    break;
                case NumosReplaySerializer.OperationsSection:
                    if (recording != null)
                        throw new InvalidDataException("The replay contains duplicate operation sections.");

                    recording = ReadOperations(section, options);
                    break;
                default:
                    if ((flags & NumosReplaySerializer.RequiredSection) != 0)
                        throw new InvalidDataException($"Required Numos section {id} is unsupported.");

                    NumosReplaySerializer.Drain(limited);
                    break;
            }

            if (limited.Remaining != 0)
                throw new InvalidDataException($"Numos section {id} has unexpected trailing data.");
        }

        if (metadata == null || checkpoint == null || recording == null)
            throw new InvalidDataException("The world replay is missing a required section.");

        if (recording.Start != checkpoint.Checkpoint.Position)
            throw new InvalidDataException("The world recording does not begin at its initial checkpoint.");

        ValidateRecording(recording);
        if (metadata.Start != recording.Start ||
            metadata.Head != recording.Head ||
            metadata.OperationCount != (ulong)recording.Operations.Count ||
            metadata.SimulationCount != (ulong)checkpoint.Checkpoint.Simulations.Count ||
            metadata.LinkSetCount != (ulong)checkpoint.Checkpoint.LinkSets.Count)
        {
            throw new InvalidDataException("World replay metadata does not match its payload.");
        }

        var archive = new AtmosWorldReplayArchive(
            checkpoint.Checkpoint,
            recording,
            checkpoint.InitialHash,
            checkpoint.HeadHash);

        archive.EnsurePortable();
        if (source.ReadByte() != -1)
            throw new InvalidDataException("The Numos file has trailing data.");

        return new NumosWorldReplayDocument(metadata.Metadata, archive);
    }

    private static void WriteMetadata(BinaryWriter writer, NumosWorldReplayDocument document)
    {
        NumosReplaySerializer.WriteString(writer, document.Metadata.ProjectName);
        writer.Write(document.Metadata.CreatedUtc.UtcTicks);
        NumosReplaySerializer.WriteString(writer, document.Metadata.ProducerName);
        NumosReplaySerializer.WriteString(writer, document.Metadata.ProducerVersion);
        NumosReplaySerializer.WriteString(writer, document.Metadata.CoreSimVersion);
        NumosReplaySerializer.WriteNullableString(writer, document.Metadata.SourceReference);
        NumosReplaySerializer.WritePosition(writer, document.Replay.Recording.Start);
        NumosReplaySerializer.WritePosition(writer, document.Replay.Recording.Head);
        writer.Write(checked((ulong)document.Replay.Recording.Operations.Count));
        writer.Write(checked((ulong)document.Replay.InitialCheckpoint.Simulations.Count));
        writer.Write(checked((ulong)document.Replay.InitialCheckpoint.LinkSets.Count));
    }

    private static WorldMetadataPayload ReadMetadata(BinaryReader reader, NumosReplayReadOptions options)
    {
        string projectName = NumosReplaySerializer.ReadString(reader, options);
        long utcTicks = reader.ReadInt64();
        string producerName = NumosReplaySerializer.ReadString(reader, options);
        string producerVersion = NumosReplaySerializer.ReadString(reader, options);
        string coreVersion = NumosReplaySerializer.ReadString(reader, options);
        string? sourceReference = NumosReplaySerializer.ReadNullableString(reader, options);
        var start = NumosReplaySerializer.ReadPosition(reader);
        var head = NumosReplaySerializer.ReadPosition(reader);
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

        return new WorldMetadataPayload(
            new NumosReplayMetadata(projectName, created, producerName, producerVersion, coreVersion, sourceReference),
            start,
            head,
            operationCount,
            simulationCount,
            linkSetCount);
    }

    private static void WriteCheckpoint(BinaryWriter writer, AtmosWorldReplayArchive archive)
    {
        var checkpoint = archive.InitialCheckpoint;
        writer.Write(checkpoint.FormatVersion);
        NumosReplaySerializer.WritePosition(writer, checkpoint.Position);
        writer.Write(checkpoint.TopologyVersion);
        WriteHash(writer, archive.InitialStateHash);
        WriteHash(writer, archive.HeadStateHash);
        NumosReplaySerializer.WriteConfig(writer, checkpoint.Config);
        writer.Write(checkpoint.Solvers.Count);
        foreach (var solver in checkpoint.Solvers)
        {
            NumosReplaySerializer.WriteString(writer, solver.Name);
            writer.Write((byte)solver.Kind);
            writer.Write(solver.Enabled);
            NumosReplaySerializer.WriteNullableString(writer, solver.NeighborSelectionKey);
        }

        writer.Write(checkpoint.SimulationGenerations.Length);
        foreach (uint generation in checkpoint.SimulationGenerations)
            writer.Write(generation);

        writer.Write(checkpoint.Simulations.Count);
        foreach (var simulation in checkpoint.Simulations)
        {
            WriteSimulationId(writer, simulation.Simulation);
            WriteSimulationCheckpoint(writer, simulation.Checkpoint);
        }

        writer.Write(checkpoint.LinkSlots.Length);
        foreach (var slot in checkpoint.LinkSlots)
        {
            writer.Write(slot.Generation);
            writer.Write(slot.State);
            writer.Write((byte)slot.Kind);
            writer.Write(slot.Links.Length);
            foreach (var link in slot.Links)
                WriteLink(writer, link);
        }
    }

    private static WorldCheckpointPayload ReadCheckpoint(BinaryReader reader, NumosReplayReadOptions options)
    {
        int format = reader.ReadInt32();
        if (format != AtmosWorldCheckpoint.CurrentFormatVersion)
            throw new InvalidDataException("The world checkpoint schema is unsupported.");

        var position = NumosReplaySerializer.ReadPosition(reader);
        ulong topologyVersion = reader.ReadUInt64();
        var initialHash = ReadHash(reader);
        var headHash = ReadHash(reader);
        var config = NumosReplaySerializer.ReadConfig(reader, options);
        int solverCount = NumosReplaySerializer.ReadCount(reader, 1024, "world solver");
        var solvers = new AtmosWorldSolverCheckpoint[solverCount];
        for (int index = 0; index < solverCount; index++)
        {
            string name = NumosReplaySerializer.ReadString(reader, options);
            var kind = (AtmosWorldSolverKind)reader.ReadByte();
            bool enabled = reader.ReadBoolean();
            string? selectionKey = NumosReplaySerializer.ReadNullableString(reader, options);
            if (string.IsNullOrWhiteSpace(name) ||
                !Enum.IsDefined(kind) ||
                selectionKey != null && string.IsNullOrWhiteSpace(selectionKey))
            {
                throw new InvalidDataException("The world solver metadata is invalid.");
            }

            solvers[index] = new AtmosWorldSolverCheckpoint(
                name,
                kind,
                enabled,
                selectionKey);
        }

        int generationCount = NumosReplaySerializer.ReadCount(reader, 1_000_000, "simulation generation");
        uint[] generations = new uint[generationCount];
        for (int index = 0; index < generationCount; index++)
            generations[index] = reader.ReadUInt32();

        int simulationCount = NumosReplaySerializer.ReadCount(reader, 1_000_000, "simulation");
        var simulations = new AtmosWorldSimulationCheckpoint[simulationCount];
        for (int index = 0; index < simulationCount; index++)
        {
            simulations[index] = new AtmosWorldSimulationCheckpoint(
                ReadSimulationId(reader),
                ReadSimulationCheckpoint(reader, options));
        }

        int linkSlotCount = NumosReplaySerializer.ReadCount(reader, 10_000_000, "link-set slot");
        var slots = new AtmosWorldLinkSlotCheckpoint[linkSlotCount];
        var publicSets = new List<AtmosWorldLinkSetCheckpoint>();
        for (int index = 0; index < linkSlotCount; index++)
        {
            uint generation = reader.ReadUInt32();
            byte state = reader.ReadByte();
            var kind = (ExplicitLinkSetKind)reader.ReadByte();
            int linkCount = NumosReplaySerializer.ReadCount(reader, 10_000_000, "explicit link");
            var links = new ExplicitLinkDefinition[linkCount];
            for (int linkIndex = 0; linkIndex < linkCount; linkIndex++)
                links[linkIndex] = ReadLink(reader);

            slots[index] = new AtmosWorldLinkSlotCheckpoint(generation, state, kind, links);
            if (state != 0)
            {
                publicSets.Add(
                    new AtmosWorldLinkSetCheckpoint(
                        new ExplicitLinkSetHandle(index, generation),
                        kind,
                        ToPublicState(state),
                        Array.AsReadOnly(links.ToArray())));
            }
        }

        var checkpoint = new AtmosWorldCheckpoint(
            position,
            topologyVersion,
            config,
            solvers,
            simulations,
            publicSets.ToArray(),
            generations,
            slots);

        if (initialHash.Position != position)
            throw new InvalidDataException("The world checkpoint schema or initial digest position is invalid.");

        return new WorldCheckpointPayload(checkpoint, initialHash, headHash);
    }

    private static void WriteSimulationCheckpoint(BinaryWriter writer, AtmosSimulationCheckpoint checkpoint)
    {
        writer.Write(checkpoint.FormatVersion);
        writer.Write(checkpoint.CompatibilityVersion);
        writer.Write(checkpoint.CompatibilityFingerprint);
        NumosReplaySerializer.WriteInt3(writer, checkpoint.Dimensions);
        NumosReplaySerializer.WritePosition(writer, checkpoint.Position);
        NumosReplaySerializer.WriteConfig(writer, checkpoint.Config);
        writer.Write(checkpoint.Solvers.Count);
        foreach (var solver in checkpoint.Solvers)
        {
            NumosReplaySerializer.WriteString(writer, solver.Name);
            writer.Write(solver.IsCustom);
            writer.Write(solver.Enabled);
            NumosReplaySerializer.WriteNullableString(writer, solver.NeighborSelectionKey);
        }

        writer.Write(checkpoint.Chunks.Count);
        foreach (var chunk in checkpoint.Chunks)
            NumosReplaySerializer.WriteChunk(writer, chunk);
    }

    private static AtmosSimulationCheckpoint ReadSimulationCheckpoint(BinaryReader reader, NumosReplayReadOptions options)
    {
        int format = reader.ReadInt32();
        if (format != AtmosSimulationCheckpoint.CurrentFormatVersion)
            throw new InvalidDataException("The simulation checkpoint schema is unsupported.");

        int compatibility = reader.ReadInt32();
        if (compatibility != AtmosSimulationCheckpoint.CurrentCompatibilityVersion)
            throw new InvalidDataException("The simulation checkpoint compatibility version is unsupported.");

        ulong fingerprint = reader.ReadUInt64();
        var dimensions = NumosReplaySerializer.ReadInt3(reader);
        var position = NumosReplaySerializer.ReadPosition(reader);
        var config = NumosReplaySerializer.ReadConfig(reader, options);
        int solverCount = NumosReplaySerializer.ReadCount(reader, 1024, "solver");
        var solvers = new AtmosSolverCheckpoint[solverCount];
        for (int index = 0; index < solverCount; index++)
        {
            string name = NumosReplaySerializer.ReadString(reader, options);
            bool isCustom = reader.ReadBoolean();
            bool enabled = reader.ReadBoolean();
            string? selectionKey = NumosReplaySerializer.ReadNullableString(reader, options);
            if (string.IsNullOrWhiteSpace(name) || selectionKey != null && string.IsNullOrWhiteSpace(selectionKey))
                throw new InvalidDataException("The solver checkpoint metadata is invalid.");

            solvers[index] = new AtmosSolverCheckpoint(name, isCustom, enabled, selectionKey);
        }

        int chunkCount = NumosReplaySerializer.ReadCount(reader, 1_000_000, "chunk");
        var chunks = new AtmosChunkCheckpoint[chunkCount];
        for (int index = 0; index < chunkCount; index++)
            chunks[index] = NumosReplaySerializer.ReadChunk(reader);

        var checkpoint = new AtmosSimulationCheckpoint(dimensions, position, config, solvers, chunks);
        if (fingerprint != checkpoint.CompatibilityFingerprint)
            throw new InvalidDataException("A simulation checkpoint schema or compatibility fingerprint is invalid.");

        return checkpoint;
    }

    private static void WriteOperations(BinaryWriter writer, AtmosWorldRecording recording)
    {
        NumosReplaySerializer.WritePosition(writer, recording.Start);
        NumosReplaySerializer.WritePosition(writer, recording.Head);
        writer.Write(recording.Operations.Count);
        foreach (var recorded in recording.Operations)
        {
            NumosReplaySerializer.WritePosition(writer, recorded.Position);
            writer.Write((ushort)recorded.Code);
            var counter = new CountingWriteStream();
            using (var countWriter = new BinaryWriter(counter, NumosReplaySerializer.Utf8, true))
            {
                WriteOperation(countWriter, recorded.Operation);
            }

            writer.Write(checked((uint)counter.Length));
            WriteOperation(writer, recorded.Operation);
        }
    }

    private static AtmosWorldRecording ReadOperations(BinaryReader reader, NumosReplayReadOptions options)
    {
        var start = NumosReplaySerializer.ReadPosition(reader);
        var head = NumosReplaySerializer.ReadPosition(reader);
        int count = NumosReplaySerializer.ReadCount(reader, options.MaxOperations, "world operation");
        var operations = new AtmosWorldRecordedOperation[count];
        for (int index = 0; index < count; index++)
        {
            var position = NumosReplaySerializer.ReadPosition(reader);
            ushort code = reader.ReadUInt16();
            uint length = reader.ReadUInt32();
            var limited = new LimitedReadStream(reader.BaseStream, length);
            using var payload = new BinaryReader(limited, NumosReplaySerializer.Utf8, true);
            var operation = ReadOperation(payload, code, options);
            if (limited.Remaining != 0)
                throw new InvalidDataException($"World replay opcode {code} has unexpected trailing data.");

            operations[index] = new AtmosWorldRecordedOperation(position, operation);
        }

        return new AtmosWorldRecording(start, head, operations);
    }

    private static void WriteOperation(BinaryWriter writer, AtmosWorldOperation operation)
    {
        if (!TryWriteGeneratedOperation(writer, operation))
            throw new NotSupportedException($"World replay opcode {operation.Code} is not serializable.");
    }

    private static AtmosWorldOperation ReadOperation(BinaryReader reader, ushort rawCode, NumosReplayReadOptions options)
    {
        if (!Enum.IsDefined((AtmosWorldOperationCode)rawCode)) throw new InvalidDataException($"World replay opcode {rawCode} is unsupported.");
        if (TryReadGeneratedOperation(reader, rawCode, options, out var generated)) return generated!;
        throw new InvalidDataException($"World replay opcode {rawCode} is unsupported.");
    }

    private static void WriteSetAtmosWorldConfigOperation(BinaryWriter writer, SetAtmosWorldConfigOperation operation)
    {
        NumosReplaySerializer.WriteConfig(writer, operation.Config);
    }

    private static SetAtmosWorldConfigOperation ReadSetAtmosWorldConfigOperation(BinaryReader reader, NumosReplayReadOptions options)
    {
        return new SetAtmosWorldConfigOperation(NumosReplaySerializer.ReadConfig(reader, options));
    }

    private static void WriteCreateAtmosLinkSetOperation(BinaryWriter writer, CreateAtmosLinkSetOperation operation)
    {
        WriteHandle(writer, operation.Handle);
        writer.Write((byte)operation.Kind);
        writer.Write(operation.Links.Count);
        foreach (var link in operation.Links)
            WriteLink(writer, link);
    }

    private static CreateAtmosLinkSetOperation ReadCreateAtmosLinkSetOperation(BinaryReader reader, NumosReplayReadOptions options)
    {
        var handle = ReadHandle(reader);
        var kind = (ExplicitLinkSetKind)reader.ReadByte();
        int count = NumosReplaySerializer.ReadCount(reader, 10_000_000, "explicit link");
        var links = new ExplicitLinkDefinition[count];
        for (int index = 0; index < count; index++)
            links[index] = ReadLink(reader);

        return new CreateAtmosLinkSetOperation(handle, kind, links);
    }

    private static void WriteLink(BinaryWriter writer, ExplicitLinkDefinition link)
    {
        WriteCell(writer, link.First);
        WriteCell(writer, link.Second);
        writer.Write((byte)link.Flags);
    }

    private static ExplicitLinkDefinition ReadLink(BinaryReader reader)
    {
        return new ExplicitLinkDefinition(ReadCell(reader), ReadCell(reader), (ExplicitLinkFlags)reader.ReadByte());
    }

    private static void WriteCell(BinaryWriter writer, AtmosCellRef cell)
    {
        WriteSimulationId(writer, cell.Simulation);
        NumosReplaySerializer.WriteInt3(writer, cell.Chunk.Position);
        writer.Write(cell.LocalVoxelIndex);
    }

    private static AtmosCellRef ReadCell(BinaryReader reader)
    {
        return new AtmosCellRef(
            ReadSimulationId(reader),
            new ChunkHandle(NumosReplaySerializer.ReadInt3(reader)),
            reader.ReadUInt16());
    }

    private static void WriteSimulationId(BinaryWriter writer, AtmosSimulationId id)
    {
        writer.Write(id.Index);
        writer.Write(id.Generation);
    }

    private static AtmosSimulationId ReadSimulationId(BinaryReader reader)
    {
        return new AtmosSimulationId(reader.ReadInt32(), reader.ReadUInt32());
    }

    private static void WriteHandle(BinaryWriter writer, ExplicitLinkSetHandle handle)
    {
        writer.Write(handle.Index);
        writer.Write(handle.Generation);
    }

    private static ExplicitLinkSetHandle ReadHandle(BinaryReader reader)
    {
        return new ExplicitLinkSetHandle(reader.ReadInt32(), reader.ReadUInt32());
    }

    private static void WriteHash(BinaryWriter writer, AtmosWorldStateHash hash)
    {
        NumosReplaySerializer.WritePosition(writer, hash.Position);
        writer.Write(hash.Digest);
    }

    private static AtmosWorldStateHash ReadHash(BinaryReader reader)
    {
        return new AtmosWorldStateHash(NumosReplaySerializer.ReadPosition(reader), reader.ReadUInt64());
    }

    private static AtmosWorldLinkSetState ToPublicState(byte state)
    {
        return state switch
        {
            1 => AtmosWorldLinkSetState.PendingActivation,
            2 => AtmosWorldLinkSetState.Active,
            3 or 4 => AtmosWorldLinkSetState.PendingRemoval,
            _ => throw new InvalidDataException("The world checkpoint contains an invalid link-set state.")
        };
    }

    private static void ValidateRecording(AtmosWorldRecording recording)
    {
        if (recording.Head.Tick < recording.Start.Tick ||
            recording.Head.OperationSequence < recording.Start.OperationSequence)
            throw new InvalidDataException("The world replay bounds are invalid.");

        ulong sequence = recording.Start.OperationSequence;
        ulong tick = recording.Start.Tick;
        foreach (var operation in recording.Operations)
        {
            if (operation.Sequence != checked(sequence + 1) ||
                operation.AfterTick < tick ||
                operation.AfterTick > recording.Head.Tick ||
                operation.Sequence > recording.Head.OperationSequence)
                throw new InvalidDataException("World operations are not contiguous and ordered within the recording bounds.");

            sequence = operation.Sequence;
            tick = operation.AfterTick;
        }

        if (sequence != recording.Head.OperationSequence)
            throw new InvalidDataException("World operation history does not reach its declared head.");
    }

    private sealed record WorldCheckpointPayload(
        AtmosWorldCheckpoint Checkpoint,
        AtmosWorldStateHash InitialHash,
        AtmosWorldStateHash HeadHash);

    private sealed record WorldMetadataPayload(
        NumosReplayMetadata Metadata,
        AtmosTimelinePosition Start,
        AtmosTimelinePosition Head,
        ulong OperationCount,
        ulong SimulationCount,
        ulong LinkSetCount);
}