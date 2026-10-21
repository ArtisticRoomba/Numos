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
        Assert.That(Ids(Source("internal sealed class Tank { public float Pressure; }")), Is.EqualTo(new[] { "NUMOSSTATE024" }));
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
            Assert.That(snapshot.GetMembers("RestoreInto"), Is.Empty, "a list of live children makes it capture-only");
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
}
