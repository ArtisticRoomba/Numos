using Numos.API;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests.Euler;

/// <summary>
///     A source cell fed every tick keeps the temperature of the incoming gas only when the injection carries the
///     flow work of a vent.
/// </summary>
[TestFixture]
public sealed class EulerVentInjectionTests
{
    private const int Size = 6;
    private const int TickRate = 20;
    private const int Seconds = 5;
    private const float IncomingTemperature = 400f;

    [Test]
    public void VentGasToVoxel_KeepsTheSourceCellAndRoomNearTheIncomingTemperature()
    {
        var (source, room) = Run(vent: true);

        TestContext.Out.WriteLine($"vent: source {source:F1} K, room mean {room:F1} K");
        Assert.Multiple(() =>
        {
            Assert.That(source, Is.EqualTo(IncomingTemperature).Within(5).Percent);
            Assert.That(room, Is.EqualTo(IncomingTemperature).Within(5).Percent);
        });
    }

    [Test]
    public void AddGasToVoxel_LetsTheSourceCellRunColdAsContrast()
    {
        var (source, room) = Run(vent: false);

        TestContext.Out.WriteLine($"add: source {source:F1} K, room mean {room:F1} K");
        Assert.That(source, Is.LessThan(IncomingTemperature * 0.9f));
    }

    private static (float Source, float Room) Run(bool vent)
    {
        var config = SimTestHelpers.CreateEulerConfig();
        using var simulation = SimTestHelpers.CreateEulerSimulation(
            config, Size, Size, 1, out var chunk, exactGasDynamics: false, tickRate: TickRate);
        float ambient = SimTestHelpers.MolesFor(config, AtmosPhysicalConstants.StandardAtmosphericPressure, IncomingTemperature);
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
            simulation.AddGasToVoxel(chunk, x, y, 0, SimTestHelpers.EulerAirGasName, ambient, IncomingTemperature);

        float perTick = ambient * 0.01f;
        for (int tick = 0; tick < Seconds * TickRate; tick++)
        {
            if (vent)
                simulation.VentGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.EulerAirGasName, perTick, IncomingTemperature);
            else
                simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.EulerAirGasName, perTick, IncomingTemperature);
            simulation.Tick();
        }

        var snapshot = simulation.GetChunkSnapshot(chunk);
        float sum = 0f;
        for (int index = 0; index < Size * Size; index++)
            sum += snapshot.Temperature[index];

        return (snapshot.Temperature[0], sum / (Size * Size));
    }
}
