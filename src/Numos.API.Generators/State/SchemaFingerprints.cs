using System.Text;

namespace Numos.API.Generators.State;

/// <summary>
///     Computes the schema fingerprint the generator writes into every state type, which a restore compares with the
///     fingerprint a checkpoint entry saved.
/// </summary>
/// <remarks>
///     <para>
///         The fingerprint is FNV-1a (64-bit) over each field in ascending id order. A field adds its wire name, a zero
///         byte, its shape description (see <see cref="Describe" />), and another zero byte, all as UTF-8. A result of 0
///         maps to 1, so 0 can mean "not recorded".
///     </para>
///     <para>
///         Only what reaches the wire goes in. A rename that keeps its wire name with <c>[NumosSerialize("Old")]</c>
///         keeps the fingerprint, and so does moving a member in source, because ids order the fields. A change of
///         wire name or of shape changes it.
///     </para>
///     <para>
///         A class restored in place shares its wire object with its base classes, so its fingerprint covers their
///         fields too. Moving a member between a base and a derived class keeps it.
///     </para>
/// </remarks>
internal static class SchemaFingerprints
{
    private const ulong OffsetBasis = 14695981039346656037;
    private const ulong Prime = 1099511628211;

    /// <summary>
    ///     Gets the fingerprint of a type with these fields.
    /// </summary>
    /// <param name="fields">The type's state fields with resolved shapes, in any order.</param>
    public static ulong Compute(IEnumerable<StateFieldModel> fields)
    {
        return Compute(fields.Select(Entry));
    }

    /// <summary>
    ///     Gets the fingerprint of a type with these schema entries.
    /// </summary>
    /// <param name="entries">
    ///     The entries in any order. A class restored in place passes its own fields and every field of its base
    ///     classes, which share one object on the wire.
    /// </param>
    public static ulong Compute(IEnumerable<SchemaEntry> entries)
    {
        ulong hash = OffsetBasis;
        foreach (SchemaEntry entry in entries.OrderBy(static entry => entry.Id))
        {
            hash = Add(hash, entry.WireName);
            hash = Add(hash, entry.Description);
        }

        return hash == 0 ? 1 : hash;
    }

    /// <summary>
    ///     Gets the schema entry of a field with a resolved shape.
    /// </summary>
    public static SchemaEntry Entry(StateFieldModel field)
    {
        return new SchemaEntry(field.Id, field.WireName, Describe(field.Shape));
    }

    /// <summary>
    ///     Writes entries as the text of the generated <c>__numosSchemaFields</c> constant: each wire name and
    ///     description, ended by a zero character, in ascending id order.
    /// </summary>
    public static string Format(IEnumerable<SchemaEntry> entries)
    {
        var text = new StringBuilder();
        foreach (SchemaEntry entry in entries.OrderBy(static entry => entry.Id))
            text.Append(entry.WireName).Append('\0').Append(entry.Description).Append('\0');

        return text.ToString();
    }

    /// <summary>
    ///     Reads the text <see cref="Format" /> writes, as a referenced assembly's constant holds it.
    /// </summary>
    /// <returns>The entries, or null when the text is malformed.</returns>
    public static List<SchemaEntry>? Parse(string text)
    {
        var entries = new List<SchemaEntry>();
        string[] parts = text.Split('\0');

        // The text ends with a zero, so the last part is empty and the rest pair up.
        if (parts.Length % 2 != 1 || parts[parts.Length - 1].Length != 0)
            return null;

        for (int index = 0; index + 1 < parts.Length; index += 2)
            entries.Add(new SchemaEntry(FieldIds.Compute(parts[index]), parts[index], parts[index + 1]));

        return entries;
    }

    /// <summary>
    ///     Describes a shape by what its codec writes, without C# type names.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An array and a list write the same bytes, and so do the two nullable forms, so each pair shares a
    ///         description. An enum contributes its underlying primitive, and a quantity alias has already resolved to
    ///         its storage primitive.
    ///     </para>
    ///     <para>
    ///         A nested state type contributes only the word <c>State</c>. Its own members have their own fingerprint,
    ///         and a change there does not reach the fingerprint of a type that holds it. A custom codec contributes
    ///         its codec type, because the codec alone decides the bytes.
    ///     </para>
    /// </remarks>
    public static string Describe(StateShape shape)
    {
        return shape.Kind switch
        {
            StateShapeKind.Primitive => shape.Name,
            StateShapeKind.Enum => "Enum(" + Describe(shape.Arguments[0]) + ")",
            StateShapeKind.List or StateShapeKind.Array => "List(" + Describe(shape.Arguments[0]) + ")",
            StateShapeKind.Set => "Set(" + Describe(shape.Arguments[0]) + ")",
            StateShapeKind.Dictionary =>
                "Dictionary(" + Describe(shape.Arguments[0]) + "," + Describe(shape.Arguments[1]) + ")",
            StateShapeKind.NullableReference or StateShapeKind.NullableValue =>
                "Nullable(" + Describe(shape.Arguments[0]) + ")",
            StateShapeKind.Custom => "Custom(" + shape.Codec + ")",
            _ => shape.Kind.ToString(),
        };
    }

    private static ulong Add(ulong hash, string text)
    {
        foreach (byte value in Encoding.UTF8.GetBytes(text))
        {
            hash ^= value;
            hash *= Prime;
        }

        // The zero byte that ends each text keeps "ab" + "c" apart from "a" + "bc". XOR with zero changes nothing.
        hash *= Prime;
        return hash;
    }
}
