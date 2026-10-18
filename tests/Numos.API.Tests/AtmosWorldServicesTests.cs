using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosWorldServicesTests
{
    [Test]
    public void RegisteredService_IsReturnedUntilUnregistered()
    {
        using var world = new AtmosWorld();
        var clock = new GameClock();

        world.Services.Register(clock);

        Assert.Multiple(() =>
        {
            Assert.That(world.Services.Get<GameClock>(), Is.SameAs(clock));
            Assert.That(world.Services.TryGet(out GameClock? found), Is.True);
            Assert.That(found, Is.SameAs(clock));
        });

        Assert.Multiple(() =>
        {
            Assert.That(world.Services.Unregister<GameClock>(), Is.True);
            Assert.That(world.Services.Unregister<GameClock>(), Is.False);
            Assert.That(world.Services.TryGet(out GameClock? missing), Is.False);
            Assert.That(missing, Is.Null);
        });
    }

    [Test]
    public void Services_AreKeyedByTheExactRegisteredType()
    {
        using var world = new AtmosWorld();
        var clock = new GameClock();

        world.Services.Register<IGameClock>(clock);

        Assert.Multiple(() =>
        {
            Assert.That(world.Services.Get<IGameClock>(), Is.SameAs(clock));
            Assert.That(world.Services.TryGet(out GameClock? _), Is.False);
        });

        // The same instance may sit under several keys.
        world.Services.Register(clock);
        Assert.That(world.Services.Get<GameClock>(), Is.SameAs(clock));
    }

    [Test]
    public void InvalidRegistrationsAndMissingLookups_AreRejected()
    {
        using var world = new AtmosWorld();
        world.Services.Register(new GameClock());

        Assert.Multiple(() =>
        {
            Assert.That(() => world.Services.Register(new GameClock()), Throws.ArgumentException);
            Assert.That(() => world.Services.Register<GameClock>(null!), Throws.ArgumentNullException);
            Assert.That(
                () => world.Services.Get<IGameClock>(),
                Throws.InvalidOperationException.With.Message.Contains(typeof(IGameClock).FullName!));
        });
    }

    [Test]
    public void SolverCallback_ResolvesHostInterfaceService()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var ticks = new List<int>();
        simulation.World.Services.Register<ICollection<int>>(ticks);
        simulation.World.Solvers.Register(
            "use-service",
            context => context.World.Services.Get<ICollection<int>>().Add(context.TickCount));

        simulation.Tick();
        simulation.Tick();

        Assert.That(ticks, Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public void ParallelWorkers_CanResolveServicesDuringATick()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        for (int x = 0; x < 8; x++)
            simulation.CreateAndRegisterChunk(new Int3(x, 0, 0));

        var clock = new GameClock { Now = 42 };
        simulation.World.Services.Register<IGameClock>(clock);
        int[] seen = [];
        simulation.World.Solvers.Register(
            "parallel-lookup",
            context =>
            {
                var chunks = context.Chunks(context.Simulations[0]);
                seen = new int[chunks.Count];
                Parallel.For(
                    0,
                    chunks.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = 4 },
                    index => seen[index] = context.World.Services.Get<IGameClock>().Now);
            });

        var tick = Task.Run(simulation.Tick);

        Assert.That(tick.Wait(TimeSpan.FromSeconds(10)), Is.True, "A worker lookup blocked the tick.");
        Assert.That(seen, Is.EqualTo(Enumerable.Repeat(42, 8)));
    }

    [Test]
    public void RegistrationChanges_AreRejectedDuringATick()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        simulation.World.Services.Register(new GameClock());
        Exception? registerFailure = null;
        Exception? unregisterFailure = null;
        Exception? workerFailure = null;
        simulation.World.Solvers.Register(
            "late-service",
            context =>
            {
                registerFailure = Capture(() => context.World.Services.Register<IGameClock>(new GameClock()));
                unregisterFailure = Capture(() => context.World.Services.Unregister<GameClock>());
                workerFailure = Task.Run(() => Capture(() => context.World.Services.Unregister<GameClock>())).Result;
            });

        simulation.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(registerFailure, Is.TypeOf<InvalidOperationException>());
            Assert.That(unregisterFailure, Is.TypeOf<InvalidOperationException>());
            Assert.That(workerFailure, Is.TypeOf<InvalidOperationException>());
            Assert.That(simulation.World.Services.TryGet(out GameClock? _), Is.True);
            Assert.That(simulation.World.Services.TryGet(out IGameClock? _), Is.False);
        });
    }

    [Test]
    public void RegistrationChanges_AreRejectedWhileRecording()
    {
        using var world = new AtmosWorld();
        var simulation = world.CreateSimulation(1, 1, 1);
        world.Services.Register(new GameClock());

        world.StartRecording();
        Assert.Multiple(() =>
        {
            Assert.That(() => world.Services.Register<IGameClock>(new GameClock()), Throws.InvalidOperationException);
            Assert.That(() => world.Services.Unregister<GameClock>(), Throws.InvalidOperationException);
            Assert.That(world.Services.Get<GameClock>(), Is.Not.Null);
        });

        world.StopRecording();

        simulation.World.StartRecording();
        Assert.That(() => world.Services.Register<IGameClock>(new GameClock()), Throws.InvalidOperationException);
        simulation.World.StopRecording();

        Assert.That(() => world.Services.Register<IGameClock>(new GameClock()), Throws.Nothing);
    }

    [Test]
    public void RegistrationChanges_AreRejectedDuringReplay()
    {
        using var world = new AtmosWorld();
        world.CreateSimulation(1, 1, 1);
        // OnRestored is the one host hook that runs during replay outside a tick, so the tick guard can't mask the
        // replay guard here.
        var probe = new RegisterOnRestore(world);
        world.Solvers.Register("replay-probe", probe);
        var checkpoint = world.CaptureCheckpoint();
        world.StartRecording();
        world.Tick();
        var recording = world.StopRecording();

        world.RestoreCheckpoint(checkpoint);
        Assert.That(probe.Failure, Is.Null);
        world.Services.Unregister<GameClock>();

        world.ReplayTo(checkpoint, recording.Operations, recording.Head);

        Assert.That(probe.Failure, Is.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void Services_SurviveCheckpointRestoreAndAreNotDisposed()
    {
        var world = new AtmosWorld();
        var simulation = world.CreateSimulation(1, 1, 1);
        simulation.CreateAndRegisterChunk(default);
        var service = new DisposableService();
        world.Services.Register(service);
        var worldCheckpoint = world.CaptureCheckpoint();
        world.Tick();

        world.RestoreCheckpoint(worldCheckpoint);
        Assert.That(world.Services.Get<DisposableService>(), Is.SameAs(service));

        var simulationCheckpoint = simulation.World.CaptureCheckpoint();
        simulation.Tick();
        simulation.World.RestoreCheckpoint(simulationCheckpoint);
        Assert.That(world.Services.Get<DisposableService>(), Is.SameAs(service));

        world.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(service.IsDisposed, Is.False);
            Assert.That(() => world.Services.Get<DisposableService>(), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => world.Services.Register(new GameClock()), Throws.TypeOf<ObjectDisposedException>());
        });
    }

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class RegisterOnRestore(AtmosWorld world) : IAtmosWorldSolver
    {
        internal Exception? Failure { get; private set; }

        public void Solve(AtmosWorldSolverContext context)
        {
        }

        public void OnRestored()
        {
            Failure = Capture(() => world.Services.Register(new GameClock()));
        }
    }

    private interface IGameClock
    {
        int Now { get; }
    }

    private sealed class GameClock : IGameClock
    {
        public int Now { get; init; }
    }

    private sealed class DisposableService : IDisposable
    {
        internal bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}