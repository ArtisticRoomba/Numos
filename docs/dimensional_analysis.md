# Dimensional analysis

To prevent developers and engineers from going insane, Numos features a built-in static dimensional analysis Roslyn Analyzer suite that assists in tracking dimensional quantities throughout the solution.

This is done entirely at compile time, and to preserve Native AOT compatibility, will likely never change. Using C# `alias`es allows us to annotate while keeping the underlying types with no overhead, so operations like vectorization are still done in the same manner with no required adjustment.
A generated alias such as `Kelvin` or `Pascal` that uses `global::System.Single` as the backing is still `System.Single` in CLR metadata, arrays remain primitive arrays, and arithmetic remains ordinary primitive arithmetic.

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

Base dimensions are names rather than a fixed enum, so a domain can introduce another independent dimension without
changing the analyzer. `1` denotes a dimensionless quantity. Multiple aliases may use the same dimensions and different
primitive storage types, such as `Kelvin` and `Kelvin64`.

Boundary conversions use six fields:

```text
convert | stableId | ExternalUnitName | global::System.Single | scale | offset
```

The generated conversion follows `canonical = input * scale + offset` and emits `FromExternalUnitName` and
`ToExternalUnitName` methods in `Numos.Units.Generated.UnitConversions`. These generated conversions also carry relevant metadata, so the analyzer can follow inputs and outputs around the solution just fine.

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

Use `[Quantity("stableId")]` on public scalar declarations and `[ElementQuantity("stableId")]` on public arrays or
indexable storage when the quantity must remain visible to analyzers in another assembly.

Unannotated numeric values are deliberately treated as unknown. Numeric
literals may adopt the surrounding quantity, allowing ordinary constants such as zero and scalar factors. When adding
a solver path, annotate its storage boundary and important intermediate values so dimensional information is not lost.
