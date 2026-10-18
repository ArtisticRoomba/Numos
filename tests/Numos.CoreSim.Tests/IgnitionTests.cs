using Numos.CoreSim.Solvers;
using NUnit.Framework;

namespace Numos.CoreSim.Tests;

public class IgnitionTests
{
    private const float Tolerance = 1e-5f;

    [Test]
    public void GrowIgnition_ZeroAdvance_KeepsFraction()
    {
        float result = ReactionSolver.GrowIgnition(0.25f, 0f);

        Assert.That(result, Is.EqualTo(0.25f).Within(Tolerance));
    }

    [Test]
    public void GrowIgnition_SmallKernel_GrowsSlowerThanPlanarFront()
    {
        float result = ReactionSolver.GrowIgnition(0.001f, 0.05f);

        Assert.That(result, Is.GreaterThan(0.001f));
        Assert.That(result, Is.LessThan(0.001f + 0.05f));
    }

    [Test]
    public void GrowIgnition_LargeFlame_GrowsLinearly()
    {
        float result = ReactionSolver.GrowIgnition(0.5f, 0.1f);

        Assert.That(result, Is.EqualTo(0.6f).Within(Tolerance));
    }

    [Test]
    public void GrowIgnition_NeverExceedsWholeVoxel()
    {
        float result = ReactionSolver.GrowIgnition(0.9f, 5f);

        Assert.That(result, Is.EqualTo(1f));
    }

    [Test]
    public void GrowIgnition_NegativeAdvance_RecedesAndStopsAtZero()
    {
        float receded = ReactionSolver.GrowIgnition(0.5f, -0.1f);
        float gone = ReactionSolver.GrowIgnition(0.5f, -5f);

        Assert.That(receded, Is.LessThan(0.5f));
        Assert.That(receded, Is.GreaterThan(0f));
        Assert.That(gone, Is.EqualTo(0f));
    }

    [Test]
    public void ApplyIgnitionSource_StartsFlameInSourceVolume()
    {
        float ignitionPercent = 0f;
        float ignitedVolumeTemp = 0f;

        ReactionSolver.ApplyIgnitionSource(ref ignitionPercent, ref ignitedVolumeTemp, 1500f, 0.002f, 1f);

        Assert.That(ignitionPercent, Is.EqualTo(0.002f).Within(Tolerance));
        Assert.That(ignitedVolumeTemp, Is.EqualTo(1500f).Within(0.01f));
    }

    [Test]
    public void ApplyIgnitionSource_ReassertedSource_DoesNotInflateFlame()
    {
        float ignitionPercent = 0f;
        float ignitedVolumeTemp = 0f;
        ReactionSolver.ApplyIgnitionSource(ref ignitionPercent, ref ignitedVolumeTemp, 1500f, 0.002f, 1f);

        ReactionSolver.ApplyIgnitionSource(ref ignitionPercent, ref ignitedVolumeTemp, 1500f, 0.002f, 1f);

        Assert.That(ignitionPercent, Is.EqualTo(0.002f).Within(Tolerance));
    }

    [Test]
    public void ApplyIgnitionSource_SmallerThanExistingFlame_ChangesNothing()
    {
        float ignitionPercent = 0.4f;
        float ignitedVolumeTemp = 900f;

        ReactionSolver.ApplyIgnitionSource(ref ignitionPercent, ref ignitedVolumeTemp, 3000f, 0.01f, 1f);

        Assert.That(ignitionPercent, Is.EqualTo(0.4f));
        Assert.That(ignitedVolumeTemp, Is.EqualTo(900f));
    }

    [Test]
    public void ApplyIgnitionSource_LargerThanExistingFlame_MixesTemperatureByVolume()
    {
        float ignitionPercent = 0.1f;
        float ignitedVolumeTemp = 1000f;

        ReactionSolver.ApplyIgnitionSource(ref ignitionPercent, ref ignitedVolumeTemp, 2000f, 0.2f, 1f);

        // 0.1 of the voxel at 1000 K plus another 0.1 at 2000 K, over 0.2 of the voxel.
        Assert.That(ignitionPercent, Is.EqualTo(0.2f).Within(Tolerance));
        Assert.That(ignitedVolumeTemp, Is.EqualTo(1500f).Within(0.01f));
    }

    [TestCase(0f, 0.002f, 1f)]
    [TestCase(1500f, 0f, 1f)]
    [TestCase(-5f, 0.002f, 1f)]
    [TestCase(1500f, 0.002f, 0f)]
    [TestCase(float.NaN, 0.002f, 1f)]
    public void ApplyIgnitionSource_InvalidInput_IsIgnored(float temperature, float volume, float voxelVolume)
    {
        float ignitionPercent = 0f;
        float ignitedVolumeTemp = 0f;

        ReactionSolver.ApplyIgnitionSource(ref ignitionPercent, ref ignitedVolumeTemp, temperature, volume, voxelVolume);

        Assert.That(ignitionPercent, Is.EqualTo(0f));
        Assert.That(ignitedVolumeTemp, Is.EqualTo(0f));
    }

    [Test]
    public void ComputeFlameSpeed_NoDepletion_IsZero()
    {
        ReactionSolver solver = new();

        float result = solver.ComputeFlameSpeed(0f);

        Assert.That(result, Is.EqualTo(0f));
    }

    [Test]
    public void ComputeFlameSpeed_FourTimesTheRate_DoublesTheSpeed()
    {
        ReactionSolver solver = new();

        float slow = solver.ComputeFlameSpeed(100f);
        float fast = solver.ComputeFlameSpeed(400f);

        Assert.That(slow, Is.GreaterThan(0f));
        Assert.That(fast, Is.EqualTo(slow * 2f).Within(Tolerance));
    }

    [Test]
    public void ComputeFlameSpeed_FollowsDiffusivityAndScale()
    {
        ReactionSolver solver = new() { ThermalDiffusivity = 1e-4f, FlameSpeedScale = 2f };

        float result = solver.ComputeFlameSpeed(100f);

        // 2 * sqrt(1e-4 m^2/s * 100 1/s) = 0.2 m/s
        Assert.That(result, Is.EqualTo(0.2f).Within(Tolerance));
    }
}
