using System.Text;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;
using Numos.Maths;

namespace Numos.Serialization;

/// <summary>
///     Reads and writes the portable Numos binary replay container without accessing filesystem paths.
/// </summary>
public static class NumosReplaySerializer
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

        using var writer = new BinaryWriter(destination, Utf8, true);
        writer.Write(Magic);
        writer.Write(ContainerVersion);
        writer.Write(ReplayContentKind);
        writer.Write(3u);
        WriteSection(writer, MetadataSection, 0, section => WriteMetadata(section, document));
        WriteSection(writer, CheckpointSection, RequiredSection, section => WriteCheckpoint(section, document.Replay));
        WriteSection(writer, OperationsSection, RequiredSection, section => WriteOperations(section, document.Replay.Recording));
    }

    /// <summary>
    ///     Reads a replay document from a readable stream and leaves the stream open.
    /// </summary>
    /// <param name="source">The stream containing exactly one Numos replay container.</param>
    /// <param name="options">Optional allocation and payload limits for untrusted input.</param>
    /// <returns>The decoded metadata and detached replay archive.</returns>
    /// <exception cref="ArgumentException">The stream is not readable.</exception>
    /// <exception cref="InvalidDataException">The container is malformed, unsupported, or internally inconsistent.</exception>
    /// <exception cref="NotSupportedException">
    ///     The replay contains host-defined state that the portable format cannot
    ///     reconstruct.
    /// </exception>
    public static NumosReplayDocument Deserialize(Stream source, NumosReplayReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("The source stream must be readable.", nameof(source));

        options ??= new NumosReplayReadOptions();
        ValidateOptions(options);
        using var reader = new BinaryReader(source, Utf8, true);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException("The stream is not a Numos file.");
        if (reader.ReadUInt16() != ContainerVersion) throw new InvalidDataException("The Numos container version is unsupported.");
        if (reader.ReadUInt16() != ReplayContentKind) throw new InvalidDataException("The Numos file does not contain a replay.");

        uint sectionCount = reader.ReadUInt32();
        if (sectionCount > 1024) throw new InvalidDataException("The Numos file declares too many sections.");

        MetadataPayload? metadata = null;
        CheckpointPayload? checkpoint = null;
        AtmosRecording? recording = null;
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

                    checkpoint = ReadCheckpoint(section, options);
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
            throw new InvalidDataException("The replay is missing a required section.");

        if (recording.Start != checkpoint.Checkpoint.Position)
            throw new InvalidDataException("The recording does not begin at the initial checkpoint.");

        ValidateRecording(recording);
        if (metadata.Start != recording.Start ||
            metadata.Head != recording.Head ||
            metadata.OperationCount != (ulong)recording.Operations.Count ||
            metadata.ChunkCount != (ulong)checkpoint.Checkpoint.Chunks.Count)
            throw new InvalidDataException("The replay metadata does not match its payload.");

        var archive = new AtmosReplayArchive(
            checkpoint.Checkpoint,
            recording,
            checkpoint.InitialHash,
            checkpoint.HeadHash);

        archive.EnsurePortable();
        if (source.ReadByte() != -1) throw new InvalidDataException("The Numos file has trailing data.");

        return new NumosReplayDocument(metadata.Metadata, archive);
    }

    internal static void WriteSection(BinaryWriter writer, ushort id, ushort flags, Action<BinaryWriter> write)
    {
        var counter = new CountingWriteStream();
        using (var countWriter = new BinaryWriter(counter, Utf8, true))
        {
            write(countWriter);
        }

        writer.Write(id);
        writer.Write(flags);
        writer.Write(checked((ulong)counter.Length));
        write(writer);
    }

    private static void WriteMetadata(BinaryWriter writer, NumosReplayDocument document)
    {
        WriteString(writer, document.Metadata.ProjectName);
        writer.Write(document.Metadata.CreatedUtc.UtcTicks);
        WriteString(writer, document.Metadata.ProducerName);
        WriteString(writer, document.Metadata.ProducerVersion);
        WriteString(writer, document.Metadata.CoreSimVersion);
        WriteNullableString(writer, document.Metadata.SourceReference);
        WritePosition(writer, document.Replay.Recording.Start);
        WritePosition(writer, document.Replay.Recording.Head);
        writer.Write(checked((ulong)document.Replay.Recording.Operations.Count));
        writer.Write(checked((ulong)document.Replay.InitialCheckpoint.Chunks.Count));
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
        ulong chunkCount = reader.ReadUInt64();
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
            chunkCount);
    }

    private static void WriteCheckpoint(BinaryWriter writer, AtmosReplayArchive archive)
    {
        var checkpoint = archive.InitialCheckpoint;
        writer.Write(checkpoint.FormatVersion);
        writer.Write(checkpoint.CompatibilityVersion);
        writer.Write(checkpoint.CompatibilityFingerprint);
        WriteInt3(writer, checkpoint.Dimensions);
        WritePosition(writer, checkpoint.Position);
        WriteHash(writer, archive.InitialStateHash);
        WriteHash(writer, archive.HeadStateHash);
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

    private static CheckpointPayload ReadCheckpoint(BinaryReader reader, NumosReplayReadOptions options)
    {
        int format = reader.ReadInt32();
        if (format != AtmosSimulationCheckpoint.CurrentFormatVersion)
            throw new InvalidDataException("The checkpoint schema is unsupported.");

        int compatibility = reader.ReadInt32();
        if (compatibility != AtmosSimulationCheckpoint.CurrentCompatibilityVersion)
            throw new InvalidDataException("The checkpoint compatibility version is unsupported.");

        ulong fingerprint = reader.ReadUInt64();
        var dimensions = ReadInt3(reader);
        var position = ReadPosition(reader);
        var initialHash = ReadHash(reader);
        var headHash = ReadHash(reader);
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
            throw new InvalidDataException("The checkpoint schema or compatibility fingerprint is invalid.");

        if (initialHash.Position != position) throw new InvalidDataException("The initial hash position is invalid.");

        return new CheckpointPayload(checkpoint, initialHash, headHash);
    }

    internal static void WriteConfig(BinaryWriter writer, AtmosConfigSnapshot config)
    {
        writer.Write(config.GlobalTemperature);
        writer.Write(config.DefaultTemperatureFallback);
        writer.Write(config.DefaultMolarHeatCapacityAtConstantVolume);
        writer.Write(config.VoxelVolume);
        writer.Write(config.SaturationReferencePressure);
        writer.Write(config.DefaultDiffusionCoefficient);
        writer.Write(config.SpaceTemperature);
        writer.Write(config.BulkFlowCoefficient);
        writer.Write(config.VacuumThreshold);
        writer.Write(config.SleepThreshold);
        writer.Write(config.SleepEpsilon);
        writer.Write(config.ThermalConductance);
        writer.Write(config.CondensationRateFactor);
        writer.Write(config.MaxPressureTransferFractionPerNeighbor);
        writer.Write(config.AccumulatorWakeThreshold);
        writer.Write(config.AccumulatorMaxAliveTicks);
        writer.Write(config.GasRegistry.Count);
        foreach (var gas in config.GasRegistry) WriteGas(writer, gas);
        writer.Write(config.SolverConfigurations.Count);
    }

    internal static AtmosConfigSnapshot ReadConfig(BinaryReader reader, NumosReplayReadOptions options)
    {
        var config = new AtmosConfig
        {
            GlobalTemperature = reader.ReadSingle(), DefaultTemperatureFallback = reader.ReadSingle(),
            DefaultMolarHeatCapacityAtConstantVolume = reader.ReadSingle(), VoxelVolume = reader.ReadSingle(),
            SaturationReferencePressure = reader.ReadSingle(), DefaultDiffusionCoefficient = reader.ReadSingle(),
            SpaceTemperature = reader.ReadSingle(), BulkFlowCoefficient = reader.ReadSingle(), VacuumThreshold = reader.ReadSingle(),
            SleepThreshold = reader.ReadInt32(), SleepEpsilon = reader.ReadSingle(), ThermalConductance = reader.ReadSingle(),
            CondensationRateFactor = reader.ReadSingle(), MaxPressureTransferFractionPerNeighbor = reader.ReadSingle(),
            AccumulatorWakeThreshold = reader.ReadSingle(), AccumulatorMaxAliveTicks = reader.ReadInt32()
        };

        int gasCount = ReadCount(reader, 1_000_000, "gas");
        for (int index = 0; index < gasCount; index++) config.GasRegistry.Add(ReadGas(reader, options));
        int customConfigurationCount = ReadCount(reader, 1_000_000, "solver configuration");
        if (customConfigurationCount != 0)
            throw new NotSupportedException("Portable replay files cannot decode custom solver configurations.");

        return config.CreateSnapshot();
    }

    private static void WriteGas(BinaryWriter writer, GasProperties gas)
    {
        WriteNullableString(writer, gas.Name);
        writer.Write(gas.MolarHeatCapacityAtConstantVolume);
        writer.Write(gas.BoilingPoint);
        writer.Write(gas.CondensationEnabled);
        writer.Write(gas.MolarEnthalpyOfVaporization);
        writer.Write(gas.LiquidId);
        writer.Write(gas.DiffusionCoefficient);
    }

    private static GasProperties ReadGas(BinaryReader reader, NumosReplayReadOptions options)
    {
        return new GasProperties
        {
            Name = ReadNullableString(reader, options)!, MolarHeatCapacityAtConstantVolume = reader.ReadSingle(),
            BoilingPoint = reader.ReadSingle(), CondensationEnabled = reader.ReadBoolean(),
            MolarEnthalpyOfVaporization = reader.ReadSingle(), LiquidId = reader.ReadInt32(),
            DiffusionCoefficient = reader.ReadSingle()
        };
    }

    internal static void WriteChunk(BinaryWriter writer, AtmosChunkCheckpoint chunk)
    {
        WriteInt3(writer, chunk.Position);
        WriteInt3(writer, chunk.Dimensions);
        writer.Write(chunk.IsAwake);
        writer.Write(chunk.SleepTimer);
        writer.Write(chunk.Classifications.Count);
        foreach (int value in chunk.Classifications) writer.Write(value);
        foreach (float value in chunk.Temperatures) writer.Write(value);
        foreach (float value in chunk.Pressures) writer.Write(value);
        foreach (float value in chunk.HeatCapacities) writer.Write(value);
        writer.Write(chunk.ActiveAirIndices.Count);
        foreach (ushort value in chunk.ActiveAirIndices) writer.Write(value);
        writer.Write(chunk.Gases.Count);
        foreach (var gas in chunk.Gases)
        {
            writer.Write(gas.GasId);
            foreach (float value in gas.Moles) writer.Write(value);
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

        int[] classifications = ReadArray(reader, voxels, static r => r.ReadInt32());
        float[] temperatures = ReadArray(reader, voxels, static r => r.ReadSingle());
        float[] pressures = ReadArray(reader, voxels, static r => r.ReadSingle());
        float[] capacities = ReadArray(reader, voxels, static r => r.ReadSingle());
        ushort[] air = ReadArray(reader, ReadCount(reader, voxels, "active air voxel"), static r => r.ReadUInt16());
        int gasCount = ReadCount(reader, 1_000_000, "gas channel");
        var gases = new AtmosGasChannelCheckpoint[gasCount];
        for (int index = 0; index < gasCount; index++)
            gases[index] = new AtmosGasChannelCheckpoint(reader.ReadInt32(), ReadArray(reader, voxels, static r => r.ReadSingle()));

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
        WritePosition(writer, recording.Start);
        WritePosition(writer, recording.Head);
        writer.Write(recording.Operations.Count);
        foreach (var operation in recording.Operations)
        {
            WritePosition(writer, operation.Position);
            writer.Write((ushort)operation.Code);
            var counter = new CountingWriteStream();
            using (var countWriter = new BinaryWriter(counter, Utf8, true))
            {
                WriteOperation(countWriter, operation.Operation);
            }

            writer.Write(checked((uint)counter.Length));
            WriteOperation(writer, operation.Operation);
        }
    }

    private static AtmosRecording ReadOperations(BinaryReader reader, NumosReplayReadOptions options)
    {
        var start = ReadPosition(reader);
        var head = ReadPosition(reader);
        int count = ReadCount(reader, options.MaxOperations, "operation");
        var operations = new AtmosRecordedOperation[count];
        for (int index = 0; index < count; index++)
        {
            var position = ReadPosition(reader);
            ushort code = reader.ReadUInt16();
            uint length = reader.ReadUInt32();
            var limited = new LimitedReadStream(reader.BaseStream, length);
            using var payload = new BinaryReader(limited, Utf8, true);
            var operation = ReadOperation(payload, code, options);
            if (limited.Remaining != 0) throw new InvalidDataException($"Replay opcode {code} has unexpected trailing data.");

            operations[index] = new AtmosRecordedOperation(position, operation);
        }

        return new AtmosRecording(start, head, operations);
    }

    internal static void WriteOperation(BinaryWriter writer, AtmosOperation operation)
    {
        switch (operation)
        {
            case SetAtmosConfigOperation op: WriteConfig(writer, op.Config); break;
            case CreateChunkOperation op:
                WriteInt3(writer, op.Position);
                break;
            case RemoveChunkOperation op: WriteInt3(writer, op.Position); break;
            case SetChunkClassificationOperation op:
                WriteInt3(writer, op.Position);
                writer.Write(op.Classification.RoomId);
                break;
            case SetChunkBoundaryClassificationOperation op:
                WriteInt3(writer, op.Position);
                writer.Write(op.Classification.RoomId);
                break;
            case SetVoxelClassificationOperation op:
                WriteInt3(writer, op.Position);
                writer.Write(op.LocalVoxelIndex);
                writer.Write(op.Classification.RoomId);
                break;
            case SetVoxelTemperatureOperation op:
                WriteInt3(writer, op.Position);
                writer.Write(op.LocalVoxelIndex);
                writer.Write(op.Temperature);
                break;
            case AddGasToVoxelOperation op:
                WriteInt3(writer, op.Position);
                writer.Write(op.LocalVoxelIndex);
                writer.Write(op.GasId);
                writer.Write(op.Moles);
                writer.Write(op.Temperature);
                break;
            case WakeChunkOperation op:
                WriteInt3(writer, op.Position);
                break;
            case SleepChunkOperation op: WriteInt3(writer, op.Position); break;
            case SetSolverEnabledOperation op:
                WriteString(writer, op.Name);
                writer.Write(op.Enabled);
                break;
            case SetVoxelMixtureOperation op:
                WriteInt3(writer, op.Position);
                writer.Write(op.LocalVoxelIndex);
                writer.Write(op.Temperature);
                writer.Write(op.Pressure);
                writer.Write(op.HeatCapacity);
                writer.Write(op.Gases.Count);
                foreach (var gas in op.Gases)
                {
                    writer.Write(gas.GasId);
                    writer.Write(gas.Moles);
                }

                break;
            default: throw new NotSupportedException($"Replay opcode {operation.Code} is not serializable.");
        }
    }

    internal static AtmosOperation ReadOperation(BinaryReader reader, ushort rawCode, NumosReplayReadOptions options)
    {
        if (!Enum.IsDefined((AtmosOperationCode)rawCode)) throw new InvalidDataException($"Replay opcode {rawCode} is unsupported.");

        var code = (AtmosOperationCode)rawCode;
        return code switch
        {
            AtmosOperationCode.SetAtmosConfig => new SetAtmosConfigOperation(ReadConfig(reader, options)),
            AtmosOperationCode.CreateChunk => new CreateChunkOperation(ReadInt3(reader)),
            AtmosOperationCode.RemoveChunk => new RemoveChunkOperation(ReadInt3(reader)),
            AtmosOperationCode.SetChunkClassification => new SetChunkClassificationOperation(
                ReadInt3(reader),
                new VoxelClassification(reader.ReadInt32())),
            AtmosOperationCode.SetChunkBoundaryClassification => new SetChunkBoundaryClassificationOperation(
                ReadInt3(reader),
                new VoxelClassification(reader.ReadInt32())),
            AtmosOperationCode.SetVoxelClassification => new SetVoxelClassificationOperation(
                ReadInt3(reader),
                reader.ReadUInt16(),
                new VoxelClassification(reader.ReadInt32())),
            AtmosOperationCode.SetVoxelTemperature => new SetVoxelTemperatureOperation(
                ReadInt3(reader),
                reader.ReadUInt16(),
                reader.ReadSingle()),
            AtmosOperationCode.AddGasToVoxel => new AddGasToVoxelOperation(
                ReadInt3(reader),
                reader.ReadUInt16(),
                reader.ReadInt32(),
                reader.ReadSingle(),
                reader.ReadSingle()),
            AtmosOperationCode.WakeChunk => new WakeChunkOperation(ReadInt3(reader)),
            AtmosOperationCode.SleepChunk => new SleepChunkOperation(ReadInt3(reader)),
            AtmosOperationCode.SetSolverEnabled => new SetSolverEnabledOperation(ReadString(reader, options), reader.ReadBoolean()),
            AtmosOperationCode.SetVoxelMixture => ReadMixture(reader),
            _ => throw new InvalidDataException($"Replay opcode {rawCode} is unsupported.")
        };
    }

    private static SetVoxelMixtureOperation ReadMixture(BinaryReader reader)
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

    private static void WriteHash(BinaryWriter writer, AtmosStateHash hash)
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

    private static T[] ReadArray<T>(BinaryReader reader, int count, Func<BinaryReader, T> read)
    {
        var values = new T[count];
        for (int i = 0; i < count; i++) values[i] = read(reader);
        return values;
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
        if (recording.Head.Tick < recording.Start.Tick ||
            recording.Head.OperationSequence < recording.Start.OperationSequence)
            throw new InvalidDataException("The replay recording bounds are invalid.");

        ulong sequence = recording.Start.OperationSequence;
        ulong tick = recording.Start.Tick;
        foreach (var operation in recording.Operations)
        {
            if (operation.Sequence != checked(sequence + 1) ||
                operation.AfterTick < tick ||
                operation.AfterTick > recording.Head.Tick ||
                operation.Sequence > recording.Head.OperationSequence)
                throw new InvalidDataException("Replay operations are not contiguous and ordered within the recording bounds.");

            sequence = operation.Sequence;
            tick = operation.AfterTick;
        }

        if (sequence != recording.Head.OperationSequence)
            throw new InvalidDataException("The replay operation history does not reach its declared head.");
    }

    private sealed record CheckpointPayload(AtmosSimulationCheckpoint Checkpoint, AtmosStateHash InitialHash, AtmosStateHash HeadHash);

    private sealed record MetadataPayload(
        NumosReplayMetadata Metadata,
        AtmosTimelinePosition Start,
        AtmosTimelinePosition Head,
        ulong OperationCount,
        ulong ChunkCount);
}