using Numos.Workbench;

namespace Numos.Viewer.Ui;

/// <summary>
///     Decides whether a newly published <see cref="WorkbenchFrame" /> holds anything the viewer draws differently.
/// </summary>
/// <remarks>
///     The host publishes a frame for every request, even when nothing changed, and the viewer requests one per UI
///     frame. Waking the UI for every new frame would therefore keep a paused viewer drawing at full rate. The host
///     reuses each unchanged section by reference, so reference checks find the frames worth drawing.
/// </remarks>
internal static class FrameChanges
{
    /// <summary>
    ///     Returns whether <paramref name="current" /> differs from <paramref name="previous" /> in anything a panel
    ///     or viewport shows. <see cref="WorkbenchFrame.Sequence" /> alone does not count.
    /// </summary>
    /// <param name="previous">The frame the viewer drew last, or <see langword="null" /> before the first frame.</param>
    /// <param name="current">The latest frame.</param>
    /// <returns><see langword="true" /> when the viewer should draw another frame.</returns>
    public static bool HasVisibleChange(WorkbenchFrame? previous, WorkbenchFrame current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous == null)
            return true;

        return previous.ProjectEpoch != current.ProjectEpoch ||
               previous.RunState != current.RunState ||
               previous.BusyLabel != current.BusyLabel ||
               previous.BusyProgress != current.BusyProgress ||
               previous.TickCount != current.TickCount ||
               previous.TimelineRevision != current.TimelineRevision ||
               previous.DefinitionRevision != current.DefinitionRevision ||
               previous.SimulationCollectionRevision != current.SimulationCollectionRevision ||
               previous.LinkSetRevision != current.LinkSetRevision ||
               previous.TopologyVersion != current.TopologyVersion ||
               previous.ProjectName != current.ProjectName ||
               !ReferenceEquals(previous.Simulations, current.Simulations) ||
               !ReferenceEquals(previous.LinkSets, current.LinkSets) ||
               !ReferenceEquals(previous.SolverSteps, current.SolverSteps) ||
               !ReferenceEquals(previous.Config, current.Config) ||
               !ReferenceEquals(previous.Timeline, current.Timeline);
    }
}
