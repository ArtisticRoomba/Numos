using System.Collections.Concurrent;
using System.Diagnostics;
using Numos.API;
using Numos.CoreSim.Datatypes.Snapshots;

namespace Numos.Workbench;

/// <summary>
///     Runs a <see cref="WorkbenchSession" /> on a thread of its own, so a front end can keep drawing while the world
///     ticks.
/// </summary>
/// <remarks>
///     <para>
///         The host owns the session from construction on. Only the host thread calls it. A front end sends work
///         with <see cref="Submit{T}" /> or <see cref="Post" /> and reads the world through the immutable
///         <see cref="Frame" />. Callbacks run on the front end's thread when it calls
///         <see cref="DrainCompletions" />.
///     </para>
///     <para>
///         Commands run between ticks, in the order they were submitted. That is the rule replay recording needs:
///         the world records an edit at the tick boundary where it applied, so a replay reproduces it. While the host
///         runs, an edit lands on whatever tick the host has reached when it drains the queue.
///     </para>
///     <para>
///         A command closure must capture values copied at submit time and must not touch front-end state. It runs
///         on the host thread while the front end keeps going, so a captured field can change under it.
///     </para>
///     <para>
///         A command that throws comes back as a failed <see cref="HostResult{T}" />. The host logs the failure
///         unless the session already did, and the thread keeps running.
///     </para>
/// </remarks>
/// <example>
///     <code>
/// using var host = new WorkbenchHost(session);
/// var position = new Int3(1, 0, 0);
/// host.Submit(s => s.AddChunk(position), result => ShowError(result.Exception));
/// host.Run();
///
/// // Once per UI frame:
/// host.DrainCompletions();
/// host.RequestFrame(AtmosChunkSnapshotFields.Pressure);
/// WorkbenchFrame frame = host.Frame;
///     </code>
/// </example>
public sealed partial class WorkbenchHost : IDisposable
{
    /// <summary>
    ///     The <see cref="WorkbenchFrame.BusyLabel" /> while a <see cref="Step" /> ticks the live head.
    /// </summary>
    public const string AdvanceJobLabel = "advance";

    /// <summary>
    ///     The <see cref="WorkbenchFrame.BusyLabel" /> of a timeline seek.
    /// </summary>
    public const string SeekJobLabel = "seek";

    /// <summary>
    ///     The name of the host thread.
    /// </summary>
    public const string ThreadName = "Numos simulation";

    private const string LogSource = "Host";

    // AtmosWorld.Update keeps the same backlog. Older wall time is dropped, so a slow world falls behind instead of
    // trying to catch up.
    private const double MaximumBacklogSeconds = 0.25;

    // A batch of ticks stops after this much wall time, so the host can publish a frame even when ticks are slow.
    private const double BatchBudgetSeconds = 1d / 60d;

    // Timeline playback seeks once per batch, at about the rate a front end draws.
    private static readonly TimeSpan PlaybackInterval = TimeSpan.FromSeconds(1d / 60d);
    private static readonly TimeSpan MinimumFaultBackoff = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan MaximumFaultBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(1);

    private readonly ConcurrentQueue<HostCommand> _commands = new();
    private readonly ConcurrentQueue<Action> _completions = new();
    private readonly ManualResetEventSlim _completionReady = new(false);
    private readonly WorkbenchSession _session;
    private readonly Lock _submitGate = new();
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);

    // The host thread owns every field below that is not volatile and not read through Volatile or Interlocked.
    private AdvanceJob? _advance;
    private double _accumulator;
    private volatile bool _disposed;
    private WorkbenchFrame _frame;
    private int _frameRequested;
    private AtmosWorld? _knownWorld;
    private long _lastRealtimeTimestamp;
    private PendingSeek? _pendingSeek;
    private long _projectEpoch;
    private int _requestedFields;
    private bool _running;
    private volatile bool _stopping;

    /// <summary>
    ///     Takes ownership of a session and starts the host thread.
    /// </summary>
    /// <param name="session">
    ///     The session to run. The host disposes it. Do not call it from any other thread after this point.
    /// </param>
    /// <remarks>
    ///     The host starts paused. <see cref="Frame" /> holds a first frame before the constructor returns.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="session" /> is <see langword="null" />.</exception>
    public WorkbenchHost(WorkbenchSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _knownWorld = session.World;
        _frame = BuildFrame(AtmosChunkSnapshotFields.None);
        // A dedicated thread, because the solver's parallel phases use the thread pool.
        _thread = new Thread(Loop) { Name = ThreadName, IsBackground = true };
        _thread.Start();
    }

    /// <summary>
    ///     The session log. It is thread-safe, so a front end can read it directly.
    /// </summary>
    public WorkbenchLog Log => _session.Log;

    /// <summary>
    ///     The latest published frame.
    /// </summary>
    /// <remarks>
    ///     The host replaces it after <see cref="RequestFrame" />. Read it once per UI frame and keep the reference
    ///     for that frame, so every panel draws the same state.
    /// </remarks>
    public WorkbenchFrame Frame => Volatile.Read(ref _frame);

    /// <summary>
    ///     Counts project replacements. It increases when a command creates, opens, or closes a project.
    /// </summary>
    /// <remarks>
    ///     The value can be ahead of <see cref="WorkbenchFrame.ProjectEpoch" /> until the next frame is published.
    /// </remarks>
    public long ProjectEpoch => Interlocked.Read(ref _projectEpoch);

    /// <summary>
    ///     Queues work for the host thread.
    /// </summary>
    /// <param name="work">
    ///     The work. It runs between ticks, after every command submitted before it. It must not capture front-end
    ///     state.
    /// </param>
    /// <param name="onComplete">
    ///     Receives the result on the thread that calls <see cref="DrainCompletions" />, or <see langword="null" />.
    /// </param>
    /// <typeparam name="T">The return type of <paramref name="work" />.</typeparam>
    /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
    public void Submit<T>(Func<WorkbenchSession, T> work, Action<HostResult<T>>? onComplete = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        Enqueue(new WorkCommand<T>((session, _) => work(session), onComplete, null));
    }

    /// <summary>
    ///     Queues a long job that reports itself in <see cref="Frame" /> while it runs.
    /// </summary>
    /// <param name="label">The <see cref="WorkbenchFrame.BusyLabel" /> while the job runs.</param>
    /// <param name="work">
    ///     The job. The second argument takes the finished fraction from 0 to 1. Call it from the host thread.
    /// </param>
    /// <param name="onComplete">
    ///     Receives the result on the thread that calls <see cref="DrainCompletions" />, or <see langword="null" />.
    /// </param>
    /// <typeparam name="T">The return type of <paramref name="work" />.</typeparam>
    /// <remarks>
    ///     The host cannot tick, drain commands, or build a full frame while a job runs. It publishes copies of the
    ///     last frame with the busy state and progress instead, so a front end can show what it is waiting for.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
    public void SubmitJob<T>(
        string label,
        Func<WorkbenchSession, Action<double>, T> work,
        Action<HostResult<T>>? onComplete = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(work);
        Enqueue(new WorkCommand<T>(work, onComplete, label));
    }

    /// <summary>
    ///     Queues work whose result nobody waits for. A failure goes to the log.
    /// </summary>
    /// <param name="work">The work. The rules of <see cref="Submit{T}" /> apply.</param>
    /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
    public void Post(Action<WorkbenchSession> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Enqueue(new WorkCommand<bool>(
            (session, _) =>
            {
                work(session);
                return true;
            },
            null,
            null));
    }

    /// <summary>
    ///     Runs the queued completion callbacks on the calling thread.
    /// </summary>
    /// <returns>The number of callbacks that ran.</returns>
    /// <remarks>
    ///     Call it from one thread, usually at the top of a front end's update. A callback that throws stops the
    ///     drain, and the remaining callbacks run on the next call.
    /// </remarks>
    public int DrainCompletions()
    {
        _completionReady.Reset();
        int count = 0;
        try
        {
            while (_completions.TryDequeue(out var completion))
            {
                count++;
                completion();
            }
        }
        finally
        {
            if (!_completions.IsEmpty)
                _completionReady.Set();
        }

        return count;
    }

    /// <summary>
    ///     Blocks until a completion callback is waiting for <see cref="DrainCompletions" />.
    /// </summary>
    /// <param name="timeout">How long to wait.</param>
    /// <returns><see langword="true" /> when a callback is waiting.</returns>
    /// <remarks>
    ///     A client without a frame loop, such as a script runner, uses this to wait for a result.
    /// </remarks>
    public bool WaitForCompletions(TimeSpan timeout)
    {
        return _completionReady.Wait(timeout);
    }

    /// <summary>
    ///     Advances the world with wall-clock time until <see cref="Pause" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The host ticks one tick at a time and drains commands between ticks, so a pause or an edit waits at
    ///         most one tick. It keeps at most 0.25 s of wall time as backlog. When ticks take longer than real time
    ///         the simulation falls behind and <see cref="WorkbenchFrame.RealtimeFactor" /> drops below 1.
    ///     </para>
    ///     <para>
    ///         While the timeline inspects history, the host plays the recording back in real time and pauses at the
    ///         head. Run stops a <see cref="Step" /> in progress. The command runs in submission order.
    ///     </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
    public void Run()
    {
        Enqueue(new ControlCommand(host => host.StartRunning()));
    }

    /// <summary>
    ///     Stops <see cref="Run" /> and any <see cref="Step" /> in progress.
    /// </summary>
    /// <remarks>
    ///     The command runs in submission order, between two ticks. No tick runs after it until the next
    ///     <see cref="Run" /> or <see cref="Step" />.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
    public void Pause()
    {
        Enqueue(new ControlCommand(host => host.StopRunning()));
    }

    /// <summary>
    ///     Pauses and then advances a number of ticks.
    /// </summary>
    /// <param name="count">How many ticks to advance. Must be at least 1.</param>
    /// <param name="onComplete">
    ///     Receives the tick count when the step ends, or <see langword="null" />. The step ends when it is done,
    ///     when <see cref="Pause" /> or <see cref="Run" /> stops it, or when a tick fails.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         On the live head the host ticks one tick at a time and drains commands between ticks. A step therefore
    ///         resumes after other commands, and a pause stops it within one tick. A second step that arrives while
    ///         one runs adds its ticks to it. The frame reports <see cref="AdvanceJobLabel" /> until it ends.
    ///     </para>
    ///     <para>
    ///         While the timeline inspects history, the step seeks forward instead and stops at the head.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is less than 1.</exception>
    /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
    public void Step(int count = 1, Action<HostResult<long>>? onComplete = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        Enqueue(new ControlCommand(
            host => host.StartStep(count, onComplete),
            (host, reason) => host.CompleteStep(onComplete, new HostResult<long>(0, reason))));
    }

    /// <summary>
    ///     Seeks the timeline to the start of a tick, dropping any earlier seek that has not started yet.
    /// </summary>
    /// <param name="tick">The completed tick to show.</param>
    /// <remarks>
    ///     A scrub bar calls this every frame. The host keeps only the newest target, so a slow seek never builds a
    ///     queue. The seek runs after the commands queued when the host next drains. A failure goes to the log.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tick" /> is above <see cref="long.MaxValue" />.</exception>
    /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
    public void SeekLatest(ulong tick)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tick, (ulong)long.MaxValue);
        lock (_submitGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Interlocked.Exchange(ref _pendingSeek, new PendingSeek((long)tick, Interlocked.Read(ref _projectEpoch)));
            _wake.Set();
        }
    }

    /// <summary>
    ///     Asks the host to publish a new <see cref="Frame" />.
    /// </summary>
    /// <param name="fields">The chunk snapshot fields the front end draws.</param>
    /// <remarks>
    ///     The host publishes at most one frame per request, after its current batch of ticks. A host nobody asks
    ///     builds no frames, so a hidden or paused front end costs nothing. Call this once per UI frame.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
    public void RequestFrame(AtmosChunkSnapshotFields fields)
    {
        lock (_submitGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Volatile.Write(ref _requestedFields, (int)fields);
            Interlocked.Exchange(ref _frameRequested, 1);
            _wake.Set();
        }
    }

    /// <summary>
    ///     Stops the host thread, waits for it, and disposes the session.
    /// </summary>
    /// <remarks>
    ///     The host finishes the command or tick it is running first. Commands still queued complete as failed with
    ///     <see cref="ObjectDisposedException" />. Their callbacks run on the next <see cref="DrainCompletions" />.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Called from a command on the host thread.</exception>
    public void Dispose()
    {
        if (Thread.CurrentThread == _thread)
            throw new InvalidOperationException("A host command cannot dispose its own host.");

        lock (_submitGate)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        _stopping = true;
        _wake.Set();
        _thread.Join();

        var disposed = new ObjectDisposedException(nameof(WorkbenchHost));
        while (_commands.TryDequeue(out var command))
            command.Cancel(this, disposed);

        if (_advance != null)
            FinishAdvance(disposed);

        _session.Dispose();

        // Every setter of _wake holds _submitGate and checks _disposed first, so none can run after the flag is set.
        // _completionReady stays undisposed on purpose. A front end still calls DrainCompletions and
        // WaitForCompletions after Dispose to collect the cancelled commands, and a disposed event would throw.
        _wake.Dispose();
    }

    private void Enqueue(HostCommand command)
    {
        lock (_submitGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _commands.Enqueue(command);
            _wake.Set();
        }
    }

    private void Complete(Action completion)
    {
        _completions.Enqueue(completion);
        _completionReady.Set();
    }

    private void Loop()
    {
        string? lastFault = null;
        var backoff = TimeSpan.Zero;
        while (!_stopping)
        {
            try
            {
                DrainCommands();
                if (_stopping)
                    break;

                TimeSpan wait = Advance();
                PublishIfRequested();
                if (wait != TimeSpan.Zero)
                    _wake.WaitOne(wait);

                lastFault = null;
                backoff = TimeSpan.Zero;
            }
            catch (Exception exception)
            {
                // A command or tick failure never reaches here. This is a host fault, so stop ticking and keep
                // serving commands. A fault that repeats on every iteration logs once and slows the loop down, so
                // it cannot flood the log or spin a core. A wake signal still cuts the wait short.
                _running = false;
                string fault = exception.GetType().FullName + ": " + exception.Message;
                if (fault != lastFault)
                {
                    lastFault = fault;
                    Log.Write(
                        WorkbenchLogLevel.Error,
                        LogSource,
                        $"The simulation host stopped running after an error: {exception.Message}",
                        exception.ToString());
                }

                backoff = backoff == TimeSpan.Zero
                    ? MinimumFaultBackoff
                    : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaximumFaultBackoff.Ticks));
                if (!_stopping)
                    _wake.WaitOne(backoff);
            }
        }
    }

    private bool HasPendingCommands()
    {
        return _stopping || !_commands.IsEmpty || Volatile.Read(ref _pendingSeek) != null;
    }

    private void DrainCommands()
    {
        while (!_stopping && _commands.TryDequeue(out var command))
        {
            command.Execute(this, _session.FailureCount);
            CheckProject();
        }

        if (_stopping)
            return;

        // A command can replace the project. A scrub target set against the old project must not seek the new one,
        // so the target carries the epoch it was set in. A target set after the replacement has the new epoch.
        var pending = Interlocked.Exchange(ref _pendingSeek, null);
        if (pending == null || pending.Epoch != Interlocked.Read(ref _projectEpoch))
            return;

        long target = pending.Tick;

        long failures = _session.FailureCount;
        BeginBusy(SeekJobLabel);
        try
        {
            _session.SeekTick((ulong)target);
        }
        catch (Exception exception)
        {
            LogFailure(exception, failures);
        }
        finally
        {
            EndBusy();
        }

        CheckProject();
    }

    /// <summary>
    ///     Runs one batch of the current step or of real-time ticking.
    /// </summary>
    /// <returns>How long the loop can wait for a signal before the next batch is due.</returns>
    private TimeSpan Advance()
    {
        if (_advance != null)
        {
            RunAdvance();
            return TimeSpan.Zero;
        }

        var world = _session.World;
        if (!_running || world == null)
        {
            _realtimeFactor = 0d;
            return Timeout.InfiniteTimeSpan;
        }

        long now = Stopwatch.GetTimestamp();
        double elapsed = Stopwatch.GetElapsedTime(_lastRealtimeTimestamp, now).TotalSeconds;
        _lastRealtimeTimestamp = now;
        if (_session.IsInspecting)
        {
            RunPlayback(elapsed);
            MeasureRealtime(world, now);
            return PlaybackInterval;
        }

        double step = 1d / world.TickRate;
        _accumulator = Math.Min(_accumulator + elapsed, MaximumBacklogSeconds);
        bool ticked = false;
        while (HasWholeTick(world.TickRate))
        {
            if (HasPendingCommands())
                break;

            _accumulator = Math.Max(0d, _accumulator - step);
            long failures = _session.FailureCount;
            try
            {
                _session.TickLive(false);
            }
            catch (Exception exception)
            {
                // The session logs a failure of the tick itself. A failure before that is logged here.
                LogFailure(exception, failures);
                StopRunning();
                return Timeout.InfiniteTimeSpan;
            }

            ticked = true;
            if (Stopwatch.GetElapsedTime(now).TotalSeconds >= BatchBudgetSeconds)
                break;
        }

        // The viewer sampled checkpoints once per frame before the host existed. One sample per batch keeps that.
        if (ticked)
            _session.ObserveLiveState();

        MeasureRealtime(world, Stopwatch.GetTimestamp());
        if (HasWholeTick(world.TickRate))
            return TimeSpan.Zero;

        var wait = TimeSpan.FromSeconds(step - _accumulator);
        return wait < MinimumWait ? MinimumWait : wait;
    }

    // Repeated subtraction leaves rounding error, so a tick counts as whole within the tolerance AtmosWorld uses.
    private bool HasWholeTick(int tickRate)
    {
        return _accumulator * tickRate >= 1d - 1e-9;
    }

    private void RunPlayback(double elapsed)
    {
        int due = _session.AccumulatePlayback((float)elapsed);
        if (due == 0)
            return;

        var timeline = _session.Timeline!;
        ulong position = timeline.Position.Tick;
        ulong head = timeline.Head.Tick;
        if (position >= head)
        {
            StopRunning();
            return;
        }

        long failures = _session.FailureCount;
        try
        {
            _session.SeekTick(position + Math.Min((ulong)due, head - position));
        }
        catch (Exception exception)
        {
            LogFailure(exception, failures);
            StopRunning();
        }
    }

    private void RunAdvance()
    {
        var job = _advance!;
        long start = Stopwatch.GetTimestamp();
        while (job.Done < job.Total)
        {
            if (HasPendingCommands())
                return;

            // A seek that landed while the step ran leaves the live head. The step ends with the ticks done so far.
            if (_session.IsInspecting)
            {
                FinishAdvance(null);
                return;
            }

            long failures = _session.FailureCount;
            try
            {
                _session.TickLive(true);
            }
            catch (Exception exception)
            {
                // The session logs a failure of the tick itself. A failure before that, such as a missing world,
                // is logged here.
                LogFailure(exception, failures);
                FinishAdvance(exception);
                return;
            }

            job.Done++;
            if (Stopwatch.GetElapsedTime(start).TotalSeconds >= BatchBudgetSeconds)
                return;
        }

        FinishAdvance(null);
    }

    private void StartRunning()
    {
        if (_advance != null)
            FinishAdvance(null);

        _running = true;
        _accumulator = 0d;
        _lastRealtimeTimestamp = Stopwatch.GetTimestamp();
        ResetRealtimeWindow();
    }

    private void StopRunning()
    {
        _running = false;
        _realtimeFactor = 0d;
        if (_advance != null)
            FinishAdvance(null);
    }

    private void StartStep(int count, Action<HostResult<long>>? onComplete)
    {
        _running = false;
        _realtimeFactor = 0d;
        var world = _session.World;
        if (world == null)
        {
            CompleteStep(onComplete, new HostResult<long>(0, new InvalidOperationException("No project is open.")));
            return;
        }

        if (_session.IsInspecting)
        {
            CompleteStep(onComplete, SeekForward(count));
            return;
        }

        if (_advance != null)
        {
            _advance.Total += count;
        }
        else
        {
            _advance = new AdvanceJob(count, Stopwatch.GetTimestamp());
        }

        if (onComplete != null)
            _advance.Callbacks.Add(onComplete);
    }

    private HostResult<long> SeekForward(int count)
    {
        var timeline = _session.Timeline!;
        ulong position = timeline.Position.Tick;
        ulong head = timeline.Head.Tick;
        if (position >= head)
            return new HostResult<long>((long)position, null);

        long failures = _session.FailureCount;
        BeginBusy(SeekJobLabel);
        try
        {
            _session.SeekTick(position + Math.Min((ulong)count, head - position));
            return new HostResult<long>((long)_session.Timeline!.Position.Tick, null);
        }
        catch (Exception exception)
        {
            LogFailure(exception, failures);
            return new HostResult<long>(0, exception);
        }
        finally
        {
            EndBusy();
        }
    }

    private void CompleteStep(Action<HostResult<long>>? onComplete, HostResult<long> result)
    {
        if (onComplete != null)
            Complete(() => onComplete(result));
    }

    private void FinishAdvance(Exception? error)
    {
        var job = _advance!;
        _advance = null;
        long tick = _session.World?.TickCount ?? 0;
        if (error == null && job.Total > 1)
        {
            double seconds = Stopwatch.GetElapsedTime(job.StartTimestamp).TotalSeconds;
            string message = job.Done == job.Total
                ? $"Advanced {job.Total} ticks to tick {tick} in {seconds:F2} s."
                : $"Stopped after {job.Done} of {job.Total} ticks at tick {tick}.";

            Log.Write(WorkbenchLogLevel.Info, LogSource, message, null, tick);
        }

        var result = new HostResult<long>(error == null ? tick : 0, error);
        foreach (var callback in job.Callbacks)
            CompleteStep(callback, result);
    }

    /// <summary>
    ///     Notices a created, opened, or closed project and drops everything that belonged to the old one.
    /// </summary>
    private void CheckProject()
    {
        var world = _session.World;
        if (ReferenceEquals(world, _knownWorld))
            return;

        _knownWorld = world;
        Interlocked.Increment(ref _projectEpoch);
        _running = false;
        _realtimeFactor = 0d;
        _accumulator = 0d;
        _cache = new FrameCache();
        if (_advance != null)
            FinishAdvance(new InvalidOperationException("The project changed before the step finished."));
    }

    private void LogFailure(Exception exception, long failuresBefore)
    {
        if (_session.FailureCount != failuresBefore)
            return;

        Log.Write(
            WorkbenchLogLevel.Error,
            LogSource,
            $"A host command failed: {exception.Message}",
            exception.ToString(),
            _session.World?.TickCount);
    }

    private abstract class HostCommand
    {
        public abstract void Execute(WorkbenchHost host, long failuresBefore);

        public abstract void Cancel(WorkbenchHost host, Exception reason);
    }

    private sealed class WorkCommand<T>(
        Func<WorkbenchSession, Action<double>, T> work,
        Action<HostResult<T>>? onComplete,
        string? label) : HostCommand
    {
        public override void Execute(WorkbenchHost host, long failuresBefore)
        {
            HostResult<T> result;
            if (label != null)
                host.BeginBusy(label);

            try
            {
                result = new HostResult<T>(work(host._session, host.ReportProgress), null);
            }
            catch (Exception exception)
            {
                host.LogFailure(exception, failuresBefore);
                result = new HostResult<T>(default, exception);
            }
            finally
            {
                if (label != null)
                    host.EndBusy();
            }

            if (onComplete != null)
                host.Complete(() => onComplete(result));
        }

        public override void Cancel(WorkbenchHost host, Exception reason)
        {
            if (onComplete != null)
                host.Complete(() => onComplete(new HostResult<T>(default, reason)));
        }
    }

    private sealed class ControlCommand(
        Action<WorkbenchHost> action,
        Action<WorkbenchHost, Exception>? cancel = null) : HostCommand
    {
        public override void Execute(WorkbenchHost host, long failuresBefore)
        {
            action(host);
        }

        public override void Cancel(WorkbenchHost host, Exception reason)
        {
            cancel?.Invoke(host, reason);
        }
    }

    private sealed record PendingSeek(long Tick, long Epoch);

    private sealed class AdvanceJob(long total, long startTimestamp)
    {
        public long Total { get; set; } = total;
        public long Done { get; set; }
        public long StartTimestamp { get; } = startTimestamp;
        public List<Action<HostResult<long>>> Callbacks { get; } = [];
    }
}
