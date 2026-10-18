using Numos.API;
using Numos.CoreSim.Replay;
using Numos.Replay.SourceGen;

namespace Numos.Serialization;

/// <summary>
///     Reads and writes <c>.numos</c> world replay documents without accessing filesystem paths.
/// </summary>
/// <remarks>
///     The <c>[WireOperation]</c> attributes below register every <see cref="AtmosWorldOperationCode" /> with
///     Numos.Replay.SourceGen, which generates this class's <c>TryWriteGeneratedOperation</c>/
///     <c>TryReadGeneratedOperation</c> partial-class members. Field order for the mechanical ones is exactly
///     constructor-declaration order; see <c>tests/Numos.Serialization.Tests/ReplayWireFormatGoldenTests.cs</c> for
///     the byte-level pin that codec must not silently drift from.
///     <para>
///         <see cref="AtmosWorldSimulationOperation" />'s <c>Operation</c> field is a nested simulation operation --
///         the generator writes its opcode and then delegates to the internal <c>NumosReplaySerializer</c>'s own codec
///         for the payload, rather than treating it as an opaque unsupported type.
///     </para>
///     <para>
///         <see cref="SetAtmosWorldConfigOperation" /> (nested config snapshot) and
///         <see cref="CreateAtmosLinkSetOperation" /> (variable-length link list) don't fit the generator's
///         mechanical field-order mapping, so they register as <c>Custom</c>, same as their sim-family
///         counterparts.
///     </para>
///     <para>
///         Every <see cref="AtmosWorldOperationCode" /> member needs one of these registrations. A missing one is a
///         NUMOSREPLAYGEN008 build error from <c>ReplayOpcodeConsistencyAnalyzer</c> in Numos.Replay.Analyzers.
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

        var replay = document.Replay;
        NumosReplaySerializer.WriteContainer(
            destination,
            section => NumosReplaySerializer.WriteMetadata(
                section,
                document.Metadata,
                replay.Recording.Start,
                replay.Recording.Head,
                replay.Recording.Operations.Count,
                replay.InitialCheckpoint.Simulations.Count,
                replay.InitialCheckpoint.LinkSets.Count),
            section => WriteCheckpoint(section, replay),
            section => WriteOperations(section, replay.Recording));
    }

    /// <summary>
    ///     Reads a complete-world replay document and leaves the source open.
    /// </summary>
    /// <param name="source">Readable stream containing exactly one world replay container.</param>
    /// <param name="options">Optional allocation and payload limits for untrusted input.</param>
    /// <returns>Decoded metadata and detached complete-world archive.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="source" /> is not readable.</exception>
    /// <exception cref="InvalidDataException">
    ///     The container is malformed, inconsistent, or holds unsupported content such as a single-simulation replay
    ///     written by an earlier Numos version.
    /// </exception>
    /// <exception cref="NotSupportedException">The replay contains host-defined state.</exception>
    public static NumosWorldReplayDocument Deserialize(Stream source, NumosReplayReadOptions? options = null)
    {
        var (metadata, checkpoint, recording) = NumosReplaySerializer.ReadContainer(source, options);

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

        archive.EnsurePortable();
        NumosReplaySerializer.EnsureEndOfContainer(source);
        return new NumosWorldReplayDocument(metadata.Metadata, archive);
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
            NumosReplaySerializer.WriteSimulationCheckpoint(writer, simulation.Checkpoint);
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

    internal static WorldCheckpointPayload ReadCheckpoint(BinaryReader reader, NumosReplayReadOptions options)
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
                NumosReplaySerializer.ReadSimulationCheckpoint(reader, options));
        }

        int linkSlotCount = NumosReplaySerializer.ReadCount(reader, 10_000_000, "link-set slot");
        var slots = new AtmosWorldLinkSlotCheckpoint[linkSlotCount];
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
            // The checkpoint maps states for its public view lazily, so reject unknown ones while reading.
            if (state != 0)
                _ = ToPublicState(state);
        }

        var checkpoint = new AtmosWorldCheckpoint(
            position,
            topologyVersion,
            config,
            solvers,
            simulations,
            generations,
            slots);

        if (initialHash.Position != position)
            throw new InvalidDataException("The world checkpoint schema or initial digest position is invalid.");

        return new WorldCheckpointPayload(checkpoint, initialHash, headHash);
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
        NumosReplaySerializer.WritePosition(writer, recording.Start);
        NumosReplaySerializer.WritePosition(writer, recording.Head);
        writer.Write(recording.Operations.Count);
        using var payload = new MemoryStream();
        using var payloadWriter = new BinaryWriter(payload, NumosReplaySerializer.Utf8, true);
        foreach (var recorded in recording.Operations)
        {
            NumosReplaySerializer.WritePosition(writer, recorded.Position);
            writer.Write((ushort)recorded.Code);
            payload.SetLength(0);
            WriteOperation(payloadWriter, recorded.Operation);
            writer.Write(checked((uint)payload.Length));
            writer.Write(payload.GetBuffer(), 0, (int)payload.Length);
        }
    }

    /// <summary>
    ///     Reads an operations section written by <see cref="WriteOperations" />, bounding each payload to its declared
    ///     length.
    /// </summary>
    internal static AtmosWorldRecording ReadOperations(BinaryReader reader, NumosReplayReadOptions options)
    {
        var start = NumosReplaySerializer.ReadPosition(reader);
        var head = NumosReplaySerializer.ReadPosition(reader);
        int count = NumosReplaySerializer.ReadCount(reader, options.MaxOperations, "world operation");
        var operations = new AtmosWorldRecordedOperation[count];
        var limited = new LimitedReadStream(reader.BaseStream, 0);
        using var payload = new BinaryReader(limited, NumosReplaySerializer.Utf8, true);
        for (int index = 0; index < count; index++)
        {
            var position = NumosReplaySerializer.ReadPosition(reader);
            ushort code = reader.ReadUInt16();
            limited.Reset(reader.ReadUInt32());
            operations[index] = new AtmosWorldRecordedOperation(position, ReadOperation(payload, code, options));
            if (limited.Remaining != 0) throw new InvalidDataException($"World replay opcode {code} has unexpected trailing data.");
        }

        return AtmosWorldRecording.Adopt(start, head, operations);
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
            new AtmosChunkHandle(NumosReplaySerializer.ReadInt3(reader)),
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

    internal sealed record WorldCheckpointPayload(
        AtmosWorldCheckpoint Checkpoint,
        AtmosWorldStateHash InitialHash,
        AtmosWorldStateHash HeadHash);
}
