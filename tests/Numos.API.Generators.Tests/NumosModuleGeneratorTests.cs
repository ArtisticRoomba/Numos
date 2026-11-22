using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Numos.API.Generators.ChunkData;
using Numos.API.Generators.Module;
using Numos.API.Generators.Solver;
using Numos.API.Generators.State;

namespace Numos.API.Generators.Tests;

[TestFixture]
public sealed class NumosModuleGeneratorTests
{
    private const string ChunkSource = """
                                       using Numos.API;

                                       namespace MyMod;

                                       public static partial class FireData
                                       {
                                           [ChunkData("module-tests/fire/exposure")]
                                           public static partial ChunkField<float> Exposure { get; }
                                       }
                                       """;

    private const string ConfigurationSource = """
                                               using Numos.CoreSim;
                                               using Numos.CoreSim.State;

                                               namespace MyMod;

                                               public sealed partial class Smoke : IAtmosSolverConfiguration
                                               {
                                                   public string Key => "module-tests/smoke";
                                                   [NumosSerialize] public float Density { get; init; }
                                               }
                                               """;

    private const string SolverSource = """
                                        using Numos.API;

                                        namespace MyMod;

                                        [AtmosSolver("module-tests/fire", RunsPerSecond = 20)]
                                        public sealed partial class FireSolver : IAtmosWorldSolver
                                        {
                                            public void Solve(AtmosWorldSolverContext context)
                                            {
                                            }
                                        }
                                        """;

    private const string PlainSource = """
                                       namespace MyMod;

                                       public sealed class Plain
                                       {
                                           public int Value;
                                       }
                                       """;

    [Test]
    public void ChunkDataField_GetsNumosModuleInAssemblyNamespace()
    {
        Compilation output = Run("My.Fire-Mod", ChunkSource);

        Assert.That(ModuleTypes(output), Is.EqualTo(new[] { "My.Fire_Mod.NumosModule" }));
        Assert.That(RoslynTestSupport.Errors(output), Is.Empty);
    }

    [Test]
    public void ChunkComponentOnly_GetsNumosModule()
    {
        const string source = """
                              using Numos.API;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              [ChunkComponent("module-tests/component")]
                              public partial struct Fire
                              {
                                  [NumosSerialize] public float Exposure;
                              }
                              """;

        Compilation output = Run("MyMod", source);

        Assert.That(ModuleTypes(output), Is.EqualTo(new[] { "MyMod.NumosModule" }));
        Assert.That(RoslynTestSupport.Errors(output), Is.Empty);
    }

    [Test]
    public void SolverConfigurationOnly_GetsNumosModule()
    {
        Compilation output = Run("MyMod", ConfigurationSource);

        Assert.That(ModuleTypes(output), Is.EqualTo(new[] { "MyMod.NumosModule" }));
        Assert.That(RoslynTestSupport.Errors(output), Is.Empty);
    }

    [Test]
    public void ChunkDataAndSolverConfiguration_GetOneNumosModule()
    {
        Compilation output = Run("MyMod", ChunkSource, ConfigurationSource);

        Assert.That(ModuleTypes(output), Is.EqualTo(new[] { "MyMod.NumosModule" }));
        Assert.That(RoslynTestSupport.Errors(output), Is.Empty);
    }

    [Test]
    public void AtmosSolverOnly_GetsNumosModule()
    {
        Compilation output = Run("MyMod", SolverSource);

        Assert.That(ModuleTypes(output), Is.EqualTo(new[] { "MyMod.NumosModule" }));
        Assert.That(RoslynTestSupport.Errors(output), Is.Empty);
    }

    [Test]
    public void SimulationComponentOnly_GetsNumosModule()
    {
        const string source = """
                              using Numos.API;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              [SimulationComponent("module-tests/pipes")]
                              public sealed partial class Pipes
                              {
                                  [NumosSerialize] public int NextId;
                              }
                              """;

        Compilation output = Run("MyMod", source);

        Assert.That(ModuleTypes(output), Is.EqualTo(new[] { "MyMod.NumosModule" }));
        Assert.That(RoslynTestSupport.Errors(output), Is.Empty);
    }

    [Test]
    public void NoRegistrations_GetsNoNumosModule()
    {
        Compilation output = Run("MyMod", PlainSource);

        Assert.That(ModuleTypes(output), Is.Empty);
    }

    [Test]
    public void ClassWithoutSolverConfiguration_GetsNoNumosModule()
    {
        const string source = """
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public sealed partial class NotAConfiguration
                              {
                                  [NumosSerialize] public float Value { get; set; }
                              }
                              """;

        Compilation output = Run("MyMod", source);

        Assert.That(ModuleTypes(output), Is.Empty);
    }

    [Test]
    public void AssemblyDeclaringTheConfigurationContract_GetsNoNumosModule()
    {
        const string source = """
                              namespace Numos.CoreSim
                              {
                                  public interface IAtmosSolverConfiguration
                                  {
                                      string Key { get; }
                                  }
                              }

                              namespace Numos.CoreSim.State
                              {
                                  [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Field)]
                                  public sealed class NumosSerializeAttribute : System.Attribute { }
                              }

                              namespace MyMod
                              {
                                  public sealed class Smoke : Numos.CoreSim.IAtmosSolverConfiguration
                                  {
                                      public string Key => "module-tests/contract";
                                      [Numos.CoreSim.State.NumosSerialize] public float Density { get; set; }
                                  }
                              }
                              """;

        CSharpParseOptions parseOptions = GeneratorHarness.ParseOptions();
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ContractAssembly",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            RoslynTestSupport.FrameworkReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new NumosModuleGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);

        driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out ImmutableArray<Diagnostic> _);

        Assert.That(ModuleTypes(output), Is.Empty);
    }

    [Test]
    public void UserDeclaredNumosModule_SuppressesTheGeneratedType()
    {
        const string source = """
                              namespace MyMod;

                              public static class NumosModule
                              {
                                  public static void EnsureRegistered() { }
                              }
                              """;

        Compilation output = Run("MyMod", ChunkSource, source);

        Assert.That(ModuleTypes(output), Is.EqualTo(new[] { "MyMod.NumosModule" }));
        Assert.That(RoslynTestSupport.Errors(output), Is.Empty);
    }

    [TestCase("", "NumosGenerated")]
    [TestCase(null, "NumosGenerated")]
    [TestCase("MyMod", "MyMod")]
    [TestCase("My.Mod.Core", "My.Mod.Core")]
    [TestCase("My-Mod", "My_Mod")]
    [TestCase("1Mod.2", "_1Mod._2")]
    [TestCase("My.namespace", "My.@namespace")]
    [TestCase("My..Mod.", "My.Mod")]
    public void SanitizeNamespace_ProducesAValidNamespace(string? assemblyName, string expected)
    {
        Assert.That(NumosModuleGenerator.SanitizeNamespace(assemblyName), Is.EqualTo(expected));
    }

    [Test]
    public void ReRunWithUnrelatedEdit_ReusesTheModuleSource()
    {
        CSharpParseOptions parseOptions = GeneratorHarness.ParseOptions();
        CSharpCompilation compilation = Compilation("MyMod", parseOptions, ChunkSource, PlainSource);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new NumosModuleGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, true));

        driver = driver.RunGenerators(compilation);
        CSharpCompilation edited = compilation.ReplaceSyntaxTree(
            compilation.SyntaxTrees.Last(),
            CSharpSyntaxTree.ParseText(PlainSource + "\n// edit", parseOptions, path: "Source1.cs"));

        driver = driver.RunGenerators(edited);
        GeneratorRunResult result = driver.GetRunResult().Results.Single();

        Assert.That(
            result.TrackedSteps[NumosModuleGenerator.NamespaceStepName].SelectMany(static step => step.Outputs)
                .Select(static output => output.Reason),
            Is.All.Matches<IncrementalStepRunReason>(static reason =>
                reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));
    }

    private static CSharpCompilation Compilation(string assemblyName, CSharpParseOptions parseOptions, params string[] sources)
    {
        CSharpCompilation compilation = GeneratorHarness.CreateCompilation(parseOptions, sources);
        return compilation.WithAssemblyName(assemblyName);
    }

    private static Compilation Run(string assemblyName, params string[] sources)
    {
        CSharpParseOptions parseOptions = GeneratorHarness.ParseOptions();
        CSharpCompilation compilation = Compilation(assemblyName, parseOptions, sources);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [
                new ChunkDataGenerator().AsSourceGenerator(),
                new StateGenerator().AsSourceGenerator(),
                new AtmosSolverGenerator().AsSourceGenerator(),
                new NumosModuleGenerator().AsSourceGenerator(),
            ],
            parseOptions: parseOptions);

        driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out ImmutableArray<Diagnostic> _);
        return output;
    }

    private static string[] ModuleTypes(Compilation compilation)
    {
        return compilation.GetSymbolsWithName(NumosModuleGenerator.TypeName, SymbolFilter.Type)
            .Select(static symbol => symbol.ToDisplayString())
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
    }
}
