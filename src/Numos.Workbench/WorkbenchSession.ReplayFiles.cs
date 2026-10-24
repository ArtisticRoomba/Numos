using System.Diagnostics;
using Numos.API;
using Numos.CoreSim;
using Numos.Serialization;
using Numos.Serialization.FileSystem;

namespace Numos.Workbench;

/// <summary>
///     A replay read from disk and rebuilt into a world, waiting to be installed.
/// </summary>
/// <param name="Path">The full path the replay came from.</param>
/// <param name="Document">The file contents.</param>
/// <param name="World">The rebuilt world. The holder owns it until <see cref="WorkbenchSession.InstallReplay" /> succeeds.</param>
/// <param name="Config">The configuration the world was built from.</param>
/// <param name="Timeline">The indexed timeline of the world.</param>
public sealed record LoadedReplay(
    string Path,
    NumosWorldReplayDocument Document,
    AtmosWorld World,
    AtmosConfig Config,
    AtmosWorldReplayTimeline Timeline) : IDisposable
{
    /// <summary>
    ///     Disposes the world. Call it when the replay is never installed.
    /// </summary>
    public void Dispose()
    {
        World.Dispose();
    }
}

/// <summary>
///     A replay captured and ready to write. The capture is already done, so <see cref="Execute" /> can run on a
///     worker thread.
/// </summary>
public sealed class ReplaySaveJob
{
    private readonly NumosWorldReplayDocument _document;
    private readonly bool _overwrite;

    internal ReplaySaveJob(string path, NumosWorldReplayDocument document, bool overwrite)
    {
        Path = path;
        _document = document;
        _overwrite = overwrite;
    }

    /// <summary>
    ///     The full destination path.
    /// </summary>
    public string Path { get; }

    /// <summary>
    ///     Writes the file atomically.
    /// </summary>
    /// <exception cref="IOException">The file exists and overwriting is off, or the write failed.</exception>
    public void Execute()
    {
        NumosReplayFile.Save(Path, _document, _overwrite);
    }
}

public sealed partial class WorkbenchSession
{
    /// <summary>
    ///     Reads a replay file and rebuilds its world and timeline.
    /// </summary>
    /// <param name="path">The `.numos` file.</param>
    /// <param name="progress">Receives indexing progress. It can be called from any thread.</param>
    /// <param name="cancellation">Cancels indexing.</param>
    /// <returns>A replay to pass to <see cref="InstallReplay" />. The caller disposes it if it is never installed.</returns>
    /// <remarks>
    ///     This method reads no session state, so it can run on a worker thread while the session ticks.
    /// </remarks>
    public LoadedReplay LoadReplay(
        string path,
        IProgress<AtmosWorldReplayIndexProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        string fullPath = Path.GetFullPath(path);
        Log.Write(WorkbenchLogLevel.Info, ReplaySource, $"Loading replay '{fullPath}'.");
        long start = Stopwatch.GetTimestamp();
        AtmosWorld? world = null;
        try
        {
            var document = NumosReplayFile.Load(fullPath);
            var replay = document.Replay;
            var config = new AtmosConfig(replay.InitialCheckpoint.Config);
            world = new AtmosWorld(config);
            var timeline = AtmosWorldReplayTimeline.Import(world, replay, 50, progress, cancellation);
            var result = new LoadedReplay(fullPath, document, world, config, timeline);
            world = null;
            string verified = timeline.IsVerified switch
            {
                true => "verified",
                false => "DIVERGENT",
                null => "unverified"
            };

            Log.Write(
                WorkbenchLogLevel.Info,
                ReplaySource,
                $"Read replay '{Path.GetFileName(fullPath)}': ticks {timeline.Start.Tick} to {timeline.Head.Tick}, " +
                $"{verified}, {Stopwatch.GetElapsedTime(start).TotalSeconds:F2} s.");

            return result;
        }
        catch (OperationCanceledException)
        {
            Log.Write(WorkbenchLogLevel.Warn, ReplaySource, "Replay loading was cancelled.");
            throw;
        }
        catch (Exception exception)
        {
            Log.Write(
                WorkbenchLogLevel.Error,
                ReplaySource,
                $"Could not load the replay: {exception.Message}",
                exception.ToString());

            throw;
        }
        finally
        {
            world?.Dispose();
        }
    }

    /// <summary>
    ///     Replaces the open project with a loaded replay.
    /// </summary>
    /// <param name="loaded">The replay. The session owns its world once this method returns normally.</param>
    /// <param name="prepare">
    ///     Runs against the replay's configuration before the old project closes. If it throws, the old project stays
    ///     open and the caller still owns <paramref name="loaded" />.
    /// </param>
    /// <param name="atHead">
    ///     When <see langword="true" />, the session moves to the head of the replay and continues its branch, so the
    ///     next <see cref="Tick" /> extends the branch. When <see langword="false" />, the session inspects the replay.
    /// </param>
    public void InstallReplay(LoadedReplay loaded, Action<AtmosConfig>? prepare = null, bool atHead = false)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(loaded);
        try
        {
            prepare?.Invoke(loaded.Config);
            string name = string.IsNullOrWhiteSpace(loaded.Document.Metadata.ProjectName)
                ? Path.GetFileNameWithoutExtension(loaded.Path)
                : loaded.Document.Metadata.ProjectName;

            Open(loaded.World, loaded.Config, name, loaded.Timeline, null);
            Info($"Loaded and verified replay '{Path.GetFileName(loaded.Path)}'.");
            if (atHead)
            {
                ReturnToHead();
                ContinueBranch();
            }
        }
        catch (Exception exception)
        {
            Failed("Could not install the replay", exception);
            throw;
        }
    }

    /// <summary>
    ///     Captures the selected branch for saving. Run on the session's thread.
    /// </summary>
    /// <param name="path">The destination. `.numos` is appended when missing.</param>
    /// <param name="throughCurrentPosition">
    ///     Save only up to the inspected position instead of the whole branch.
    /// </param>
    /// <param name="overwrite">Whether an existing file may be replaced.</param>
    /// <returns>A job whose <see cref="ReplaySaveJob.Execute" /> writes the file.</returns>
    public ReplaySaveJob PrepareReplaySave(string path, bool throughCurrentPosition, bool overwrite)
    {
        return Guard(
            "Could not prepare the replay for saving",
            () =>
            {
                RequireWorld();
                string fullPath = ReplayPaths.Normalize(path);
                var replay = Branches!.CaptureSelectedBranch(throughCurrentPosition);
                replay.EnsurePortable();
                var metadata = new NumosReplayMetadata(
                    ProjectName ?? Path.GetFileNameWithoutExtension(fullPath),
                    DateTimeOffset.UtcNow,
                    Host.Name,
                    Host.Version,
                    CoreSimBuildInfo.PackageVersion,
                    CoreSimBuildInfo.GitCommit ?? CoreSimBuildInfo.SourceReference);

                return new ReplaySaveJob(fullPath, new NumosWorldReplayDocument(metadata, replay), overwrite);
            });
    }

    /// <summary>
    ///     Captures the selected branch and writes it to disk.
    /// </summary>
    /// <param name="path">The destination. `.numos` is appended when missing.</param>
    /// <param name="throughCurrentPosition">Save only up to the inspected position.</param>
    /// <param name="overwrite">Whether an existing file may be replaced.</param>
    /// <returns>The full path written.</returns>
    public string SaveReplay(string path, bool throughCurrentPosition = false, bool overwrite = false)
    {
        var job = PrepareReplaySave(path, throughCurrentPosition, overwrite);
        WriteReplay(job, throughCurrentPosition);
        return job.Path;
    }

    /// <summary>
    ///     Writes a prepared job and logs the result. Safe to call from a worker thread.
    /// </summary>
    /// <param name="job">The job from <see cref="PrepareReplaySave" />.</param>
    /// <param name="throughCurrentPosition">Only for the log line. Pass the value given to the job.</param>
    public void WriteReplay(ReplaySaveJob job, bool throughCurrentPosition = false)
    {
        try
        {
            job.Execute();
            Log.Write(
                WorkbenchLogLevel.Info,
                ReplaySource,
                $"Saved replay to '{job.Path}' ({(throughCurrentPosition ? "selected branch up to the inspected position" : "full selected branch")}).");
        }
        catch (Exception exception)
        {
            Log.Write(
                WorkbenchLogLevel.Error,
                ReplaySource,
                $"Could not save the replay: {exception.Message}",
                exception.ToString());

            throw;
        }
    }
}
