using System.Buffers;
using System.Diagnostics;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Solvers;

namespace Numos.CoreSim;

/// <summary>
///     Tracks conservative, progressively merged voxel aggregates for one chunk.
/// </summary>
/// <remarks>
///     Aggregates are topology only: atmospheric state remains materialized in the chunk's voxel arrays.
///     A merge is accepted from the post-pipeline state only when every member can be projected to the
///     combined equilibrium within the configured pressure, temperature, and composition limits. Each root
///     participates in at most one merge per tick, preventing overlapping neighbor projections and traversal-
///     order-dependent loss of mass or energy.
/// </remarks>
internal sealed class AggregateVoxels
{
    private const ulong FingerprintOffset = 14695981039346656037UL;
    private const ulong FingerprintPrime = 1099511628211UL;

    private bool[] _included = [];
    private int[] _gasOrder = [];
    private int[] _mergeBuffer = [];
    private int[] _next = [];
    private int[] _parent = [];
    private bool[] _participated = [];
    private double[] _speciesTotals = [];
    private double[] _voxelEffectiveTemperature = [];
    private double[] _voxelHeatCapacity = [];
    private double[] _voxelPressure = [];
    private double[] _voxelTotalMoles = [];

    private StateFingerprint _previousFingerprint;
    private bool _hasPreviousFingerprint;
    private int _includedCount;
    private bool _isInitialized;

    /// <summary>
    ///     Invalidates every progressive aggregate and the stable-state verification window.
    /// </summary>
    /// <returns>Whether the externally observable snap-group map contained a multi-voxel group.</returns>
    internal bool Reset()
    {
        bool snapGroupMapChanged = HasMultiVoxelAggregate();
        _isInitialized = false;
        _hasPreviousFingerprint = false;
        return snapGroupMapChanged;
    }

    private bool HasMultiVoxelAggregate()
    {
        if (!_isInitialized)
            return false;

        for (var voxelIndex = 0; voxelIndex < _parent.Length; voxelIndex++)
        {
            if (_parent[voxelIndex] == voxelIndex && _next[voxelIndex] >= 0)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Validates existing aggregates, performs one deterministic merge round, and advances automatic sleep.
    /// </summary>
    internal void FinalizeTick(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        HashSet<ulong> processedRootPairs)
    {
        Debug.Assert(chunk.IsAwake);
        RentWorkspace(chunk.VoxelCount);
        try
        {
            EnsureInitialized(chunk);
            PrepareGasOrder(chunk);

            StateFingerprint observedFingerprint = CalculateFingerprint(chunk);
            bool materializedStateChanged = !_hasPreviousFingerprint ||
                                            observedFingerprint != _previousFingerprint;
            bool aggregateChanged = ValidateExistingAggregates(
                chunk, config, materializedStateChanged, out bool allStatesValid);
            aggregateChanged |= MergeEligibleNeighbors(chunk, config, processedRootPairs);
            bool fullyAggregated = allStatesValid && AreAllPassableEdgesInternal(chunk);

            _previousFingerprint = CalculateFingerprint(chunk);
            _hasPreviousFingerprint = true;

            if (aggregateChanged || materializedStateChanged || !fullyAggregated)
            {
                chunk.SleepTimer = 0;
                return;
            }

            if (chunk.SleepTimer < int.MaxValue)
                chunk.SleepTimer++;
            // Thermodynamics runs every other tick. Even a caller-configured zero threshold must observe at least
            // one complete lower-frequency pass before committing sleep, or an unchanged intervening tick could
            // freeze an actionable thermal or phase-change gradient.
            int verificationThreshold = Math.Max(
                config.SleepThreshold, AtmosSolverConstants.ThermodynamicsTickInterval);
            if (chunk.SleepTimer > verificationThreshold)
                chunk.SleepAutomatically();
        }
        finally
        {
            ReturnWorkspace();
        }
    }

    private void RentWorkspace(int voxelCount)
    {
        _mergeBuffer = ArrayPool<int>.Shared.Rent(voxelCount);
        _voxelEffectiveTemperature = ArrayPool<double>.Shared.Rent(voxelCount);
        _voxelHeatCapacity = ArrayPool<double>.Shared.Rent(voxelCount);
        _voxelPressure = ArrayPool<double>.Shared.Rent(voxelCount);
        _voxelTotalMoles = ArrayPool<double>.Shared.Rent(voxelCount);
    }

    private void ReturnWorkspace()
    {
        ArrayPool<int>.Shared.Return(_mergeBuffer);
        ArrayPool<double>.Shared.Return(_voxelEffectiveTemperature);
        ArrayPool<double>.Shared.Return(_voxelHeatCapacity);
        ArrayPool<double>.Shared.Return(_voxelPressure);
        ArrayPool<double>.Shared.Return(_voxelTotalMoles);
        _mergeBuffer = [];
        _voxelEffectiveTemperature = [];
        _voxelHeatCapacity = [];
        _voxelPressure = [];
        _voxelTotalMoles = [];
    }

    /// <summary>
    ///     Returns whether two face-neighbor voxels already share one established aggregate.
    /// </summary>
    internal bool AreAggregatedTogether(ushort firstVoxel, ushort secondVoxel)
    {
        if (!_isInitialized ||
            firstVoxel >= _parent.Length || secondVoxel >= _parent.Length)
            return false;

        int firstRoot = _parent[firstVoxel];
        return firstRoot >= 0 && firstRoot == _parent[secondVoxel];
    }

    /// <summary>
    ///     Copies the canonical group ID for each voxel in an established multi-voxel aggregate.
    /// </summary>
    /// <remarks>
    ///     The result describes solver-owned aggregate topology, not similarity inferred from the materialized
    ///     pressure or composition arrays. A group ID is its lowest local flat voxel index. Inactive voxels,
    ///     singleton roots, and reset topology are reported as <c>-1</c>.
    /// </remarks>
    internal void CopySnapGroupMap(Span<int> destination)
    {
        destination.Fill(-1);
        if (!_isInitialized)
            return;

        Debug.Assert(destination.Length == _parent.Length);
        for (var voxelIndex = 0; voxelIndex < destination.Length; voxelIndex++)
        {
            int root = _parent[voxelIndex];
            if (root >= 0 && _next[root] >= 0)
                destination[voxelIndex] = root;
        }
    }

    /// <summary>
    ///     Returns whether the live materialized state still matches the last finalized aggregate state.
    /// </summary>
    /// <remarks>
    ///     Solvers may skip internal aggregate edges only while this exact fingerprint is current. A public
    ///     mutation, boundary transfer, or earlier custom stage otherwise has to be observed normally before
    ///     the terminal coordinator revalidates or splits the aggregate.
    /// </remarks>
    internal bool IsMaterializedStateCurrent(AtmosChunk chunk)
    {
        if (!_isInitialized || !_hasPreviousFingerprint)
            return false;

        PrepareGasOrder(chunk);
        return CalculateFingerprint(chunk) == _previousFingerprint;
    }

    private void EnsureInitialized(AtmosChunk chunk)
    {
        int voxelCount = chunk.VoxelCount;
        if (_parent.Length != voxelCount)
        {
            _included = new bool[voxelCount];
            _next = new int[voxelCount];
            _parent = new int[voxelCount];
            _participated = new bool[voxelCount];
            _isInitialized = false;
        }

        if (_isInitialized)
            return;

        Array.Clear(_included);
        Array.Fill(_next, -1);
        Array.Fill(_parent, -1);

        _includedCount = chunk.ActiveAirCount;
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            int voxelIndex = chunk.ActiveAirIndices[activeIndex];
            _included[voxelIndex] = true;
            _parent[voxelIndex] = voxelIndex;
        }

        _hasPreviousFingerprint = false;
        _isInitialized = true;
    }

    private void PrepareGasOrder(AtmosChunk chunk)
    {
        int gasCount = chunk.ActiveGasCount;
        if (_gasOrder.Length < gasCount)
            Array.Resize(ref _gasOrder, gasCount);
        if (_speciesTotals.Length < gasCount)
            Array.Resize(ref _speciesTotals, gasCount);

        for (var gas = 0; gas < gasCount; gas++)
            _gasOrder[gas] = gas;

        // Gas channels are created in mutation order. Sorting their indices by stable gas ID keeps aggregate
        // reduction and writeback deterministic when callers create the same mixture in a different order.
        for (var index = 1; index < gasCount; index++)
        {
            int channelIndex = _gasOrder[index];
            int gasId = chunk.ActiveGases[channelIndex].GasId;
            int insertionIndex = index;
            while (insertionIndex > 0 &&
                   chunk.ActiveGases[_gasOrder[insertionIndex - 1]].GasId > gasId)
            {
                _gasOrder[insertionIndex] = _gasOrder[insertionIndex - 1];
                insertionIndex--;
            }

            _gasOrder[insertionIndex] = channelIndex;
        }
    }

    private bool ValidateExistingAggregates(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        bool materializedStateChanged, out bool allStatesValid)
    {
        var aggregateChanged = false;
        allStatesValid = true;

        for (var root = 0; root < chunk.VoxelCount; root++)
        {
            if (_parent[root] != root)
                continue;

            if (!TryBuildEquilibrium(chunk, config, root, -1, out EquilibriumState equilibrium))
            {
                allStatesValid = false;
                if (_next[root] >= 0)
                {
                    Split(root);
                    aggregateChanged = true;
                }

                continue;
            }

            if (_next[root] < 0)
                continue;

            if (!IsWithinCorrectionLimits(chunk, config, root, -1, equilibrium))
            {
                Split(root);
                aggregateChanged = true;
                continue;
            }

            if (materializedStateChanged)
                aggregateChanged |= Materialize(chunk, config, root, equilibrium);
        }

        return aggregateChanged;
    }

    private bool MergeEligibleNeighbors(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        HashSet<ulong> processedRootPairs)
    {
        Array.Clear(_participated);
        processedRootPairs.Clear();
        var aggregateChanged = false;

        for (var voxelIndex = 0; voxelIndex < chunk.VoxelCount; voxelIndex++)
        {
            if (!_included[voxelIndex])
                continue;

            GetCoordinates(chunk, voxelIndex, out int x, out int y, out int z);
            if (x + 1 < chunk.Width)
                TryMergeEdge(chunk, config, processedRootPairs,
                    voxelIndex, voxelIndex + 1, ref aggregateChanged);
            if (y + 1 < chunk.Height)
                TryMergeEdge(chunk, config, processedRootPairs,
                    voxelIndex, voxelIndex + chunk.Width, ref aggregateChanged);
            if (z + 1 < chunk.Depth)
                TryMergeEdge(chunk, config, processedRootPairs, voxelIndex,
                    voxelIndex + chunk.Width * chunk.Height, ref aggregateChanged);
        }

        return aggregateChanged;
    }

    private void TryMergeEdge(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        HashSet<ulong> processedRootPairs, int firstVoxel, int secondVoxel,
        ref bool aggregateChanged)
    {
        if (!_included[secondVoxel])
            return;

        int firstRoot = _parent[firstVoxel];
        int secondRoot = _parent[secondVoxel];
        Debug.Assert(firstRoot >= 0 && secondRoot >= 0);
        if (firstRoot == secondRoot || _participated[firstRoot] || _participated[secondRoot])
            return;
        if (_next[firstRoot] >= 0 || _next[secondRoot] >= 0)
        {
            int lowerRoot = Math.Min(firstRoot, secondRoot);
            int upperRoot = Math.Max(firstRoot, secondRoot);
            ulong pairKey = ((ulong)(uint)lowerRoot << 32) | (uint)upperRoot;
            if (!processedRootPairs.Add(pairKey))
                return;
        }
        if (!TryBuildEquilibrium(chunk, config, firstRoot, secondRoot,
                out EquilibriumState equilibrium) ||
            !IsWithinCorrectionLimits(chunk, config, firstRoot, secondRoot, equilibrium))
            return;

        int mergedRoot = Merge(firstRoot, secondRoot);
        _participated[mergedRoot] = true;
        Materialize(chunk, config, mergedRoot, equilibrium);
        aggregateChanged = true;
    }

    private bool TryBuildEquilibrium(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        int firstRoot, int secondRoot, out EquilibriumState equilibrium)
    {
        Array.Clear(_speciesTotals, 0, chunk.ActiveGasCount);
        int memberCount = CopyMembersInCanonicalOrder(firstRoot, secondRoot);
        var totalMoles = 0d;
        var totalHeatCapacity = 0d;
        var totalEnergy = 0d;

        for (var memberIndex = 0; memberIndex < memberCount; memberIndex++)
        {
            int voxelIndex = _mergeBuffer[memberIndex];
            float storedTemperature = chunk.Temperature[voxelIndex];
            float effectiveTemperature = config.GetEffectiveTemperature(storedTemperature);
            if (!float.IsFinite(effectiveTemperature) || effectiveTemperature <= 0f)
            {
                equilibrium = default;
                return false;
            }

            double voxelMoles = 0d;
            double voxelHeatCapacity = 0d;
            for (var orderedGas = 0; orderedGas < chunk.ActiveGasCount; orderedGas++)
            {
                int channelIndex = _gasOrder[orderedGas];
                float moles = chunk.ActiveGases[channelIndex].Moles[voxelIndex];
                if (!float.IsFinite(moles) || moles < 0f)
                {
                    equilibrium = default;
                    return false;
                }

                _speciesTotals[channelIndex] += moles;
                voxelMoles += moles;
                voxelHeatCapacity += (double)moles *
                    config.GetMolarHeatCapacityAtConstantVolume(
                        chunk.ActiveGases[channelIndex].GasId);
            }

            double voxelPressure = voxelMoles * effectiveTemperature * config.PressurePerMoleKelvin;
            if (!double.IsFinite(voxelHeatCapacity) || !double.IsFinite(voxelPressure))
            {
                equilibrium = default;
                return false;
            }

            _voxelEffectiveTemperature[voxelIndex] = effectiveTemperature;
            _voxelHeatCapacity[voxelIndex] = voxelHeatCapacity;
            _voxelPressure[voxelIndex] = voxelPressure;
            _voxelTotalMoles[voxelIndex] = voxelMoles;
            totalMoles += voxelMoles;
            totalHeatCapacity += voxelHeatCapacity;
            totalEnergy += voxelHeatCapacity * effectiveTemperature;
        }

        if (memberCount <= 0 || !double.IsFinite(totalMoles) || !double.IsFinite(totalHeatCapacity) ||
            !double.IsFinite(totalEnergy))
        {
            equilibrium = default;
            return false;
        }

        if (totalHeatCapacity <= 0d)
        {
            equilibrium = new EquilibriumState(memberCount, totalMoles, 0d, 0d, 0d, 0d);
            return totalMoles == 0d;
        }

        double temperature = totalEnergy / totalHeatCapacity;
        double equilibriumPressure = totalMoles / memberCount * temperature * config.PressurePerMoleKelvin;
        if (!double.IsFinite(temperature) || temperature <= 0d ||
            !double.IsFinite(equilibriumPressure) || equilibriumPressure < 0d ||
            equilibriumPressure > float.MaxValue)
        {
            equilibrium = default;
            return false;
        }

        equilibrium = new EquilibriumState(memberCount, totalMoles, totalHeatCapacity,
            totalEnergy, temperature, equilibriumPressure);
        return true;
    }

    private bool IsWithinCorrectionLimits(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        int firstRoot, int secondRoot, EquilibriumState equilibrium)
    {
        for (var rootSlot = 0; rootSlot < 2; rootSlot++)
        {
            int root = rootSlot == 0 ? firstRoot : secondRoot;
            if (root < 0)
                continue;

            for (int voxelIndex = root; voxelIndex >= 0; voxelIndex = _next[voxelIndex])
            {
                double pressureCorrection = Math.Abs(
                    _voxelPressure[voxelIndex] - equilibrium.Pressure);
                double pressureScale = Math.Max(
                    Math.Max(_voxelPressure[voxelIndex], equilibrium.Pressure),
                    config.VacuumThreshold);
                double pressureCorrectionLimit = Math.Max(
                    config.SleepEpsilon,
                    config.VoxelSnapPressureRelativeEpsilon * pressureScale);
                if (!double.IsFinite(pressureCorrection) ||
                    pressureCorrection > pressureCorrectionLimit)
                    return false;

                if (equilibrium.TotalHeatCapacity <= 0d)
                    continue;

                double voxelTotalMoles = _voxelTotalMoles[voxelIndex];
                // Vacuum has no physically defined temperature or composition. Pressure bounds how much gas
                // may be projected into it; temperature and mole-fraction limits apply once gas is present.
                if (voxelTotalMoles <= 0d)
                    continue;
                if (Math.Abs(_voxelEffectiveTemperature[voxelIndex] - equilibrium.Temperature) >
                    config.VoxelSnapTemperatureEpsilon)
                    return false;

                for (var orderedGas = 0; orderedGas < chunk.ActiveGasCount; orderedGas++)
                {
                    int channelIndex = _gasOrder[orderedGas];
                    double targetFraction = _speciesTotals[channelIndex] / equilibrium.TotalMoles;
                    double currentFraction = voxelTotalMoles > 0d
                        ? chunk.ActiveGases[channelIndex].Moles[voxelIndex] / voxelTotalMoles
                        : 0d;
                    if (Math.Abs(currentFraction - targetFraction) >
                        config.VoxelSnapMoleFractionEpsilon)
                        return false;
                }
            }
        }

        return CanMaterializeFinite(chunk, config, firstRoot, secondRoot, equilibrium);
    }

    private bool CanMaterializeFinite(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        int firstRoot, int secondRoot, EquilibriumState equilibrium)
    {
        int memberCount = CopyMembersInCanonicalOrder(firstRoot, secondRoot);
        Debug.Assert(memberCount == equilibrium.MemberCount);
        for (var memberIndex = 0; memberIndex < memberCount; memberIndex++)
        {
            int voxelIndex = _mergeBuffer[memberIndex];
            _voxelHeatCapacity[voxelIndex] = 0d;
            _voxelTotalMoles[voxelIndex] = 0d;
        }

        for (var orderedGas = 0; orderedGas < chunk.ActiveGasCount; orderedGas++)
        {
            int channelIndex = _gasOrder[orderedGas];
            double molarHeatCapacity = config.GetMolarHeatCapacityAtConstantVolume(
                chunk.ActiveGases[channelIndex].GasId);
            double remainingMoles = _speciesTotals[channelIndex];
            int remainingMembers = memberCount;
            for (var memberIndex = 0; memberIndex < memberCount; memberIndex++)
            {
                int voxelIndex = _mergeBuffer[memberIndex];
                float targetMoles = (float)Math.Max(0d, remainingMoles / remainingMembers);
                if (!float.IsFinite(targetMoles))
                    return false;

                double projectedHeatCapacity = _voxelHeatCapacity[voxelIndex] +
                                               targetMoles * molarHeatCapacity;
                if (!double.IsFinite(projectedHeatCapacity) ||
                    projectedHeatCapacity > float.MaxValue)
                    return false;

                _voxelHeatCapacity[voxelIndex] = projectedHeatCapacity;
                _voxelTotalMoles[voxelIndex] += targetMoles;
                remainingMoles -= targetMoles;
                remainingMembers--;
            }
        }

        double remainingEnergy = equilibrium.TotalEnergy;
        double remainingHeatCapacity = 0d;
        for (var memberIndex = 0; memberIndex < memberCount; memberIndex++)
            remainingHeatCapacity += _voxelHeatCapacity[_mergeBuffer[memberIndex]];

        for (var memberIndex = 0; memberIndex < memberCount; memberIndex++)
        {
            int voxelIndex = _mergeBuffer[memberIndex];
            double voxelHeatCapacity = _voxelHeatCapacity[voxelIndex];
            float projectedMoles = (float)_voxelTotalMoles[voxelIndex];
            if (!float.IsFinite(projectedMoles))
                return false;
            if (voxelHeatCapacity <= 0d)
            {
                if (projectedMoles != 0f)
                    return false;
                continue;
            }

            double targetTemperature = remainingEnergy / remainingHeatCapacity;
            if (!double.IsFinite(targetTemperature) || targetTemperature <= 0d ||
                targetTemperature > float.MaxValue)
                return false;

            float storedTemperature = (float)targetTemperature;
            if (!float.IsFinite(storedTemperature) || storedTemperature <= 0f)
                return false;
            float projectedPressure = AtmosSolverMath.CalculatePressure(
                config, projectedMoles, storedTemperature);
            if (!float.IsFinite(projectedPressure))
                return false;
            remainingEnergy -= (double)storedTemperature * voxelHeatCapacity;
            remainingHeatCapacity -= voxelHeatCapacity;
        }

        return true;
    }

    private int CopyMembersInCanonicalOrder(int firstRoot, int secondRoot)
    {
        int firstMember = firstRoot;
        int secondMember = secondRoot;
        var memberCount = 0;
        while (firstMember >= 0 || secondMember >= 0)
        {
            if (secondMember < 0 || firstMember >= 0 && firstMember < secondMember)
            {
                _mergeBuffer[memberCount++] = firstMember;
                firstMember = _next[firstMember];
            }
            else
            {
                _mergeBuffer[memberCount++] = secondMember;
                secondMember = _next[secondMember];
            }
        }

        return memberCount;
    }

    private bool Materialize(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        int root, EquilibriumState equilibrium)
    {
        var materializedStateChanged = false;

        for (var orderedGas = 0; orderedGas < chunk.ActiveGasCount; orderedGas++)
        {
            int channelIndex = _gasOrder[orderedGas];
            double remainingMoles = _speciesTotals[channelIndex];
            int remainingMembers = equilibrium.MemberCount;
            for (int voxelIndex = root; voxelIndex >= 0; voxelIndex = _next[voxelIndex])
            {
                float targetMoles = (float)Math.Max(0d, remainingMoles / remainingMembers);
                ref float storedMoles = ref chunk.ActiveGases[channelIndex].Moles[voxelIndex];
                materializedStateChanged |= SetIfDifferent(ref storedMoles, targetMoles);
                remainingMoles -= targetMoles;
                remainingMembers--;
            }
        }

        double actualTotalHeatCapacity = 0d;
        for (int voxelIndex = root; voxelIndex >= 0; voxelIndex = _next[voxelIndex])
        {
            double voxelMoles = 0d;
            double voxelHeatCapacity = 0d;
            for (var orderedGas = 0; orderedGas < chunk.ActiveGasCount; orderedGas++)
            {
                int channelIndex = _gasOrder[orderedGas];
                float moles = chunk.ActiveGases[channelIndex].Moles[voxelIndex];
                voxelMoles += moles;
                voxelHeatCapacity += (double)moles *
                    config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[channelIndex].GasId);
            }

            _voxelTotalMoles[voxelIndex] = voxelMoles;
            _voxelHeatCapacity[voxelIndex] = voxelHeatCapacity;
            actualTotalHeatCapacity += voxelHeatCapacity;
        }

        double remainingEnergy = equilibrium.TotalEnergy;
        double remainingHeatCapacity = actualTotalHeatCapacity;
        for (int voxelIndex = root; voxelIndex >= 0; voxelIndex = _next[voxelIndex])
        {
            double voxelHeatCapacity = _voxelHeatCapacity[voxelIndex];
            if (voxelHeatCapacity > 0d)
            {
                double target = remainingHeatCapacity > 0d
                    ? remainingEnergy / remainingHeatCapacity
                    : equilibrium.Temperature;
                float targetTemperature = double.IsFinite(target) && target > 0d && target <= float.MaxValue
                    ? (float)target
                    : (float)equilibrium.Temperature;
                float storedTemperature = chunk.Temperature[voxelIndex];
                if (BitConverter.SingleToInt32Bits(storedTemperature) !=
                    BitConverter.SingleToInt32Bits(targetTemperature))
                {
                    chunk.Temperature[voxelIndex] = targetTemperature;
                    materializedStateChanged = true;
                }
                remainingEnergy -= (double)targetTemperature * voxelHeatCapacity;
                remainingHeatCapacity -= voxelHeatCapacity;
            }

            chunk.TotalHeatCapacity[voxelIndex] = (float)voxelHeatCapacity;
            chunk.TotalPressure[voxelIndex] = AtmosSolverMath.CalculatePressure(
                config, (float)_voxelTotalMoles[voxelIndex], chunk.Temperature[voxelIndex]);
        }

        return materializedStateChanged;
    }

    private int Merge(int firstRoot, int secondRoot)
    {
        int memberCount = CopyMembersInCanonicalOrder(firstRoot, secondRoot);
        int mergedRoot = _mergeBuffer[0];
        for (var index = 0; index < memberCount; index++)
        {
            int member = _mergeBuffer[index];
            _parent[member] = mergedRoot;
            _next[member] = index + 1 < memberCount ? _mergeBuffer[index + 1] : -1;
        }

        return mergedRoot;
    }

    private void Split(int root)
    {
        var memberCount = 0;
        for (int member = root; member >= 0; member = _next[member])
            _mergeBuffer[memberCount++] = member;

        for (var index = 0; index < memberCount; index++)
        {
            int member = _mergeBuffer[index];
            _parent[member] = member;
            _next[member] = -1;
        }
    }

    private bool AreAllPassableEdgesInternal(AtmosChunk chunk)
    {
        for (var voxelIndex = 0; voxelIndex < chunk.VoxelCount; voxelIndex++)
        {
            if (!_included[voxelIndex])
                continue;

            int root = _parent[voxelIndex];
            GetCoordinates(chunk, voxelIndex, out int x, out int y, out int z);
            if (x + 1 < chunk.Width && !IsInternalEdge(root, voxelIndex + 1))
                return false;
            if (y + 1 < chunk.Height && !IsInternalEdge(root, voxelIndex + chunk.Width))
                return false;
            if (z + 1 < chunk.Depth &&
                !IsInternalEdge(root, voxelIndex + chunk.Width * chunk.Height))
                return false;
        }

        return true;
    }

    private bool IsInternalEdge(int root, int neighborIndex)
    {
        return !_included[neighborIndex] || _parent[neighborIndex] == root;
    }

    private StateFingerprint CalculateFingerprint(AtmosChunk chunk)
    {
        ulong first = FingerprintOffset;
        ulong second = 0x9E3779B97F4A7C15UL;
        Mix(ref first, ref second, _includedCount);
        Mix(ref first, ref second, chunk.ActiveGasCount);

        for (var orderedGas = 0; orderedGas < chunk.ActiveGasCount; orderedGas++)
        {
            int channelIndex = _gasOrder[orderedGas];
            Mix(ref first, ref second, chunk.ActiveGases[channelIndex].GasId);
        }

        for (var voxelIndex = 0; voxelIndex < chunk.VoxelCount; voxelIndex++)
        {
            if (!_included[voxelIndex])
                continue;

            Mix(ref first, ref second, voxelIndex);
            Mix(ref first, ref second, BitConverter.SingleToInt32Bits(chunk.Temperature[voxelIndex]));
            for (var orderedGas = 0; orderedGas < chunk.ActiveGasCount; orderedGas++)
            {
                int channelIndex = _gasOrder[orderedGas];
                Mix(ref first, ref second,
                    BitConverter.SingleToInt32Bits(chunk.ActiveGases[channelIndex].Moles[voxelIndex]));
            }
        }

        return new StateFingerprint(first, second);
    }

    private static void Mix(ref ulong first, ref ulong second, int value)
    {
        unchecked
        {
            ulong data = (uint)value;
            first = (first ^ data) * FingerprintPrime;
            second ^= data + 0x9E3779B97F4A7C15UL + (second << 6) + (second >> 2);
        }
    }

    private static bool SetIfDifferent(ref float storage, float value)
    {
        if (BitConverter.SingleToInt32Bits(storage) == BitConverter.SingleToInt32Bits(value))
            return false;

        storage = value;
        return true;
    }

    private static void GetCoordinates(AtmosChunk chunk, int voxelIndex,
        out int x, out int y, out int z)
    {
        x = voxelIndex % chunk.Width;
        int yz = voxelIndex / chunk.Width;
        y = yz % chunk.Height;
        z = yz / chunk.Height;
    }

    private readonly record struct EquilibriumState(
        int MemberCount,
        double TotalMoles,
        double TotalHeatCapacity,
        double TotalEnergy,
        double Temperature,
        double Pressure);

    private readonly record struct StateFingerprint(ulong First, ulong Second);
}
