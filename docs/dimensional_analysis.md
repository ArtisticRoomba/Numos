# Dimensional analysis

To prevent developers and engineers from going insane, Numos has a static dimensional analysis Roslyn analyzer suite.
It tracks dimensional quantities throughout the solution.

The analysis runs entirely at compile time. This keeps Native AOT compatibility, and that is unlikely to change. C# `alias`es
annotate the code and keep the underlying types, so operations like vectorization work as before. A generated alias
such as `Kelvin` or `Pascal` backed by `global::System.Single` is still `System.Single` in CLR metadata. Arrays stay
primitive arrays, and arithmetic stays ordinary primitive arithmetic.

The system has two components:

- `Numos.Units.Analyzers` generates aliases from a catalog and reports dimension mismatches as build errors.
- `Numos.Units` supplies metadata attributes for public and cross-assembly declarations whose aliases are erased from
  CLR signatures.

## Catalog format

`Numos.CoreSim.numosunits` is included as an MSBuild `AdditionalFiles` item. Quantity declarations use four
pipe-separated fields:

```text
stableId | Alias | global::System.Single | baseDimension=exponent,...
```

Base dimensions are names, not a fixed enum. A domain can add an independent dimension without changing the analyzer.
`1` denotes a dimensionless quantity. Several aliases can share the same dimensions and use different primitive
storage types, such as `Kelvin` and `Kelvin64`.

Boundary conversions use six fields:

```text
convert | stableId | ExternalUnitName | global::System.Single | scale | offset
```

The generated conversion follows `canonical = input * scale + offset`. It emits `FromExternalUnitName` and
`ToExternalUnitName` methods in `Numos.Units.Generated.UnitConversions`. These conversions carry metadata, so the
analyzer follows their inputs and outputs across the solution.

## Source annotations

Use a generated alias for local variables, parameters, return types, and generic element types:

```csharp
internal static Pascal CalculatePressure(
    Mole amount,
    Kelvin temperature,
    PascalPerMoleKelvin coefficient)
{
    return amount * temperature * coefficient;
}
```

Use `[Quantity("stableId")]` on public scalar declarations. Use `[ElementQuantity("stableId")]` on public arrays or
indexable storage. Both attributes keep the quantity visible to analyzers in another assembly.

The analyzer treats unannotated numeric values as unknown. Numeric literals can adopt the surrounding quantity, so
constants such as zero and scalar factors work as expected. When you add a solver path, annotate its storage boundary
and important intermediate values. This keeps the dimensional information intact.
