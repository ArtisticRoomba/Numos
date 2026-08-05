namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class GasChannelTests
{
    [Test]
    public void DefaultValue_IsNotInitialized()
    {
        var channel = default(GasChannel);

        Assert.Multiple(() =>
        {
            Assert.That(channel.IsInitialized, Is.False);
            Assert.That(channel.Moles, Is.Null);
            Assert.That(channel.GasId, Is.Zero);
        });
    }

    [Test]
    public void Initialize_SetsGasIdAndClearsRequestedVoxelRange()
    {
        var channel = new GasChannel();
        try
        {
            channel.Initialize(12, 7);

            Assert.Multiple(() =>
            {
                Assert.That(channel.IsInitialized, Is.True);
                Assert.That(channel.GasId, Is.EqualTo(12));
                Assert.That(channel.Moles, Has.Length.GreaterThanOrEqualTo(7));
                Assert.That(channel.Moles!.Take(7), Is.All.Zero);
            });
        }
        finally
        {
            channel.Release();
        }
    }

    [Test]
    public void Release_ClearsInitializationStateAndCanBeCalledAgain()
    {
        var channel = new GasChannel();
        channel.Initialize(4, 2);
        channel.Moles![0] = 5f;

        channel.Release();
        channel.Release();

        Assert.Multiple(() =>
        {
            Assert.That(channel.IsInitialized, Is.False);
            Assert.That(channel.Moles, Is.Null);
            Assert.That(channel.GasId, Is.EqualTo(4));
        });
    }

    [Test]
    public void ReleasedChannel_CanBeInitializedAgainWithNewIdentityAndSize()
    {
        var channel = new GasChannel();
        try
        {
            channel.Initialize(2, 3);
            channel.Moles![0] = 9f;
            channel.Release();

            channel.Initialize(8, 5);

            Assert.Multiple(() =>
            {
                Assert.That(channel.IsInitialized, Is.True);
                Assert.That(channel.GasId, Is.EqualTo(8));
                Assert.That(channel.Moles, Has.Length.GreaterThanOrEqualTo(5));
                Assert.That(channel.Moles!.Take(5), Is.All.Zero);
            });
        }
        finally
        {
            channel.Release();
        }
    }
}