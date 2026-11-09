namespace Numos.Viewer.Workspace;

/// <summary>
///     The one selection of a project. The tree, Properties, and viewport picking all read and write it.
/// </summary>
/// <remarks>
///     <para>
///         The selection is an id, not a node, so it survives tree rebuilds. <see cref="Reconcile" /> handles a node
///         that leaves the tree. On a live timeline the selection moves to the nearest ancestor that still exists.
///         While the timeline inspects history it keeps the missing node, so returning to the head restores it.
///     </para>
///     <para>
///         A command result can select a node that the next frame will add, such as a new portal. That selection
///         waits for the frame instead of falling back at once.
///     </para>
/// </remarks>
internal sealed class WorkspaceSelection
{
    private long _pendingThroughSequence = long.MinValue;

    /// <summary>
    ///     The selected node, or <see langword="null" />.
    /// </summary>
    public TreeNodeId? Current { get; private set; }

    /// <summary>
    ///     The selected node as the tree last held it, or <see langword="null" /> before the tree has listed it. It
    ///     stays while the node is missing, so Properties can still name it.
    /// </summary>
    public TreeNode? Node { get; private set; }

    /// <summary>
    ///     The ids above <see cref="Current" />, root first. The fallback walks them from the end.
    /// </summary>
    public IReadOnlyList<TreeNodeId> Ancestors { get; private set; } = [];

    /// <summary>
    ///     Counts changes of <see cref="Current" />, so a reader can tell that the selection moved.
    /// </summary>
    public long Revision { get; private set; }

    /// <summary>
    ///     Whether the tree lacks <see cref="Current" /> as of the last <see cref="Select" /> or
    ///     <see cref="Reconcile" />.
    /// </summary>
    public bool IsMissing { get; private set; }

    /// <summary>
    ///     Selects a node, or clears the selection when <paramref name="id" /> is <see langword="null" />.
    /// </summary>
    /// <param name="id">The node to select.</param>
    /// <param name="tree">The tree to resolve the node and its ancestors in.</param>
    /// <param name="pendingThroughSequence">
    ///     For a node the tree does not list yet: the last frame sequence that may still lack it. The selection keeps
    ///     the node until the tree reads a later frame.
    /// </param>
    /// <returns><see langword="true" /> when <see cref="Current" /> changed.</returns>
    public bool Select(TreeNodeId? id, SimulationTree tree, long pendingThroughSequence = long.MinValue)
    {
        bool changed = Current != id;
        Current = id;
        if (changed)
            Revision++;

        if (id is not { } value)
        {
            Node = null;
            Ancestors = [];
            IsMissing = false;
            _pendingThroughSequence = long.MinValue;
            return changed;
        }

        if (tree.TryGetNode(value, out var node))
        {
            Resolve(value, node, tree);
            return changed;
        }

        if (changed)
        {
            Node = null;
            Ancestors = GetCanonicalAncestors(value);
        }

        IsMissing = true;
        _pendingThroughSequence = Math.Max(pendingThroughSequence, tree.FrameSequence);
        return changed;
    }

    /// <summary>
    ///     Follows a tree refresh. Call it once per UI frame after <see cref="SimulationTree.Refresh" />.
    /// </summary>
    /// <param name="tree">The refreshed tree.</param>
    /// <param name="inspecting">Whether the timeline inspects history, which keeps a missing node selected.</param>
    /// <returns><see langword="true" /> when <see cref="Current" /> changed.</returns>
    public bool Reconcile(SimulationTree tree, bool inspecting)
    {
        if (Current is not { } id)
            return false;

        if (tree.TryGetNode(id, out var node))
        {
            if (!ReferenceEquals(node, Node) || IsMissing)
                Resolve(id, node, tree);

            return false;
        }

        IsMissing = true;
        if (inspecting || tree.FrameSequence <= _pendingThroughSequence)
            return false;

        for (int index = Ancestors.Count - 1; index >= 0; index--)
        {
            if (tree.TryGetNode(Ancestors[index], out var ancestor))
            {
                Current = ancestor.Id;
                Revision++;
                Resolve(ancestor.Id, ancestor, tree);
                return true;
            }
        }

        return Select(null, tree);
    }

    private void Resolve(TreeNodeId id, TreeNode node, SimulationTree tree)
    {
        Node = node;
        Ancestors = tree.GetAncestors(id);
        IsMissing = false;
        _pendingThroughSequence = long.MinValue;
    }

    private static IReadOnlyList<TreeNodeId> GetCanonicalAncestors(TreeNodeId id)
    {
        var ancestors = new List<TreeNodeId>();
        var parent = id.GetCanonicalParent();
        while (parent is { } value)
        {
            ancestors.Add(value);
            parent = value.GetCanonicalParent();
        }

        ancestors.Reverse();
        return ancestors;
    }
}
