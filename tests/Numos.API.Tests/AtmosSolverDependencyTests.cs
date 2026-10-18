using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;

namespace Numos.API.Tests;

[TestFixture]
public sealed partial class AtmosSolverDependencyTests
{
    private const float FixedStep = 1f / 20f;

    public enum ConsumerKind
    {
        Generated,
        HandWritten,
    }

    [TestCase(ConsumerKind.Generated)]
    [TestCase(ConsumerKind.HandWritten)]
    public void ServiceAndSolverResolve_OptionalStaysNull(ConsumerKind kind)
    {
        using var world = CreateWorld();
        var clock = new Clock();
        var producer = new ProducerSolver();
        var consumer = CreateConsumer(kind);
        world.Services.Register<IClock>(clock);
        world.Solvers.Register("producer", producer);
        world.Solvers.Register("consumer", consumer);

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(consumer.Clock, Is.SameAs(clock));
            Assert.That(consumer.Producer, Is.SameAs(producer));
            Assert.That(consumer.Extra, Is.Null);
            Assert.That(consumer.Solves, Is.EqualTo(1));
        });
    }

    [TestCase(ConsumerKind.Generated)]
    [TestCase(ConsumerKind.HandWritten)]
    public void SameInstanceUnderTwoNames_IsNotAmbiguous(ConsumerKind kind)
    {
        using var world = CreateWorld();
        var producer = new ProducerSolver();
        var consumer = CreateConsumer(kind);
        world.Services.Register<IClock>(new Clock());
        world.Solvers.Register("producer-a", producer);
        world.Solvers.Register("producer-b", producer);
        world.Solvers.Register("consumer", consumer);

        world.Tick();

        Assert.That(consumer.Producer, Is.SameAs(producer));
    }

    [TestCase(ConsumerKind.Generated)]
    [TestCase(ConsumerKind.HandWritten)]
    public void MissingRequired_ThrowsAtTickWithWorldUnchanged_ThenRetries(ConsumerKind kind)
    {
        using var world = CreateWorld();
        var simulation = world.Simulations.Single();
        world.Solvers.Register("producer", new ProducerSolver());
        var consumer = CreateConsumer(kind);
        world.Solvers.Register("consumer", consumer);
        var hash = world.ComputeStateHash();

        Assert.That(
            world.Tick,
            Throws.InvalidOperationException.With.Message.StartsWith(
                $"Solver 'consumer' member 'Clock' depends on '{typeof(IClock)}'"));

        Assert.Multiple(() =>
        {
            Assert.That(world.TickCount, Is.Zero);
            Assert.That(simulation.TickCount, Is.Zero);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(hash));
            Assert.That(consumer.Solves, Is.Zero);
        });

        var clock = new Clock();
        world.Services.Register<IClock>(clock);
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(world.TickCount, Is.EqualTo(1));
            Assert.That(consumer.Clock, Is.SameAs(clock));
        });
    }

    [TestCase(ConsumerKind.Generated)]
    [TestCase(ConsumerKind.HandWritten)]
    public void MissingRequired_ThrowsFromResolveDependencies(ConsumerKind kind)
    {
        using var world = CreateWorld();
        world.Services.Register<IClock>(new Clock());
        world.Solvers.Register("consumer", CreateConsumer(kind));
        var hash = world.ComputeStateHash();

        Assert.That(
            world.Solvers.ResolveDependencies,
            Throws.InvalidOperationException.With.Message.StartsWith(
                $"Solver 'consumer' member 'Producer' depends on '{typeof(ProducerSolver)}'"));
        Assert.Multiple(() =>
        {
            Assert.That(world.TickCount, Is.Zero);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(hash));
        });
    }

    [Test]
    public void ResolveDependencies_InjectsEagerlyWithoutTicking()
    {
        using var world = CreateWorld();
        var clock = new Clock();
        var consumer = new HandWrittenConsumer();
        world.Services.Register<IClock>(clock);
        world.Solvers.Register("producer", new ProducerSolver());
        world.Solvers.Register("consumer", consumer);

        world.Solvers.ResolveDependencies();
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(consumer.Clock, Is.SameAs(clock));
            Assert.That(consumer.Injections, Is.EqualTo(1), "the tick reuses the eager injection");
            Assert.That(world.TickCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void ResolveDependencies_IsRejectedDuringATick()
    {
        using var world = CreateWorld();
        Exception? failure = null;
        world.Solvers.Register("resolver", context =>
        {
            try
            {
                context.World.Solvers.ResolveDependencies();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        world.Tick();

        Assert.That(failure, Is.InstanceOf<InvalidOperationException>());
    }

    [TestCase(ConsumerKind.Generated)]
    [TestCase(ConsumerKind.HandWritten)]
    public void TwoDistinctSolversOfTheRequestedType_AreAmbiguous(ConsumerKind kind)
    {
        using var world = CreateWorld();
        world.Services.Register<IClock>(new Clock());
        world.Solvers.Register("producer-a", new ProducerSolver());
        world.Solvers.Register("producer-b", new ProducerSolver());
        world.Solvers.Register("consumer", CreateConsumer(kind));

        Assert.That(
            world.Tick,
            Throws.InvalidOperationException.With.Message.StartsWith(
                $"Solver 'consumer' member 'Producer' matches multiple solvers of type '{typeof(ProducerSolver)}': " +
                "'producer-a', 'producer-b'."));
        Assert.That(world.TickCount, Is.Zero);
    }

    [Test]
    public void ServiceTakesPrecedenceOverSolvers()
    {
        using var world = CreateWorld();
        var preferred = new ProducerSolver();
        world.Services.Register<IClock>(new Clock());
        world.Services.Register(preferred);
        world.Solvers.Register("producer-a", new ProducerSolver());
        world.Solvers.Register("producer-b", new ProducerSolver());
        var consumer = new GeneratedConsumer();
        world.Solvers.Register("consumer", consumer);

        world.Tick();

        Assert.That(consumer.Producer, Is.SameAs(preferred));
    }

    [TestCase(ConsumerKind.Generated)]
    [TestCase(ConsumerKind.HandWritten)]
    public void PipelineAndServiceChanges_ReinjectBeforeTheNextTick(ConsumerKind kind)
    {
        using var world = CreateWorld();
        var firstClock = new Clock();
        var firstProducer = new ProducerSolver();
        var consumer = CreateConsumer(kind);
        world.Services.Register<IClock>(firstClock);
        world.Solvers.Register("producer", firstProducer);
        world.Solvers.Register("consumer", consumer);
        world.Tick();

        var secondProducer = new ProducerSolver();
        world.Solvers.Unregister("producer");
        world.Solvers.Register("producer", secondProducer);
        world.Tick();
        Assert.That(consumer.Producer, Is.SameAs(secondProducer));

        var secondClock = new Clock();
        var extra = new ExtraService();
        world.Services.Unregister<IClock>();
        world.Services.Register<IClock>(secondClock);
        world.Services.Register(extra);
        world.Tick();
        Assert.Multiple(() =>
        {
            Assert.That(consumer.Clock, Is.SameAs(secondClock));
            Assert.That(consumer.Extra, Is.SameAs(extra));
        });

        world.Services.Unregister<ExtraService>();
        world.Tick();
        Assert.That(consumer.Extra, Is.Null, "a removed optional dependency is cleared, not kept");
    }

    [Test]
    public void UnchangedWorld_DoesNotReinject()
    {
        using var world = CreateWorld();
        var consumer = new HandWrittenConsumer();
        world.Services.Register<IClock>(new Clock());
        world.Solvers.Register("producer", new ProducerSolver());
        world.Solvers.Register("consumer", consumer);

        world.Tick();
        world.Tick();
        world.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, false);
        world.Tick();

        Assert.That(consumer.Injections, Is.EqualTo(1));
    }

    [Test]
    public void ResetToDefaults_ReinjectsSolversRegisteredAfterward()
    {
        using var world = CreateWorld();
        var consumer = new HandWrittenConsumer();
        world.Services.Register<IClock>(new Clock());
        world.Solvers.Register("producer", new ProducerSolver());
        world.Solvers.Register("consumer", consumer);
        world.Tick();

        world.Solvers.ResetToDefaults();
        var producer = new ProducerSolver();
        world.Solvers.Register("producer", producer);
        world.Solvers.Register("consumer", consumer);
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(consumer.Producer, Is.SameAs(producer));
            Assert.That(consumer.Injections, Is.EqualTo(2));
        });
    }

    [TestCase(ConsumerKind.Generated)]
    [TestCase(ConsumerKind.HandWritten)]
    public void DisabledSolver_IsSkippedThenInjectedWhenEnabled(ConsumerKind kind)
    {
        using var world = CreateWorld();
        var consumer = CreateConsumer(kind);
        world.Solvers.Register("consumer", consumer);
        world.Solvers.SetEnabled("consumer", false);

        // Nothing it needs is registered yet, but a disabled solver isn't injected, so the tick still runs.
        world.Tick();
        Assert.That(consumer.Clock, Is.Null);

        var clock = new Clock();
        var producer = new ProducerSolver();
        world.Services.Register<IClock>(clock);
        world.Solvers.Register("producer", producer);
        world.Solvers.SetEnabled("consumer", true);
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(consumer.Clock, Is.SameAs(clock));
            Assert.That(consumer.Producer, Is.SameAs(producer));
            Assert.That(consumer.Solves, Is.EqualTo(1));
        });
    }

    [Test]
    public void SolverDependingOnItsOwnType_ResolvesOtherInstancesOnly()
    {
        using var world = CreateWorld();
        var first = new PeerSolver();
        world.Solvers.Register("first", first);
        world.Tick();
        Assert.That(first.Peer, Is.Null, "a solver never resolves itself");

        var second = new PeerSolver();
        world.Solvers.Register("second", second);
        world.Tick();
        Assert.Multiple(() =>
        {
            Assert.That(first.Peer, Is.SameAs(second));
            Assert.That(second.Peer, Is.SameAs(first));
        });
    }

    [Test]
    public void Inheritance_InjectsBaseAndDerivedDependencies()
    {
        using var world = CreateWorld();
        var clock = new Clock();
        var producer = new ProducerSolver();
        var derived = new DerivedSolver();
        world.Services.Register<IClock>(clock);
        world.Solvers.Register("producer", producer);
        world.Solvers.Register("derived", derived);

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(derived.BaseClock, Is.SameAs(clock));
            Assert.That(derived.DerivedProducer, Is.SameAs(producer));
            Assert.That(derived.SawBoth, Is.True);
        });
    }

    [Test]
    public void Update_WithMissingDependency_LeavesAccumulatedTimeUntouched()
    {
        using var world = CreateWorld();
        world.Solvers.Register("producer", new ProducerSolver());
        world.Solvers.Register("consumer", new HandWrittenConsumer());

        Assert.That(() => world.Update(FixedStep * 1.5f), Throws.InvalidOperationException);
        Assert.That(world.TickCount, Is.Zero);

        world.Services.Register<IClock>(new Clock());
        world.Update(FixedStep);

        // Had the failed update banked its 1.5 steps, this one would have run two ticks.
        Assert.That(world.TickCount, Is.EqualTo(1));
    }

    [TestCase(ConsumerKind.Generated)]
    [TestCase(ConsumerKind.HandWritten)]
    public void WorldReplay_InjectsBeforeReplayedTicks(ConsumerKind kind)
    {
        using var world = CreateWorld();
        var clock = new Clock();
        var consumer = CreateConsumer(kind);
        world.Services.Register<IClock>(clock);
        world.Solvers.Register("producer", new ProducerSolver());
        world.Solvers.Register("consumer", consumer);
        var checkpoint = world.CaptureCheckpoint();
        world.StartRecording();
        world.Tick();
        world.Tick();
        var recording = world.StopRecording();
        var head = world.ComputeStateHash();

        world.ReplayTo(checkpoint, recording.Operations, recording.Head);

        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(head));
            Assert.That(consumer.Clock, Is.SameAs(clock));
            Assert.That(consumer.Solves, Is.EqualTo(4));
        });
    }

    [Test]
    public void WorldReplay_WithMissingDependency_PutsThePreviousStateBack()
    {
        using var world = CreateWorld();
        world.Services.Register<IClock>(new Clock());
        world.Solvers.Register("producer", new ProducerSolver());
        world.Solvers.Register("consumer", new GeneratedConsumer());
        var checkpoint = world.CaptureCheckpoint();
        world.StartRecording();
        world.Tick();
        world.Tick();
        var recording = world.StopRecording();
        world.Tick();
        var hash = world.ComputeStateHash();

        world.Services.Unregister<IClock>();
        Assert.That(
            () => world.ReplayTo(checkpoint, recording.Operations, recording.Head),
            Throws.InvalidOperationException.With.Message.Contains("member 'Clock'"));

        Assert.Multiple(() =>
        {
            Assert.That(world.TickCount, Is.EqualTo(3));
            Assert.That(world.ComputeStateHash(), Is.EqualTo(hash));
        });
    }

    [Test]
    public void SimulationReplay_WithMissingDependency_PutsThePreviousStateBack()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var world = simulation.World;
        world.Services.Register<IClock>(new Clock());
        world.Solvers.Register("producer", new ProducerSolver());
        var consumer = new HandWrittenConsumer();
        world.Solvers.Register("consumer", consumer);
        var checkpoint = simulation.CaptureCheckpoint();
        simulation.StartRecording();
        simulation.Tick();
        simulation.Tick();
        var recording = simulation.StopRecording();
        var hash = simulation.ComputeStateHash();

        simulation.ReplayTo(checkpoint, recording.Operations, recording.Head);
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));

        world.Services.Unregister<IClock>();
        Assert.That(
            () => simulation.ReplayTo(checkpoint, recording.Operations, recording.Head),
            Throws.InvalidOperationException.With.Message.Contains("member 'Clock'"));
        Assert.Multiple(() =>
        {
            Assert.That(simulation.TickCount, Is.EqualTo(2));
            Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
        });
    }

    private static AtmosWorld CreateWorld()
    {
        var world = new AtmosWorld(new AtmosConfig
        {
            GasRegistry = [new GasProperties { Name = "TestGas", MolarHeatCapacityAtConstantVolume = 20f }]
        });
        var simulation = world.CreateSimulation(2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        return world;
    }

    private static IConsumer CreateConsumer(ConsumerKind kind)
    {
        return kind == ConsumerKind.Generated ? new GeneratedConsumer() : new HandWrittenConsumer();
    }

    public interface IClock
    {
    }

    private sealed class Clock : IClock
    {
    }

    private sealed class ExtraService
    {
    }

    public sealed class ProducerSolver : IAtmosWorldSolver
    {
        public void Solve(AtmosWorldSolverContext context)
        {
        }
    }

    private interface IConsumer : IAtmosWorldSolver
    {
        IClock? Clock { get; }
        ProducerSolver? Producer { get; }
        ExtraService? Extra { get; }
        int Solves { get; }
    }

    private sealed partial class GeneratedConsumer : IConsumer
    {
        [SolverDependency]
        public partial IClock Clock { get; }

        [SolverDependency]
        public partial ProducerSolver Producer { get; }

        [SolverDependency(Optional = true)]
        public partial ExtraService? Extra { get; }

        public int Solves { get; private set; }

        public void Solve(AtmosWorldSolverContext context)
        {
            Solves++;
        }
    }

    private sealed class HandWrittenConsumer : IConsumer, IAtmosSolverDependencies
    {
        public IClock? Clock { get; private set; }
        public ProducerSolver? Producer { get; private set; }
        public ExtraService? Extra { get; private set; }
        public int Solves { get; private set; }
        public int Injections { get; private set; }

        public void InjectDependencies(AtmosSolverDependencyResolver resolver)
        {
            Clock = resolver.Resolve<IClock>(nameof(Clock));
            Producer = resolver.Resolve<ProducerSolver>(nameof(Producer));
            Extra = resolver.ResolveOptional<ExtraService>();
            Injections++;
        }

        public void Solve(AtmosWorldSolverContext context)
        {
            Solves++;
        }
    }

    private sealed partial class PeerSolver : IAtmosWorldSolver
    {
        [SolverDependency(Optional = true)]
        public partial PeerSolver? Peer { get; }

        public void Solve(AtmosWorldSolverContext context)
        {
        }
    }

    private abstract partial class BaseSolver : IAtmosWorldSolver
    {
        [SolverDependency]
        protected partial IClock Clock { get; }

        public IClock BaseClock => Clock;

        public abstract void Solve(AtmosWorldSolverContext context);
    }

    private sealed partial class DerivedSolver : BaseSolver
    {
        [SolverDependency]
        private partial ProducerSolver Producer { get; }

        public ProducerSolver DerivedProducer => Producer;

        public bool SawBoth { get; private set; }

        public override void Solve(AtmosWorldSolverContext context)
        {
            SawBoth = Clock is not null && Producer is not null;
        }
    }
}
