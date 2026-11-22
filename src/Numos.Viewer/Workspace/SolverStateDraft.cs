using Numos.Workbench;

namespace Numos.Viewer.Workspace;

/// <summary>
///     The control that edits one solver state member.
/// </summary>
internal enum SolverStateEditor
{
    /// <summary>
    ///     The member is read-only to tools, or it is a collection or another value without a text form.
    /// </summary>
    ReadOnly,

    /// <summary>
    ///     A <see langword="bool" /> member, edited with a checkbox.
    /// </summary>
    Boolean,

    /// <summary>
    ///     An enum member with a value, edited with a list of its names.
    /// </summary>
    Enum,

    /// <summary>
    ///     A number member that cannot be null, edited as text that only takes number characters.
    /// </summary>
    Number,

    /// <summary>
    ///     A string or a nullable member, edited as free text.
    /// </summary>
    Text,
}

/// <summary>
///     The world state that a solver state read belongs to. A read is out of date once any part changes.
/// </summary>
/// <param name="ProjectEpoch">The frame's project epoch.</param>
/// <param name="TickCount">The frame's tick count.</param>
/// <param name="TimelineRevision">The frame's timeline revision, which changes on a seek.</param>
/// <param name="DefinitionRevision">The frame's definition revision, which changes when the pipeline changes.</param>
internal readonly record struct SolverStateVersion(
    long ProjectEpoch,
    long TickCount,
    long TimelineRevision,
    long DefinitionRevision)
{
    public static SolverStateVersion From(WorkbenchFrame frame)
    {
        return new SolverStateVersion(frame.ProjectEpoch, frame.TickCount, frame.TimelineRevision, frame.DefinitionRevision);
    }
}

/// <summary>
///     The "Solver state" section of the selected stage: the members the host last read, and the text being typed.
/// </summary>
/// <remarks>
///     <para>
///         The members live on the host thread, so the section shows a copy. <see cref="TryBeginRefresh" /> decides
///         when to read again. It reads at once for a new stage or after <see cref="MarkStale" />. Otherwise it reads
///         when the world has moved, at most once per <see cref="RefreshSeconds" />, so a running world does not send a
///         read every UI frame.
///     </para>
///     <para>
///         Each read has a number. A result for an older read, such as one for the stage selected before, is dropped.
///     </para>
/// </remarks>
internal sealed class SolverStateDraft
{
    /// <summary>
    ///     The shortest time between two reads while the world moves.
    /// </summary>
    public const double RefreshSeconds = 0.25;

    /// <summary>
    ///     Text typed into a member's field and not committed yet, keyed by member name.
    /// </summary>
    public readonly Dictionary<string, string> TextDrafts = new(StringComparer.Ordinal);

    private double _readAt = double.NegativeInfinity;
    private SolverStateVersion _readFor;
    private int _request;
    private bool _stale = true;

    // Whether Feedback holds a read failure, which the next good read clears. An edit failure stays until the next
    // edit or stage change.
    private bool _readFailed;

    /// <summary>
    ///     The stage the members belong to, or <see langword="null" /> before the first read.
    /// </summary>
    public string? Stage { get; private set; }

    /// <summary>
    ///     The members from the last read, in declaration order.
    /// </summary>
    public IReadOnlyList<WorkbenchStateMember> Members { get; private set; } = [];

    /// <summary>
    ///     Whether a read for <see cref="Stage" /> has completed.
    /// </summary>
    public bool IsLoaded { get; private set; }

    /// <summary>
    ///     Whether the stage holds a class solver. A built-in or delegate stage has no state.
    /// </summary>
    public bool HasState { get; private set; }

    /// <summary>
    ///     Whether a read is on its way.
    /// </summary>
    public bool IsPending { get; private set; }

    /// <summary>
    ///     The last edit that failed, or the last read when it failed.
    /// </summary>
    public FeedbackMessage? Feedback { get; private set; }

    /// <summary>
    ///     Decides whether to read the stage's members now, and records the read when it does.
    /// </summary>
    /// <param name="stage">The selected stage.</param>
    /// <param name="version">The world state on screen.</param>
    /// <param name="now">The time in seconds, from any clock that only moves forward.</param>
    /// <param name="request">The number to pass to <see cref="Complete" /> or <see cref="Fail" />.</param>
    /// <returns><see langword="true" /> when the caller must send the read.</returns>
    public bool TryBeginRefresh(string stage, SolverStateVersion version, double now, out int request)
    {
        ArgumentNullException.ThrowIfNull(stage);
        request = 0;
        if (stage != Stage)
        {
            Stage = stage;
            Members = [];
            IsLoaded = false;
            HasState = false;
            IsPending = false;
            Feedback = null;
            TextDrafts.Clear();
            _stale = true;
            _readFailed = false;

            // A read still on its way belongs to the old stage, so a new number makes Complete drop it.
            _request++;
        }

        if (IsPending)
            return false;

        if (!_stale && (version == _readFor || now - _readAt < RefreshSeconds))
            return false;

        _stale = false;
        _readFor = version;
        _readAt = now;
        IsPending = true;
        request = ++_request;
        return true;
    }

    /// <summary>
    ///     Stores the members of a completed read.
    /// </summary>
    /// <param name="request">The number from <see cref="TryBeginRefresh" />.</param>
    /// <param name="hasState">Whether the stage holds a class solver.</param>
    /// <param name="members">The members.</param>
    public void Complete(int request, bool hasState, IReadOnlyList<WorkbenchStateMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (request != _request)
            return;

        IsPending = false;
        IsLoaded = true;
        HasState = hasState;
        Members = members;
        if (_readFailed)
        {
            Feedback = null;
            _readFailed = false;
        }
    }

    /// <summary>
    ///     Ends a read that threw.
    /// </summary>
    /// <param name="request">The number from <see cref="TryBeginRefresh" />.</param>
    /// <param name="message">The failure to show.</param>
    public void Fail(int request, string message)
    {
        if (request != _request)
            return;

        IsPending = false;
        IsLoaded = true;
        HasState = false;
        Members = [];
        Feedback = new FeedbackMessage(message, true);
        _readFailed = true;
    }

    /// <summary>
    ///     Makes the next <see cref="TryBeginRefresh" /> read at once.
    /// </summary>
    public void MarkStale()
    {
        _stale = true;
    }

    /// <summary>
    ///     Shows why an edit failed and reads the members again, so the section stops showing the rejected text.
    /// </summary>
    /// <param name="stage">The stage the edit went to. A failure for another stage is dropped.</param>
    /// <param name="message">The failure to show.</param>
    public void FailEdit(string stage, string message)
    {
        if (stage != Stage)
            return;

        Feedback = new FeedbackMessage(message, true);
        _readFailed = false;
        _stale = true;
    }

    /// <summary>
    ///     Replaces one member with the value an edit returned.
    /// </summary>
    /// <param name="stage">The stage the edit went to. An edit for another stage is dropped.</param>
    /// <param name="member">The member after the write.</param>
    public void ApplyEdit(string stage, WorkbenchStateMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (stage != Stage)
            return;

        Feedback = null;
        _readFailed = false;

        var members = Members.ToArray();
        for (int index = 0; index < members.Length; index++)
        {
            if (members[index].Name == member.Name)
                members[index] = member;
        }

        Members = members;
    }

    /// <summary>
    ///     Picks the control for a member.
    /// </summary>
    public static SolverStateEditor GetEditor(WorkbenchStateMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (!member.CanWrite || !member.IsScalar)
            return SolverStateEditor.ReadOnly;

        if (member.TypeName == "bool")
            return SolverStateEditor.Boolean;

        if (member.Value is System.Enum)
            return SolverStateEditor.Enum;

        return member.TypeName switch
        {
            "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "float" or "double" =>
                SolverStateEditor.Number,
            _ => SolverStateEditor.Text,
        };
    }

    /// <summary>
    ///     Lists the names of an enum member's type, in declaration order.
    /// </summary>
    /// <returns>The names, or an empty array when the member holds no enum value.</returns>
    public static string[] GetEnumNames(WorkbenchStateMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.Value is System.Enum value ? System.Enum.GetNames(value.GetType()) : [];
    }

    /// <summary>
    ///     Turns typed text into the value text that <see cref="WorkbenchSession.SetSolverState" /> takes.
    /// </summary>
    /// <returns>
    ///     <see langword="null" /> for a nullable member when the text is empty or `null`. The text without surrounding
    ///     spaces for a number, and the text unchanged otherwise.
    /// </returns>
    public static string? ToValueText(WorkbenchStateMember member, string text)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = text.Trim();
        if (member.TypeName.EndsWith('?') && (trimmed.Length == 0 || trimmed == "null"))
            return null;

        return GetEditor(member) == SolverStateEditor.Number ? trimmed : text;
    }
}
