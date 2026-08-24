using Numos.Build;

namespace Numos.CoreSim;

/// <summary>
///     Describes the version and source provenance embedded in CoreSim.
/// </summary>
public static class CoreSimBuildInfo
{
    /// <summary>
    ///     Gets the NuGet package version associated with this build.
    /// </summary>
    public static string PackageVersion => GeneratedBuildInfo.PackageVersion;

    /// <summary>
    ///     Gets the informational version, including the source revision when available.
    /// </summary>
    public static string InformationalVersion => GeneratedBuildInfo.InformationalVersion;

    /// <summary>
    ///     Gets the complete Git commit hash, or <c>unknown</c> when unavailable.
    /// </summary>
    public static string GitCommit => GeneratedBuildInfo.GitCommit;

    /// <summary>
    ///     Gets an abbreviated Git commit hash suitable for display.
    /// </summary>
    public static string GitCommitShort => GitCommit.Length > 12 ? GitCommit[..12] : GitCommit;

    /// <summary>
    ///     Gets the complete source reference captured at build time.
    /// </summary>
    public static string SourceReference => GeneratedBuildInfo.SourceReference;

    /// <summary>
    ///     Gets the normalized Git branch, or <see langword="null" /> for tags and detached builds.
    /// </summary>
    public static string? GitBranch => SourceReference.StartsWith("refs/heads/", StringComparison.Ordinal)
        ? SourceReference["refs/heads/".Length..]
        : null;

    /// <summary>
    ///     Gets the canonical source repository URL.
    /// </summary>
    public static string RepositoryUrl => GeneratedBuildInfo.RepositoryUrl;

    /// <summary>
    ///     Gets a URL for the exact source commit, or <see langword="null" /> when unavailable.
    /// </summary>
    public static string? CommitUrl => IsKnown(RepositoryUrl) && IsKnown(GitCommit)
        ? $"{RepositoryUrl.TrimEnd('/')}/commit/{GitCommit}"
        : null;

    /// <summary>
    ///     Gets the MSBuild configuration used to create the assembly.
    /// </summary>
    public static string BuildConfiguration => GeneratedBuildInfo.BuildConfiguration;

    /// <summary>
    ///     Gets the target framework used to create the assembly.
    /// </summary>
    public static string TargetFramework => GeneratedBuildInfo.TargetFramework;

    /// <summary>
    ///     Gets the .NET SDK version used to create the assembly.
    /// </summary>
    public static string SdkVersion => GeneratedBuildInfo.SdkVersion;

    /// <summary>
    ///     Gets whether the assembly was produced as a continuous-integration build.
    /// </summary>
    public static bool IsContinuousIntegrationBuild => GeneratedBuildInfo.IsContinuousIntegrationBuild;

    private static bool IsKnown(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "unknown", StringComparison.Ordinal);
    }
}