namespace Numos.CoreSim.Tests;

public class CoreSimBuildInfoTests
{
    [Test]
    public void BuildInfoContainsPackageAndSourceProvenance()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CoreSimBuildInfo.PackageVersion, Does.Match(@"^\d+\.\d+\.\d+"));
            Assert.That(CoreSimBuildInfo.InformationalVersion, Does.StartWith(CoreSimBuildInfo.PackageVersion));
            Assert.That(CoreSimBuildInfo.BuildConfiguration, Is.Not.Empty);
            Assert.That(CoreSimBuildInfo.TargetFramework, Is.EqualTo("net10.0"));
            Assert.That(CoreSimBuildInfo.SdkVersion, Is.Not.Empty);
        });

        if (CoreSimBuildInfo.GitCommit != "unknown")
        {
            Assert.Multiple(() =>
            {
                Assert.That(CoreSimBuildInfo.GitCommit, Does.Match("^[0-9a-f]{40}$"));
                Assert.That(CoreSimBuildInfo.GitCommitShort, Has.Length.EqualTo(12));
                Assert.That(CoreSimBuildInfo.CommitUrl, Does.EndWith(CoreSimBuildInfo.GitCommit));
            });
        }
    }
}