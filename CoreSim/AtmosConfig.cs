namespace Numos;

public class AtmosConfig
{
    public List<GasProperties> GasRegistry { get; set; } = new();

    public float GlobalTemperature { get; set; } = 293.15f;
    public float DefaultTemperatureFallback { get; set; } = 293.15f;
    public float SpaceTemperature { get; set; } = 2.7f;

    public float FlowFriction { get; set; } = 0.25f;
    public float DampingFactor { get; set; } = 0.5f;
    public float SnapThreshold { get; set; } = 5.0f;
    public float MinFlowCutoff { get; set; } = 0.1f;
    public float VacuumThreshold { get; set; } = 1.0f;

    public int SleepThreshold { get; set; } = 100;
    public float SleepEpsilon { get; set; } = 3.5f;

    public float ThermalConductivity { get; set; } = 0.05f;
    public float CondensationRateFactor { get; set; } = 0.5f;
    public float CflFlowCap { get; set; } = 0.16f;
    public float AccumulatorWakeThreshold { get; set; } = 15.0f;
    public int AccumulatorMaxAliveTicks { get; set; } = 20;
}