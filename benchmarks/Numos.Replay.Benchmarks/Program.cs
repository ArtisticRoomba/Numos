using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Replay;
using Numos.Maths;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
bool quick = args.Contains("--quick", StringComparer.Ordinal);
int iterations = quick ? 3 : 7;
Console.WriteLine(
    $"# {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, processors={Environment.ProcessorCount}");

Console.WriteLine("chunks,voxels,operation,median_ms,allocated_bytes_per_run,checkpoint_payload_bytes,checkpoint_retained_bytes");
foreach (int chunkCount in quick ? new[] { 1, 8 } : new[] { 1, 8, 32 })
{
    using var simulation = new AtmosSimulation(
        new AtmosConfig
        {
            GasRegistry =
            [
                new GasProperties { Name = "A", DiffusionCoefficient = 0.02f },
                new GasProperties { Name = "B", MolarHeatCapacityAtConstantVolume = 30f }
            ],
            SleepThreshold = int.MaxValue
        },
        8,
        8,
        4);

    for (int index = 0; index < chunkCount; index++)
    {
        var chunk = simulation.CreateAndRegisterChunk(new Int3(index % 4, index / 4, 0));
        for (ushort voxel = 0; voxel < 256; voxel++)
        {
            simulation.AddGasToVoxel(chunk, voxel, 0, 1f + voxel % 7 * 0.03f, 280f + voxel % 13);
            simulation.AddGasToVoxel(chunk, voxel, 1, 0.2f, 330f);
        }
    }

    var checkpoint = simulation.CaptureCheckpoint();
    // Keep copies rooted while measuring retained memory after a full collection.
    var retained = new AtmosSimulationCheckpoint[16];
    long before = GC.GetTotalMemory(true);
    for (int index = 0; index < retained.Length; index++) retained[index] = simulation.CaptureCheckpoint();
    long retainedBytes = Math.Max(0, GC.GetTotalMemory(true) - before) / retained.Length;
    GC.KeepAlive(retained);

    Measure("capture", () => GC.KeepAlive(simulation.CaptureCheckpoint()));
    Measure("hash", () => GC.KeepAlive(simulation.ComputeStateHash()));
    Measure("restore", () => simulation.RestoreCheckpoint(checkpoint));
    foreach (int ticks in new[] { 1, 10, 50, 100 })
    {
        Measure(
            $"restore+{ticks}",
            () =>
            {
                simulation.RestoreCheckpoint(checkpoint);
                for (int index = 0; index < ticks; index++) simulation.Tick();
            });
    }

    simulation.RestoreCheckpoint(checkpoint);
    var timeline = new AtmosReplayTimeline(simulation);
    for (int tick = 0; tick < 200; tick++)
    {
        if (tick % 17 == 0) simulation.AddGasToVoxel(new AtmosChunkHandle(default), 0, 0, 0.25f, 300f);
        simulation.Tick();
        timeline.ObserveLiveState();
    }

    Measure("scrub-near", () => timeline.SeekTick(199));
    Measure("scrub-far", () => timeline.SeekTick(49));
    Measure(
        "scrub-drag-10",
        () =>
        {
            for (ulong tick = 80; tick < 90; tick++) timeline.SeekTick(tick);
        });

    Measure("return-to-head", () => timeline.ReturnToHead(), () => timeline.SeekTick(49));

    void Measure(string name, Action action, Action? setup = null)
    {
        setup?.Invoke();
        action();
        double[] timings = new double[iterations];
        long allocated = 0;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            setup?.Invoke();
            long bytes = GC.GetTotalAllocatedBytes(true);
            long started = Stopwatch.GetTimestamp();
            action();
            timings[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocated += GC.GetTotalAllocatedBytes(true) - bytes;
        }

        Array.Sort(timings);
        Console.WriteLine(
            $"{chunkCount},{chunkCount * 256},{name},{timings[iterations / 2]:F3},{allocated / iterations},{checkpoint.PayloadBytes},{retainedBytes}");
    }
}