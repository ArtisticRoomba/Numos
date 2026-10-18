using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.Tests;

[TestFixture]
public sealed class SolverDependencyDiagnosticTests
{
    private static string Solver(string members, string declaration = "public sealed partial class Solver : IAtmosWorldSolver")
    {
        return $$"""
                 using Numos.API;

                 namespace MyMod;

                 public interface IService
                 {
                 }

                 {{declaration}}
                 {
                     {{members}}

                     public void Solve(AtmosWorldSolverContext context)
                     {
                     }
                 }
                 """;
    }

    private static string[] GeneratorIds(params string[] sources)
    {
        GeneratorHarness.RunDependencies(out _, out var diagnostics, sources);
        return GeneratorHarness.Ids(diagnostics);
    }

    [Test]
    public void NonPartialType_ReportsNumosDep001AndGeneratesNothing()
    {
        GeneratorRunResult result = GeneratorHarness.RunDependencies(out _, out var diagnostics,
            Solver("[SolverDependency] private partial IService Service { get; }",
                "public sealed class Solver : IAtmosWorldSolver"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSDEP001" }));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void NonPartialContainingType_ReportsNumosDep001()
    {
        const string source = """
                              namespace MyMod;

                              public class Outer
                              {
                                  public sealed partial class Solver : Numos.API.IAtmosWorldSolver
                                  {
                                      [Numos.API.SolverDependency]
                                      private partial System.IServiceProvider Provider { get; }

                                      public void Solve(Numos.API.AtmosWorldSolverContext context)
                                      {
                                      }
                                  }
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunDependencies(out _, out var diagnostics, source);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSDEP001" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.Outer'"));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void Struct_ReportsNumosDep002()
    {
        const string source = """
                              namespace MyMod;

                              public partial struct NotAClass
                              {
                                  [Numos.API.SolverDependency]
                                  private partial System.IServiceProvider Provider { get; }
                              }
                              """;

        Assert.That(GeneratorIds(source), Is.EqualTo(new[] { "NUMOSDEP002" }));
    }

    [Test]
    public void PropertyWithSetter_ReportsNumosDep003()
    {
        Assert.That(GeneratorIds(Solver("[SolverDependency] private partial IService Service { get; set; }")),
            Is.EqualTo(new[] { "NUMOSDEP003" }));
    }

    [Test]
    public void PropertyWithInit_ReportsNumosDep003()
    {
        Assert.That(GeneratorIds(Solver("[SolverDependency] private partial IService Service { get; init; }")),
            Is.EqualTo(new[] { "NUMOSDEP003" }));
    }

    [Test]
    public void NonPartialProperty_ReportsNumosDep003()
    {
        Assert.That(GeneratorIds(Solver("[SolverDependency] private IService Service { get; } = null!;")),
            Is.EqualTo(new[] { "NUMOSDEP003" }));
    }

    [Test]
    public void StaticProperty_ReportsNumosDep004()
    {
        Assert.That(GeneratorIds(Solver("[SolverDependency] private static partial IService Service { get; }")),
            Is.EqualTo(new[] { "NUMOSDEP004" }));
    }

    [Test]
    public void ValueTypeProperty_ReportsNumosDep005()
    {
        Assert.That(GeneratorIds(Solver("[SolverDependency] private partial int Count { get; }")),
            Is.EqualTo(new[] { "NUMOSDEP005" }));
    }

    [Test]
    public void UnconstrainedTypeParameter_ReportsNumosDep005()
    {
        Assert.That(GeneratorIds(Solver("[SolverDependency] private partial T Value { get; }",
                "public sealed partial class Solver<T> : IAtmosWorldSolver")),
            Is.EqualTo(new[] { "NUMOSDEP005" }));
    }

    [Test]
    public void OptionalWithoutNullableType_WarnsNumosDep006AndStillCompiles()
    {
        GeneratorHarness.RunDependencies(out Compilation output, out var diagnostics,
            Solver("[SolverDependency(Optional = true)] private partial IService Service { get; }"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSDEP006" }));
        Assert.That(diagnostics[0].Severity, Is.EqualTo(DiagnosticSeverity.Warning));
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        Assert.That(output.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Warning), Is.Empty);
    }

    [Test]
    public void RequiredWithNullableType_WarnsNumosDep006AndStillCompiles()
    {
        GeneratorHarness.RunDependencies(out Compilation output, out var diagnostics,
            Solver("[SolverDependency] private partial IService? Service { get; }"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSDEP006" }));
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
    }

    [Test]
    public void NullableDisabled_DoesNotWarnNumosDep006()
    {
        Assert.That(GeneratorIds("#nullable disable\n" +
                                 Solver("[SolverDependency(Optional = true)] private partial IService Service { get; }")),
            Is.Empty);
    }

    [Test]
    public void TypeWithoutWorldSolver_WarnsNumosDep007Once()
    {
        const string source = """
                              namespace MyMod;

                              public sealed partial class NotASolver
                              {
                                  [Numos.API.SolverDependency]
                                  private partial System.IServiceProvider First { get; }

                                  [Numos.API.SolverDependency]
                                  private partial System.IServiceProvider Second { get; }
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunDependencies(out Compilation output, out var diagnostics,
            source);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSDEP007" }));
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        Assert.That(result.GeneratedSources, Has.Length.EqualTo(1));
    }

    [Test]
    public void AbstractTypeWithoutWorldSolver_DoesNotWarn()
    {
        const string source = """
                              namespace MyMod;

                              public abstract partial class SharedDependencies
                              {
                                  [Numos.API.SolverDependency]
                                  protected partial System.IServiceProvider Provider { get; }
                              }
                              """;

        Assert.That(GeneratorIds(source), Is.Empty);
    }

    [Test]
    public void BaseImplementingInterfaceWithoutHook_ReportsNumosDep008()
    {
        const string source = """
                              using Numos.API;

                              namespace MyMod;

                              public abstract class Base : IAtmosWorldSolver, IAtmosSolverDependencies
                              {
                                  public void InjectDependencies(AtmosSolverDependencyResolver resolver)
                                  {
                                  }

                                  public void Solve(AtmosWorldSolverContext context)
                                  {
                                  }
                              }

                              public sealed partial class Derived : Base
                              {
                                  [SolverDependency]
                                  private partial System.IServiceProvider Provider { get; }
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunDependencies(out _, out var diagnostics, source);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSDEP008" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.Base'"));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void SealedBaseHook_ReportsNumosDep008()
    {
        const string source = """
                              using Numos.API;

                              namespace MyMod;

                              public abstract class Root : IAtmosWorldSolver, IAtmosSolverDependencies
                              {
                                  void IAtmosSolverDependencies.InjectDependencies(AtmosSolverDependencyResolver resolver)
                                  {
                                      InjectSolverDependencies(resolver);
                                  }

                                  protected virtual void InjectSolverDependencies(AtmosSolverDependencyResolver resolver)
                                  {
                                  }

                                  public void Solve(AtmosWorldSolverContext context)
                                  {
                                  }
                              }

                              public abstract class Middle : Root
                              {
                                  protected sealed override void InjectSolverDependencies(AtmosSolverDependencyResolver resolver)
                                  {
                                  }
                              }

                              public sealed partial class Derived : Middle
                              {
                                  [SolverDependency]
                                  private partial System.IServiceProvider Provider { get; }
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunDependencies(out _, out var diagnostics, source);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSDEP008" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.Middle'"));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void HandWrittenHookOnSameType_ReportsNumosDep009()
    {
        Assert.That(GeneratorIds(Solver("""
                                        [SolverDependency] private partial IService Service { get; }

                                        private void InjectSolverDependencies(AtmosSolverDependencyResolver resolver)
                                        {
                                        }
                                        """)),
            Is.EqualTo(new[] { "NUMOSDEP009" }));
    }

    [Test]
    public void HandWrittenInterfaceOnSameType_ReportsNumosDep009()
    {
        Assert.That(GeneratorIds(Solver("""
                                        [SolverDependency] private partial IService Service { get; }

                                        void IAtmosSolverDependencies.InjectDependencies(AtmosSolverDependencyResolver resolver)
                                        {
                                        }
                                        """,
                "public sealed partial class Solver : IAtmosSolverDependencies, IAtmosWorldSolver")),
            Is.EqualTo(new[] { "NUMOSDEP009" }));
    }

    [Test]
    public void InvalidProperty_DoesNotBlockValidSiblings()
    {
        GeneratorRunResult result = GeneratorHarness.RunDependencies(out _, out var diagnostics,
            Solver("""
                   [SolverDependency] private partial IService Good { get; }
                   [SolverDependency] private static partial IService Bad { get; }
                   """));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSDEP004" }));
        string source = result.GeneratedSources.Single().SourceText.ToString();
        Assert.That(source, Does.Contain("__numosDependency_Good"));
        Assert.That(source, Does.Not.Contain("__numosDependency_Bad"));
    }
}
