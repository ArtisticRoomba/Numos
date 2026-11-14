namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class ViewerBuildInfoTests
{
    [Test]
    public void InformationalVersionRevisionMatchesGitCommit()
    {
        // The SDK appends the build's source revision after the first "+", so a mismatch means the commit was overridden.
        string version = ViewerBuildInfo.InformationalVersion;
        int plus = version.IndexOf('+');

        if (plus >= 0)
        {
            Assert.That(version[(plus + 1)..], Is.EqualTo(ViewerBuildInfo.GitCommit));
        }
    }
}
