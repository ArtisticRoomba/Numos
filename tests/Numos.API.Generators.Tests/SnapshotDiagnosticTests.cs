using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.Tests;

[TestFixture]
public sealed class SnapshotDiagnosticTests
{
    private static string Source(string live, string snapshot = "[NumosSnapshotOf(typeof(Tank))] internal sealed partial class TankSnapshot;")
    {
        return $$"""
                 using System.Collections.Generic;
                 using Numos.CoreSim.State;

                 namespace MyMod;

                 {{live}}

                 {{snapshot}}
                 """;
    }

    private const string Tank = """
                                internal sealed class Tank
                                {
                                    [NumosSerialize] public float Pressure;
                                    [NumosSerialize] public int[] Cells = new int[4];
                                }
                                """;

    private static string[] Ids(string source, out Compilation output)
    {
        GeneratorHarness.RunSnapshots(out output, out var diagnostics, source);
        return GeneratorHarness.Ids(diagnostics);
    }

    private static string[] Ids(string source) => Ids(source, out _);

    [Test]
    public void ValidSnapshot_CompilesWithoutDiagnostics()
    {
        string[] ids = Ids(Source(Tank), out Compilation output);
        Assert.Multiple(() =>
        {
            Assert.That(ids, Is.Empty);
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        });
    }

    [Test]
    public void StateGenerator_LeavesTheLiveTypeAlone()
    {
        // As a plain serializable type, Tank would need a reader constructor and would be partial.
        string live = """
                      internal sealed class Tank
                      {
                          public Tank(int size) => Cells = new int[size];

                          [NumosSerialize] public int[] Cells;
                          [NumosSerialize] internal int Size => Cells.Length;
                      }
                      """;

        Assert.That(Ids(Source(live)), Is.Empty);
    }

    [Test]
    public void NonPartialSnapshot_ReportsNumosState021()
    {
        Assert.That(
            Ids(Source(Tank, "[NumosSnapshotOf(typeof(Tank))] internal sealed class TankSnapshot { }")),
            Is.EqualTo(new[] { "NUMOSSTATE021" }));
    }

    [TestCase("[NumosSnapshotOf(typeof(Tank))] internal partial struct TankSnapshot;")]
    [TestCase("[NumosSnapshotOf(typeof(Tank))] internal sealed partial record TankSnapshot;")]
    [TestCase("[NumosSnapshotOf(typeof(Tank))] internal sealed partial class TankSnapshot<T>;")]
    [TestCase("[NumosSnapshotOf(typeof(Tank))] internal abstract partial class TankSnapshot;")]
    public void MisshapenSnapshot_ReportsNumosState022(string snapshot)
    {
        Assert.That(Ids(Source(Tank, snapshot)), Is.EqualTo(new[] { "NUMOSSTATE022" }));
    }

    [TestCase("internal sealed class Tank<T> { [NumosSerialize] public float Pressure; }", "typeof(Tank<int>)")]
    [TestCase("internal struct Tank { [NumosSerialize] public float Pressure; }", "typeof(Tank)")]
    [TestCase("internal sealed class Tank;", "typeof(System.Version)")]
    public void InvalidLiveType_ReportsNumosState023(string live, string target)
    {
        Assert.That(
            Ids(Source(live, $"[NumosSnapshotOf({target})] internal sealed partial class TankSnapshot;")),
            Is.EqualTo(new[] { "NUMOSSTATE023" }));
    }

    [Test]
    public void LiveTypeWithoutMarkedMembers_ReportsNumosState024()
    {
        Assert.That(Ids(Source("internal sealed class Tank { public readonly float Pressure; }")), Is.EqualTo(new[] { "NUMOSSTATE024" }));
    }

    [Test]
    public void PrivateLiveMember_ReportsNumosState025()
    {
        Assert.That(
            Ids(Source("internal sealed class Tank { [NumosSerialize] private float _pressure; }")),
            Is.EqualTo(new[] { "NUMOSSTATE025" }));
    }

    [TestCase("[NumosSerialize] public List<int> Cells = [];")]
    [TestCase("[NumosSerialize] public object Payload = new();")]
    [TestCase("[NumosSerialize] public List<int>[] Cells = [];")]
    public void UncopyableLiveMember_ReportsNumosState026(string member)
    {
        Assert.That(Ids(Source($"internal sealed class Tank {{ {member} }}")), Is.EqualTo(new[] { "NUMOSSTATE026" }));
    }

    [Test]
    public void AdapterProperty_MayReturnAnyStateType()
    {
        string live = """
                      internal sealed class Tank
                      {
                          private readonly List<int> _cells = [1, 2];

                          [NumosSerialize("Cells")]
                          internal IReadOnlyList<int> CellsSnapshot
                          {
                              get => _cells.ToArray();
                              set
                              {
                                  _cells.Clear();
                                  _cells.AddRange(value);
                              }
                          }
                      }
                      """;

        string[] ids = Ids(Source(live), out Compilation output);
        Assert.Multiple(() =>
        {
            Assert.That(ids, Is.Empty);
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        });
    }

    [Test]
    public void HandWrittenMemberWithAGeneratedName_ReportsNumosState027()
    {
        Assert.That(
            Ids(Source(Tank, "[NumosSnapshotOf(typeof(Tank))] internal sealed partial class TankSnapshot { public float Pressure => 0f; }")),
            Is.EqualTo(new[] { "NUMOSSTATE027" }));
    }

    [Test]
    public void HandWrittenOwnedMember_ReportsNumosState008()
    {
        Assert.That(
            Ids(Source(Tank, "[NumosSnapshotOf(typeof(Tank))] internal sealed partial class TankSnapshot { internal static void Capture() { } }")),
            Is.EqualTo(new[] { "NUMOSSTATE008" }));
    }

    [TestCase("public long Used;")]
    [TestCase("public readonly int Used;")]
    [TestCase("private int Used;")]
    [TestCase("")]
    public void BadCountMember_ReportsNumosState028(string count)
    {
        string live = $$"""
                        internal sealed class Tank
                        {
                            {{count}}
                            [NumosSerialize(Count = "Used")] public int[] Cells = new int[4];
                        }
                        """;

        Assert.That(Ids(Source(live)), Is.EqualTo(new[] { "NUMOSSTATE028" }));
    }

    [Test]
    public void CountOnAScalar_ReportsNumosState029()
    {
        string live = """
                      internal sealed class Tank
                      {
                          public int Used;
                          [NumosSerialize(Count = nameof(Used))] public float Pressure;
                      }
                      """;

        Assert.That(Ids(Source(live)), Is.EqualTo(new[] { "NUMOSSTATE029" }));
    }

    [Test]
    public void TwoSnapshotsOfOneLiveType_ReportsNumosState030()
    {
        string snapshots = """
                           [NumosSnapshotOf(typeof(Tank))] internal sealed partial class TankSnapshot;
                           [NumosSnapshotOf(typeof(Tank))] internal sealed partial class OtherTankSnapshot;
                           """;

        Assert.That(Ids(Source(Tank, snapshots)), Is.EqualTo(new[] { "NUMOSSTATE030" }));
    }

    [Test]
    public void CountOnAPlainSerializableType_ReportsNumosState031()
    {
        string source = """
                        using Numos.CoreSim.State;

                        namespace MyMod;

                        public sealed partial class Settings
                        {
                            public int Used { get; set; }
                            [NumosSerialize(Count = nameof(Used))] public int[] Cells { get; set; } = [];
                        }
                        """;

        Assert.That(Ids(source), Does.Contain("NUMOSSTATE031"));
    }

    [Test]
    public void LiveMemberWhoseTypeHasASnapshot_IsCapturedAsThatSnapshot()
    {
        string live = """
                      internal sealed class Valve { [NumosSerialize] public bool Open; }

                      internal sealed class Tank
                      {
                          [NumosSerialize] public Valve Main = new();
                          [NumosSerialize] public Valve[] Spares = [];
                      }

                      [NumosSnapshotOf(typeof(Valve))] internal sealed partial class ValveSnapshot;
                      """;

        string[] ids = Ids(Source(live), out Compilation output);
        Assert.Multiple(() =>
        {
            Assert.That(ids, Is.Empty);
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
            INamedTypeSymbol snapshot = output.GetTypeByMetadataName("MyMod.TankSnapshot")!;
            Assert.That(snapshot.GetMembers("Main").OfType<IPropertySymbol>().Single().Type.Name, Is.EqualTo("ValveSnapshot"));
            Assert.That(
                snapshot.GetMembers("RestoreInto").Single().GetAttributes().Select(static attribute => attribute.AttributeClass!.Name),
                Does.Contain("ObsoleteAttribute"),
                "a list of live children makes it capture-only");
        });
    }

    /// <summary>
    ///     Compiles <paramref name="source" /> with and without a call to <c>RestoreInto</c>, and returns the errors the
    ///     call adds.
    /// </summary>
    private static Diagnostic[] RestoreIntoErrors(string source, string snapshot, string live)
    {
        string caller = $"internal static class Caller {{ public static void Restore({snapshot} snapshot, {live} live) => snapshot.RestoreInto(live); }}";
        string[] ids = Ids(source, out Compilation without);
        Assert.That(ids, Is.Empty);
        Assert.That(GeneratorHarness.Errors(without), Is.Empty);

        ids = Ids(source + "\n" + caller, out Compilation with);
        Assert.That(ids, Is.Empty);
        return GeneratorHarness.Errors(with);
    }

    [Test]
    public void RestoreIntoOnAListOfLiveChildren_FailsTheBuildAndNamesTheMember()
    {
        string live = """
                      internal sealed class Child { [NumosSerialize] public bool Open; }

                      internal sealed class Parent
                      {
                          [NumosSerialize] public float Pressure;
                          [NumosSerialize] public Child[] Children = [];
                      }

                      [NumosSnapshotOf(typeof(Child))] internal sealed partial class ChildSnapshot;
                      """;

        string source = Source(live, "[NumosSnapshotOf(typeof(Parent))] internal sealed partial class ParentSnapshot;");
        Diagnostic[] errors = RestoreIntoErrors(source, "ParentSnapshot", "Parent");

        Assert.Multiple(() =>
        {
            Assert.That(GeneratorHarness.Ids(errors), Is.EqualTo(new[] { "CS0619" }));
            Assert.That(
                errors[0].GetMessage(),
                Does.Contain("MyMod.ParentSnapshot is capture-only: 'MyMod.Parent.Children' is a list of live objects"));
        });
    }

    [Test]
    public void RestoreIntoWithAGetOnlyAdapter_NamesTheMissingSetter()
    {
        string live = """
                      internal sealed class Tank
                      {
                          private readonly List<int> _cells = [1, 2];

                          [NumosSerialize] public float Pressure;
                          [NumosSerialize("Cells")] internal IReadOnlyList<int> CellsSnapshot => _cells.ToArray();
                      }
                      """;

        Diagnostic[] errors = RestoreIntoErrors(Source(live), "TankSnapshot", "Tank");

        Assert.Multiple(() =>
        {
            Assert.That(GeneratorHarness.Ids(errors), Is.EqualTo(new[] { "CS0619" }));
            Assert.That(errors[0].GetMessage(), Does.Contain("'MyMod.Tank.CellsSnapshot' has no setter the snapshot can call"));
        });
    }

    [Test]
    public void RestoreIntoThroughACaptureOnlyNestedSnapshot_NamesTheNestedSnapshot()
    {
        string live = """
                      internal sealed class Valve
                      {
                          [NumosSerialize] public bool Open;
                          [NumosSerialize] internal IReadOnlyList<int> Positions => new[] { 1 };
                      }

                      internal sealed class Tank
                      {
                          [NumosSerialize] public float Pressure;
                          [NumosSerialize] public Valve Main = new();
                      }

                      [NumosSnapshotOf(typeof(Valve))] internal sealed partial class ValveSnapshot;
                      """;

        Diagnostic[] errors = RestoreIntoErrors(Source(live), "TankSnapshot", "Tank");

        Assert.Multiple(() =>
        {
            Assert.That(GeneratorHarness.Ids(errors), Is.EqualTo(new[] { "CS0619" }));
            Assert.That(
                errors[0].GetMessage(),
                Does.Contain("'MyMod.Tank.Main' is restored through 'MyMod.ValveSnapshot', which is capture-only"));
        });
    }

    [Test]
    public void RestoreIntoOnARestorableSnapshot_Compiles()
    {
        Assert.That(RestoreIntoErrors(Source(Tank), "TankSnapshot", "Tank"), Is.Empty);
    }

    [Test]
    public void UnmarkedMutableLiveField_ReportsNumosState016AndStillGenerates()
    {
        string live = """
                      internal sealed class Tank
                      {
                          [NumosSerialize] public float Pressure;
                          public int Leaks;
                      }
                      """;

        GeneratorRunResult[] results = GeneratorHarness.RunSnapshots(out Compilation output, out var diagnostics, Source(live));
        Assert.Multiple(() =>
        {
            Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE016" }));
            Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.Tank.Leaks'"));
            Assert.That(
                results.SelectMany(static result => result.GeneratedSources).Select(static source => source.HintName),
                Does.Contain("MyMod.TankSnapshot.NumosSnapshot.g.cs"));
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        });
    }

    [TestCase("[NumosIgnore] public int Leaks;")]
    [TestCase("[NumosIgnore] public int Leaks { get; set; }")]
    [TestCase("public readonly int Leaks;")]
    [TestCase("public int Leaks { get; }")]
    [TestCase("public static int Leaks;")]
    [TestCase("public int Leaks { get => 0; set { } }")]
    public void ExemptUnmarkedLiveMember_DoesNotReportNumosState016(string member)
    {
        string live = $$"""
                        internal sealed class Tank
                        {
                            [NumosSerialize] public float Pressure;
                            {{member}}
                        }
                        """;

        string[] ids = Ids(Source(live), out Compilation output);
        Assert.Multiple(() =>
        {
            Assert.That(ids, Is.Empty);
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        });
    }

    [Test]
    public void CountMemberOfALiveType_DoesNotReportNumosState016()
    {
        string live = """
                      internal sealed class Tank
                      {
                          public int Used;
                          [NumosSerialize(Count = nameof(Used))] public ushort[] Active = new ushort[4];
                      }
                      """;

        Assert.That(Ids(Source(live)), Is.Empty);
    }

    [Test]
    public void FlatArrayMembers_GetALengthCheckAndAByteTotal()
    {
        string live = """
                      internal sealed class Grid
                      {
                          public int Used;

                          [NumosSerialize] public Numos.Collections.FlatArray<float> Pressures;
                          [NumosSerialize] public Numos.Collections.FlatArray<int> Rooms;
                          [NumosSerialize(Count = nameof(Used))] public ushort[] Active = new ushort[4];
                      }
                      """;

        string[] ids = Ids(Source(live, "[NumosSnapshotOf(typeof(Grid))] internal sealed partial class GridSnapshot;"), out Compilation output);
        Assert.Multiple(() =>
        {
            Assert.That(ids, Is.Empty);
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
            INamedTypeSymbol snapshot = output.GetTypeByMetadataName("MyMod.GridSnapshot")!;
            Assert.That(snapshot.GetMembers("CheckFlatArrayLengths"), Has.Length.EqualTo(1));
            Assert.That(snapshot.GetMembers("FlatArrayPayloadBytes"), Has.Length.EqualTo(1));
        });
    }

    [Test]
    public void SnapshotWithoutFlatArrayMembers_GetsNeitherFlatArrayMember()
    {
        Ids(Source(Tank), out Compilation output);
        INamedTypeSymbol snapshot = output.GetTypeByMetadataName("MyMod.TankSnapshot")!;
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.GetMembers("CheckFlatArrayLengths"), Is.Empty);
            Assert.That(snapshot.GetMembers("FlatArrayPayloadBytes"), Is.Empty);
        });
    }

    [Test]
    public void PartialDeclaration_ChoosesTheGeneratedMembersVisibility()
    {
        string live = """
                      public sealed class Tank
                      {
                          [NumosSerialize("Pressure")] internal float PressureState => 1f;
                      }
                      """;

        string snapshot = """
                          [NumosSnapshotOf(typeof(Tank))]
                          public sealed partial class TankSnapshot
                          {
                              /// <summary>The pressure.</summary>
                              public partial float Pressure { get; }
                          }
                          """;

        string[] ids = Ids(Source(live, snapshot), out Compilation output);
        Assert.Multiple(() =>
        {
            Assert.That(ids, Is.Empty);
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
            IPropertySymbol pressure = output.GetTypeByMetadataName("MyMod.TankSnapshot")!
                .GetMembers("Pressure").OfType<IPropertySymbol>().Single();

            Assert.That(pressure.DeclaredAccessibility, Is.EqualTo(Accessibility.Public));
        });
    }

    [Test]
    public void PartialDeclarationWithASetter_ReportsNumosState027()
    {
        string snapshot = """
                          [NumosSnapshotOf(typeof(Tank))]
                          internal sealed partial class TankSnapshot
                          {
                              public partial float Pressure { get; set; }
                          }
                          """;

        Assert.That(Ids(Source(Tank, snapshot)), Does.Contain("NUMOSSTATE027"));
    }

    [Test]
    public void FileLocalSnapshot_ReportsNumosState032WithoutCrashing()
    {
        string source = Source(Tank, "[NumosSnapshotOf(typeof(Tank))] file sealed partial class TankSnapshot;");
        GeneratorRunResult[] results = GeneratorHarness.RunSnapshots(out _, out var diagnostics, source);

        Assert.Multiple(() =>
        {
            Assert.That(results.Select(static result => result.Exception), Has.All.Null);
            Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE032" }));
            Assert.That(results.SelectMany(static result => result.GeneratedSources), Is.Empty);
        });
    }

    [Test]
    public void SnapshotNestedInAFileLocalType_ReportsNumosState032WithoutCrashing()
    {
        string snapshot = """
                          file partial class Holder
                          {
                              [NumosSnapshotOf(typeof(Tank))] internal sealed partial class TankSnapshot;
                          }
                          """;

        GeneratorRunResult[] results = GeneratorHarness.RunSnapshots(out _, out var diagnostics, Source(Tank, snapshot));

        Assert.Multiple(() =>
        {
            Assert.That(results.Select(static result => result.Exception), Has.All.Null);
            Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE032" }));
        });
    }

    [Test]
    public void FileLocalLiveType_ReportsNumosState023WithoutCrashing()
    {
        string live = """
                      file sealed class Tank
                      {
                          [NumosSerialize] public float Pressure;
                      }
                      """;

        GeneratorRunResult[] results = GeneratorHarness.RunSnapshots(out _, out var diagnostics, Source(live));

        Assert.Multiple(() =>
        {
            Assert.That(results.Select(static result => result.Exception), Has.All.Null);
            Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE023" }));
            Assert.That(results.SelectMany(static result => result.GeneratedSources), Is.Empty);
        });
    }

    [Test]
    public void LiveTypeNestedInAFileLocalType_ReportsNumosState023()
    {
        string live = """
                      file static class Holder
                      {
                          public sealed class Tank
                          {
                              [NumosSerialize] public float Pressure;
                          }
                      }
                      """;

        string snapshot = "[NumosSnapshotOf(typeof(Holder.Tank))] internal sealed partial class TankSnapshot;";
        Assert.That(Ids(Source(live, snapshot)), Is.EqualTo(new[] { "NUMOSSTATE023" }));
    }

    [Test]
    public void FieldTargetOnLiveProperty_ReportsNumosState033Only()
    {
        string live = """
                      internal sealed class Tank
                      {
                          [NumosSerialize] public float Pressure;
                          [field: NumosSerialize] public int Extra { get; set; }
                      }
                      """;

        Assert.That(Ids(Source(live)), Is.EqualTo(new[] { "NUMOSSTATE033" }));
    }

    [Test]
    public void FieldTargetOnTheOnlyLiveMark_DoesNotAlsoReportNumosState024()
    {
        string live = "internal sealed class Tank { [field: NumosSerialize] public int Extra { get; set; } }";
        Assert.That(Ids(Source(live)), Is.EqualTo(new[] { "NUMOSSTATE033" }));
    }

    [Test]
    public void FieldTargetOnSnapshotMember_ReportsNumosState033()
    {
        string snapshot = """
                          [NumosSnapshotOf(typeof(Tank))]
                          internal sealed partial class TankSnapshot
                          {
                              [field: NumosSerialize] public int Extra { get; set; }
                          }
                          """;

        Assert.That(Ids(Source(Tank, snapshot)), Is.EqualTo(new[] { "NUMOSSTATE033" }));
    }

    [Test]
    public void MarkedIndexerOnLiveType_ReportsNumosState034InsteadOfSkippingTheSnapshot()
    {
        string live = """
                      internal sealed class Tank
                      {
                          [NumosSerialize] public float Pressure;
                          [NumosSerialize] public float this[int index] { get => 0f; set { } }
                      }
                      """;

        GeneratorRunResult[] results = GeneratorHarness.RunSnapshots(out _, out var diagnostics, Source(live));
        Assert.Multiple(() =>
        {
            Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE034" }));
            Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.Tank.this[int]'"));
            Assert.That(results.SelectMany(static result => result.GeneratedSources), Is.Empty);
        });
    }

    [Test]
    public void MarkedIndexerAsTheOnlyLiveMark_DoesNotAlsoReportNumosState024()
    {
        string live = "internal sealed class Tank { [NumosSerialize] public float this[int index] => 0f; }";
        Assert.That(Ids(Source(live)), Is.EqualTo(new[] { "NUMOSSTATE034" }));
    }

    [Test]
    public void MarkedIndexerOnSnapshotMember_ReportsNumosState034()
    {
        string snapshot = """
                          [NumosSnapshotOf(typeof(Tank))]
                          internal sealed partial class TankSnapshot
                          {
                              [NumosSerialize] public int this[int index] { get => 0; set { } }
                          }
                          """;

        Assert.That(Ids(Source(Tank, snapshot)), Is.EqualTo(new[] { "NUMOSSTATE034" }));
    }
}
