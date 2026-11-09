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
        MaxSubstepsPerSecond = source.MaxSubstepsPerSecond;
        SettlePeriod = source.SettlePeriod;
        VelocityFloor = source.VelocityFloor;
        SleepSpeed = source.SleepSpeed;
        MaxVelocity = source.MaxVelocity;
        WakePressureDelta = source.WakePressureDelta;
        AllMachCorrection = source.AllMachCorrection;
        Reconstruction = source.Reconstruction;
        MomentumDamping = source.MomentumDamping;
        DefaultLinkAreaFraction = source.DefaultLinkAreaFraction;
        MaxSoundSpeedReduction = source.MaxSoundSpeedReduction;
        FullSpeedPressureJump = source.FullSpeedPressureJump;
        FullSpeedVelocity = source.FullSpeedVelocity;
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
    [Quantity("dimensionless")]
    public Scalar CourantNumber { get; init; } = 0.5f;

    /// <summary>
    ///     Cap on the CFL substeps the stage runs per second of gas time, or 0 for no cap.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Each call of the stage may run at most <c>ceil(MaxSubstepsPerSecond · Interval / TickRate)</c> substeps,
    ///         so the default of 1280 gives 64 substeps for a 50 ms call, 22 for the default 16-tick call at 960 Hz,
    ///         and 2 for a 1 ms call.
    ///     </para>
    ///     <para>
    ///         The substep count follows the fastest signal in the world, so one shock front into vacuum can demand
    ///         hundreds of substeps. When the cap engages the stage stops early and the call advances less than
    ///         the stage's full time step of gas time: the flow slows down instead of the frame
    ///         stalling. Negative values normalize to 0.
    ///     </para>
    /// </remarks>
    [NumosSerialize]
    public int MaxSubstepsPerSecond { get; init; } = 1280;

    /// <summary>
    ///     Interval between the stage's settle passes, in seconds (s).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The velocity floor, species diffusion, vacuum cleanup, momentum settling, and the sleep decision run once
    ///         per settle pass, not on every call. The stage converts this to ticks as
    ///         <c>max(1, round(SettlePeriod · TickRate))</c>. At a tick rate of 20 Hz or less the default of 0.05 s
    ///         settles on every call. At 960 Hz it settles every 48 ticks, which is every third call at the default
    ///         <c>euler-flow</c> interval of 16 ticks.
    ///     </para>
    ///     <para>
    ///         The floor needs this period because a pressure gradient of a pascal or two accelerates a voxel by
    ///         far less than the floor in 1 ms, so a per-call floor would zero the momentum before it could build up.
    ///         NaN and negative values normalize to 0, which settles on every call.
    ///     </para>
    /// </remarks>
    [NumosSerialize]
    [Quantity("seconds")]
    public Second SettlePeriod { get; init; } = 0.05f;

    /// <summary>
    ///     Speed below which a voxel's momentum is zeroed at the end of the tick, in metres per second (m/s).
    /// </summary>
    /// <remarks>
    ///     Without a floor a settled room keeps sloshing at microscopic speeds forever, which keeps its chunks awake
    ///     and keeps the CFL loop substepping over noise. Non-finite and negative values normalize to 0, which
    ///     disables the floor.
    ///     The floor compares the solver's working velocity. A chunk written at sound-speed reduction α has a real
    ///     velocity threshold of the floor divided by α, so a slowed flow stops at the same scaled speed as a full-speed
    ///     one. See <see cref="MaxSoundSpeedReduction" />.
    /// </remarks>
    [NumosSerialize]
    [Quantity("velocity")]
    public MetrePerSecond VelocityFloor { get; init; } = 0.005f;

    /// <summary>
    ///     Speed below which motion no longer keeps a chunk awake, in metres per second (m/s).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A chunk whose fastest voxel moves at this speed or faster cannot fall asleep. A slower chunk sleeps on
    ///         its pressure differences alone, like a still one. When it falls asleep the stage zeroes its momentum
    ///         and turns the kinetic energy into heat, so a sleeping chunk still carries no momentum. Residual sound
    ///         in a large open room rings well above <see cref="VelocityFloor" /> for a long time, and without this
    ///         setting such a room never sleeps.
    ///     </para>
    ///     <para>
    ///         Motion wakes a sleeping neighbour only above the larger of this and <see cref="VelocityFloor" />, so
    ///         a ringing neighbour does not wake a chunk that just fell asleep. The default is 0.5 m/s. A value of 0
    ///         keeps the old rule: any momentum keeps a chunk awake, and motion above <see cref="VelocityFloor" />
    ///         wakes a neighbour. Non-finite and negative values normalize to 0.
    ///     </para>
    /// </remarks>
    [NumosSerialize]
    [Quantity("velocity")]
    public MetrePerSecond SleepSpeed { get; init; } = 0.5f;

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
    ///     this. The default of 50 Pa sits above the acoustic pressure of slow residual ringing (about 0.12 m/s or
    ///     less in room air), so that ringing does not keep re-waking neighbours that just fell asleep. The cost is
    ///     that a sleeping chunk keeps any difference below 50 Pa frozen in until something else wakes it.
    ///     Non-finite and negative values normalize to 0, which wakes on any difference.
    /// </remarks>
    [NumosSerialize]
    [Quantity("pressure")]
    public Pascal WakePressureDelta { get; init; } = 50f;

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
    ///     Undefined values normalize to <see cref="EulerReconstruction.FirstOrder" />.
    /// </remarks>
    [NumosSerialize]
    public EulerReconstruction Reconstruction { get; init; } = EulerReconstruction.FirstOrder;

    /// <summary>
    ///     Exponential decay rate applied to every voxel's momentum on each Euler call, in inverse seconds (1/s).
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
    [Quantity("dimensionless")]
    public Scalar DefaultLinkAreaFraction { get; init; } = 1f;

    /// <summary>
    ///     Largest factor by which the stage may slow the sound speed of a calm region, or 1 for no reduction.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A factor α scales velocity by α and time by 1/α, so the CFL loop takes about α times fewer substeps.
    ///         Mass and every species stay exact. A pressure-driven transient plays in slow motion, and a
    ///         source-driven flow keeps its real velocity. The solver picks α per connected region of active chunks
    ///         from <see cref="FullSpeedPressureJump" /> and <see cref="FullSpeedVelocity" />.
    ///     </para>
    ///     <para>
    ///         <see cref="MaxVelocity" /> limits the scaled velocity α·|u|, not the real one. <see cref="VelocityFloor" />
    ///         also compares the scaled velocity, so a slowed chunk stops flow below the floor divided by α in real
    ///         velocity. The default of 1 runs the
    ///         unscaled solver. Values below 1 and non-finite values normalize to 1. Values above 16 clamp to 16.
    ///     </para>
    /// </remarks>
    [NumosSerialize]
    [Quantity("dimensionless")]
    public Scalar MaxSoundSpeedReduction { get; init; } = 1f;

    /// <summary>
    ///     Pressure difference across an open face above which a region runs at full sound speed, in pascals (Pa).
    /// </summary>
    /// <remarks>
    ///     A region that already runs slowed needs a jump above this value to return to full speed. A region at full
    ///     speed needs a jump below half of it to slow down. The gap stops a region from flipping between the two.
    ///     Only used when <see cref="MaxSoundSpeedReduction" /> is above 1. Non-finite and negative values normalize
    ///     to 500 Pa.
    /// </remarks>
    [NumosSerialize]
    [Quantity("pressure")]
    public Pascal FullSpeedPressureJump { get; init; } = 500f;

    /// <summary>
    ///     Gas speed above which a region runs at full sound speed, in metres per second (m/s).
    /// </summary>
    /// <remarks>
    ///     The same hysteresis applies as for <see cref="FullSpeedPressureJump" />: the return threshold is this value
    ///     and the slow-down threshold is half of it. Only used when <see cref="MaxSoundSpeedReduction" /> is above 1.
    ///     Non-finite and negative values normalize to 2 m/s.
    /// </remarks>
    [NumosSerialize]
    [Quantity("velocity")]
    public MetrePerSecond FullSpeedVelocity { get; init; } = 2f;

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
            MaxSubstepsPerSecond = Math.Max(0, MaxSubstepsPerSecond),
            SettlePeriod = float.IsNaN(SettlePeriod) ? 0f : MathF.Max(0f, SettlePeriod),
            VelocityFloor = FloatMath.GetNonnegativeFinite(VelocityFloor),
            SleepSpeed = FloatMath.GetNonnegativeFinite(SleepSpeed),
            MaxVelocity = FloatMath.IsFinitePositive(MaxVelocity) ? MaxVelocity : 5000f,
            WakePressureDelta = FloatMath.GetNonnegativeFinite(WakePressureDelta),
            AllMachCorrection = AllMachCorrection,
            Reconstruction = Enum.IsDefined(Reconstruction) ? Reconstruction : EulerReconstruction.FirstOrder,
            MomentumDamping = FloatMath.GetNonnegativeFinite(MomentumDamping),
            DefaultLinkAreaFraction = FloatMath.ClampUnitInterval(DefaultLinkAreaFraction),
            MaxSoundSpeedReduction = float.IsFinite(MaxSoundSpeedReduction) ? Math.Clamp(MaxSoundSpeedReduction, 1f, 16f) : 1f,
            FullSpeedPressureJump = float.IsFinite(FullSpeedPressureJump) && FullSpeedPressureJump >= 0f ? FullSpeedPressureJump : 500f,
            FullSpeedVelocity = float.IsFinite(FullSpeedVelocity) && FullSpeedVelocity >= 0f ? FullSpeedVelocity : 2f
        };

        return SemanticallyEquals(normalized) ? this : normalized;
    }

    /// <summary>
    ///     Normal speed across a chunk face above which motion wakes the sleeping side: the larger of
    ///     <see cref="VelocityFloor" /> and <see cref="SleepSpeed" />.
    /// </summary>
    internal MetrePerSecond WakeSpeed => MathF.Max(VelocityFloor, SleepSpeed);

    internal static EulerFlowConfig Get(IAtmosConfig config)
    {
        return SolverConfigurationLookup.TryGet(config.SolverConfigurations, ConfigurationKey, out EulerFlowConfig? euler)
            ? euler
            : Default;
    }
}
