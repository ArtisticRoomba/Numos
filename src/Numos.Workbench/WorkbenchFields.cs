using System.Globalization;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;

namespace Numos.Workbench;

/// <summary>
///     Reads one scalar value for one voxel from a detached chunk snapshot.
/// </summary>
/// <param name="snapshot">A snapshot that holds at least the fields the sampler declared.</param>
/// <param name="localIndex">The voxel index inside the chunk, as `x + width * (y + height * z)`.</param>
/// <returns>The value, or NaN when the voxel has none.</returns>
public delegate float WorkbenchFieldSampler(in AtmosChunkSnapshot snapshot, int localIndex);

/// <summary>
///     A named scalar quantity that can be sampled, exported, and probed, such as `pressure`.
/// </summary>
/// <remarks>
///     A field reads only detached snapshots, so sampling never changes the simulation. A solver package can add
///     fields, such as density or velocity, through <see cref="WorkbenchFieldRegistry.Register" />.
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
    ///     The name scripts use.
    /// </summary>
    public string Name { get; }

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
///         The default registry holds `temperature` (K), `pressure` (Pa), `totalMoles` (mol), `classification`, and a
///         parametric `moles:&lt;gasId&gt;` (mol) for each gas. Solid voxels and missing chunks read as NaN, except for
///         `classification`, which has no value only for a missing chunk.
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
            $"Unknown field '{name}'. Known fields: {string.Join(", ", Names)}, {MolesPrefix}<gasId>.");
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
