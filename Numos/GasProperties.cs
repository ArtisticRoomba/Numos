namespace Opal.Prototypes.AtmosAndThermalGeneric;

/// <summary>
/// Defines the physical properties of a gas for thermodynamic and concentration diffusion calculations.
/// </summary>
public struct GasProperties
{
    public string Name;
    public float SpecificHeatCapacity;
    public float BoilingPoint;
    public float CondensationPoint;
    public float LatentHeatOfVaporization;
    public int LiquidId;
    public float DiffusionCoefficient; // Passive Fickian diffusion rate
}
