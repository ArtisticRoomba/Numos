using Numos.CoreSim.Replay;
using Magnitudo;

namespace Numos.CoreSim;

/// <summary>
///     Defines the physical properties of a gas.
/// </summary>
public record struct GasProperties
{
    /// <summary>
    ///     Normal boiling temperature, in kelvins (K), at
    ///     <see cref="AtmosConfig.SaturationReferencePressure" />.
    /// </summary>
    [Quantity("temperature")]
    [GasCheckpointField(2)]
    public Kelvin BoilingPoint;

    /// <summary>
    ///     Whether this species participates in the condensation model.
    /// </summary>
    [GasCheckpointField(3)]
    public bool CondensationEnabled;

    /// <summary>
    ///     Reference diffusivity for this species at <see cref="AtmosConfig.GlobalTemperature" /> and
    ///     <see cref="AtmosConfig.SaturationReferencePressure" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An effective diffusivity in square metres per second (m²/s). Negative values clamp to zero, and
    ///         non-finite values disable diffusion for this species. The diffusion solvers send each neighbor
    ///         <c>D * (T / GlobalTemperature)^1.5 * (SaturationReferencePressure / P) * n * dt / dx^2</c> moles per
    ///         tick, Fick's law on a grid of edge <c>dx</c>, then cap each neighbor's share at a seventh of the source.
    ///     </para>
    ///     <para>
    ///         The default of 0.02 m²/s is about a thousand times molecular diffusivity in air. It stands in for the
    ///         convective mixing the grid does not resolve.
    ///     </para>
    /// </remarks>
    [Quantity("diffusivity")]
    [GasCheckpointField(6)]
    public SquareMetrePerSecond DiffusionCoefficient;

    /// <summary>
    ///     Reserved ID for a liquid produced by condensation.
    /// </summary>
    /// <remarks>
    ///     Numos currently removes condensed vapor without producing liquid state or an event, so this field is
    ///     not consumed by the built-in solver. A custom liquid integration may interpret it.
    /// </remarks>
    /// TODO FAR FUTURE fluid sim :godo:
    [GasCheckpointField(5)]
    public int LiquidId;

    /// <summary>
    ///     Molar enthalpy of vaporization, in joules per mole (J/mol).
    /// </summary>
    /// <remarks>
    ///     The phase-equilibrium model uses this value in Clausius–Clapeyron. The constant-volume energy
    ///     balance converts it to an approximate internal-energy change, <c>ΔU_vap = ΔH_vap - RT</c>.
    /// </remarks>
    [Quantity("molarEnergy")]
    [GasCheckpointField(4)]
    public JoulePerMole MolarEnthalpyOfVaporization;

    /// <summary>
    ///     Molar heat capacity at constant volume, in joules per mole-kelvin (J/(mol·K)).
    /// </summary>
    /// <remarks>
    ///     This value determines the sensible energy carried by gas during injection and flow, the voxel's
    ///     total heat capacity, and the energy removed during condensation. Non-finite values and values less
    ///     than or equal to zero use <see cref="AtmosConfig.DefaultMolarHeatCapacityAtConstantVolume" />.
    /// </remarks>
    [Quantity("molarHeatCapacity")]
    [GasCheckpointField(1)]
    public JoulePerMoleKelvin MolarHeatCapacityAtConstantVolume;

    /// <summary>
    ///     Molar mass, in kilograms per mole (kg/mol).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The compressible transport solver converts moles to mass with it, which is where a voxel's density,
    ///         momentum, and sound speed come from.
    ///     </para>
    ///     <para>
    ///         Zero, negative, and non-finite values mean "unknown": the gas is treated as weighing
    ///         <see cref="AtmosConfig.ReferenceMolarMass" />, which is also how every gas behaves when no molar
    ///         masses are set. Invalid values are normalized to zero when the configuration is applied.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     Oxygen is <c>0.031998f</c> and hydrogen is <c>0.002016f</c>.
    /// </example>
    [Quantity("molarMass")]
    [GasCheckpointField(7)]
    public KilogramPerMole MolarMass;

    /// <summary>
    ///     Unique name that identifies the gas in a <see cref="GasRegistry" /> and in reaction definitions.
    /// </summary>
    [GasCheckpointField(0)]
    public string Name;
}