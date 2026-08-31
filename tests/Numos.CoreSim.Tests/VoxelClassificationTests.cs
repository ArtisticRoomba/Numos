using Numos.CoreSim.Datatypes.Primitives;

namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class VoxelClassificationTests
{
    [Test]
    public void DefaultConstructors_CreateUnassignedClassification()
    {
        var defaultValue = default(VoxelClassification);
        var constructedValue = new VoxelClassification();

        Assert.Multiple(() =>
        {
            Assert.That(defaultValue.RoomId, Is.EqualTo(VoxelClassification.RoomUnassigned));
            Assert.That(defaultValue.IsUnassigned, Is.True);
            Assert.That(constructedValue, Is.EqualTo(defaultValue));
        });
    }

    [TestCase(VoxelClassification.RoomUnassigned, true, false, false)]
    [TestCase(VoxelClassification.RoomSolid, false, true, false)]
    [TestCase(VoxelClassification.RoomVoid, false, false, true)]
    [TestCase(42, false, false, false)]
    public void Predicates_RecognizeOnlyTheirReservedClassification(
        int roomId, bool isUnassigned, bool isSolid, bool isVoid)
    {
        var classification = new VoxelClassification(roomId);

        Assert.Multiple(() =>
        {
            Assert.That(classification.RoomId, Is.EqualTo(roomId));
            Assert.That(classification.IsUnassigned, Is.EqualTo(isUnassigned));
            Assert.That(classification.IsSolid, Is.EqualTo(isSolid));
            Assert.That(classification.IsVoid, Is.EqualTo(isVoid));
        });
    }

    [TestCase(int.MinValue)]
    [TestCase(VoxelClassification.RoomSolid)]
    [TestCase(VoxelClassification.RoomVoid)]
    [TestCase(VoxelClassification.RoomUnassigned)]
    [TestCase(int.MaxValue)]
    public void ImplicitConversions_RoundTripEveryIntegerRoomId(int roomId)
    {
        VoxelClassification classification = roomId;
        int convertedBack = classification;

        Assert.Multiple(() =>
        {
            Assert.That(classification.RoomId, Is.EqualTo(roomId));
            Assert.That(convertedBack, Is.EqualTo(roomId));
        });
    }

    [Test]
    public void RecordEquality_UsesRoomId()
    {
        var value = new VoxelClassification(17);

        Assert.Multiple(() =>
        {
            Assert.That(value, Is.EqualTo(new VoxelClassification(17)));
            Assert.That(value, Is.Not.EqualTo(new VoxelClassification(18)));
        });
    }
}