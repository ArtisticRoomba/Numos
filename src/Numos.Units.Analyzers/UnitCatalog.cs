using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Numos.Units.Analyzers;

internal sealed class UnitCatalog
{
    private readonly ImmutableDictionary<string, QuantityDefinition> _byAlias;
    private readonly ImmutableDictionary<string, QuantityDefinition> _byId;

    private UnitCatalog(
        IEnumerable<QuantityDefinition> definitions,
        IEnumerable<UnitConversionDefinition>? conversions = null)
    {
        QuantityDefinition[] materialized = definitions.ToArray();
        _byAlias = materialized.ToImmutableDictionary(
            definition => definition.Alias,
            StringComparer.Ordinal);

        _byId = materialized.GroupBy(definition => definition.Id, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        Definitions = materialized.ToImmutableArray();
        Conversions = (conversions ?? Array.Empty<UnitConversionDefinition>()).ToImmutableArray();
    }

    internal ImmutableArray<QuantityDefinition> Definitions { get; }
    internal ImmutableArray<UnitConversionDefinition> Conversions { get; }

    internal static UnitCatalog Empty { get; } = new(Array.Empty<QuantityDefinition>());

    internal bool TryGetAlias(string alias, out QuantityDefinition definition)
    {
        return _byAlias.TryGetValue(alias, out definition!);
    }

    internal bool TryGetId(string id, out QuantityDefinition definition)
    {
        return _byId.TryGetValue(id, out definition!);
    }

    internal static UnitCatalog Parse(
        IEnumerable<AdditionalText> files, CancellationToken cancellationToken,
        Action<Diagnostic>? reportDiagnostic = null)
    {
        var definitions = new List<QuantityDefinition>();
        var conversions = new List<UnitConversionDefinition>();
        var conversionNames = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files.Where(file =>
                     file.Path.EndsWith(".numosunits", StringComparison.OrdinalIgnoreCase)))
        {
            var text = file.GetText(cancellationToken);
            if (text is null)
                continue;

            for (int lineIndex = 0; lineIndex < text.Lines.Count; lineIndex++)
            {
                string line = text.Lines[lineIndex].ToString().Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                try
                {
                    if (line.StartsWith("convert", StringComparison.Ordinal))
                    {
                        var conversion = ParseConversionLine(line);
                        if (!conversionNames.Add(conversion.UnitName))
                            throw new FormatException($"Conversion unit '{conversion.UnitName}' is declared more than once.");

                        conversions.Add(conversion);
                    }
                    else
                    {
                        var definition = ParseQuantityLine(line);
                        if (!aliases.Add(definition.Alias))
                            throw new FormatException($"Alias '{definition.Alias}' is declared more than once.");

                        definitions.Add(definition);
                    }
                }
                catch (FormatException exception)
                {
                    if (reportDiagnostic is null)
                        continue;

                    var textLine = text.Lines[lineIndex];
                    var location = Location.Create(
                        file.Path,
                        textLine.Span,
                        new LinePositionSpan(
                            new LinePosition(lineIndex, 0),
                            new LinePosition(lineIndex, textLine.Span.Length)));

                    reportDiagnostic(Diagnostic.Create(Diagnostics.InvalidCatalog, location, exception.Message));
                }
            }
        }

        if (definitions.Count == 0)
            return Empty;

        var catalog = new UnitCatalog(definitions, conversions);
        if (reportDiagnostic is not null)
        {
            foreach (var conversion in conversions)
            {
                if (!catalog.TryGetId(conversion.QuantityId, out _))
                {
                    reportDiagnostic(
                        Diagnostic.Create(
                            Diagnostics.InvalidCatalog,
                            Location.None,
                            $"Conversion '{conversion.UnitName}' references unknown quantity '{conversion.QuantityId}'."));
                }
            }
        }

        return catalog;
    }

    private static QuantityDefinition ParseQuantityLine(string line)
    {
        string[] parts = line.Split('|');
        if (parts.Length != 4)
            throw new FormatException("Expected 'id | alias | storage type | dimensions'.");

        string id = parts[0].Trim();
        string alias = parts[1].Trim();
        string storageType = parts[2].Trim();
        if (!IsIdentifier(id) || !IsIdentifier(alias) || storageType.Length == 0)
            throw new FormatException("Quantity IDs and aliases must be identifiers and a storage type is required.");

        var dimensions = ImmutableSortedDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        string dimensionText = parts[3].Trim();
        if (dimensionText.Length != 0 && dimensionText != "1")
        {
            foreach (string dimensionEntry in dimensionText.Split(','))
            {
                string[] pair = dimensionEntry.Split('=');
                if (pair.Length != 2 ||
                    !IsIdentifier(pair[0].Trim()) ||
                    !int.TryParse(pair[1].Trim(), out int exponent))
                    throw new FormatException($"Invalid dimension entry '{dimensionEntry.Trim()}'.");

                if (exponent != 0)
                    dimensions[pair[0].Trim()] = exponent;
            }
        }

        return new QuantityDefinition(id, alias, storageType, new DimensionVector(dimensions.ToImmutable()));
    }

    private static UnitConversionDefinition ParseConversionLine(string line)
    {
        string[] parts = line.Split('|');
        if (parts.Length != 6 || parts[0].Trim() != "convert")
            throw new FormatException("Expected 'convert | quantity id | unit name | storage type | scale | offset'.");

        string quantityId = parts[1].Trim();
        string unitName = parts[2].Trim();
        string storageType = parts[3].Trim();
        if (!IsIdentifier(quantityId) || !IsIdentifier(unitName) || storageType.Length == 0)
            throw new FormatException("Conversion quantity IDs and unit names must be identifiers.");

        if (!double.TryParse(
                parts[4].Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double scale) ||
            scale == 0d ||
            double.IsNaN(scale) ||
            double.IsInfinity(scale))
            throw new FormatException("Conversion scale must be a finite, nonzero number.");

        if (!double.TryParse(
                parts[5].Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double offset) ||
            double.IsNaN(offset) ||
            double.IsInfinity(offset))
            throw new FormatException("Conversion offset must be a finite number.");

        return new UnitConversionDefinition(quantityId, unitName, storageType, scale, offset);
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0 || !(char.IsLetter(value[0]) || value[0] == '_'))
            return false;

        return value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');
    }
}

internal sealed class UnitConversionDefinition
{
    internal UnitConversionDefinition(
        string quantityId, string unitName, string storageType,
        double scale, double offset)
    {
        QuantityId = quantityId;
        UnitName = unitName;
        StorageType = storageType;
        Scale = scale;
        Offset = offset;
    }

    internal string QuantityId { get; }
    internal string UnitName { get; }
    internal string StorageType { get; }
    internal double Scale { get; }
    internal double Offset { get; }
}

internal sealed class QuantityDefinition
{
    internal QuantityDefinition(string id, string alias, string storageType, DimensionVector dimensions)
    {
        Id = id;
        Alias = alias;
        StorageType = storageType;
        Dimensions = dimensions;
    }

    internal string Id { get; }
    internal string Alias { get; }
    internal string StorageType { get; }
    internal DimensionVector Dimensions { get; }
}

internal sealed class DimensionVector : IEquatable<DimensionVector>
{
    private readonly ImmutableSortedDictionary<string, int> _exponents;

    internal DimensionVector(ImmutableSortedDictionary<string, int> exponents)
    {
        _exponents = exponents;
    }

    public bool IsScalar()
    {
        return _exponents.All(e => e.Value == 0);
    }

    public bool Equals(DimensionVector? other)
    {
        return other is not null && _exponents.SequenceEqual(other._exponents);
    }

    internal DimensionVector Multiply(DimensionVector other)
    {
        return Combine(other, 1);
    }

    internal DimensionVector Divide(DimensionVector other)
    {
        return Combine(other, -1);
    }

    internal DimensionVector Invert()
    {
        return new DimensionVector(
            _exponents.ToImmutableSortedDictionary(pair => pair.Key, pair => -pair.Value, StringComparer.Ordinal));
    }

    private DimensionVector Combine(DimensionVector other, int sign)
    {
        var result = _exponents.ToBuilder();
        foreach (KeyValuePair<string, int> pair in other._exponents)
        {
            result.TryGetValue(pair.Key, out int current);
            int exponent = current + sign * pair.Value;
            if (exponent == 0)
                result.Remove(pair.Key);
            else
                result[pair.Key] = exponent;
        }

        return new DimensionVector(result.ToImmutable());
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as DimensionVector);
    }

    public override int GetHashCode()
    {
        int hash = 17;
        foreach (KeyValuePair<string, int> pair in _exponents)
            hash = unchecked(hash * 31 + pair.Key.GetHashCode() * 397 ^ pair.Value);

        return hash;
    }

    public override string ToString()
    {
        return _exponents.Count == 0
            ? "dimensionless"
            : string.Join(
                " ",
                _exponents.Select(pair => _exponents.Count == 1 && pair.Value == 1
                    ? pair.Key
                    : $"{pair.Key}^{pair.Value}"));
    }
}