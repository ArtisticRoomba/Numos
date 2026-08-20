namespace Numos.CoreSim;

/// <summary>
///     Defines the physical properties of a gas.
/// </summary>
public struct GasProperties
{
    /// <summary>
    ///     Display name of the gas.
    /// </summary>
    public string Name;

    /// <summary>
    ///     Molar heat capacity at constant volume, in joules per mole-kelvin (J/(mol·K)).
    /// </summary>
    /// <remarks>
    ///     This value determines the sensible energy carried by gas during injection and flow, the voxel's
    ///     total heat capacity, and the energy removed during condensation. Non-finite values and values less
    ///     than or equal to zero use <see cref="AtmosConfig.DefaultMolarHeatCapacityAtConstantVolume" />.
    /// </remarks>
    public float MolarHeatCapacityAtConstantVolume;

    /// <summary>
    ///     Normal boiling temperature, in kelvins (K), at
    ///     <see cref="AtmosConfig.SaturationReferencePressure" />.
    /// </summary>
    public float BoilingPoint;

    /// <summary>
    ///     Whether this species participates in the condensation model.
    /// </summary>
    public bool CondensationEnabled;

    /// <summary>
    ///     Molar enthalpy of vaporization, in joules per mole (J/mol).
    /// </summary>
    /// <remarks>
    ///     The phase-equilibrium model uses this value in Clausius–Clapeyron. The constant-volume energy
    ///     balance converts it to an approximate internal-energy change, <c>ΔU_vap = ΔH_vap - RT</c>.
    /// </remarks>
    public float MolarEnthalpyOfVaporization;

    /// <summary>
    ///     Reserved ID for a liquid produced by condensation.
    /// </summary>
    /// <remarks>
    ///     Numos currently removes condensed vapor without producing liquid state or an event, so this field is
    ///     not consumed by the built-in solver. A custom liquid integration may interpret it.
    /// </remarks>
    /// TODO FAR FUTURE fluid sim :godo:
    public int LiquidId;

    /// <summary>
    ///     Dimensionless fraction of the per-species mole imbalance mixed per simulation tick.
    /// </summary>
    /// <remarks>
    ///     Values are normalized to [0, 1] and the explicit face update caps the effective fraction at 0.5;
    ///     non-finite values disable diffusion for this species.
    /// </remarks>
    public float DiffusionCoefficient;
}
