namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     The flow groups of one <c>euler-flow</c> call: sets of active workspaces that share no open face with any
///     other set, plus the links the solver cut between groups during the call.
/// </summary>
/// <remarks>
///     <para>
///         Two workspaces belong to one group when a chain of linked workspaces joins them and each link crosses a
///         chunk face with at least one open air face (<see cref="EulerFaceTopology.HasOpenFace" />). Groups
///         exchange no flux, so each one can run its own substep loop at its own scaling factor. See
///         <see cref="EulerAcousticScaling" />.
///     </para>
///     <para>
///         A chunk activated during a group's loop joins that group. Its links to workspaces of other groups are cut:
///         both neighbour slots stay null, so each side treats the face like an inactive neighbour, which reflects.
///         A wall conserves mass. <see cref="RestoreCuts" /> puts the links back before the write-back, because
///         species diffusion reads them.
///     </para>
/// </remarks>
internal sealed class EulerFlowGroups
{
    private readonly List<Group> _groups = [];
    private readonly List<Group> _batches = [];
    private readonly List<float> _groupScales = [];
    private readonly List<float> _batchScales = [];
    private readonly List<CutLink> _cuts = [];
    private int[] _parent = [];
    private int[] _label = [];

    /// <summary>
    ///     The members of one group or batch, in the order of the active list.
    /// </summary>
    internal sealed class Group
    {
        internal EulerChunkWorkspace[] Members = [];
        internal int Count;
    }

    private readonly record struct CutLink(EulerChunkWorkspace Workspace, int Direction, EulerChunkWorkspace Neighbor);

    /// <summary>
    ///     Number of groups the last <see cref="Build" /> found. They are numbered in the order of their first member in
    ///     the active list.
    /// </summary>
    internal int GroupCount { get; private set; }

    /// <summary>
    ///     Number of batches the last <see cref="Batch" /> made. Zero after <see cref="Build" /> until then.
    /// </summary>
    internal int Count { get; private set; }

    /// <summary>
    ///     The batch at <paramref name="index" />, in order of factor ascending.
    /// </summary>
    internal Group this[int index] => _batches[index];

    /// <summary>
    ///     The group at <paramref name="index" />, as <see cref="Build" /> found it.
    /// </summary>
    internal Group GroupAt(int index) => _groups[index];

    /// <summary>
    ///     The factor shared by every group of the batch at <paramref name="index" />.
    /// </summary>
    internal Scalar BatchScale(int index) => _batchScales[index];

    /// <summary>
    ///     Records the factor group <paramref name="index" /> picked, for <see cref="Batch" />.
    /// </summary>
    internal void SetGroupScale(int index, float scale) => _groupScales[index] = scale;

    /// <summary>
    ///     Splits the active workspaces into groups and stores each workspace's group in
    ///     <see cref="EulerChunkWorkspace.Group" />. Tick thread only, after the call's activations and gathers.
    /// </summary>
    internal void Build(EulerChunkWorkspace[] active, int count)
    {
        if (_parent.Length < count)
        {
            int length = Math.Max(count, _parent.Length * 2);
            _parent = new int[length];
            _label = new int[length];
        }

        for (int index = 0; index < count; index++)
        {
            active[index].Group = index;
            _parent[index] = index;
            _label[index] = -1;
        }

        // Links are symmetric, so the positive directions see every pair once.
        for (int index = 0; index < count; index++)
        {
            EulerChunkWorkspace workspace = active[index];
            for (int direction = 1; direction < workspace.NeighborWorkspaces.Length; direction += 2)
            {
                EulerChunkWorkspace? neighbor = workspace.NeighborWorkspaces[direction];
                if (neighbor != null && EulerFaceTopology.HasOpenFace(workspace.Chunk, direction, neighbor.Chunk))
                    Union(index, neighbor.Group);
            }
        }

        GroupCount = 0;
        for (int index = 0; index < count; index++)
        {
            int root = Find(index);
            if (_label[root] < 0)
            {
                _label[root] = GroupCount++;
                if (_groups.Count < GroupCount)
                    _groups.Add(new Group());

                _groups[GroupCount - 1].Count = 0;
            }

            // Every union is done, so the active index in Group is no longer needed.
            int label = _label[root];
            active[index].Group = label;
            Append(_groups[label], active[index]);
        }

        Count = 0;
        _groupScales.Clear();
        for (int index = 0; index < GroupCount; index++)
            _groupScales.Add(1f);
    }

    /// <summary>
    ///     Merges the groups that picked the same factor into batches, ordered by factor ascending, and stores each
    ///     workspace's batch in <see cref="EulerChunkWorkspace.Group" />. Call it after <see cref="Build" /> and
    ///     <see cref="SetGroupScale" />. Tick thread only.
    /// </summary>
    internal void Batch(EulerChunkWorkspace[] active, int count)
    {
        _batchScales.Clear();
        foreach (float scale in _groupScales)
        {
            int position = _batchScales.BinarySearch(scale);
            if (position < 0)
                _batchScales.Insert(~position, scale);
        }

        Count = _batchScales.Count;
        while (_batches.Count < Count)
            _batches.Add(new Group());

        for (int index = 0; index < Count; index++)
            _batches[index].Count = 0;

        for (int index = 0; index < count; index++)
        {
            int batch = _batchScales.BinarySearch(_groupScales[active[index].Group]);
            active[index].Group = batch;
            Append(_batches[batch], active[index]);
        }
    }

    /// <summary>
    ///     Rebuilds a batch's member list from the active list. Call it after an activation, which can add members
    ///     and moves the active list.
    /// </summary>
    internal void Refill(int index, EulerChunkWorkspace[] active, int count)
    {
        Group group = _batches[index];
        group.Count = 0;
        for (int position = 0; position < count; position++)
        {
            if (active[position].Group == index)
                Append(group, active[position]);
        }
    }

    /// <summary>
    ///     Records that the link from <paramref name="workspace" /> to <paramref name="neighbor" /> across
    ///     <paramref name="direction" /> stays cut until <see cref="RestoreCuts" />. The caller leaves both slots null.
    /// </summary>
    internal void RecordCut(EulerChunkWorkspace workspace, int direction, EulerChunkWorkspace neighbor)
    {
        _cuts.Add(new CutLink(workspace, direction, neighbor));
    }

    /// <summary>
    ///     Links every pair cut during the call again, in both directions.
    /// </summary>
    /// <returns>The number of pairs restored.</returns>
    internal int RestoreCuts()
    {
        int restored = _cuts.Count;
        foreach (CutLink cut in _cuts)
        {
            cut.Workspace.NeighborWorkspaces[cut.Direction] = cut.Neighbor;
            cut.Neighbor.NeighborWorkspaces[cut.Direction ^ 1] = cut.Workspace;
        }

        _cuts.Clear();
        return restored;
    }

    /// <summary>
    ///     Forgets recorded cuts without restoring them. Used when the call ends early, because the next call
    ///     attaches every workspace again.
    /// </summary>
    internal void DiscardCuts() => _cuts.Clear();

    private static void Append(Group group, EulerChunkWorkspace workspace)
    {
        if (group.Count == group.Members.Length)
            Array.Resize(ref group.Members, Math.Max(4, group.Count * 2));

        group.Members[group.Count++] = workspace;
    }

    private int Find(int index)
    {
        while (_parent[index] != index)
        {
            _parent[index] = _parent[_parent[index]];
            index = _parent[index];
        }

        return index;
    }

    private void Union(int a, int b)
    {
        a = Find(a);
        b = Find(b);
        if (a != b)
            _parent[Math.Max(a, b)] = Math.Min(a, b);
    }
}
