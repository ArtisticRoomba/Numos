# Dimensional analysis

Numos catches unit mistakes at build time with [Magnitudo](https://github.com/ArtisticRoomba/Magnitudo). Magnitudo is
an external library, published on nuget.org as `Magnitudo` and `Magnitudo.Analyzers`. Adding a pressure to a
temperature in solver code is a compile error, not a wrong number found in a replay diff.

The analysis runs at compile time, so it keeps Native AOT compatibility. A quantity alias such as `Kelvin` or `Pascal`
is a C# `using` alias for a primitive. In CLR metadata `Kelvin` is `System.Single`. Arrays stay primitive arrays, and
arithmetic stays ordinary primitive arithmetic, so vectorized code is unchanged.

Magnitudo's own docs cover the catalog format, the full rules, and the `QNT000` to `QNT004` diagnostics. This page
covers only how Numos uses it.

## How Numos uses Magnitudo

`Numos.CoreSim` references both packages. `Magnitudo.Analyzers` reads the catalog at
`src/Numos.CoreSim/Numos.CoreSim.magnitudo`, which the project includes as an `AdditionalFiles` item. The catalog
declares each alias (`Kelvin`, `Pascal`, `Mole`, and their 64-bit variants) and the external unit conversions.

The generated conversions in `Magnitudo.Generated.UnitConversions` are `internal` to `Numos.CoreSim`. Other assemblies
cannot call them. Pass plain SI values across assembly boundaries.

To add a quantity, add a line to the catalog. Use the existing base dimensions where possible. A domain can add a new
base dimension by name.

## Annotating code

Use a quantity alias for locals, parameters, return types, and element types in solver code:

```csharp
internal static Pascal CalculatePressure(
    Mole amount,
    Kelvin temperature,
    PascalPerMoleKelvin coefficient)
{
    return amount * temperature * coefficient;
}
```

Use `[Quantity("stableId")]` on public scalar declarations. Use `[ElementQuantity("stableId")]` on public arrays and
indexable storage. The aliases are erased from CLR signatures, and the attributes keep the quantity visible to
analyzers in other assemblies.

The analyzer treats unannotated numeric values as unknown. Annotate storage boundaries and important intermediate
values when you add a solver path. Two cases catch people out:

- The analyzer does not follow an element quantity through a nullable array such as `Mole[]?`. Copy the array to a
  non-null local first.
- A cast like `(Pascal)1f` on a literal adopts the surrounding quantity, so it does not trigger a diagnostic. Use
  typed variables or parameters when you test a deliberate mismatch.
