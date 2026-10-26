namespace Numos.CoreSim;

internal interface IAtmosConfig
{
    IReadOnlyList<IAtmosSolverConfiguration> SolverConfigurations { get; }
    Kelvin GlobalTemperature { get; }
    Kelvin DefaultTemperatureFallback { get; }
    JoulePerMoleKelvin DefaultMolarHeatCapacityAtConstantVolume { get; }
    CubicMetre VoxelVolume { get; }
    Pascal SaturationReferencePressure { get; }
    SquareMetrePerSecond DefaultDiffusionCoefficient { get; }
    Kelvin SpaceTemperature { get; }
    Scalar BulkFlowCoefficient { get; }
    Pascal VacuumThreshold { get; }
    int SleepThreshold { get; }
    Scalar SleepEpsilon { get; }
    JoulePerKelvin ThermalConductance { get; }
    Scalar CondensationRateFactor { get; }
    Scalar MaxPressureTransferFractionPerNeighbor { get; }
    Pascal AccumulatorWakeThreshold { get; }
    int AccumulatorMaxAliveTicks { get; }
    KilogramPerMole ReferenceMolarMass { get; }
    PascalPerMoleKelvin PressurePerMoleKelvin { get; }

    int GasPropertyCount { get; }
    Kelvin GetValidatedTemp(Kelvin storedTemperature);
    CubicMetre GetVoxelVolume();
    JoulePerMoleKelvin GetMolarHeatCapacityAtConstantVolume(int gasId);
    SquareMetrePerSecond GetDiffusionCoefficient(int gasId);
    KilogramPerMole GetMolarMass(int gasId);
    bool TryGetGasProperties(int gasId, out GasProperties properties);
}