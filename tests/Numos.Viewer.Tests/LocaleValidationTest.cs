using System.Globalization;
using System.Text;
using Linguini.Bundle.Builder;
using Linguini.Syntax.Parser;

namespace Numos.Viewer.Tests;

[TestFixture]
public class LocaleValidationTest
{
    [Test]
    public void Locale_Validation()
    {
        var cultures = Directory.EnumerateDirectories(Locale.LocaleDirectory);
        foreach (var culture in cultures)
        {
            var cultureName = Path.GetFileName(culture);
            var files = Directory.GetFiles(
                culture, "*.ftl", SearchOption.TopDirectoryOnly);

            if (files.Length == 0)
            {
                throw new FileNotFoundException(
                    $"No .ftl files were found for culture " +
                    $"'{cultureName}' in '{culture}'.");
            }
        
            Array.Sort(files, StringComparer.Ordinal);

            foreach (var file in files)
            {
                var errors = LinguiniParser.FromFile(file).Parse().Errors;
                var errorString = new StringBuilder();
                foreach (var error in errors)
                {
                    errorString.AppendLine($"Error: {error.Message}, File: {Path.GetFileName(file)}, Row: {error.Row}, Mark range: {error.Slice?.ToString()}, Kind: {error.Kind.ToString()}");
                }
                
                if (errors.Count > 0)
                    Assert.Fail(errorString.ToString());
            }
            
            
        }
    }
}