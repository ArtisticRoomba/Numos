using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

/// <summary>
///     Picks the constructor a generated reader calls, and works out how each read field reaches it.
/// </summary>
/// <remarks>
///     Works on resolved shapes rather than symbols: inside a project with a units catalog, quantity aliases are error
///     types, so <c>Mole</c> and <c>float</c> only compare equal once both are reduced to their storage primitive.
/// </remarks>
internal static class StateBinding
{
    private const string Generic = "System.Collections.Generic.";
    private const string Frozen = "System.Collections.Frozen.";
    private const string ImmutableArray = "System.Collections.Immutable.ImmutableArray";
    private const string ArrayName = "[]";

    private static readonly string[] Sequences =
    [
        Generic + "IEnumerable", Generic + "IReadOnlyCollection", Generic + "ICollection", Generic + "IList",
        Generic + "IReadOnlyList", ArrayName,
    ];

    // Which parameter collection types each declared field collection already is, so the value passes through as-is.
    private static readonly Dictionary<string, string[]> Implements = new(StringComparer.Ordinal)
    {
        [Generic + "IReadOnlyList"] = [Generic + "IEnumerable", Generic + "IReadOnlyCollection", Generic + "IReadOnlyList"],
        [ArrayName] = Sequences,
        [ImmutableArray] = [.. Sequences.Where(static name => name != ArrayName), ImmutableArray],
        [Generic + "IReadOnlySet"] = [Generic + "IEnumerable", Generic + "IReadOnlyCollection", Generic + "IReadOnlySet"],
        [Frozen + "FrozenSet"] =
        [
            Generic + "IEnumerable", Generic + "IReadOnlyCollection", Generic + "ICollection", Generic + "IReadOnlySet",
            Generic + "ISet", Frozen + "FrozenSet",
        ],
        [Generic + "IReadOnlyDictionary"] = [Generic + "IReadOnlyDictionary"],
        [Generic + "Dictionary"] = [Generic + "IReadOnlyDictionary", Generic + "IDictionary", Generic + "Dictionary"],
        [Frozen + "FrozenDictionary"] =
            [Generic + "IReadOnlyDictionary", Generic + "IDictionary", Frozen + "FrozenDictionary"],
    };

    /// <summary>
    ///     Binds the constructor with the most parameters that works.
    /// </summary>
    /// <returns>The plan, or null with <paramref name="reason" /> explaining why the largest candidate failed.</returns>
    public static StateReadPlan? Bind(
        EquatableArray<StateConstructorModel> constructors,
        IReadOnlyList<StateFieldModel> fields,
        EquatableArray<QuantityAlias> aliases,
        out string reason)
    {
        StateReadPlan? best = null;
        int bestCount = -1;
        string? bestFailure = null;
        int failureCount = -1;
        foreach (StateConstructorModel constructor in constructors)
        {
            int count = constructor.Parameters.Count;
            StateReadPlan? plan = TryBind(constructor, fields, aliases, out string? failure);
            if (plan is not null)
            {
                if (count > bestCount)
                {
                    best = plan;
                    bestCount = count;
                }
            }
            else if (count > failureCount)
            {
                bestFailure = failure;
                failureCount = count;
            }
        }

        reason = bestFailure ?? "it declares no instance constructor";
        return best;
    }

    private static StateReadPlan? TryBind(
        StateConstructorModel constructor,
        IReadOnlyList<StateFieldModel> fields,
        EquatableArray<QuantityAlias> aliases,
        out string? failure)
    {
        var bound = new HashSet<int>();
        var arguments = new List<string>();
        foreach (StateParameterModel parameter in constructor.Parameters)
        {
            StateFieldModel? field = Match(parameter.Name, fields);
            if (field is null)
            {
                failure = "constructor parameter '" + parameter.Name + "' doesn't match any [StateField] member by name";
                return null;
            }

            if (!bound.Add(field.Id))
            {
                failure = "two constructor parameters bind to '" + field.Name + "'";
                return null;
            }

            StateShape target = parameter.Shape.Resolve(aliases);
            string local = "field" + field.Id;
            string? value = target.FindUnsupported() is null ? Convert(field.Shape, target, local) : null;
            if (value is null)
            {
                failure = "constructor parameter '" + parameter.Name + "' has type '" + parameter.TypeDisplay +
                          "', which can't take '" + field.Name + "' of type '" + Describe(field.Shape) + "'";

                return null;
            }

            string fallback = parameter.DefaultLiteral is null
                ? Convert(field.Shape, target, DefaultValue(field.Shape))!
                : Literal(target, parameter.DefaultLiteral);

            arguments.Add("has" + field.Id + " ? " + value + " : " + fallback);
        }

        var initializers = new List<StateInitializerModel>();
        foreach (StateFieldModel field in fields)
        {
            if (bound.Contains(field.Id))
                continue;

            if (!field.Settable)
            {
                failure = "'" + field.Name + "' isn't a constructor parameter and has no set or init accessor";
                return null;
            }

            // Init-only members have to be set in the initializer, so a missing one gets the codec default. Anything
            // assignable later is only touched when the data has it, keeping the constructor's or initializer's value.
            initializers.Add(
                field.InitOnly
                    ? new StateInitializerModel(
                        field.MemberName,
                        "has" + field.Id + " ? field" + field.Id + " : " + DefaultValue(field.Shape),
                        field.Id,
                        false)
                    : new StateInitializerModel(field.MemberName, "field" + field.Id, field.Id, true));
        }

        failure = null;
        return new StateReadPlan(arguments.ToEquatableArray(), initializers.ToEquatableArray());
    }

    private static StateFieldModel? Match(string parameter, IReadOnlyList<StateFieldModel> fields)
    {
        StateFieldModel? insensitive = null;
        int insensitiveMatches = 0;
        foreach (StateFieldModel field in fields)
        {
            if (field.Name == parameter)
                return field;

            if (string.Equals(field.Name, parameter, StringComparison.OrdinalIgnoreCase))
            {
                insensitive = field;
                insensitiveMatches++;
            }
        }

        // Two fields differing only by case leave the parameter ambiguous; treat it as unmatched.
        return insensitiveMatches == 1 ? insensitive : null;
    }

    /// <summary>
    ///     Gets an expression converting <paramref name="value" />, of the field's type, to the parameter's type.
    /// </summary>
    /// <returns>The expression, or null when the parameter can't take the field.</returns>
    private static string? Convert(StateShape field, StateShape parameter, string value)
    {
        if (field == parameter)
            return value;

        if (parameter.Kind == StateShapeKind.NullableReference)
        {
            // A non-null value always fits a nullable parameter; a nullable one only fits without a copy.
            StateShape inner = parameter.Arguments[0];
            if (field.Kind != StateShapeKind.NullableReference)
                return Convert(field, inner, value);

            return IsDirect(field.Arguments[0], inner) ? value : null;
        }

        if (parameter.Kind == StateShapeKind.NullableValue && parameter.Arguments[0] == field)
            return value;

        if (!IsCollection(field) || !IsCollection(parameter) || !field.Arguments.Equals(parameter.Arguments))
            return null;

        string from = ContainerName(field);
        string to = ContainerName(parameter);
        if (Implements.TryGetValue(from, out string[]? direct) && direct.Contains(to))
            return value;

        string elements = string.Join(", ", field.Arguments.Select(static argument => argument.TypeExpression));
        if (field.Kind == StateShapeKind.Dictionary)
        {
            return to switch
            {
                Generic + "IDictionary" or Generic + "IReadOnlyDictionary" or Generic + "Dictionary" =>
                    "new global::System.Collections.Generic.Dictionary<" + elements + ">(" + value + ")",
                Frozen + "FrozenDictionary" => "global::System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(" + value + ")",
                _ => null,
            };
        }

        if (field.Kind == StateShapeKind.Set)
        {
            switch (to)
            {
                case Generic + "ISet" or Generic + "IReadOnlySet" or Generic + "HashSet":
                    return "new global::System.Collections.Generic.HashSet<" + elements + ">(" + value + ")";
                case Frozen + "FrozenSet":
                    return "global::System.Collections.Frozen.FrozenSet.ToFrozenSet(" + value + ")";
            }
        }

        if (Array.IndexOf(Sequences, to) >= 0)
            return "global::System.Linq.Enumerable.ToArray(" + value + ")";

        return to switch
        {
            Generic + "List" => "new global::System.Collections.Generic.List<" + elements + ">(" + value + ")",
            ImmutableArray => "global::System.Collections.Immutable.ImmutableArray.CreateRange(" + value + ")",
            _ => null,
        };
    }

    private static bool IsDirect(StateShape field, StateShape parameter)
    {
        if (field == parameter)
            return true;

        return IsCollection(field) && IsCollection(parameter) && field.Arguments.Equals(parameter.Arguments) &&
               Implements.TryGetValue(ContainerName(field), out string[]? direct) && direct.Contains(ContainerName(parameter));
    }

    private static bool IsCollection(StateShape shape)
    {
        return shape.Kind is StateShapeKind.List or StateShapeKind.Array or StateShapeKind.Set or StateShapeKind.Dictionary
            or StateShapeKind.Container;
    }

    private static string ContainerName(StateShape shape)
    {
        if (shape.Kind == StateShapeKind.Array)
            return ArrayName;

        return shape.Name.StartsWith("global::", StringComparison.Ordinal) ? shape.Name.Substring("global::".Length) : shape.Name;
    }

    private static string Literal(StateShape target, string literal)
    {
        if (literal == "default")
            return literal;

        // Parenthesized so a negative literal still reads as a cast, not a subtraction.
        return target.Kind is StateShapeKind.Primitive or StateShapeKind.String or StateShapeKind.Enum or StateShapeKind.NullableValue
            ? "(" + target.TypeExpression + ")(" + literal + ")"
            : literal;
    }

    private static string DefaultValue(StateShape shape)
    {
        return "global::Numos.CoreSim.State.Advanced.StateCodecs.DefaultValue<" + shape.TypeExpression + ", " + shape.CodecExpression + ">()";
    }

    private static string Describe(StateShape shape)
    {
        return shape.TypeExpression.Replace("global::", "");
    }
}
