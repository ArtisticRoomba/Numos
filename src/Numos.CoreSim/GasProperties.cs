using Numos.CoreSim.Replay;
using Numos.Units;

namespace Numos.CoreSim;

/// <summary>
///     Defines the physical properties of a gas.
/// </summary>
public struct GasProperties
{
    /// <summary>
    ///     Unique name that identifies the gas in a <see cref="GasRegistry" /> and in reaction definitions.
    /// </summary>
    [GasCheckpointField(0)]
    public string Name;

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
    ///     Reference diffusivity for this species at <see cref="AtmosConfig.GlobalTemperature" /> and
    ///     <see cref="AtmosConfig.SaturationReferencePressure" />.
    /// </summary>
    /// <remarks>
    ///     Values are clamped to [0, 1]; non-finite values disable diffusion for this species. The diffusion solvers
    ///     scale this by <c>(T / GlobalTemperature)^1.5 * (SaturationReferencePressure / P) * dx * dt</c> and the
    ///     source moles, then cap each neighbor's share at a seventh of the source.
    /// </remarks>
    [GasCheckpointField(6)]
    public Scalar DiffusionCoefficient;
}