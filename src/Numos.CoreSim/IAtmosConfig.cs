namespace Numos.CoreSim;

internal interface IAtmosConfig
{
    float GlobalTemperature { get; }
    float DefaultTemperatureFallback { get; }
    float DefaultMolarHeatCapacityAtConstantVolume { get; }
    float VoxelVolume { get; }
    float SaturationReferencePressure { get; }
    float DefaultDiffusionCoefficient { get; }
    float SpaceTemperature { get; }
    float BulkFlowCoefficient { get; }
    float VacuumThreshold { get; }
    int SleepThreshold { get; }
    float SleepEpsilon { get; }
    float ThermalConductance { get; }
    float CondensationRateFactor { get; }
    float MaxPressureTransferFractionPerNeighbor { get; }
    float AccumulatorWakeThreshold { get; }
    int AccumulatorMaxAliveTicks { get; }
    float PressurePerMoleKelvin { get; }
    internal float GetValidatedTemp(float storedTemperature);
    internal float GetVoxelVolume();
    internal float GetMolarHeatCapacityAtConstantVolume(int gasId);
    internal float GetDiffusionCoefficient(int gasId);
    internal bool TryGetGasProperties(int gasId, out GasProperties properties);
}