using Numos.CoreSim.Replay;
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
///         so the snapshot the solver reads never holds an out-of-range value; the property documentation says what
///         each one normalizes to.
///     </para>
///     <para>
///         This configuration is portable: a <c>.numos</c> replay file carries it, unlike
///         <see cref="GasReactions.GasReactionConfig" />, whose reactions reference host gas objects.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     config.SolverConfigurations.Add(new EulerFlowConfig { CourantNumber = 0.4f, MaxVelocity = 2000f });
///     simulation.SetAtmosConfig(config);
///     </code>
/// </example>
public sealed class EulerFlowConfig : IAtmosSolverConfiguration, IPortableSolverConfiguration
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
    ///     repair. Non-finite and nonpositive values normalize to 0.5; values above 1 clamp to 1.
    /// </remarks>
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
    public int MaxSubstepsPerTick { get; init; } = 64;

    /// <summary>
    ///     Speed below which a voxel's momentum is zeroed at the end of the tick, in metres per second (m/s).
    /// </summary>
    /// <remarks>
    ///     Without a floor a settled room keeps sloshing at microscopic speeds forever, which keeps its chunks awake
    ///     and keeps the CFL loop substepping over noise. Non-finite and negative values normalize to 0, which
    ///     disables the floor.
    /// </remarks>
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
    [Quantity("velocity")]
    public MetrePerSecond MaxVelocity { get; init; } = 5000f;

    /// <summary>
    ///     Pressure difference across a chunk boundary face that wakes the sleeping side, in pascals (Pa).
    /// </summary>
    /// <remarks>
    ///     A face between an awake chunk and a sleeping one is only activated when the pressures differ by more than
    ///     this, so a 1 Pa ripple at a sealed door does not wake the whole station. Non-finite and negative values
    ///     normalize to 0, which wakes on any difference.
    /// </remarks>
    [Quantity("pressure")]
    public Pascal WakePressureDelta { get; init; } = 1f;

    /// <summary>
    ///     Whether the face flux scales its pressure dissipation by the local Mach number.
    /// </summary>
    /// <remarks>
    ///     Godunov fluxes dissipate at the sound speed, which smears flows that move at a small fraction of it. The
    ///     correction keeps slow flows sharp at a small extra cost per face. Off by default.
    /// </remarks>
    public bool AllMachCorrection { get; init; }

    /// <summary>
    ///     Reconstruction used to build the face states.
    /// </summary>
    /// <remarks>
    ///     <see cref="EulerReconstruction.MusclHancock" /> is not implemented and runs as
    ///     <see cref="EulerReconstruction.FirstOrder" />. Undefined values normalize to
    ///     <see cref="EulerReconstruction.FirstOrder" />.
    /// </remarks>
    public EulerReconstruction Reconstruction { get; init; } = EulerReconstruction.FirstOrder;

    /// <summary>
    ///     Exponential decay rate applied to every voxel's momentum each tick, in inverse seconds (1/s).
    /// </summary>
    /// <remarks>
    ///     A crude stand-in for wall friction and drag from whatever furniture the host has in the room, so a
    ///     hallway does not ring at its acoustic modes indefinitely. Non-finite and negative values normalize to 0,
    ///     which applies no damping.
    /// </remarks>
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

    /// <inheritdoc />
    /// <remarks>Floats are compared bit-for-bit, so equality agrees with <see cref="ComputeStateHash" />.</remarks>
    public bool SemanticallyEquals(IAtmosSolverConfiguration other)
    {
        return other is EulerFlowConfig config &&
               SameBits(CourantNumber, config.CourantNumber) &&
               MaxSubstepsPerTick == config.MaxSubstepsPerTick &&
               SameBits(VelocityFloor, config.VelocityFloor) &&
               SameBits(MaxVelocity, config.MaxVelocity) &&
               SameBits(WakePressureDelta, config.WakePressureDelta) &&
               AllMachCorrection == config.AllMachCorrection &&
               Reconstruction == config.Reconstruction &&
               SameBits(MomentumDamping, config.MomentumDamping) &&
               SameBits(DefaultLinkAreaFraction, config.DefaultLinkAreaFraction);
    }

    /// <inheritdoc />
    public ulong ComputeStateHash()
    {
        var hash = new AtmosStateHasher();
        hash.Add(CourantNumber);
        hash.Add(MaxSubstepsPerTick);
        hash.Add(VelocityFloor);
        hash.Add(MaxVelocity);
        hash.Add(WakePressureDelta);
        hash.Add(AllMachCorrection);
        hash.Add((int)Reconstruction);
        hash.Add(MomentumDamping);
        hash.Add(DefaultLinkAreaFraction);
        return hash.Value;
    }

    // Same order as ReadWire. Pinned by the golden wire-format test, so a new setting goes at the end and needs a
    // world checkpoint format bump.
    void IPortableSolverConfiguration.WriteWire(BinaryWriter writer)
    {
        writer.Write(CourantNumber);
        writer.Write(MaxSubstepsPerTick);
        writer.Write(VelocityFloor);
        writer.Write(MaxVelocity);
        writer.Write(WakePressureDelta);
        writer.Write(AllMachCorrection);
        writer.Write((byte)Reconstruction);
        writer.Write(MomentumDamping);
        writer.Write(DefaultLinkAreaFraction);
    }

    /// <summary>
    ///     Reads a payload written by <see cref="IPortableSolverConfiguration.WriteWire" />.
    /// </summary>
    /// <exception cref="InvalidDataException">The payload names an undefined reconstruction.</exception>
    internal static EulerFlowConfig ReadWire(BinaryReader reader)
    {
        Scalar courantNumber = reader.ReadSingle();
        int maxSubstepsPerTick = reader.ReadInt32();
        MetrePerSecond velocityFloor = reader.ReadSingle();
        MetrePerSecond maxVelocity = reader.ReadSingle();
        Pascal wakePressureDelta = reader.ReadSingle();
        bool allMachCorrection = reader.ReadBoolean();
        var reconstruction = (EulerReconstruction)reader.ReadByte();
        if (!Enum.IsDefined(reconstruction))
            throw new InvalidDataException("The Euler flow configuration names an unknown reconstruction.");

        PerSecond momentumDamping = reader.ReadSingle();
        Scalar defaultLinkAreaFraction = reader.ReadSingle();
        return new EulerFlowConfig
        {
            CourantNumber = courantNumber,
            MaxSubstepsPerTick = maxSubstepsPerTick,
            VelocityFloor = velocityFloor,
            MaxVelocity = maxVelocity,
            WakePressureDelta = wakePressureDelta,
            AllMachCorrection = allMachCorrection,
            Reconstruction = reconstruction,
            MomentumDamping = momentumDamping,
            DefaultLinkAreaFraction = defaultLinkAreaFraction
        };
    }

    internal static EulerFlowConfig Get(IAtmosConfig config)
    {
        return SolverConfigurationLookup.TryGet(config.SolverConfigurations, ConfigurationKey, out EulerFlowConfig? euler)
            ? euler
            : Default;
    }

    private static bool SameBits(float left, float right)
    {
        return BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);
    }
}