using System.Collections;
using System.Globalization;
using Numos.CoreSim.State;

namespace Numos.Workbench;

/// <summary>
///     One <c>[NumosSerialize]</c> member of a class solver or a simulation component, read for a tool.
/// </summary>
/// <param name="Name">The member name as declared.</param>
/// <param name="WireName">The name the member's wire id hashes.</param>
/// <param name="TypeName">The member type in C# spelling, such as `float` or `List&lt;int&gt;`.</param>
/// <param name="Unit">The quantity alias the member is declared with, such as `Kelvin`, or <see langword="null" />.</param>
/// <param name="CanWrite">
///     Whether <see cref="WorkbenchSession.SetSolverState" /> and
///     <see cref="WorkbenchSession.SetSimulationComponentState" /> accept the member.
/// </param>
/// <param name="IsScalar">
///     Whether the member is a number, <see langword="bool" />, <see langword="string" />, or enum, or a nullable form of
///     one. Only a scalar has a <paramref name="Value" />.
/// </param>
/// <param name="Value">The value of a scalar member, boxed, or <see langword="null" />.</param>
/// <param name="Text">
///     The value as invariant text. A collection shows its element count, and any other value its
///     <see cref="object.ToString" />.
/// </param>
public sealed record WorkbenchStateMember(
    string Name,
    string WireName,
    string TypeName,
    string? Unit,
    bool CanWrite,
    bool IsScalar,
    object? Value,
    string Text);

/// <summary>
///     One simulation component and its members.
/// </summary>
/// <param name="Key">The component key.</param>
/// <param name="Members">The members in declaration order.</param>
public sealed record WorkbenchComponentState(string Key, IReadOnlyList<WorkbenchStateMember> Members);

/// <summary>
///     One entry of <see cref="Numos.API.AtmosSolverCatalog" />.
/// </summary>
/// <param name="Name">The stage name.</param>
/// <param name="Before">The declared stage it runs directly before, or <see langword="null" />.</param>
/// <param name="After">The declared stage it runs directly after, or <see langword="null" />.</param>
/// <param name="RunsPerSecond">The declared rate, or zero.</param>
/// <param name="Interval">The declared interval in ticks, or zero.</param>
/// <param name="IsAdded">Whether the project's pipeline holds the stage.</param>
public sealed record WorkbenchCatalogSolver(
    string Name,
    string? Before,
    string? After,
    int RunsPerSecond,
    int Interval,
    bool IsAdded);

/// <summary>
///     Reads, formats, and parses the values of <see cref="AtmosStateMember" /> descriptors as invariant text.
/// </summary>
/// <remarks>
///     The viewer and the headless runner parse a tool value with the same rules. Numbers use the invariant culture.
///     An enum takes one of its names, matched exactly. A <see langword="bool" /> takes `true` or `false`.
/// </remarks>
public static class WorkbenchStateValues
{
    /// <summary>
    ///     Reads one member of <paramref name="instance" />.
    /// </summary>
    /// <param name="member">The descriptor.</param>
    /// <param name="instance">An instance of the type that declares the member.</param>
    /// <returns>The member as a tool shows it.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null" />.</exception>
    public static WorkbenchStateMember Read(AtmosStateMember member, object instance)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(instance);
        object? value = member.GetValue(instance);
        bool scalar = IsScalar(member.ValueType);
        return new WorkbenchStateMember(
            member.Name,
            member.WireName,
            FormatTypeName(member.ValueType),
            member.Unit,
            member.CanWrite,
            scalar,
            scalar ? value : null,
            Format(value));
    }

    /// <summary>
    ///     Gets whether a tool can show <paramref name="type" /> as one value: a number, <see langword="bool" />,
    ///     <see langword="string" />, enum, or a nullable form of one.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="type" /> is <see langword="null" />.</exception>
    public static bool IsScalar(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(string) || underlying.IsEnum)
            return true;

        return underlying.IsPrimitive && underlying != typeof(char) && underlying != typeof(IntPtr) &&
               underlying != typeof(UIntPtr);
    }

    /// <summary>
    ///     Formats a member value as invariant text.
    /// </summary>
    /// <param name="value">The value, or <see langword="null" />.</param>
    /// <returns>
    ///     `null` for <see langword="null" />, `true` or `false` for a <see langword="bool" />, the shortest round-trip
    ///     text for a number, the name of an enum value, and `N items` for a collection.
    /// </returns>
    public static string Format(object? value)
    {
        return value switch
        {
            null => "null",
            bool flag => flag ? "true" : "false",
            string text => text,
            IFormattable formattable when !value.GetType().IsEnum =>
                formattable.ToString(null, CultureInfo.InvariantCulture),
            IEnumerable sequence => FormatCount(sequence),
            _ => value.ToString() ?? "",
        };
    }

    /// <summary>
    ///     Parses tool text into a value that <see cref="AtmosStateMember.SetValue" /> accepts for
    ///     <paramref name="type" />.
    /// </summary>
    /// <param name="type">The member's <see cref="AtmosStateMember.ValueType" />.</param>
    /// <param name="text">The text, or <see langword="null" /> for a null value.</param>
    /// <param name="value">The parsed value, boxed as <paramref name="type" /> or its underlying type.</param>
    /// <param name="error">Why the text does not parse, or <see langword="null" />.</param>
    /// <returns><see langword="true" /> when the text parses.</returns>
    /// <remarks>
    ///     <see langword="null" /> parses for a <see cref="Nullable{T}" /> type and for <see langword="string" />. A
    ///     <see langword="string" /> member that is not nullable then rejects it when the value is written.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="type" /> is <see langword="null" />.</exception>
    public static bool TryParse(Type type, string? text, out object? value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(type);
        value = null;
        error = null;
        Type? nullableOf = Nullable.GetUnderlyingType(type);
        Type target = nullableOf ?? type;
        if (text is null)
        {
            if (nullableOf != null || target == typeof(string))
                return true;

            error = $"A {FormatTypeName(type)} member cannot be null.";
            return false;
        }

        if (target == typeof(string))
        {
            value = text;
            return true;
        }

        if (target.IsEnum)
        {
            string[] names = Enum.GetNames(target);
            if (Array.IndexOf(names, text) < 0)
            {
                error = $"'{text}' is not a {target.Name} name. Use one of {string.Join(", ", names)}.";
                return false;
            }

            value = Enum.Parse(target, text);
            return true;
        }

        const NumberStyles integer = NumberStyles.Integer;
        const NumberStyles real = NumberStyles.Float;
        var culture = CultureInfo.InvariantCulture;
        bool parsed = Type.GetTypeCode(target) switch
        {
            TypeCode.Boolean => Box(bool.TryParse(text, out bool flag), flag, out value),
            TypeCode.Byte => Box(byte.TryParse(text, integer, culture, out byte b), b, out value),
            TypeCode.SByte => Box(sbyte.TryParse(text, integer, culture, out sbyte sb), sb, out value),
            TypeCode.Int16 => Box(short.TryParse(text, integer, culture, out short s), s, out value),
            TypeCode.UInt16 => Box(ushort.TryParse(text, integer, culture, out ushort us), us, out value),
            TypeCode.Int32 => Box(int.TryParse(text, integer, culture, out int i), i, out value),
            TypeCode.UInt32 => Box(uint.TryParse(text, integer, culture, out uint ui), ui, out value),
            TypeCode.Int64 => Box(long.TryParse(text, integer, culture, out long l), l, out value),
            TypeCode.UInt64 => Box(ulong.TryParse(text, integer, culture, out ulong ul), ul, out value),
            TypeCode.Single => Box(float.TryParse(text, real, culture, out float f), f, out value),
            TypeCode.Double => Box(double.TryParse(text, real, culture, out double d), d, out value),
            _ => Unsupported(target, out value, out error),
        };

        if (!parsed && error == null)
            error = $"'{text}' is not a valid {FormatTypeName(target)}.";

        return parsed;
    }

    /// <summary>
    ///     Spells <paramref name="type" /> the way C# source does, such as `float?`, `int[]`, or
    ///     `Dictionary&lt;string, int&gt;`.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="type" /> is <see langword="null" />.</exception>
    public static string FormatTypeName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return FormatTypeName(underlying) + "?";

        if (type.IsArray)
            return FormatTypeName(type.GetElementType()!) + "[]";

        string? keyword = Type.GetTypeCode(type) switch
        {
            _ when type.IsEnum => null,
            TypeCode.Boolean => "bool",
            TypeCode.Byte => "byte",
            TypeCode.SByte => "sbyte",
            TypeCode.Int16 => "short",
            TypeCode.UInt16 => "ushort",
            TypeCode.Int32 => "int",
            TypeCode.UInt32 => "uint",
            TypeCode.Int64 => "long",
            TypeCode.UInt64 => "ulong",
            TypeCode.Single => "float",
            TypeCode.Double => "double",
            TypeCode.String => "string",
            _ => null,
        };

        if (keyword != null)
            return keyword;

        if (!type.IsGenericType)
            return type.Name;

        string name = type.Name;
        int tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0)
            name = name[..tick];

        return name + "<" + string.Join(", ", type.GetGenericArguments().Select(FormatTypeName)) + ">";
    }

    private static bool Box<T>(bool parsed, T parsedValue, out object? value)
    {
        value = parsed ? parsedValue : null;
        return parsed;
    }

    private static bool Unsupported(Type type, out object? value, out string? error)
    {
        value = null;
        error = $"A tool cannot write a {FormatTypeName(type)} member.";
        return false;
    }

    private static string FormatCount(IEnumerable sequence)
    {
        int count = 0;
        if (sequence is ICollection collection)
        {
            count = collection.Count;
        }
        else
        {
            foreach (object? _ in sequence)
                count++;
        }

        return count == 1 ? "1 item" : $"{count} items";
    }
}
