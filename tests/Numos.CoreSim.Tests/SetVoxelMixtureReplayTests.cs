using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;
using Numos.Maths;

namespace Numos.CoreSim.Tests;

public class SetVoxelMixtureReplayTests
{
    [TestCase(float.NaN, 0f, 0f)]
    [TestCase(0f, float.PositiveInfinity, 0f)]
    [TestCase(0f, 0f, float.NegativeInfinity)]
    public void Apply_NonFiniteMomentum_ThrowsAndChangesNothing(float x, float y, float z)
    {
        var kernel = new AtmosKernel(2, 2, 1);
        kernel.CreateAndRegisterChunk(Int3.Zero, 2, 2, 1);
        var before = kernel.CaptureCheckpoint().ComputeStateHash();
        var operation = new SetVoxelMixtureOperation(
            Int3.Zero, 0, 293.15f, 101325f, 100f, x, y, z, Array.Empty<AtmosGasAmount>());

        Assert.Throws<ArgumentOutOfRangeException>(() => kernel.ApplyRecordedOperation(operation));

        Assert.That(kernel.CaptureCheckpoint().ComputeStateHash(), Is.EqualTo(before));
    }
}
