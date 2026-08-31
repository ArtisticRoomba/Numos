namespace Numos.Units;

/// <summary>
///     Associates values stored in an array or indexable container with a quantity from the consuming project's
///     unit catalog.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter |
                AttributeTargets.ReturnValue)]
public sealed class ElementQuantityAttribute : Attribute
{
    /// <summary>Creates an element annotation for a catalog quantity identifier.</summary>
    public ElementQuantityAttribute(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    /// <summary>The stable identifier used in a <c>*.numosunits</c> catalog.</summary>
    public string Id { get; }
}