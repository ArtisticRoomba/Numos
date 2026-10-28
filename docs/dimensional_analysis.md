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

`[UnitConversions]` on `Numos.CoreSim.UnitConversions` puts the generated conversions in that public class. Consumers
can call them at their input boundaries, for example `UnitConversions.FromKilojoulePerMole(483.6f)`.

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
values when you add a solver path. A cast like `(Pascal)1f` on a literal adopts the surrounding quantity, so it does
not trigger a diagnostic. Use typed variables or parameters when you test a deliberate mismatch.

Vector kernels put the alias in the lane type. A `Vector<Pascal>` holds a pressure in every lane, and a load from a
`Pascal[]` keeps it. Masks, and hoisted constants such as `Vector<float> zero` that meet several quantities, stay
unannotated. A shared spill buffer stays a plain `Span<float>` with QNT002 suppressed around the stores into it.
