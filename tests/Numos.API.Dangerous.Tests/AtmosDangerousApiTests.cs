namespace Numos.API.Dangerous.Tests;

[TestFixture]
public sealed class AtmosDangerousApiTests
{
    [Test]
    public void Dangerous_WithNullSimulation_Throws()
    {
        AtmosSimulation? simulation = null;

        Assert.That(() => simulation!.Dangerous(),
            Throws.TypeOf<ArgumentNullException>()
                .With.Property(nameof(ArgumentNullException.ParamName)).EqualTo("simulation"));
    }

    [Test]
    public void Dangerous_WithLiveSimulation_ReturnsEntryPoint()
    {
        using var simulation = new AtmosSimulation();

        var dangerous = simulation.Dangerous();

        Assert.That(dangerous, Is.TypeOf<AtmosDangerousApi>());
    }

    [Test]
    public void Dangerous_WithDisposedSimulation_Throws()
    {
        var simulation = new AtmosSimulation();
        simulation.Dispose();

        Assert.That(simulation.Dangerous, Throws.TypeOf<ObjectDisposedException>());
    }
}