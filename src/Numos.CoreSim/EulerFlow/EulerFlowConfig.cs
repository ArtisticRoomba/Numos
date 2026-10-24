using Numos.CoreSim.State;
using Numos.Maths;
using Magnitudo;

namespace Numos.CoreSim.EulerFlow;

/// <summary>
///     Settings for the compressible Euler transport solver, the <c>euler-flow</c> stage.
/// </summary>
/// <remarks>
///     <para>
///         Register this object in <see cref="AtmosConfig.SolverConfigurations" />. When no entry is registered the
///         stage runs with <see cref="Default" />. Every property is normalized when the configuration is captured,
///         so the snapshot the solver reads never holds an out-of-range value. The property documentation says what
///         each one normalizes to.
///     </para>
///     <para>
///         The <see cref="NumosSerializeAttribute" /> properties are the configuration's state. Numos generates its
///         equality, state hash, and wire format from them, so a <c>.numos</c> replay file carries it.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     config.SolverConfigurations.Add(new EulerFlowConfig { CourantNumber = 0.4f, MaxVelocity = 2000f });
///     simulation.SetAtmosConfig(config);
///     </code>
/// </example>
public sealed partial class EulerFlowConfig : IAtmosSolverConfiguration
{
    /// <summary>
    ///     Identifies the configuration consumed by the Euler transport solver.
    /// </summary>
    public const string ConfigurationKey = "euler-flow";

    /// <summary>
    ///     Creates a configuration with every setting at its default.
    /// </summary>
    public EulerFlowConfig()
    {
    }

    /// <summary>
    ///     Copies another configuration, so an object initializer can override a few settings of an applied one.
    /// </summary>
    /// <param name="source">The configuration to copy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is <see langword="null" />.</exception>
    /// <example>
    ///     <code>
    ///     var faster = new EulerFlowConfig(current) { CourantNumber = 0.8f };
    ///     </code>
    /// </example>
    public EulerFlowConfig(EulerFlowConfig source)
    {
        ArgumentNullException.ThrowIfNull(source);
        CourantNumber = source.CourantNumber;
        MaxSubstepsPerTick = source.MaxSubstepsPerTick;
        VelocityFloor = source.VelocityFloor;
        MaxVelocity = source.MaxVelocity;
        WakePressureDelta = source.WakePressureDelta;
        AllMachCorrection = source.AllMachCorrection;
        Reconstruction = source.Reconstruction;
        MomentumDamping = source.MomentumDamping;
        DefaultLinkAreaFraction = source.DefaultLinkAreaFraction;
    }

    /// <summary>
    ///     Gets the configuration the solver runs with when none is registered.
    /// </summary>
    public static EulerFlowConfig Default { get; } = new();

    /// <summary>
    ///     Largest fraction of a voxel a signal may cross per substep, so the substep is
    ///     <c>CourantNumber · Δx / max(|u| + c)</c> over the active faces.
    /// </summary>
    /// <remarks>
    ///     The default of 0.5 is the positivity bound for the dimensionally split first-order scheme: below it a
    ///     substep cannot drain more gas from a voxel than it holds. Values up to 0.8 are linearly stable and take
    ///     fewer substeps, but a face next to vacuum can then produce a negative density that the solver has to
    ///     repair. Non-finite and nonpositive values normalize to 0.5. Values above 1 clamp to 1.
    /// </remarks>
    [NumosSerialize]
    public Scalar CourantNumber { get; init; } = 0.5f;

    /// <summary>
    ///     Cap on the number of CFL substeps the stage runs in one tick, or 0 for no cap.
    /// </summary>
    /// <remarks>
    ///     The substep count follows the fastest signal in the world, so one shock front into vacuum can demand
    ///     hundreds of substeps. When the cap engages the stage stops early and the tick advances less than
    ///     <see cref="AtmosSolverConstants.FixedTimeStep" /> of gas time: the flow slows down instead of the frame
    ///     stalling. Negative values normalize to 0.
    /// </remarks>
    [NumosSerialize]
    public int MaxSubstepsPerTick { get; init; } = 64;

    /// <summary>
    ///     Speed below which a voxel's momentum is zeroed at the end of the tick, in metres per second (m/s).
    /// </summary>
    /// <remarks>
    ///     Without a floor a settled room keeps sloshing at microscopic speeds forever, which keeps its chunks awake
    ///     and keeps the CFL loop substepping over noise. Non-finite and negative values normalize to 0, which
    ///     disables the floor.
    /// </remarks>
    [NumosSerialize]
    [Quantity("velocity")]
    public MetrePerSecond VelocityFloor { get; init; } = 0.005f;

    /// <summary>
    ///     Speed a voxel's velocity is clamped to, in metres per second (m/s).
    /// </summary>
    /// <remarks>
    ///     A safety net rather than physics: expansion into hard vacuum is bounded by <c>2c/(γ − 1)</c>, about
    ///     1700 m/s for room-temperature air, so the default of 5000 m/s only engages when a state has already gone
    ///     wrong. The solver counts clamped voxels in a diagnostic counter so a misbehaving scene is visible.
    ///     Non-finite and nonpositive values normalize to 5000 m/s.
    /// </remarks>
    [NumosSerialize]
    [Quantity("velocity")]
    public MetrePerSecond MaxVelocity { get; init; } = 5000f;

    /// <summary>
    ///     Pressure difference across a chunk boundary face that wakes the sleeping side, in pascals (Pa).
    /// </summary>
    /// <remarks>
    ///     A face between an awake chunk and a sleeping one is only activated when the pressures differ by more than
    ///     this, so a few pascals of ripple at a sealed door do not wake the whole station. A settled room keeps a few
    ///     pascals of residual non-uniformity. A lower value lets awake chunks keep re-waking neighbours that just
    ///     fell asleep. Non-finite and negative values normalize to 0, which wakes on any difference.
    /// </remarks>
    [NumosSerialize]
    [Quantity("pressure")]
    public Pascal WakePressureDelta { get; init; } = 10f;

    /// <summary>
    ///     Whether the face flux scales its pressure dissipation by the local Mach number.
    /// </summary>
    /// <remarks>
    ///     Godunov fluxes dissipate at the sound speed, which smears flows that move at a small fraction of it. The
    ///     correction keeps slow flows sharp at a small extra cost per face. Off by default.
    /// </remarks>
    [NumosSerialize]
    public bool AllMachCorrection { get; init; }

    /// <summary>
    ///     Reconstruction used to build the face states.
    /// </summary>
    /// <remarks>
    ///     Second-order reconstruction is planned. Undefined values normalize to
    ///     <see cref="EulerReconstruction.FirstOrder" />.
    /// </remarks>
    [NumosSerialize]
    public EulerReconstruction Reconstruction { get; init; } = EulerReconstruction.FirstOrder;

    /// <summary>
    ///     Exponential decay rate applied to every voxel's momentum each tick, in inverse seconds (1/s).
    /// </summary>
    /// <remarks>
    ///     A crude stand-in for wall friction and drag from whatever furniture the host has in the room, so a
    ///     hallway does not ring at its acoustic modes indefinitely. Non-finite and negative values normalize to 0,
    ///     which applies no damping.
    /// </remarks>
    [NumosSerialize]
    [Quantity("frequency")]
    public PerSecond MomentumDamping { get; init; }

    /// <summary>
    ///     Fraction of a full voxel face assumed open for a portal or dock link that declares no area.
    /// </summary>
    /// <remarks>
    ///     Applies to explicit-link gas flux under both the <c>euler-flow</c> and legacy <c>advection</c> models, so
    ///     changing it also rescales unsized links on a grid that still runs the legacy model. Values are clamped to
    ///     [0, 1]. Non-finite values normalize to 0, which closes such links to gas transport.
    /// </remarks>
    [NumosSerialize]
    public Scalar DefaultLinkAreaFraction { get; init; } = 1f;

    /// <inheritdoc />
    public string Key => ConfigurationKey;

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="gasRegistry" /> is <see langword="null" />.</exception>
    public IAtmosSolverConfiguration CreateSnapshot(IGasRegistry gasRegistry)
    {
        ArgumentNullException.ThrowIfNull(gasRegistry);
        var normalized = new EulerFlowConfig
        {
            CourantNumber = FloatMath.IsFinitePositive(CourantNumber) ? MathF.Min(CourantNumber, 1f) : 0.5f,
            MaxSubstepsPerTick = Math.Max(0, MaxSubstepsPerTick),
            VelocityFloor = FloatMath.GetNonnegativeFinite(VelocityFloor),
            MaxVelocity = FloatMath.IsFinitePositive(MaxVelocity) ? MaxVelocity : 5000f,
            WakePressureDelta = FloatMath.GetNonnegativeFinite(WakePressureDelta),
            AllMachCorrection = AllMachCorrection,
            Reconstruction = Enum.IsDefined(Reconstruction) ? Reconstruction : EulerReconstruction.FirstOrder,
            MomentumDamping = FloatMath.GetNonnegativeFinite(MomentumDamping),
            DefaultLinkAreaFraction = FloatMath.ClampUnitInterval(DefaultLinkAreaFraction)
        };

        return SemanticallyEquals(normalized) ? this : normalized;
    }

    internal static EulerFlowConfig Get(IAtmosConfig config)
    {
        return SolverConfigurationLookup.TryGet(config.SolverConfigurations, ConfigurationKey, out EulerFlowConfig? euler)
            ? euler
            : Default;
    }
}
