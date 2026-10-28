using System.Buffers;
using Numos.CoreSim.Replay;
using Numos.CoreSim.State.Advanced;

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
    ///     Writes the world checkpoint section: the format version, the archive's initial and head hashes, then the
    ///     checkpoint's generated state payload with its length.
    /// </summary>
    /// <remarks>
    ///     The version and hashes stay outside the payload. A reader has to check the version before it can trust the
    ///     payload's layout, and the hashes describe the archive rather than the checkpoint.
    /// </remarks>
    internal static void WriteCheckpoint(BinaryWriter writer, AtmosWorldReplayArchive archive)
    {
        var checkpoint = archive.InitialCheckpoint;
        writer.Write(checkpoint.FormatVersion);
        WriteHash(writer, archive.InitialStateHash);
        WriteHash(writer, archive.HeadStateHash);

        // Sized for the voxel data plus framing, so a large world is written without the buffer regrowing.
        long estimate = 4096;
        foreach (var simulation in checkpoint.Simulations)
            estimate += simulation.Checkpoint.PayloadBytes + 256L * simulation.Checkpoint.Chunks.Count;

        var sink = StateBinarySink.Rent((int)Math.Min(estimate, Array.MaxLength));
        try
        {
            NumosState.Write(ref sink, checkpoint);
            AtmosReplayCodec.WritePayload(writer, sink.WrittenSpan);
        }
        finally
        {
            sink.Return();
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

        var initialHash = ReadHash(reader);
        var headHash = ReadHash(reader);
        int length = AtmosReplayCodec.ReadCount(reader, int.MaxValue, "world checkpoint byte");

        // Everything decoded is copied out of the payload, so its buffer can go back to the pool.
        byte[] payload = AtmosReplayCodec.RentPayload(reader, length);
        AtmosWorldCheckpoint checkpoint;
        try
        {
            var state = new StateReader(payload.AsSpan(0, length), maxStringBytes)
            {
                SolverConfigurations = (reader as ReplayReader)?.SolverConfigurations,
            };

            checkpoint = NumosState.Read<AtmosWorldCheckpoint>(ref state);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }

        if (initialHash.Position != checkpoint.Position)
            throw new InvalidDataException("The world checkpoint schema or initial digest position is invalid.");

        return (checkpoint, initialHash, headHash);
    }

    private static void WriteSetAtmosWorldSolverStateOperation(BinaryWriter writer, SetAtmosWorldSolverStateOperation operation)
    {
        AtmosReplayCodec.WriteString(writer, operation.Name);
        AtmosReplayCodec.WritePayload(writer, operation.State.Span);
    }

    private static SetAtmosWorldSolverStateOperation ReadSetAtmosWorldSolverStateOperation(BinaryReader reader, int maxStringBytes)
    {
        string name = AtmosReplayCodec.ReadString(reader, maxStringBytes);
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("A solver state operation names a blank solver.");

        byte[] payload = AtmosReplayCodec.ReadPayload(reader, AtmosReplayCodec.ReadCount(reader, int.MaxValue, "solver state byte"));
        return new SetAtmosWorldSolverStateOperation(name, payload);
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

    private static void WriteSetAtmosWorldSolverLayoutOperation(
        BinaryWriter writer,
        SetAtmosWorldSolverLayoutOperation operation)
    {
        writer.Write(operation.Stages.Count);
        foreach (var stage in operation.Stages)
        {
            AtmosReplayCodec.WriteString(writer, stage.Name);
            writer.Write(stage.Enabled);
        }
    }

    private static SetAtmosWorldSolverLayoutOperation ReadSetAtmosWorldSolverLayoutOperation(
        BinaryReader reader,
        int maxStringBytes)
    {
        int count = AtmosReplayCodec.ReadCount(reader, 1024, "world solver");
        var stages = new AtmosWorldSolverLayoutEntry[count];
        for (int index = 0; index < count; index++)
        {
            string name = AtmosReplayCodec.ReadString(reader, maxStringBytes);
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException("The world solver layout contains a blank stage name.");

            stages[index] = new AtmosWorldSolverLayoutEntry(name, reader.ReadBoolean());
        }

        return new SetAtmosWorldSolverLayoutOperation(stages);
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

    // An unset area fraction is written as NaN. Registration never accepts NaN as a value, so the two cannot collide.
    private static void WriteLink(BinaryWriter writer, ExplicitLinkDefinition link)
    {
        WriteCell(writer, link.First);
        WriteCell(writer, link.Second);
        writer.Write((byte)link.Flags);
        writer.Write(link.AreaFraction ?? float.NaN);
    }

    private static ExplicitLinkDefinition ReadLink(BinaryReader reader)
    {
        var first = ReadCell(reader);
        var second = ReadCell(reader);
        var flags = (ExplicitLinkFlags)reader.ReadByte();
        float areaFraction = reader.ReadSingle();
        return new ExplicitLinkDefinition(first, second, flags, float.IsNaN(areaFraction) ? null : areaFraction);
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