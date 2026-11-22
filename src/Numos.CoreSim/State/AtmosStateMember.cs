namespace Numos.CoreSim.State;

/// <summary>
///     Describes one <see cref="NumosSerializeAttribute" /> member of a class solver or a <c>[SimulationComponent]</c>
///     class, so a tool can show and edit it without reflection.
/// </summary>
/// <remarks>
///     <para>
///         The <c>Numos.API</c> source generator builds these. Get them from <see cref="INumosStateDescribed" />, or
///         through <c>AtmosWorldSolverPipeline.TryGetState</c> and <c>AtmosSimulation.TryGetComponentState</c>.
///     </para>
///     <para>
///         Only a number, <see langword="bool" />, <see langword="string" />, or enum member, or a nullable form of one,
///         has a <see cref="SetValue" />. A member marked <c>[NumosSerialize(HostEditable = false)]</c> has none either,
///         because the recording boundary does not hash it and so would miss the edit. Change such a member through a
///         solver command.
///     </para>
///     <para>
///         Call <see cref="SetValue" /> from host code between ticks, as for any other host edit. Numos records the
///         change at the next recording boundary.
///     </para>
/// </remarks>
public sealed class AtmosStateMember
{
    /// <summary>
    ///     Creates a descriptor. Generated code calls this.
    /// </summary>
    /// <param name="name">The member name as declared.</param>
    /// <param name="wireName">The name the member's wire id hashes.</param>
    /// <param name="valueType">The member's runtime type. A quantity alias is its storage primitive.</param>
    /// <param name="unit">The quantity alias the member is declared with, or <see langword="null" />.</param>
    /// <param name="getValue">Reads the member from an instance of the declaring type.</param>
    /// <param name="setValue">Writes the member, or <see langword="null" /> when tools must not write it.</param>
    /// <exception cref="ArgumentNullException">
    ///     <paramref name="name" />, <paramref name="wireName" />, <paramref name="valueType" />, or
    ///     <paramref name="getValue" /> is <see langword="null" />.
    /// </exception>
    public AtmosStateMember(
        string name, string wireName, Type valueType, string? unit, Func<object, object?> getValue,
        Action<object, object?>? setValue)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(wireName);
        ArgumentNullException.ThrowIfNull(valueType);
        ArgumentNullException.ThrowIfNull(getValue);
        Name = name;
        WireName = wireName;
        ValueType = valueType;
        Unit = unit;
        GetValue = getValue;
        SetValue = setValue;
    }

    /// <summary>
    ///     Gets the member name as declared.
    /// </summary>
    public string Name { get; }

    /// <summary>
    ///     Gets the name the member's wire id hashes: <see cref="Name" />, or the name passed to
    ///     <see cref="NumosSerializeAttribute(string)" />.
    /// </summary>
    public string WireName { get; }

    /// <summary>
    ///     Gets the member's runtime type. A quantity alias such as <c>Kelvin</c> is its storage primitive here, and
    ///     <see cref="Unit" /> keeps the alias name.
    /// </summary>
    public Type ValueType { get; }

    /// <summary>
    ///     Gets the quantity alias the member is declared with, such as <c>Kelvin</c>, or <see langword="null" /> for a
    ///     member declared with any other type.
    /// </summary>
    public string? Unit { get; }

    /// <summary>
    ///     Gets whether <see cref="SetValue" /> is present.
    /// </summary>
    public bool CanWrite => SetValue is not null;

    /// <summary>
    ///     Gets the reader. It takes an instance of the declaring type and returns the member's value, boxed.
    /// </summary>
    /// <remarks>
    ///     A collection or nested state value is the live instance the member holds. Do not change it through this
    ///     reference.
    /// </remarks>
    /// <exception cref="InvalidCastException">The argument is not an instance of the declaring type.</exception>
    public Func<object, object?> GetValue { get; }

    /// <summary>
    ///     Gets the writer, or <see langword="null" /> when <see cref="CanWrite" /> is <see langword="false" />. It takes
    ///     an instance of the declaring type and the new value.
    /// </summary>
    /// <remarks>
    ///     The value must be a boxed <see cref="ValueType" />, or <see langword="null" /> for a nullable member. The
    ///     writer does not convert, so a <see langword="double" /> for a <see langword="float" /> member throws.
    /// </remarks>
    /// <exception cref="InvalidCastException">
    ///     The first argument is not an instance of the declaring type, or the value is not a <see cref="ValueType" />.
    /// </exception>
    /// <exception cref="ArgumentNullException">The value is <see langword="null" /> and the member is not nullable.</exception>
    public Action<object, object?>? SetValue { get; }

    /// <summary>
    ///     Returns the member name and type.
    /// </summary>
    public override string ToString() => Name + " (" + ValueType.Name + ")";
}

/// <summary>
///     Implemented by the source generator for a class solver or a <c>[SimulationComponent]</c> class with
///     <see cref="NumosSerializeAttribute" /> members.
/// </summary>
/// <remarks>
///     Do not implement it by hand. The generator keeps the list in step with the state walker.
/// </remarks>
public interface INumosStateDescribed
{
    /// <summary>
    ///     Gets one descriptor per marked member, in declaration order. The members of a base class come first.
    /// </summary>
    /// <remarks>
    ///     Every instance of a type returns the same list.
    /// </remarks>
    IReadOnlyList<AtmosStateMember> StateMembers { get; }
}
