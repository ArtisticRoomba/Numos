using System.Globalization;
using Linguini.Bundle.Resolver;
using Linguini.Shared.Types;
using Linguini.Shared.Types.Bundle;

namespace Numos.LocaleLint;

/// <summary>
///     Finds the cardinal plural categories a culture produces, using the rules Linguini applies at run time.
/// </summary>
internal static class Plurals
{
    private const int SampleCount = 1001;
    /// <summary>
    ///     The CLDR category names a select variant can use as a key.
    /// </summary>
    public readonly static IReadOnlySet<string> CategoryNames =
        new HashSet<string>(StringComparer.Ordinal) { "zero", "one", "two", "few", "many", "other" };

    /// <summary>
    ///     Returns the categories that the integers 0 to 1000 map to for a culture.
    /// </summary>
    /// <remarks>
    ///     Fractions can add <c>other</c> for some cultures. The sample covers integers only, because the viewer
    ///     selects on counts.
    /// </remarks>
    public static IReadOnlySet<string> IntegerCategories(CultureInfo culture)
    {
        var categories = new SortedSet<string>(StringComparer.Ordinal);
        for (int n = 0; n < SampleCount; n++)
        {
            var category = PluralRules.GetPluralCategory(culture, RuleType.Cardinal, (FluentNumber)n);
            categories.Add(category.ToString().ToLowerInvariant());
        }

        return categories;
    }
}