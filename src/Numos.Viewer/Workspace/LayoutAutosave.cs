namespace Numos.Viewer.Workspace;

/// <summary>
///     Decides when a changed window layout gets written. The first change starts a wall-clock delay. Changes during
///     the delay join the same save, so a window drag writes the file once per delay and not once per frame.
/// </summary>
/// <remarks>
///     ImGui has its own save delay, but it counts down frame time and only advances while frames run. A viewer that
///     waits for events stops drawing frames, so ImGui's delay can stall with the save still pending. This class
///     counts wall-clock time instead. The caller keeps frames coming while <see cref="IsPending" /> is set.
/// </remarks>
internal sealed class LayoutAutosave
{
    private TimeSpan _dueAt;

    /// <summary>
    ///     Creates a scheduler that saves <paramref name="delay" /> after the first unsaved change.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delay" /> is negative.</exception>
    public LayoutAutosave(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        Delay = delay;
    }

    /// <summary>
    ///     Time from the first unsaved change to the save.
    /// </summary>
    public TimeSpan Delay { get; }

    /// <summary>
    ///     Whether a change waits to be saved.
    /// </summary>
    public bool IsPending { get; private set; }

    /// <summary>
    ///     Records a layout change. A change while a save is pending does not move the save later.
    /// </summary>
    /// <param name="now">The current time on a monotonic clock.</param>
    public void MarkChanged(TimeSpan now)
    {
        if (IsPending)
            return;

        IsPending = true;
        _dueAt = now + Delay;
    }

    /// <summary>
    ///     Takes the pending save when it is due. The next change after this starts a new delay.
    /// </summary>
    /// <param name="now">The current time on the clock that <see cref="MarkChanged" /> got.</param>
    /// <returns><see langword="true" /> when the caller must write the layout now.</returns>
    public bool TryTakeDueSave(TimeSpan now)
    {
        if (!IsPending || now < _dueAt)
            return false;

        IsPending = false;
        return true;
    }
}
