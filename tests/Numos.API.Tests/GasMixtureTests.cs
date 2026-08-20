using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class GasMixtureTests
{
    [Test]
    public void CreateGasMixture_ValidatesVolumeAndTemperature()
    {
        using var simulation = CreateSimulation();

        Assert.Multiple(() =>
        {
            Assert.That(() => simulation.CreateGasMixture(0f), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => simulation.CreateGasMixture(float.PositiveInfinity),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => simulation.CreateGasMixture(1f, -1f),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => simulation.CreateGasMixture(1f, float.NaN),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void OwnedMixture_SupportsSparseArbitraryGasIdsAndDeterministicSnapshots()
    {
        using var simulation = CreateSimulation();
        var mixture = simulation.CreateGasMixture(2f, 300f);

        mixture.SetMoles(20, 1.5f);
        mixture.SetMoles(3, 2f);
        mixture.AdjustMoles(20, -0.5f);

        var snapshot = mixture.GetSnapshot();
        float expectedPressure = 3f * AtmosPhysicalConstants.MolarGasConstant * 300f / 2f;

        Assert.Multiple(() =>
        {
            Assert.That(mixture.Owner, Is.SameAs(simulation));
            Assert.That(mixture.ActiveGasCount, Is.EqualTo(2));
            Assert.That(mixture.TotalMoles, Is.EqualTo(3f));
            Assert.That(mixture.GetMoles(999), Is.Zero);
            Assert.That(mixture.Pressure, Is.EqualTo(expectedPressure).Within(0.001f));
            Assert.That(snapshot.Gases.Select(static gas => gas.GasId), Is.EqualTo(new[] { 3, 20 }));
            Assert.That(snapshot.GetMoles(3), Is.EqualTo(2f));
            Assert.That(snapshot.GetMoles(20), Is.EqualTo(1f));
        });

        snapshot.Gases[0] = new GasMixtureGas(3, 100f);
        Assert.That(mixture.GetMoles(3), Is.EqualTo(2f));
    }

    [Test]
    public void OwnedMixture_VolumeChangesPressureWithoutChangingContents()
    {
        using var simulation = CreateSimulation();
        var mixture = simulation.CreateGasMixture(1f, 300f);
        mixture.SetMoles(0, 2f);
        float initialPressure = mixture.Pressure;

        mixture.Volume = 4f;

        Assert.Multiple(() =>
        {
            Assert.That(mixture.Volume, Is.EqualTo(4f));
            Assert.That(mixture.Pressure, Is.EqualTo(initialPressure / 4f).Within(0.001f));
            Assert.That(mixture.TotalMoles, Is.EqualTo(2f));
            Assert.That(() => mixture.Volume = float.NaN, Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void AddGas_MixesTemperatureByConstantVolumeHeatCapacity()
    {
        using var simulation = CreateSimulation();
        var mixture = simulation.CreateGasMixture(1f, 300f);
        mixture.SetMoles(0, 1f);

        mixture.AddGas(1, 1f, 600f);

        Assert.Multiple(() =>
        {
            Assert.That(mixture.TotalMoles, Is.EqualTo(2f));
            Assert.That(mixture.GetMoles(0), Is.EqualTo(1f));
            Assert.That(mixture.GetMoles(1), Is.EqualTo(1f));
            Assert.That(mixture.Temperature, Is.EqualTo(500f).Within(0.0001f));
        });
    }

    [Test]
    public void AddGas_LargeFiniteEnergiesMixWithoutIntermediateOverflow()
    {
        using var simulation = CreateSimulation(float.MaxValue);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var owned = simulation.CreateGasMixture(float.MaxValue, 300f);
        var voxel = simulation.GetVoxelGasMixture(chunk, 0);
        owned.AddGas(0, 1e30f, 300f);
        voxel.AddGas(0, 1e30f, 300f);

        Assert.Multiple(() =>
        {
            Assert.That(() => owned.AddGas(1, 1e30f, 1e30f), Throws.Nothing);
            Assert.That(() => voxel.AddGas(1, 1e30f, 1e30f), Throws.Nothing);
        });

        // The test registry uses Cv=10 for gas 0 and Cv=20 for gas 1.
        float expectedTemperature = 2e30f / 3f;
        Assert.Multiple(() =>
        {
            Assert.That(float.IsFinite(owned.Temperature), Is.True);
            Assert.That(float.IsFinite(voxel.Temperature), Is.True);
            Assert.That(owned.Temperature,
                Is.EqualTo(expectedTemperature).Within(expectedTemperature * 0.000001f));
            Assert.That(voxel.Temperature,
                Is.EqualTo(expectedTemperature).Within(expectedTemperature * 0.000001f));
            Assert.That(float.IsFinite(owned.Pressure), Is.True);
            Assert.That(float.IsFinite(voxel.Pressure), Is.True);
        });
    }

    [Test]
    public void EnergyOperationsUseOwnersCurrentGasRegistry()
    {
        using var simulation = CreateSimulation();
        var mixture = simulation.CreateGasMixture(1f, 300f);
        mixture.SetMoles(0, 1f);
        simulation.SetAtmosConfig(new AtmosConfig
        {
            GasRegistry =
            [
                new GasProperties { Name = "Updated", MolarHeatCapacityAtConstantVolume = 20f },
                new GasProperties { Name = "Incoming", MolarHeatCapacityAtConstantVolume = 10f }
            ]
        });

        mixture.AddGas(1, 1f, 600f);

        Assert.That(mixture.Temperature, Is.EqualTo(400f).Within(0.0001f));
    }

    [Test]
    public void RemoveRatio_PreservesCompositionTemperatureAndOwner()
    {
        using var simulation = CreateSimulation();
        var source = simulation.CreateGasMixture(3f, 350f);
        source.SetMoles(0, 2f);
        source.SetMoles(7, 6f);

        var removed = source.RemoveRatio(0.25f);

        Assert.Multiple(() =>
        {
            Assert.That(source.GetMoles(0), Is.EqualTo(1.5f));
            Assert.That(source.GetMoles(7), Is.EqualTo(4.5f));
            Assert.That(removed.GetMoles(0), Is.EqualTo(0.5f));
            Assert.That(removed.GetMoles(7), Is.EqualTo(1.5f));
            Assert.That(removed.Volume, Is.EqualTo(3f));
            Assert.That(removed.Temperature, Is.EqualTo(350f));
            Assert.That(removed.Owner, Is.SameAs(simulation));
            Assert.That(source.TotalMoles + removed.TotalMoles, Is.EqualTo(8f));
        });
    }

    [Test]
    public void CloneAndClear_DoNotShareBackingState()
    {
        using var simulation = CreateSimulation();
        var source = simulation.CreateGasMixture(2f, 320f);
        source.SetMoles(4, 3f);

        var clone = source.Clone();
        source.Clear();

        Assert.Multiple(() =>
        {
            Assert.That(source.TotalMoles, Is.Zero);
            Assert.That(source.Temperature, Is.EqualTo(320f));
            Assert.That(clone.GetMoles(4), Is.EqualTo(3f));
            Assert.That(clone.Volume, Is.EqualTo(2f));
        });
    }

    [Test]
    public void TransferTo_ConservesMolesAndSensibleEnergy()
    {
        using var simulation = CreateSimulation();
        var source = simulation.CreateGasMixture(1f, 600f);
        source.SetMoles(1, 2f);
        var destination = simulation.CreateGasMixture(1f, 300f);
        destination.SetMoles(0, 1f);

        float transferred = source.TransferTo(destination, 1f);

        Assert.Multiple(() =>
        {
            Assert.That(transferred, Is.EqualTo(1f));
            Assert.That(source.GetMoles(1), Is.EqualTo(1f));
            Assert.That(source.Temperature, Is.EqualTo(600f));
            Assert.That(destination.GetMoles(0), Is.EqualTo(1f));
            Assert.That(destination.GetMoles(1), Is.EqualTo(1f));
            Assert.That(destination.Temperature, Is.EqualTo(500f).Within(0.0001f));
            Assert.That(source.TotalMoles + destination.TotalMoles, Is.EqualTo(3f));
        });
    }

    [Test]
    public void TransferTo_DifferentOwnerIsRejectedWithoutMutation()
    {
        using var firstSimulation = CreateSimulation();
        using var secondSimulation = CreateSimulation();
        var source = firstSimulation.CreateGasMixture(1f, 300f);
        source.SetMoles(0, 2f);
        var destination = secondSimulation.CreateGasMixture(1f, 300f);

        Assert.That(() => source.TransferTo(destination, 1f), Throws.ArgumentException);
        Assert.Multiple(() =>
        {
            Assert.That(source.TotalMoles, Is.EqualTo(2f));
            Assert.That(destination.TotalMoles, Is.Zero);
        });
    }

    [Test]
    public void VoxelMixture_MutatesLiveSoaStateWithoutExposingStorage()
    {
        using var simulation = CreateSimulation(voxelVolume: 2f);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var before = simulation.GetChunkSnapshot(chunk).Version;
        var mixture = simulation.GetVoxelGasMixture(chunk, 0);

        mixture.AddGas(0, 2f, 300f);
        mixture.SetMoles(7, 1f);
        mixture.AdjustMoles(0, -0.5f);

        var voxel = simulation.GetVoxelSnapshot(chunk, 0);
        float expectedPressure = 2.5f * AtmosPhysicalConstants.MolarGasConstant * 300f / 2f;

        Assert.Multiple(() =>
        {
            Assert.That(mixture.Volume, Is.EqualTo(2f));
            Assert.That(mixture.TotalMoles, Is.EqualTo(2.5f));
            Assert.That(mixture.ActiveGasCount, Is.EqualTo(2));
            Assert.That(mixture.Pressure, Is.EqualTo(expectedPressure).Within(0.001f));
            Assert.That(voxel.Pressure, Is.EqualTo(expectedPressure).Within(0.001f));
            Assert.That(voxel.ChunkVersion, Is.Not.EqualTo(before));
            Assert.That(voxel.Gases.Single(gas => gas.GasId == 0).Moles, Is.EqualTo(1.5f));
            Assert.That(voxel.Gases.Single(gas => gas.GasId == 7).Moles, Is.EqualTo(1f));
        });
    }

    [Test]
    public void VoxelMixture_TemperatureMutationRefreshesSnapshotPressureImmediately()
    {
        using var simulation = CreateSimulation(voxelVolume: 2f);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var mixture = simulation.GetVoxelGasMixture(chunk, 0);
        mixture.SetMoles(0, 2f);

        mixture.Temperature = 450f;

        float expectedPressure = 2f * AtmosPhysicalConstants.MolarGasConstant * 450f / 2f;
        Assert.Multiple(() =>
        {
            Assert.That(mixture.Pressure, Is.EqualTo(expectedPressure).Within(0.001f));
            Assert.That(simulation.GetVoxelSnapshot(chunk, 0).Pressure,
                Is.EqualTo(expectedPressure).Within(0.001f));
        });
    }

    [Test]
    public void VoxelMixture_ChannelTableGrowsPastInitialCapacity()
    {
        using var simulation = CreateSimulation();
        var chunk = simulation.CreateAndRegisterChunk(default);
        var mixture = simulation.GetVoxelGasMixture(chunk, 0);
        mixture.Temperature = 300f;

        int gasCount = AtmosChunkConstants.InitialGasChannelCapacity + 5;
        for (var gasId = 0; gasId < gasCount; gasId++)
            mixture.SetMoles(gasId, 1f);

        Assert.Multiple(() =>
        {
            Assert.That(mixture.ActiveGasCount, Is.EqualTo(gasCount));
            Assert.That(mixture.TotalMoles, Is.EqualTo(gasCount));
            Assert.That(mixture.GetSnapshot().Gases.Select(static gas => gas.GasId),
                Is.EqualTo(Enumerable.Range(0, gasCount)));
        });
    }

    [Test]
    public void StoredTemperature_AllowsRawValuesAndUsesConfiguredFallback()
    {
        var config = CreateSimulationConfig();
        config.DefaultTemperatureFallback = 123f;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var owned = simulation.CreateGasMixture(1f, 300f);
        var voxel = simulation.GetVoxelGasMixture(chunk, 0);

        owned.SetMoles(0, 1f);
        voxel.SetMoles(0, 1f);
        owned.Temperature = float.NaN;
        voxel.Temperature = float.NaN;

        float expectedPressure = AtmosPhysicalConstants.MolarGasConstant * config.DefaultTemperatureFallback;
        Assert.Multiple(() =>
        {
            Assert.That(owned.Temperature, Is.NaN);
            Assert.That(voxel.Temperature, Is.NaN);
            Assert.That(owned.Pressure, Is.EqualTo(expectedPressure).Within(0.001f));
            Assert.That(voxel.Pressure, Is.EqualTo(expectedPressure).Within(0.001f));
        });
    }

    [Test]
    public void Pressure_UnrepresentableLiveFallbackIsRejectedWithoutMutatingMixtures()
    {
        var config = CreateSimulationConfig();
        config.DefaultTemperatureFallback = 300f;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var owned = simulation.CreateGasMixture(1f, 0f);
        var voxel = simulation.GetVoxelGasMixture(chunk, 0);
        owned.SetMoles(0, 1f);
        voxel.SetMoles(0, 1f);
        float ownedPressure = owned.Pressure;
        float voxelPressure = voxel.Pressure;

        config.DefaultTemperatureFallback = float.MaxValue;

        Assert.Multiple(() =>
        {
            Assert.That(() => _ = owned.Pressure, Throws.TypeOf<InvalidOperationException>());
            Assert.That(() => _ = voxel.Pressure, Throws.TypeOf<InvalidOperationException>());
            Assert.That(owned.Temperature, Is.Zero);
            Assert.That(voxel.Temperature, Is.Zero);
            Assert.That(owned.TotalMoles, Is.EqualTo(1f));
            Assert.That(voxel.TotalMoles, Is.EqualTo(1f));
        });

        config.DefaultTemperatureFallback = 300f;

        Assert.Multiple(() =>
        {
            Assert.That(owned.Pressure, Is.EqualTo(ownedPressure));
            Assert.That(voxel.Pressure, Is.EqualTo(voxelPressure));
        });
    }

    [Test]
    public void TemperatureMutation_UnrepresentablePressureIsRejectedAtomically()
    {
        using var simulation = CreateSimulation();
        var chunk = simulation.CreateAndRegisterChunk(default);
        var owned = simulation.CreateGasMixture(1f, 300f);
        var voxel = simulation.GetVoxelGasMixture(chunk, 0);
        owned.SetMoles(0, 1f);
        voxel.SetMoles(0, 1f);
        simulation.SleepChunk(chunk);
        var beforeVoxel = simulation.GetVoxelSnapshot(chunk, 0);
        var beforeOwned = owned.GetSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(() => owned.Temperature = float.MaxValue,
                Throws.TypeOf<InvalidOperationException>());
            Assert.That(() => voxel.Temperature = float.MaxValue,
                Throws.TypeOf<InvalidOperationException>());
        });

        var afterVoxel = simulation.GetVoxelSnapshot(chunk, 0);
        var afterOwned = owned.GetSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(afterOwned.Volume, Is.EqualTo(beforeOwned.Volume));
            Assert.That(afterOwned.Temperature, Is.EqualTo(beforeOwned.Temperature));
            Assert.That(afterOwned.Pressure, Is.EqualTo(beforeOwned.Pressure));
            Assert.That(afterOwned.TotalMoles, Is.EqualTo(beforeOwned.TotalMoles));
            Assert.That(afterOwned.Gases, Is.EqualTo(beforeOwned.Gases));
            Assert.That(afterVoxel.ChunkVersion, Is.EqualTo(beforeVoxel.ChunkVersion));
            Assert.That(afterVoxel.Temperature, Is.EqualTo(beforeVoxel.Temperature));
            Assert.That(afterVoxel.Pressure, Is.EqualTo(beforeVoxel.Pressure));
            Assert.That(afterVoxel.Gases, Is.EqualTo(beforeVoxel.Gases));
            Assert.That(simulation.GetChunkSnapshot(chunk).IsAwake, Is.False);
        });
    }

    [Test]
    public void VoxelMixture_ScalarMutationPreservesRoomCapacityGuard()
    {
        using var simulation = new AtmosSimulation(CreateSimulationConfig(), 3, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default, maxActiveRooms: 1);
        simulation.SetChunkClassification(chunk, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(chunk, 0, new VoxelClassification(1));
        simulation.SetVoxelClassification(chunk, 2, new VoxelClassification(2));
        var first = simulation.GetVoxelGasMixture(chunk, 0);
        var second = simulation.GetVoxelGasMixture(chunk, 2);
        first.SetMoles(0, 1f);

        Assert.That(() => second.SetMoles(0, 1f), Throws.InvalidOperationException);
        Assert.That(second.TotalMoles, Is.Zero);
    }

    [Test]
    public void VoxelMixture_IsBoundToOriginalChunkGeneration()
    {
        using var simulation = CreateSimulation();
        var position = new Int3(2, 3, 4);
        var original = simulation.CreateAndRegisterChunk(position);
        var stale = simulation.GetVoxelGasMixture(original, 0);
        Assert.That(simulation.UnregisterChunk(original), Is.True);
        var replacement = simulation.CreateAndRegisterChunk(position);

        Assert.That(() => stale.GetSnapshot(), Throws.InvalidOperationException);
        Assert.That(simulation.GetVoxelGasMixture(replacement, 0).TotalMoles, Is.Zero);
    }

    [Test]
    public void TransferTo_NonGasVoxelIsRejectedAtomically()
    {
        using var simulation = CreateSimulation();
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetVoxelClassification(chunk, 0, VoxelClassification.RoomSolid);
        var source = simulation.CreateGasMixture(1f, 300f);
        source.SetMoles(0, 2f);
        var destination = simulation.GetVoxelGasMixture(chunk, 0);

        Assert.That(() => source.TransferTo(destination, 1f), Throws.InvalidOperationException);
        Assert.Multiple(() =>
        {
            Assert.That(source.TotalMoles, Is.EqualTo(2f));
            Assert.That(destination.TotalMoles, Is.Zero);
        });
    }

    [Test]
    public void TransferTo_ActiveRoomCapacityFailureIsAtomic()
    {
        using var simulation = new AtmosSimulation(CreateSimulationConfig(), 3, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default, maxActiveRooms: 1);
        simulation.SetChunkClassification(chunk, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(chunk, 0, new VoxelClassification(1));
        simulation.SetVoxelClassification(chunk, 2, new VoxelClassification(2));
        var source = simulation.GetVoxelGasMixture(chunk, 0);
        var destination = simulation.GetVoxelGasMixture(chunk, 2);
        source.AddGas(0, 2f, 300f);

        Assert.That(() => source.TransferTo(destination, 1f), Throws.InvalidOperationException);
        Assert.Multiple(() =>
        {
            Assert.That(source.TotalMoles, Is.EqualTo(2f));
            Assert.That(destination.TotalMoles, Is.Zero);
        });
    }

    [Test]
    public void ContainerAndVoxelTransfersAreBidirectionalAndConservative()
    {
        using var simulation = CreateSimulation();
        var chunk = simulation.CreateAndRegisterChunk(default);
        var voxel = simulation.GetVoxelGasMixture(chunk, 0, 0, 0);
        var canister = simulation.CreateGasMixture(1f, 400f);
        canister.SetMoles(0, 4f);

        Assert.That(canister.TransferTo(voxel, 3f), Is.EqualTo(3f));
        var sample = voxel.Remove(1f);

        Assert.Multiple(() =>
        {
            Assert.That(canister.TotalMoles, Is.EqualTo(1f));
            Assert.That(voxel.TotalMoles, Is.EqualTo(2f));
            Assert.That(sample.TotalMoles, Is.EqualTo(1f));
            Assert.That(canister.TotalMoles + voxel.TotalMoles + sample.TotalMoles, Is.EqualTo(4f));
            Assert.That(voxel.Temperature, Is.EqualTo(400f));
            Assert.That(sample.Temperature, Is.EqualTo(400f));
        });
    }

    [Test]
    public void TwoCapabilitiesForSameVoxelDoNotTransferToThemselves()
    {
        using var simulation = CreateSimulation();
        var chunk = simulation.CreateAndRegisterChunk(default);
        var first = simulation.GetVoxelGasMixture(chunk, 0);
        var second = simulation.GetVoxelGasMixture(chunk, 0);
        first.AddGas(0, 2f, 300f);

        Assert.That(first.TransferTo(second, 1f), Is.Zero);
        Assert.That(first.TotalMoles, Is.EqualTo(2f));
    }

    [Test]
    public void MixtureOperationsAreSerializedAcrossThreads()
    {
        using var simulation = CreateSimulation();
        var mixture = simulation.CreateGasMixture(1f, 300f);

        Parallel.For(0, 1000, _ => mixture.AdjustMoles(0, 1f));

        Assert.That(mixture.GetMoles(0), Is.EqualTo(1000f));
    }

    [Test]
    public void SimulationDisposalInvalidatesOwnedAndVoxelMixtures()
    {
        var simulation = CreateSimulation();
        var owned = simulation.CreateGasMixture(1f, 300f);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var voxel = simulation.GetVoxelGasMixture(chunk, 0);

        simulation.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(() => _ = owned.TotalMoles, Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => _ = voxel.TotalMoles, Throws.TypeOf<ObjectDisposedException>());
            Assert.That(owned.Owner, Is.SameAs(simulation));
            Assert.That(voxel.Owner, Is.SameAs(simulation));
        });
    }

    [Test]
    public void TransferTo_RejectsExternalInterfaceImplementations()
    {
        using var simulation = CreateSimulation();
        var source = simulation.CreateGasMixture(1f, 300f);
        source.SetMoles(0, 1f);

        Assert.That(() => source.TransferTo(new ExternalMixture(simulation), 1f), Throws.ArgumentException);
        Assert.That(source.TotalMoles, Is.EqualTo(1f));
    }

    private static AtmosSimulation CreateSimulation(float voxelVolume = 1f)
    {
        return new AtmosSimulation(CreateSimulationConfig(voxelVolume), 1, 1, 1);
    }

    private static AtmosConfig CreateSimulationConfig(float voxelVolume = 1f)
    {
        return new AtmosConfig
        {
            VoxelVolume = voxelVolume,
            GasRegistry =
            [
                new GasProperties { Name = "Light", MolarHeatCapacityAtConstantVolume = 10f },
                new GasProperties { Name = "Heavy", MolarHeatCapacityAtConstantVolume = 20f }
            ]
        };
    }

    private sealed class ExternalMixture(AtmosSimulation owner) : IGasMixture
    {
        public AtmosSimulation Owner { get; } = owner;
        public float Volume => 1f;
        public float Temperature { get; set; }
        public float Pressure => 0f;
        public float TotalMoles => 0f;
        public int ActiveGasCount => 0;
        public float GetMoles(int gasId) => 0f;
        public void SetMoles(int gasId, float moles) => throw new NotSupportedException();
        public void AdjustMoles(int gasId, float deltaMoles) => throw new NotSupportedException();
        public void AddGas(int gasId, float moles, float temperature) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public GasMixture Remove(float moles) => throw new NotSupportedException();
        public GasMixture RemoveRatio(float ratio) => throw new NotSupportedException();
        public GasMixture RemoveVolume(float volume) => throw new NotSupportedException();
        public float TransferTo(IGasMixture destination, float moles) => throw new NotSupportedException();
        public float TransferRatioTo(IGasMixture destination, float ratio) => throw new NotSupportedException();
        public GasMixture Clone() => throw new NotSupportedException();
        public GasMixtureSnapshot GetSnapshot() => throw new NotSupportedException();
    }
}
