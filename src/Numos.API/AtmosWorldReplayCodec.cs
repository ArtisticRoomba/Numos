using Numos.CoreSim.Replay;

namespace Numos.API;

/// <summary>
///     Binary codec for world operations and world checkpoints.
/// </summary>
/// <remarks>
///     Numos.Replay.Generators emits <c>WriteOperation</c>/<c>ReadOperation</c> for every concrete
///     <see cref="AtmosWorldOperation" />, following the same rules as <see cref="AtmosReplayCodec" />: field types
///     without a built-in encoding use a <c>Write{TypeName}</c>/<c>Read{TypeName}</c> pair from this class or from
///     <see cref="AtmosReplayCodec" />, and an operation with hand-written <c>Write{OperationName}</c>/
///     <c>Read{OperationName}</c> methods here is encoded by those.
/// </remarks>
internal static partial class AtmosWorldReplayCodec
{
    /// <summary>
    ///     Writes the world checkpoint section, with the archive's initial and head hashes embedded after the
    ///     topology version.
    /// </summary>
    internal static void WriteCheckpoint(BinaryWriter writer, AtmosWorldReplayArchive archive)
    {
        var checkpoint = archive.InitialCheckpoint;
        writer.Write(checkpoint.FormatVersion);
        AtmosReplayCodec.WritePosition(writer, checkpoint.Position);
        writer.Write(checkpoint.TopologyVersion);
        WriteHash(writer, archive.InitialStateHash);
        WriteHash(writer, archive.HeadStateHash);
        AtmosReplayCodec.WriteConfig(writer, checkpoint.Config);
        writer.Write(checkpoint.Solvers.Count);
        foreach (var solver in checkpoint.Solvers)
        {
            AtmosReplayCodec.WriteString(writer, solver.Name);
            writer.Write((byte)solver.Kind);
            writer.Write(solver.Enabled);
            AtmosReplayCodec.WriteNullableString(writer, solver.NeighborSelectionKey);
        }

        writer.Write(checkpoint.SimulationGenerations.Length);
        foreach (uint generation in checkpoint.SimulationGenerations)
            writer.Write(generation);

        writer.Write(checkpoint.Simulations.Count);
        foreach (var simulation in checkpoint.Simulations)
        {
            WriteAtmosSimulationId(writer, simulation.Simulation);
            AtmosReplayCodec.WriteSimulationCheckpoint(writer, simulation.Checkpoint);
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

    /// <summary>
    ///     Reads a checkpoint section written by <see cref="WriteCheckpoint" />.
    /// </summary>
    internal static (AtmosWorldCheckpoint Checkpoint, AtmosWorldStateHash InitialHash, AtmosWorldStateHash HeadHash)
        ReadCheckpoint(BinaryReader reader, int maxStringBytes)
    {
        int format = reader.ReadInt32();
        if (format != AtmosWorldCheckpoint.CurrentFormatVersion)
            throw new InvalidDataException("The world checkpoint schema is unsupported.");

        var position = AtmosReplayCodec.ReadPosition(reader);
        ulong topologyVersion = reader.ReadUInt64();
        var initialHash = ReadHash(reader);
        var headHash = ReadHash(reader);
        var config = AtmosReplayCodec.ReadConfig(reader, maxStringBytes);
        int solverCount = AtmosReplayCodec.ReadCount(reader, 1024, "world solver");
        var solvers = new AtmosWorldSolverCheckpoint[solverCount];
        for (int index = 0; index < solverCount; index++)
        {
            string name = AtmosReplayCodec.ReadString(reader, maxStringBytes);
            var kind = (AtmosWorldSolverKind)reader.ReadByte();
            bool enabled = reader.ReadBoolean();
            string? selectionKey = AtmosReplayCodec.ReadNullableString(reader, maxStringBytes);
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

        int generationCount = AtmosReplayCodec.ReadCount(reader, 1_000_000, "simulation generation");
        uint[] generations = new uint[generationCount];
        for (int index = 0; index < generationCount; index++)
            generations[index] = reader.ReadUInt32();

        int simulationCount = AtmosReplayCodec.ReadCount(reader, 1_000_000, "simulation");
        var simulations = new AtmosWorldSimulationCheckpoint[simulationCount];
        for (int index = 0; index < simulationCount; index++)
        {
            simulations[index] = new AtmosWorldSimulationCheckpoint(
                ReadAtmosSimulationId(reader),
                AtmosReplayCodec.ReadSimulationCheckpoint(reader));
        }

        int linkSlotCount = AtmosReplayCodec.ReadCount(reader, 10_000_000, "link-set slot");
        var slots = new AtmosWorldLinkSlotCheckpoint[linkSlotCount];
        for (int index = 0; index < linkSlotCount; index++)
        {
            uint generation = reader.ReadUInt32();
            byte state = reader.ReadByte();
            var kind = (ExplicitLinkSetKind)reader.ReadByte();
            int linkCount = AtmosReplayCodec.ReadCount(reader, 10_000_000, "explicit link");
            var links = new ExplicitLinkDefinition[linkCount];
            for (int linkIndex = 0; linkIndex < linkCount; linkIndex++)
                links[linkIndex] = ReadLink(reader);

            slots[index] = new AtmosWorldLinkSlotCheckpoint(generation, state, kind, links);
            // The checkpoint maps states for its public view lazily, so reject unknown ones while reading.
            if (state is not (0 or 1 or 2 or 3 or 4))
                throw new InvalidDataException("The world checkpoint contains an invalid link-set state.");
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

        return (checkpoint, initialHash, headHash);
    }

    private static void WriteSetAtmosWorldConfigOperation(BinaryWriter writer, SetAtmosWorldConfigOperation operation)
    {
        AtmosReplayCodec.WriteConfig(writer, operation.Config);
    }

    private static SetAtmosWorldConfigOperation ReadSetAtmosWorldConfigOperation(BinaryReader reader, int maxStringBytes)
    {
        return new SetAtmosWorldConfigOperation(AtmosReplayCodec.ReadConfig(reader, maxStringBytes));
    }

    private static void WriteCreateAtmosLinkSetOperation(BinaryWriter writer, CreateAtmosLinkSetOperation operation)
    {
        WriteExplicitLinkSetHandle(writer, operation.Handle);
        writer.Write((byte)operation.Kind);
        writer.Write(operation.Links.Count);
        foreach (var link in operation.Links)
            WriteLink(writer, link);
    }

    private static CreateAtmosLinkSetOperation ReadCreateAtmosLinkSetOperation(BinaryReader reader)
    {
        var handle = ReadExplicitLinkSetHandle(reader);
        var kind = (ExplicitLinkSetKind)reader.ReadByte();
        int count = AtmosReplayCodec.ReadCount(reader, 10_000_000, "explicit link");
        var links = new ExplicitLinkDefinition[count];
        for (int index = 0; index < count; index++)
            links[index] = ReadLink(reader);

        return new CreateAtmosLinkSetOperation(handle, kind, links);
    }

    /// <summary>
    ///     Writes a nested simulation operation as its opcode followed by its payload.
    /// </summary>
    private static void WriteAtmosOperation(BinaryWriter writer, AtmosOperation operation)
    {
        writer.Write((ushort)operation.Code);
        AtmosReplayCodec.WriteOperation(writer, operation);
    }

    private static AtmosOperation ReadAtmosOperation(BinaryReader reader, int maxStringBytes)
    {
        ushort code = reader.ReadUInt16();
        return AtmosReplayCodec.ReadOperation(reader, code, maxStringBytes);
    }

    private static void WriteAtmosSimulationId(BinaryWriter writer, AtmosSimulationId id)
    {
        writer.Write(id.Index);
        writer.Write(id.Generation);
    }

    private static AtmosSimulationId ReadAtmosSimulationId(BinaryReader reader)
    {
        return new AtmosSimulationId(reader.ReadInt32(), reader.ReadUInt32());
    }

    private static void WriteExplicitLinkSetHandle(BinaryWriter writer, ExplicitLinkSetHandle handle)
    {
        writer.Write(handle.Index);
        writer.Write(handle.Generation);
    }

    private static ExplicitLinkSetHandle ReadExplicitLinkSetHandle(BinaryReader reader)
    {
        return new ExplicitLinkSetHandle(reader.ReadInt32(), reader.ReadUInt32());
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
        WriteAtmosSimulationId(writer, cell.Simulation);
        AtmosReplayCodec.WriteInt3(writer, cell.Chunk.Position);
        writer.Write(cell.LocalVoxelIndex);
    }

    private static AtmosCellRef ReadCell(BinaryReader reader)
    {
        return new AtmosCellRef(
            ReadAtmosSimulationId(reader),
            new AtmosChunkHandle(AtmosReplayCodec.ReadInt3(reader)),
            reader.ReadUInt16());
    }

    private static void WriteHash(BinaryWriter writer, AtmosWorldStateHash hash)
    {
        AtmosReplayCodec.WritePosition(writer, hash.Position);
        writer.Write(hash.Digest);
    }

    private static AtmosWorldStateHash ReadHash(BinaryReader reader)
    {
        return new AtmosWorldStateHash(AtmosReplayCodec.ReadPosition(reader), reader.ReadUInt64());
    }
}