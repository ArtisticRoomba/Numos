using System.Buffers.Binary;
using System.Reflection;
using Numos.CoreSim;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;
using Numos.Tests;

namespace Numos.API.Tests;

/// <summary>
///     The first version of a solver's state.
/// </summary>
public sealed partial class SchemaSolverV1 : IAtmosWorldSolver
{
    [NumosSerialize] public float Heat;
    [NumosSerialize] public int Count;

    public void Solve(AtmosWorldSolverContext context)
    {
    }
}

/// <summary>
///     <see cref="SchemaSolverV1" /> with a C# rename that keeps the wire name, so the schema is the same.
/// </summary>
public sealed partial class SchemaSolverRenamed : IAtmosWorldSolver
{
    [NumosSerialize("Heat")] public float Temperature;
    [NumosSerialize] public int Count;

    public void Solve(AtmosWorldSolverContext context)
    {
    }
}

/// <summary>
///     <see cref="SchemaSolverV1" /> without <c>Count</c> and with a new <c>Spread</c>, so the schema differs.
/// </summary>
public sealed partial class SchemaSolverV2 : IAtmosWorldSolver
{
    [NumosSerialize] public float Heat;
    [NumosSerialize] public double Spread;

    public void Solve(AtmosWorldSolverContext context)
    {
    }
}

[TestFixture]
public sealed class SchemaFingerprintTests
{
    private const string Name = "tests/schema/solver";

    private static AtmosWorld CreateWorld(IAtmosWorldSolver solver)
    {
        var world = new AtmosWorld(new TestAtmosConfig());
        world.CreateSimulation(1, 1, 1);
        world.Solvers.Register(Name, solver);
        return world;
    }

    private static ulong FingerprintOf(object state) => ((INumosStateSchema)state).SchemaFingerprint;

    [Test]
    public void Capture_StoresTheFingerprintOfEachSolverAndComponentType()
    {
        var solver = new SchemaSolverV1();
        using var world = CreateWorld(solver);
        var pipes = world.Simulations.Single().GetComponent<PipeNetwork>();

        var checkpoint = world.CaptureCheckpoint();
        Assert.Multiple(() =>
        {
            Assert.That(checkpoint.SolverStates.Single().SchemaFingerprint, Is.EqualTo(FingerprintOf(solver)).And.Not.Zero);
            Assert.That(checkpoint.SimulationComponents.Single().SchemaFingerprint, Is.EqualTo(FingerprintOf(pipes)).And.Not.Zero);
            Assert.That(FingerprintOf(new SchemaSolverRenamed()), Is.EqualTo(FingerprintOf(solver)));
            Assert.That(FingerprintOf(new SchemaSolverV2()), Is.Not.EqualTo(FingerprintOf(solver)));
        });
    }

    [Test]
    public void Restore_WithARenameThatKeepsTheWireName_ReportsNothingEvenWhenStrict()
    {
        using var source = CreateWorld(new SchemaSolverV1 { Heat = 4f, Count = 2 });
        var checkpoint = source.CaptureCheckpoint();

        var renamed = new SchemaSolverRenamed();
        using var target = CreateWorld(renamed);

        Assert.That(target.RestoreCheckpoint(checkpoint, AtmosSchemaCheck.Strict), Is.Empty);
        Assert.Multiple(() =>
        {
            Assert.That(renamed.Temperature, Is.EqualTo(4f));
            Assert.That(renamed.Count, Is.EqualTo(2));
        });
    }

    [Test]
    public void Restore_WithAnotherSchema_IsTolerantAndReportsTheSolverName()
    {
        using var source = CreateWorld(new SchemaSolverV1 { Heat = 4f, Count = 2 });
        var checkpoint = source.CaptureCheckpoint();

        var changed = new SchemaSolverV2 { Spread = 0.5 };
        using var target = CreateWorld(changed);
        var reported = target.RestoreCheckpoint(checkpoint, AtmosSchemaCheck.Tolerant);

        Assert.Multiple(() =>
        {
            Assert.That(reported, Is.EqualTo(new[] { Name }));
            Assert.That(changed.Heat, Is.EqualTo(4f), "the shared field is read");
            Assert.That(changed.Spread, Is.EqualTo(0.5), "a field the payload lacks keeps its value");
        });

        Assert.That(() => target.RestoreCheckpoint(checkpoint), Throws.Nothing, "the original overload stays tolerant");
    }

    [Test]
    public void Restore_WithAnotherSchema_WhenStrict_ThrowsNamingTheTypeAndChangesNothing()
    {
        using var source = CreateWorld(new SchemaSolverV1 { Heat = 4f, Count = 2 });
        source.Tick();
        var checkpoint = source.CaptureCheckpoint();

        var changed = new SchemaSolverV2 { Heat = 7f, Spread = 0.5 };
        using var target = CreateWorld(changed);
        var before = target.ComputeStateHash();

        Assert.That(
            () => target.RestoreCheckpoint(checkpoint, AtmosSchemaCheck.Strict),
            Throws.InvalidOperationException
                .With.Message.Contains(typeof(SchemaSolverV2).FullName)
                .And.Message.Contains("another shape"));

        Assert.Multiple(() =>
        {
            Assert.That(changed.Heat, Is.EqualTo(7f));
            Assert.That(target.ComputeStateHash(), Is.EqualTo(before));
        });
    }

    [Test]
    public void ReplayTo_CarriesTheChangedSchemas()
    {
        using var source = CreateWorld(new SchemaSolverV1 { Heat = 4f });
        source.Simulations.Single().GetComponent<PipeNetwork>().NextId = 3;
        var checkpoint = WithComponentFingerprint(source.CaptureCheckpoint(), static saved => saved ^ 1);

        using var target = CreateWorld(new SchemaSolverV2());
        var result = target.ReplayTo(checkpoint, [], checkpoint.Position);
        Assert.Multiple(() =>
        {
            Assert.That(result.ChangedSchemas, Is.EqualTo(new[] { Name, PipeNetwork.Key }));
            Assert.That(target.Simulations.Single().GetComponent<PipeNetwork>().NextId, Is.EqualTo(3));
        });

        using var same = CreateWorld(new SchemaSolverV1());
        Assert.That(same.ReplayTo(source.CaptureCheckpoint(), [], checkpoint.Position).ChangedSchemas, Is.Empty);
    }

    [Test]
    public void Restore_OfAnEntryWithoutAFingerprint_ReportsNothing()
    {
        using var source = CreateWorld(new SchemaSolverV1());
        source.Simulations.Single().GetComponent<PipeNetwork>();
        var checkpoint = WithComponentFingerprint(source.CaptureCheckpoint(), static _ => 0);
        Assert.That(checkpoint.SimulationComponents.Single().SchemaFingerprint, Is.Zero);

        using var target = CreateWorld(new SchemaSolverV1());
        Assert.That(target.RestoreCheckpoint(checkpoint, AtmosSchemaCheck.Strict), Is.Empty);
    }

    /// <summary>
    ///     A built-in checkpoint, configuration, or solver type changed shape. The pinned value sits next to
    ///     <see cref="AtmosWorldCheckpoint.CurrentFormatVersion" />.
    /// </summary>
    [Test]
    public void BuiltInSchemas_MatchThePinnedFingerprint()
    {
        Assembly[] assemblies = [typeof(AtmosConfig).Assembly, typeof(AtmosWorld).Assembly];
        var types = assemblies
            .SelectMany(static assembly => assembly.GetTypes())
            .Where(static type => typeof(INumosStateSchema).IsAssignableFrom(type) && !type.IsInterface)
            .Select(static type => (Type: type, Fingerprint: GeneratedFingerprint(type)))
            .OrderBy(static entry => entry.Fingerprint)
            .ThenBy(static entry => entry.Type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.That(types.Select(static entry => entry.Type), Does.Contain(typeof(AtmosWorldCheckpoint)));
        Assert.That(types.Select(static entry => entry.Type), Does.Contain(typeof(AtmosConfig)));

        // Type names stay out, so renaming a type without touching its fields keeps the value.
        ulong combined = 14695981039346656037;
        Span<byte> bytes = stackalloc byte[8];
        foreach (var entry in types)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, entry.Fingerprint);
            foreach (byte value in bytes)
            {
                combined ^= value;
                combined *= 1099511628211;
            }
        }

        ulong pinned = (ulong)typeof(AtmosWorldCheckpoint)
            .GetField("BuiltInSchemaFingerprint", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;

        if (combined != pinned)
        {
            foreach (var entry in types.OrderBy(static entry => entry.Type.FullName, StringComparer.Ordinal))
                TestContext.Out.WriteLine($"0x{entry.Fingerprint:X16}  {entry.Type.FullName}");
        }

        Assert.That(
            combined,
            Is.EqualTo(pinned),
            $"The built-in state schema changed. Set AtmosWorldCheckpoint.BuiltInSchemaFingerprint to 0x{combined:X16}, " +
            "and decide whether AtmosWorldCheckpoint.CurrentFormatVersion must move. The test output lists each type's " +
            "fingerprint.");
    }

    private static ulong GeneratedFingerprint(Type type)
    {
        var field = type.GetField("__numosSchemaFingerprint", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(field, Is.Not.Null, $"{type.FullName} implements INumosStateSchema without the generated constant.");
        return (ulong)field!.GetRawConstantValue()!;
    }

    /// <summary>
    ///     Rewrites the fingerprint of the checkpoint's only component entry through the wire bytes, the way an
    ///     older build of the component class would have written it.
    /// </summary>
    private static AtmosWorldCheckpoint WithComponentFingerprint(AtmosWorldCheckpoint checkpoint, Func<ulong, ulong> change)
    {
        ulong saved = checkpoint.SimulationComponents.Single().SchemaFingerprint;
        var sink = new StateBinarySink();
        NumosState.Write(ref sink, checkpoint);
        byte[] bytes = sink.WrittenSpan.ToArray();

        Span<byte> pattern = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(pattern, saved);
        int at = bytes.AsSpan().IndexOf(pattern);
        Assert.That(at, Is.GreaterThanOrEqualTo(0));
        Assert.That(bytes.AsSpan(at + 1).IndexOf(pattern), Is.LessThan(0), "the fingerprint appears once");
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), change(saved));

        var reader = new StateReader(bytes);
        return NumosState.Read<AtmosWorldCheckpoint>(ref reader, checkpoint.Config.GasRegistry);
    }
}
