using Numos.CoreSim.Solvers;

namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class ChunkFieldRegistryTests
{
    // The registry is process-wide, so every key here is namespaced to this fixture.
    private static string Key(string name)
    {
        return $"{typeof(ChunkFieldRegistryTests).FullName}/{name}";
    }

    [Test]
    public void Register_SameDefinition_ReturnsSameDescriptor()
    {
        var first = ChunkFieldRegistry.Register<int>(Key("idempotent"), true, null);
        var second = ChunkFieldRegistry.Register<int>(Key("idempotent"), true, null);

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void Register_DistinctKeys_GetDistinctSlots()
    {
        var left = ChunkFieldRegistry.Register<int>(Key("distinct-left"), false, null);
        var right = ChunkFieldRegistry.Register<int>(Key("distinct-right"), false, null);

        Assert.That(right.Slot, Is.Not.EqualTo(left.Slot));
    }

    [Test]
    public void Register_ConflictingElementType_ThrowsNamingBothDefinitions()
    {
        ChunkFieldRegistry.Register<int>(Key("conflict-type"), false, null);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ChunkFieldRegistry.Register<float>(Key("conflict-type"), false, null));

        Assert.That(exception!.Message, Does.Contain(typeof(int).FullName).And.Contain(typeof(float).FullName));
    }

    [Test]
    public void Register_ConflictingLength_Throws()
    {
        ChunkFieldRegistry.Register<int>(Key("conflict-length"), false, 4);

        Assert.Multiple(() =>
        {
            Assert.Throws<InvalidOperationException>(() =>
                ChunkFieldRegistry.Register<int>(Key("conflict-length"), false, 5));
            Assert.Throws<InvalidOperationException>(() =>
                ChunkFieldRegistry.Register<int>(Key("conflict-length"), false, null));
        });
    }

    [Test]
    public void Register_ConflictingRollback_Throws()
    {
        ChunkFieldRegistry.Register<int>(Key("conflict-rollback"), false, null);

        Assert.Throws<InvalidOperationException>(() =>
            ChunkFieldRegistry.Register<int>(Key("conflict-rollback"), true, null));
    }

    [Test]
    public void Register_RollbackWithReferences_Throws()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentException>(() =>
                ChunkFieldRegistry.Register<string>(Key("rollback-string"), true, null));
            Assert.Throws<ArgumentException>(() =>
                ChunkFieldRegistry.Register<ReferenceHolder>(Key("rollback-holder"), true, null));
        });

        // A rejected registration must not claim the key.
        Assert.That(ChunkFieldRegistry.Register<string>(Key("rollback-string"), false, null).CaptureForRollback, Is.False);
    }

    [Test]
    public void Register_TransientWithReferences_IsAllowed()
    {
        var field = ChunkFieldRegistry.Register<string>(Key("transient-string"), false, null);

        Assert.That(field.CaptureForRollback, Is.False);
    }

    [TestCase("")]
    [TestCase("   ")]
    public void Register_BlankKey_Throws(string key)
    {
        Assert.Throws<ArgumentException>(() => ChunkFieldRegistry.Register<int>(key, false, null));
    }

    [Test]
    public void Register_NegativeLength_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ChunkFieldRegistry.Register<int>(Key("negative-length"), false, -1));
    }

    [Test]
    public void PublicHandle_FirstPublisherWins()
    {
        var field = ChunkFieldRegistry.Register<int>(Key("public-handle"), false, null);
        object first = new();
        object second = new();

        Assert.Multiple(() =>
        {
            Assert.That(field.PublicHandle, Is.Null);
            Assert.That(field.GetOrSetPublicHandle(first), Is.SameAs(first));
            Assert.That(field.GetOrSetPublicHandle(second), Is.SameAs(first));
            Assert.That(field.PublicHandle, Is.SameAs(first));
        });
    }

    private readonly record struct ReferenceHolder(object? Value);
}