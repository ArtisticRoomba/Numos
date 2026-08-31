namespace Numos.Units;

/// <summary>
///     Associates a primitive numeric declaration with a quantity from the consuming project's unit catalog.
/// </summary>
/// <remarks>
///     The attribute is metadata for compile-time analyzers. It does not wrap, convert, or otherwise alter the
///     annotated value at runtime.
/// </remarks>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter |
                AttributeTargets.ReturnValue)]
public sealed class QuantityAttribute : Attribute
{
    /// <summary>Creates an annotation for a catalog quantity identifier.</summary>
    public QuantityAttribute(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    /// <summary>The stable identifier used in a <c>*.numosunits</c> catalog.</summary>
    public string Id { get; }
}