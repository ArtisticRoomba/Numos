using Numos.API;

namespace Numos.Viewer.Workspace;

/// <summary>
///     One endpoint of the new portal form.
/// </summary>
internal enum PortalEndpoint
{
    None,
    First,
    Second
}

/// <summary>
///     The viewport pick that fills an endpoint of the new portal form in the Topology properties.
/// </summary>
/// <remarks>
///     <para>
///         Picking a voxel normally selects its chunk, and clicking another simulation's viewport selects that
///         simulation. Either one would take Properties away from the portal form between the two picks. While a
///         pick is armed, a voxel click fills the armed endpoint and leaves the selection alone. A click on another
///         simulation's viewport only activates that simulation.
///     </para>
///     <para>
///         The pick ends when both endpoints hold a cell, when the user cancels it, or when
///         <see cref="Reconcile" /> sees that the form is gone.
///     </para>
/// </remarks>
internal sealed class PortalEndpointCapture
{
    /// <summary>
    ///     The endpoint the next voxel click fills, or <see cref="PortalEndpoint.None" />.
    /// </summary>
    public PortalEndpoint Target { get; private set; }

    public bool IsArmed => Target != PortalEndpoint.None;

    /// <summary>
    ///     Arms the pick for <paramref name="endpoint" />. The endpoint keeps its old cell until a pick replaces it.
    /// </summary>
    public void Arm(PortalEndpoint endpoint)
    {
        Target = endpoint;
    }

    public void Cancel()
    {
        Target = PortalEndpoint.None;
    }

    /// <summary>
    ///     Writes a picked cell into the armed endpoint of <paramref name="draft" />. The pick then moves to the other
    ///     endpoint when that one is empty, and ends otherwise.
    /// </summary>
    /// <param name="cell">The picked voxel.</param>
    /// <param name="draft">The topology draft that holds the endpoints.</param>
    /// <returns>The endpoint that received the cell, or <see cref="PortalEndpoint.None" /> when no pick is armed.</returns>
    public PortalEndpoint Accept(AtmosCellRef cell, TopologyDraft draft)
    {
        var filled = Target;
        switch (filled)
        {
            case PortalEndpoint.First:
                draft.PortalFirst = cell;
                Target = draft.PortalSecond.HasValue ? PortalEndpoint.None : PortalEndpoint.Second;
                break;
            case PortalEndpoint.Second:
                draft.PortalSecond = cell;
                Target = draft.PortalFirst.HasValue ? PortalEndpoint.None : PortalEndpoint.First;
                break;
        }

        return filled;
    }

    /// <summary>
    ///     Ends the pick when the form that armed it is no longer shown. Call it once per UI frame after the selection
    ///     is reconciled.
    /// </summary>
    /// <param name="selection">The selected tree node. The form shows only while it is the Topology node.</param>
    /// <param name="inspecting">Whether the timeline inspects history, which makes the form read-only.</param>
    /// <returns><see langword="true" /> when the pick ended.</returns>
    public bool Reconcile(TreeNodeId? selection, bool inspecting)
    {
        if (!IsArmed || (selection == TreeNodeId.Topology && !inspecting))
            return false;

        Cancel();
        return true;
    }
}
