namespace Numos.CoreSim;

internal interface IAtmosConfig
{
    IReadOnlyList<IAtmosSolverConfiguration> SolverConfigurations { get; }
    Kelvin GlobalTemperature { get; }
    Kelvin DefaultTemperatureFallback { get; }
    JoulePerMoleKelvin DefaultMolarHeatCapacityAtConstantVolume { get; }
    CubicMetre VoxelVolume { get; }
    Pascal SaturationReferencePressure { get; }
    Scalar DefaultDiffusionCoefficient { get; }
    Kelvin SpaceTemperature { get; }
    Scalar BulkFlowCoefficient { get; }
    Pascal VacuumThreshold { get; }
    int SleepThreshold { get; }
    Pascal SleepEpsilon { get; }
    JoulePerKelvin ThermalConductance { get; }
    Scalar CondensationRateFactor { get; }
    Scalar MaxPressureTransferFractionPerNeighbor { get; }
    Pascal AccumulatorWakeThreshold { get; }
    int AccumulatorMaxAliveTicks { get; }
    PascalPerMoleKelvin PressurePerMoleKelvin { get; }

    int GasPropertyCount { get; }
    Kelvin GetValidatedTemp(Kelvin storedTemperature);
    CubicMetre GetVoxelVolume();
    JoulePerMoleKelvin GetMolarHeatCapacityAtConstantVolume(int gasId);
    Scalar GetDiffusionCoefficient(int gasId);
    bool TryGetGasProperties(int gasId, out GasProperties properties);
}