using System.Globalization;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.Workbench;

/// <summary>
///     Reads one scalar value for one voxel from a detached chunk snapshot.
/// </summary>
/// <param name="snapshot">A snapshot that holds at least the fields the sampler declared.</param>
/// <param name="localIndex">The voxel index inside the chunk, as `x + width * (y + height * z)`.</param>
/// <returns>The value, or NaN when the voxel has none.</returns>
public delegate float WorkbenchFieldSampler(in AtmosChunkSnapshot snapshot, int localIndex);

/// <summary>
///     Options that change the value of some fields without changing which fields exist.
/// </summary>
/// <param name="SchlierenReference">
///     The density gradient magnitude, in kg/m⁴, that a schlieren field maps to its darkest value. When
///     <see langword="null" />, a region read uses the largest gradient inside the region. Give a fixed value so the
///     frames of a time series share one scale.
/// </param>
public readonly record struct WorkbenchFieldOptions(float? SchlierenReference = null);

/// <summary>
///     The data a <see cref="WorkbenchRegionComputer" /> reads.
/// </summary>
public readonly ref struct WorkbenchRegionInput
{
    internal WorkbenchRegionInput(
        ReadOnlySpan<float> source,
        Int3 size,
        float voxelEdge,
        float? schlierenReference)
    {
        Source = source;
        Size = size;
        VoxelEdge = voxelEdge;
        SchlierenReference = schlierenReference;
    }

    /// <summary>
    ///     The source field over the region plus a one-voxel halo, with x changing fastest. Its size is
    ///     <see cref="Size" /> plus 2 on every axis. NaN marks a voxel without a value, such as a solid voxel or a voxel
    ///     outside every chunk.
    /// </summary>
    public ReadOnlySpan<float> Source { get; }

    /// <summary>
    ///     The size of the region in voxels, without the halo.
    /// </summary>
    public Int3 Size { get; }

    /// <summary>
    ///     The voxel edge length in metres.
    /// </summary>
    public float VoxelEdge { get; }

    /// <summary>
    ///     The reference gradient from <see cref="WorkbenchFieldOptions" />, or <see langword="null" /> when the
    ///     computer should normalize over the region.
    /// </summary>
    public float? SchlierenReference { get; }
}

/// <summary>
///     Computes a field over a whole region from a source field that has a one-voxel halo.
/// </summary>
/// <param name="input">The source values and the geometry.</param>
/// <param name="destination">Receives one value per region voxel with x changing fastest.</param>
public delegate void WorkbenchRegionComputer(in WorkbenchRegionInput input, Span<float> destination);

/// <summary>
///     A named scalar quantity that can be sampled, exported, and probed, such as `pressure`.
/// </summary>
/// <remarks>
///     <para>
///         A field reads only detached snapshots, so sampling never changes the simulation. A solver package can add
///         fields through <see cref="WorkbenchFieldRegistry.Register" />.
///     </para>
///     <para>
///         A voxel field reads one voxel of one snapshot. A region field needs the neighbors of a voxel, which can lie
///         in another chunk. The session reads its source field over the region plus a one-voxel halo and passes the
///         result to a <see cref="WorkbenchRegionComputer" />. The density gradient and the schlieren are region fields.
///     </para>
/// </remarks>
public sealed class WorkbenchField
{
    /// <summary>
    ///     Creates a field.
    /// </summary>
    /// <param name="name">The name scripts use. Names are not case-sensitive.</param>
    /// <param name="unit">The unit label, or an empty string for a dimensionless value.</param>
    /// <param name="required">The snapshot fields the sampler reads. Request no more than that.</param>
    /// <param name="sampler">Reads the value for one voxel.</param>
    /// <param name="valueInSolid">
    ///     When <see langword="false" />, the field is NaN in solid voxels and <paramref name="sampler" /> never sees them.
    /// </param>
    public WorkbenchField(
        string name,
        string unit,
        AtmosChunkSnapshotFields required,
        WorkbenchFieldSampler sampler,
        bool valueInSolid = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(sampler);
        Name = name;
        Unit = unit ?? string.Empty;
        Required = required | AtmosChunkSnapshotFields.VoxelClassification;
        Sampler = sampler;
        ValueInSolid = valueInSolid;
    }

    /// <summary>
    ///     Creates a region field.
    /// </summary>
    /// <param name="name">The name scripts use. Names are not case-sensitive.</param>
    /// <param name="unit">The unit label, or an empty string for a dimensionless value.</param>
    /// <param name="haloSource">The name of the voxel field the computer reads with a one-voxel halo.</param>
    /// <param name="computer">Computes the values of a whole region.</param>
    /// <param name="needsReferenceAtPoints">
    ///     Whether sampling at single voxels requires <see cref="WorkbenchFieldOptions.SchlierenReference" />. A field
    ///     that normalizes over its region has no region at a point.
    /// </param>
    public WorkbenchField(
        string name,
        string unit,
        string haloSource,
        WorkbenchRegionComputer computer,
        bool needsReferenceAtPoints = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(haloSource);
        ArgumentNullException.ThrowIfNull(computer);
        Name = name;
        Unit = unit ?? string.Empty;
        Required = AtmosChunkSnapshotFields.VoxelClassification;
        Sampler = static (in _, _) => float.NaN;
        HaloSource = haloSource;
        RegionComputer = computer;
        NeedsReferenceAtPoints = needsReferenceAtPoints;
    }

    /// <summary>
    ///     The name scripts use.
    /// </summary>
    public string Name { get; }

    /// <summary>
    ///     The name of the voxel field a region field reads, or <see langword="null" /> for a voxel field.
    /// </summary>
    public string? HaloSource { get; }

    /// <summary>
    ///     Whether this field is computed over a region instead of read from one voxel.
    /// </summary>
    public bool IsRegionField => RegionComputer != null;

    /// <summary>
    ///     Whether sampling at single voxels needs <see cref="WorkbenchFieldOptions.SchlierenReference" />.
    /// </summary>
    public bool NeedsReferenceAtPoints { get; }

    private WorkbenchRegionComputer? RegionComputer { get; }

    internal void ComputeRegion(in WorkbenchRegionInput input, Span<float> destination)
    {
        RegionComputer!(in input, destination);
    }

    /// <summary>
    ///     The unit label.
    /// </summary>
    public string Unit { get; }

    /// <summary>
    ///     The snapshot fields a snapshot needs before <see cref="Sample" /> runs.
    /// </summary>
    /// <remarks>
    ///     This always includes the voxel classification, because the field must know which voxels are solid.
    /// </remarks>
    public AtmosChunkSnapshotFields Required { get; }

    /// <summary>
    ///     Whether solid voxels return a real value instead of NaN.
    /// </summary>
    public bool ValueInSolid { get; }

    private WorkbenchFieldSampler Sampler { get; }

    /// <summary>
    ///     Reads the value for one voxel.
    /// </summary>
    /// <param name="snapshot">A snapshot that holds <see cref="Required" />.</param>
    /// <param name="localIndex">The voxel index inside the chunk.</param>
    /// <returns>The value, or NaN for a solid voxel when <see cref="ValueInSolid" /> is false.</returns>
    public float Sample(in AtmosChunkSnapshot snapshot, int localIndex)
    {
        if (IsRegionField)
            throw new InvalidOperationException($"Field '{Name}' is a region field. Read it through the session.");

        if (!ValueInSolid && snapshot.VoxelRoomMap[localIndex] == VoxelClassification.RoomSolid)
            return float.NaN;

        return Sampler(in snapshot, localIndex);
    }
}

/// <summary>
///     The set of fields a session can sample.
/// </summary>
/// <remarks>
///     <para>
///         The default registry holds `temperature` (K), `pressure` (Pa), `totalMoles` (mol), `classification`, the
///         velocity fields `velocityX`, `velocityY`, `velocityZ`, and `speed` (m/s), and a parametric
///         `moles:&lt;gasId&gt;` (mol) for each gas. Solid voxels and missing chunks read as NaN, except for
///         `classification`, which has no value only for a missing chunk.
///     </para>
///     <para>
///         <see cref="RegisterFlowFields" /> adds `density` (kg/m³), `densityGradient` (kg/m⁴), and `schlieren`
///         (dimensionless). The `schlieren:&lt;k&gt;` family sets the steepness.
///     </para>
///     <para>
///         The registry is not thread-safe. Register fields before the first sample.
///     </para>
/// </remarks>
public sealed class WorkbenchFieldRegistry
{
    private readonly Dictionary<string, WorkbenchField> _fields = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Func<string, WorkbenchField?>> _resolvers = [];

    /// <summary>
    ///     The name prefix of the per-gas field family.
    /// </summary>
    public const string MolesPrefix = "moles:";

    /// <summary>
    ///     Creates a registry with the built-in fields.
    /// </summary>
    public static WorkbenchFieldRegistry CreateDefault()
    {
        var registry = new WorkbenchFieldRegistry();
        registry.Register(new WorkbenchField(
            "temperature",
            "K",
            AtmosChunkSnapshotFields.Temperature,
            static (in snapshot, index) => snapshot.Temperature[index]));

        registry.Register(new WorkbenchField(
            "pressure",
            "Pa",
            AtmosChunkSnapshotFields.Pressure,
            static (in snapshot, index) => snapshot.TotalPressure[index]));

        registry.Register(new WorkbenchField(
            "totalMoles",
            "mol",
            AtmosChunkSnapshotFields.Gases,
            static (in snapshot, index) => SumMoles(in snapshot, index)));

        registry.Register(new WorkbenchField(
            "classification",
            string.Empty,
            AtmosChunkSnapshotFields.VoxelClassification,
            static (in snapshot, index) => snapshot.VoxelRoomMap[index],
            true));

        registry.Register(new WorkbenchField(
            "velocityX",
            "m/s",
            AtmosChunkSnapshotFields.Velocity,
            static (in snapshot, index) => snapshot.VelocityX[index]));

        registry.Register(new WorkbenchField(
            "velocityY",
            "m/s",
            AtmosChunkSnapshotFields.Velocity,
            static (in snapshot, index) => snapshot.VelocityY[index]));

        registry.Register(new WorkbenchField(
            "velocityZ",
            "m/s",
            AtmosChunkSnapshotFields.Velocity,
            static (in snapshot, index) => snapshot.VelocityZ[index]));

        registry.Register(new WorkbenchField(
            "speed",
            "m/s",
            AtmosChunkSnapshotFields.Velocity,
            static (in snapshot, index) =>
                FlowFieldMath.Speed(snapshot.VelocityX[index], snapshot.VelocityY[index], snapshot.VelocityZ[index])));

        registry.RegisterResolver(static name =>
        {
            if (!name.StartsWith(MolesPrefix, StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(
                    name.AsSpan(MolesPrefix.Length),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int gasId))
            {
                return null;
            }

            return new WorkbenchField(
                MolesPrefix + gasId.ToString(CultureInfo.InvariantCulture),
                "mol",
                AtmosChunkSnapshotFields.Gases,
                (in snapshot, index) => GasMoles(in snapshot, gasId, index));
        });

        return registry;
    }

    /// <summary>
    ///     The name prefix of the schlieren family, such as `schlieren:12`.
    /// </summary>
    public const string SchlierenPrefix = "schlieren:";

    /// <summary>
    ///     The name of the density field.
    /// </summary>
    public const string DensityName = "density";

    /// <summary>
    ///     Adds the density, density gradient, and schlieren fields.
    /// </summary>
    /// <param name="configProvider">
    ///     Returns the configuration to read when a field is computed. The density follows later configuration
    ///     changes, because the field asks for the configuration on every read.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         `density` is <c>sum(n_k M_k) / V</c> in kg/m³ and is NaN in solid and void voxels. `densityGradient` is
    ///         the magnitude of its gradient in kg/m⁴, computed by <see cref="FlowFieldMath" />. `schlieren` is
    ///         <c>exp(-k |grad rho| / g_ref)</c> with <c>k = 15</c>, and `schlieren:&lt;k&gt;` sets <c>k</c>.
    ///     </para>
    ///     <para>
    ///         The reference <c>g_ref</c> is <see cref="WorkbenchFieldOptions.SchlierenReference" />, or the largest
    ///         gradient inside the region that is read.
    ///     </para>
    /// </remarks>
    public void RegisterFlowFields(Func<AtmosConfig> configProvider)
    {
        ArgumentNullException.ThrowIfNull(configProvider);
        Register(new WorkbenchField(
            DensityName,
            "kg/m3",
            AtmosChunkSnapshotFields.Gases,
            (in snapshot, index) => snapshot.VoxelRoomMap[index] == VoxelClassification.RoomVoid
                ? float.NaN
                : FlowFieldMath.GetDensity(snapshot.Gases, index, configProvider())));

        Register(new WorkbenchField(
            "densityGradient",
            "kg/m4",
            DensityName,
            static (in input, destination) => FlowFieldMath.ComputeGradientMagnitude(
                input.Source,
                input.Size,
                input.VoxelEdge,
                destination)));

        Register(CreateSchlieren("schlieren", FlowFieldMath.DefaultSchlierenSteepness));
        RegisterResolver(static name =>
        {
            if (!name.StartsWith(SchlierenPrefix, StringComparison.OrdinalIgnoreCase) ||
                !float.TryParse(
                    name.AsSpan(SchlierenPrefix.Length),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float steepness) ||
                !float.IsFinite(steepness) ||
                steepness <= 0f)
            {
                return null;
            }

            return CreateSchlieren(
                SchlierenPrefix + steepness.ToString("R", CultureInfo.InvariantCulture),
                steepness);
        });
    }

    private static WorkbenchField CreateSchlieren(string name, float steepness)
    {
        return new WorkbenchField(
            name,
            string.Empty,
            DensityName,
            (in input, destination) =>
            {
                FlowFieldMath.ComputeGradientMagnitude(input.Source, input.Size, input.VoxelEdge, destination);
                float reference = input.SchlierenReference ?? FlowFieldMath.MaximumFinite(destination);
                for (int index = 0; index < destination.Length; index++)
                    destination[index] = FlowFieldMath.Schlieren(destination[index], reference, steepness);
            },
            true);
    }

    /// <summary>
    ///     The names of the fixed fields in registration order. Resolver families such as `moles:N` are not listed.
    /// </summary>
    public IReadOnlyCollection<string> Names => _fields.Values.Select(static item => item.Name).ToArray();

    /// <summary>
    ///     Adds or replaces a fixed field.
    /// </summary>
    /// <param name="field">The field. A field with the same name is replaced.</param>
    public void Register(WorkbenchField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        _fields[field.Name] = field;
    }

    /// <summary>
    ///     Adds a resolver for a family of fields, such as `moles:N`.
    /// </summary>
    /// <param name="resolver">Returns the field for a name, or <see langword="null" /> when the name is not in the family.</param>
    /// <remarks>
    ///     Fixed fields win over resolvers. Resolvers run in registration order.
    /// </remarks>
    public void RegisterResolver(Func<string, WorkbenchField?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolvers.Add(resolver);
    }

    /// <summary>
    ///     Finds a field by name.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <param name="field">The field when found.</param>
    /// <returns><see langword="true" /> when the name is known.</returns>
    public bool TryResolve(string name, out WorkbenchField field)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            string trimmed = name.Trim();
            if (_fields.TryGetValue(trimmed, out var fixedField))
            {
                field = fixedField;
                return true;
            }

            foreach (var resolver in _resolvers)
            {
                if (resolver(trimmed) is { } resolved)
                {
                    field = resolved;
                    return true;
                }
            }
        }

        field = null!;
        return false;
    }

    /// <summary>
    ///     Finds a field by name or throws.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The name is not a registered field.</exception>
    public WorkbenchField Resolve(string name)
    {
        if (TryResolve(name, out var field))
            return field;

        throw new KeyNotFoundException(
            $"Unknown field '{name}'. Known fields: {string.Join(", ", Names)}, {MolesPrefix}<gasId>, " +
            $"{SchlierenPrefix}<k>.");
    }

    /// <summary>
    ///     Resolves several names and returns the snapshot fields all of them need.
    /// </summary>
    /// <param name="names">The field names.</param>
    /// <param name="required">The union of the snapshot fields the fields read.</param>
    /// <returns>The fields in the order of <paramref name="names" />.</returns>
    /// <exception cref="KeyNotFoundException">A name is not a registered field.</exception>
    public WorkbenchField[] ResolveAll(IReadOnlyList<string> names, out AtmosChunkSnapshotFields required)
    {
        required = AtmosChunkSnapshotFields.None;
        var fields = new WorkbenchField[names.Count];
        for (int index = 0; index < fields.Length; index++)
        {
            fields[index] = Resolve(names[index]);
            required |= fields[index].Required;
        }

        return fields;
    }

    private static float SumMoles(in AtmosChunkSnapshot snapshot, int index)
    {
        float total = 0f;
        foreach (var gas in snapshot.Gases)
        {
            float moles = gas.Moles[index];
            if (GasAmounts.IsValidGasAmount(moles))
                total += moles;
        }

        return total;
    }

    private static float GasMoles(in AtmosChunkSnapshot snapshot, int gasId, int index)
    {
        foreach (var gas in snapshot.Gases)
        {
            if (gas.GasId == gasId)
                return gas.Moles[index];
        }

        return 0f;
    }
}
