using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Headless.Protocol;
using Numos.Maths;

namespace Numos.Headless.Diagnostics;

/// <summary>
///     Converts authoritative detached snapshots into deterministic, JSON-friendly diagnostics.
/// </summary>
public static class SimulationStateAnalyzer
{
    public static SimulationStateReport Analyze(
        AtmosSimulation simulation,
        AtmosConfig config,
        SimulationObservationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(config);
        options ??= new SimulationObservationOptions();

        int issueLimit = Math.Clamp(
            options.MaxIssueLocations,
            0,
            SimulationObservationOptions.MaximumMaxIssueLocations);
        AtmosChunkHandle[] handles = SelectHandles(simulation.GetChunkHandles(), options.Chunk);
        var requests = handles
            .Select(static handle => new AtmosChunkSnapshotRequest(
                handle.Position,
                default,
                AtmosChunkSnapshotFields.All))
            .ToArray();
        AtmosChunkSnapshotBatch batch = simulation.GetChangedChunkSnapshots(requests);
        AtmosChunkSnapshot[] snapshots = batch.ChangedChunks
            .OrderBy(static snapshot => snapshot.GridPosition.X)
            .ThenBy(static snapshot => snapshot.GridPosition.Y)
            .ThenBy(static snapshot => snapshot.GridPosition.Z)
            .ToArray();

        Dictionary<Int3, HashSet<int>> selectedVoxels = ResolveVoxelSelections(snapshots, options);
        var issues = new IssueCollector(issueLimit);
        var global = new SummaryAccumulator(config);
        var chunkReports = new ChunkStateReport[snapshots.Length];

        for (var chunkIndex = 0; chunkIndex < snapshots.Length; chunkIndex++)
        {
            AtmosChunkSnapshot snapshot = snapshots[chunkIndex];
            var chunkSummary = new SummaryAccumulator(config);
            var voxelReports = new List<VoxelStateReport>();
            selectedVoxels.TryGetValue(snapshot.GridPosition, out HashSet<int>? selectedIndices);

            int voxelCount = GetVoxelCount(snapshot.Dimensions);
            for (var localIndex = 0; localIndex < voxelCount; localIndex++)
            {
                Coordinate localPosition = GetCoordinates(localIndex, snapshot.Dimensions);
                VoxelAnalysis voxel = chunkSummary.AddVoxel(
                    snapshot,
                    localIndex,
                    localPosition,
                    issues);
                global.AddVoxel(snapshot, localIndex, localPosition, null);

                bool explicitlySelected = selectedIndices?.Contains(localIndex) == true;
                bool includeFromFullScan = options.IncludeVoxels &&
                                           (!options.OnlyGasBearingVoxels || voxel.IsGasBearing);
                if (explicitlySelected || includeFromFullScan)
                    voxelReports.Add(CreateVoxelReport(snapshot, localIndex, localPosition, voxel, config));
            }

            chunkReports[chunkIndex] = new ChunkStateReport(
                Coordinate.From(snapshot.GridPosition),
                Coordinate.From(snapshot.Dimensions),
                snapshot.Version.Generation,
                snapshot.Version.Revision,
                snapshot.IsAwake,
                snapshot.SleepTimer,
                snapshot.ActiveAirCount,
                snapshot.ActiveGasCount,
                chunkSummary.ToChunkReport(),
                voxelReports.ToArray());
            global.AddChunk(snapshot);
        }

        return new SimulationStateReport(
            batch.TickCount,
            AtmosSimulation.SimulationRate,
            simulation.ChunkCount,
            CreateConfigurationReport(config),
            simulation.Solvers.Steps.Select(CreateSolverReport).ToArray(),
            global.ToGlobalReport(),
            chunkReports,
            issues.Items.ToArray(),
            issues.Truncated);
    }

    private static AtmosChunkHandle[] SelectHandles(
        AtmosChunkHandle[] handles,
        Coordinate? selectedChunk)
    {
        if (!selectedChunk.HasValue)
            return handles;

        Int3 position = selectedChunk.Value.ToInt3();
        foreach (AtmosChunkHandle handle in handles)
        {
            if (handle.Position == position)
                return [handle];
        }

        throw new KeyNotFoundException($"No chunk is registered at {position}.");
    }

    private static Dictionary<Int3, HashSet<int>> ResolveVoxelSelections(
        IReadOnlyList<AtmosChunkSnapshot> snapshots,
        SimulationObservationOptions options)
    {
        var result = new Dictionary<Int3, HashSet<int>>();
        if (options.Voxels == null || options.Voxels.Count == 0)
            return result;

        var snapshotsByPosition = snapshots.ToDictionary(static snapshot => snapshot.GridPosition);
        foreach (VoxelSelection selection in options.Voxels)
        {
            Int3 chunkPosition = selection.Chunk.ToInt3();
            if (options.Chunk.HasValue && options.Chunk.Value.ToInt3() != chunkPosition)
            {
                throw new ArgumentException(
                    $"Voxel selection chunk {chunkPosition} is outside the requested chunk scope.",
                    nameof(options));
            }

            if (!snapshotsByPosition.TryGetValue(chunkPosition, out AtmosChunkSnapshot snapshot))
                throw new KeyNotFoundException($"No chunk is registered at {chunkPosition}.");

            Coordinate voxel = selection.Voxel;
            if (voxel.X < 0 || voxel.X >= snapshot.Dimensions.X ||
                voxel.Y < 0 || voxel.Y >= snapshot.Dimensions.Y ||
                voxel.Z < 0 || voxel.Z >= snapshot.Dimensions.Z)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"Voxel {voxel.X}, {voxel.Y}, {voxel.Z} is outside chunk {chunkPosition} with dimensions " +
                    $"{snapshot.Dimensions.X}, {snapshot.Dimensions.Y}, {snapshot.Dimensions.Z}.");
            }

            int localIndex = voxel.X + snapshot.Dimensions.X *
                (voxel.Y + snapshot.Dimensions.Y * voxel.Z);
            if (!result.TryGetValue(chunkPosition, out HashSet<int>? indices))
            {
                indices = [];
                result.Add(chunkPosition, indices);
            }

            indices.Add(localIndex);
        }

        return result;
    }

    private static SimulationConfigurationReport CreateConfigurationReport(AtmosConfig config)
    {
        var gases = new GasConfigurationReport[config.GasRegistry.Count];
        for (var gasId = 0; gasId < gases.Length; gasId++)
        {
            GasProperties gas = config.GasRegistry[gasId];
            gases[gasId] = new GasConfigurationReport(
                gasId,
                gas.Name,
                gas.MolarHeatCapacityAtConstantVolume,
                gas.BoilingPoint,
                gas.CondensationEnabled,
                gas.MolarEnthalpyOfVaporization,
                gas.LiquidId,
                gas.DiffusionCoefficient);
        }

        return new SimulationConfigurationReport(
            config.GlobalTemperature,
            config.DefaultTemperatureFallback,
            config.DefaultMolarHeatCapacityAtConstantVolume,
            config.VoxelVolume,
            config.SaturationReferencePressure,
            config.DefaultDiffusionCoefficient,
            config.SpaceTemperature,
            config.BulkFlowCoefficient,
            config.VacuumThreshold,
            config.SleepThreshold,
            config.SleepEpsilon,
            config.ThermalConductance,
            config.CondensationRateFactor,
            config.MaxPressureTransferFractionPerNeighbor,
            config.AccumulatorWakeThreshold,
            config.AccumulatorMaxAliveTicks,
            gases);
    }

    private static SolverStepReport CreateSolverReport(AtmosSolverStep step)
    {
        string kind = step.Kind switch
        {
            AtmosSolverKind.BuiltIn => "builtIn",
            AtmosSolverKind.Custom => "custom",
            _ => throw new ArgumentOutOfRangeException(nameof(step))
        };
        return new SolverStepReport(step.Name, step.IsEnabled, kind);
    }

    private static VoxelStateReport CreateVoxelReport(
        AtmosChunkSnapshot snapshot,
        int localIndex,
        Coordinate localPosition,
        VoxelAnalysis voxel,
        AtmosConfig config)
    {
        VoxelGasReport[] gases = snapshot.Gases
            .OrderBy(static gas => gas.GasId)
            .Select(gas => new VoxelGasReport(
                gas.GasId,
                GetGasName(config, gas.GasId),
                gas.Moles[localIndex]))
            .ToArray();
        return new VoxelStateReport(
            localIndex,
            localPosition,
            snapshot.VoxelRoomMap[localIndex],
            voxel.IsGasCapable,
            voxel.IsGasBearing,
            snapshot.TotalPressure[localIndex],
            snapshot.Temperature[localIndex],
            voxel.TotalMoles,
            voxel.SensibleEnergy,
            gases);
    }

    private static string? GetGasName(AtmosConfig config, int gasId)
    {
        return gasId >= 0 && gasId < config.GasRegistry.Count
            ? config.GasRegistry[gasId].Name
            : null;
    }

    private static int GetVoxelCount(Int3 dimensions)
    {
        return checked(dimensions.X * dimensions.Y * dimensions.Z);
    }

    private static Coordinate GetCoordinates(int localIndex, Int3 dimensions)
    {
        int x = localIndex % dimensions.X;
        int remainder = localIndex / dimensions.X;
        int y = remainder % dimensions.Y;
        int z = remainder / dimensions.Y;
        return new Coordinate(x, y, z);
    }

    private sealed class SummaryAccumulator(AtmosConfig config)
    {
        private readonly Dictionary<int, double> _gasMoles = [];
        private readonly FiniteStatisticsAccumulator _pressure = new();
        private readonly FiniteStatisticsAccumulator _temperature = new();
        private readonly AnomalyCountsAccumulator _anomalies = new();
        private int _chunkCount;
        private int _voxelCount;
        private int _gasCapableVoxelCount;
        private int _solidVoxelCount;
        private int _voidVoxelCount;
        private int _gasBearingVoxelCount;
        private int _activeAirCount;
        private int _activeGasChannelCount;
        private int _awakeChunkCount;
        private double _totalMoles;
        private double _sensibleEnergy;

        internal VoxelAnalysis AddVoxel(
            AtmosChunkSnapshot snapshot,
            int localIndex,
            Coordinate localPosition,
            IssueCollector? issues)
        {
            _voxelCount++;
            int roomId = snapshot.VoxelRoomMap[localIndex];
            bool isSolid = roomId == VoxelClassification.RoomSolid;
            bool isVoid = roomId == VoxelClassification.RoomVoid;
            bool isGasCapable = !isSolid && !isVoid;
            if (isSolid)
                _solidVoxelCount++;
            else if (isVoid)
                _voidVoxelCount++;
            else
                _gasCapableVoxelCount++;

            float pressure = snapshot.TotalPressure[localIndex];
            float temperature = snapshot.Temperature[localIndex];
            _pressure.Add(pressure);
            _temperature.Add(temperature);
            InspectScalar(pressure, "Pressure", snapshot, localPosition, localIndex, null, issues);
            InspectScalar(temperature, "Temperature", snapshot, localPosition, localIndex, null, issues);

            double voxelMoles = 0d;
            double voxelEnergy = 0d;
            bool isGasBearing = false;
            float effectiveTemperature = GetEffectiveTemperature(config, temperature);
            foreach (GasSnapshot gas in snapshot.Gases)
            {
                float moles = gas.Moles[localIndex];
                voxelMoles += moles;
                // Invalid and negative amounts are still state worth retaining when a caller asks for
                // gas-bearing voxels only; treating any non-zero value as occupied keeps them visible.
                isGasBearing |= moles != 0f;
                _gasMoles.TryGetValue(gas.GasId, out double currentMoles);
                _gasMoles[gas.GasId] = currentMoles + moles;
                voxelEnergy += (double)moles * GetMolarHeatCapacity(config, gas.GasId) * effectiveTemperature;
                InspectScalar(moles, "Moles", snapshot, localPosition, localIndex, gas.GasId, issues);
            }

            if (isGasBearing)
                _gasBearingVoxelCount++;
            _totalMoles += voxelMoles;
            _sensibleEnergy += voxelEnergy;
            return new VoxelAnalysis(isGasCapable, isGasBearing, voxelMoles, voxelEnergy);
        }

        internal void AddChunk(AtmosChunkSnapshot snapshot)
        {
            _chunkCount++;
            _activeAirCount += snapshot.ActiveAirCount;
            _activeGasChannelCount += snapshot.ActiveGasCount;
            if (snapshot.IsAwake)
                _awakeChunkCount++;
        }

        internal ChunkSummaryReport ToChunkReport()
        {
            return new ChunkSummaryReport(
                _voxelCount,
                _gasCapableVoxelCount,
                _solidVoxelCount,
                _voidVoxelCount,
                _gasBearingVoxelCount,
                _totalMoles,
                _sensibleEnergy,
                _pressure.ToReport(),
                _temperature.ToReport(),
                CreateGasTotals(),
                _anomalies.ToReport());
        }

        internal SimulationSummaryReport ToGlobalReport()
        {
            return new SimulationSummaryReport(
                _chunkCount,
                _voxelCount,
                _gasCapableVoxelCount,
                _solidVoxelCount,
                _voidVoxelCount,
                _gasBearingVoxelCount,
                _activeAirCount,
                _activeGasChannelCount,
                _awakeChunkCount,
                _chunkCount - _awakeChunkCount,
                _totalMoles,
                _sensibleEnergy,
                _pressure.ToReport(),
                _temperature.ToReport(),
                CreateGasTotals(),
                _anomalies.ToReport());
        }

        private GasTotalReport[] CreateGasTotals()
        {
            return _gasMoles
                .OrderBy(static pair => pair.Key)
                .Select(pair => new GasTotalReport(pair.Key, GetGasName(config, pair.Key), pair.Value))
                .ToArray();
        }

        private void InspectScalar(
            float value,
            string field,
            AtmosChunkSnapshot snapshot,
            Coordinate localPosition,
            int localIndex,
            int? gasId,
            IssueCollector? issues)
        {
            if (!float.IsFinite(value))
            {
                _anomalies.AddNonFinite(field);
                issues?.Add(new AnomalyIssueReport(
                    $"nonFinite{field}",
                    Coordinate.From(snapshot.GridPosition),
                    localPosition,
                    localIndex,
                    gasId,
                    value));
            }
            else if (value < 0f)
            {
                _anomalies.AddNegative(field);
                issues?.Add(new AnomalyIssueReport(
                    $"negative{field}",
                    Coordinate.From(snapshot.GridPosition),
                    localPosition,
                    localIndex,
                    gasId,
                    value));
            }
        }
    }

    private sealed class FiniteStatisticsAccumulator
    {
        private int _sampleCount;
        private int _finiteCount;
        private double _sum;
        private double _minimum = double.PositiveInfinity;
        private double _maximum = double.NegativeInfinity;

        internal void Add(float value)
        {
            _sampleCount++;
            if (!float.IsFinite(value))
                return;
            _finiteCount++;
            _sum += value;
            _minimum = Math.Min(_minimum, value);
            _maximum = Math.Max(_maximum, value);
        }

        internal FiniteStatisticsReport ToReport()
        {
            return new FiniteStatisticsReport(
                _sampleCount,
                _finiteCount,
                _sampleCount - _finiteCount,
                _finiteCount == 0 ? null : _minimum,
                _finiteCount == 0 ? null : _maximum,
                _finiteCount == 0 ? null : _sum / _finiteCount);
        }
    }

    private sealed class AnomalyCountsAccumulator
    {
        private int _nonFinitePressure;
        private int _negativePressure;
        private int _nonFiniteTemperature;
        private int _negativeTemperature;
        private int _nonFiniteMoles;
        private int _negativeMoles;

        internal void AddNonFinite(string field)
        {
            switch (field)
            {
                case "Pressure": _nonFinitePressure++; break;
                case "Temperature": _nonFiniteTemperature++; break;
                case "Moles": _nonFiniteMoles++; break;
            }
        }

        internal void AddNegative(string field)
        {
            switch (field)
            {
                case "Pressure": _negativePressure++; break;
                case "Temperature": _negativeTemperature++; break;
                case "Moles": _negativeMoles++; break;
            }
        }

        internal AnomalyCountsReport ToReport()
        {
            return new AnomalyCountsReport(
                _nonFinitePressure,
                _negativePressure,
                _nonFiniteTemperature,
                _negativeTemperature,
                _nonFiniteMoles,
                _negativeMoles);
        }
    }

    private sealed class IssueCollector(int maximumCount)
    {
        internal List<AnomalyIssueReport> Items { get; } = [];
        internal bool Truncated { get; private set; }

        internal void Add(AnomalyIssueReport issue)
        {
            if (Items.Count < maximumCount)
                Items.Add(issue);
            else
                Truncated = true;
        }
    }

    private static float GetEffectiveTemperature(AtmosConfig config, float temperature)
    {
        if (float.IsFinite(temperature) && temperature > 0f)
            return temperature;
        return float.IsFinite(config.DefaultTemperatureFallback) && config.DefaultTemperatureFallback > 0f
            ? config.DefaultTemperatureFallback
            : AtmosPhysicalConstants.RoomTemperature;
    }

    private static float GetMolarHeatCapacity(AtmosConfig config, int gasId)
    {
        float fallback = float.IsFinite(config.DefaultMolarHeatCapacityAtConstantVolume) &&
                         config.DefaultMolarHeatCapacityAtConstantVolume > 0f
            ? config.DefaultMolarHeatCapacityAtConstantVolume
            : AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume;
        if (gasId < 0 || gasId >= config.GasRegistry.Count)
            return fallback;
        float configured = config.GasRegistry[gasId].MolarHeatCapacityAtConstantVolume;
        return float.IsFinite(configured) && configured > 0f ? configured : fallback;
    }

    private readonly record struct VoxelAnalysis(
        bool IsGasCapable,
        bool IsGasBearing,
        double TotalMoles,
        double SensibleEnergy);
}