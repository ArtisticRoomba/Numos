using Numos.CoreSim;
using Numos.Workbench;

namespace Numos.Viewer.Ui;

/// <summary>
///     Holds the editable copy of the world configuration that the Atmos configuration properties show and change.
/// </summary>
/// <remarks>
///     <para>
///         The viewer never writes the configuration the host runs with. A panel edit changes <see cref="Value" />,
///         and <see cref="BeginApply" /> hands the host a separate copy to apply. The draft keeps the edit, so a
///         second edit made before the first one lands builds on it instead of undoing it.
///     </para>
///     <para>
///         <see cref="Sync" /> replaces the draft with the applied configuration once no apply is outstanding. A
///         rejected apply marks the draft stale, so the next sync drops the rejected value.
///     </para>
/// </remarks>
internal sealed class ConfigDraft
{
    private int _generation;
    private bool _stale = true;
    private AtmosConfigSnapshot? _source;
    private long _sourceRevision = long.MinValue;

    /// <summary>
    ///     The draft, or <see langword="null" /> when no project is open.
    /// </summary>
    public AtmosConfig? Value { get; private set; }

    /// <summary>
    ///     How many applies the host has not finished yet.
    /// </summary>
    public int PendingApplies { get; private set; }

    /// <summary>
    ///     Drops the draft and every outstanding apply, for a project that closed or changed.
    /// </summary>
    public void Reset()
    {
        _generation++;
        PendingApplies = 0;
        _stale = true;
        _source = null;
        _sourceRevision = long.MinValue;
        Value = null;
    }

    /// <summary>
    ///     Replaces the draft with the applied configuration when it changed and no apply is outstanding.
    /// </summary>
    /// <param name="source">The configuration the world runs with, or <see langword="null" /> without a project.</param>
    /// <param name="definitionRevision">The frame's <see cref="WorkbenchFrame.DefinitionRevision" />.</param>
    /// <returns><see langword="true" /> when the draft was replaced.</returns>
    public bool Sync(AtmosConfigSnapshot? source, long definitionRevision)
    {
        if (source == null)
        {
            Value = null;
            _source = null;
            return false;
        }

        if (PendingApplies > 0)
            return false;

        if (!_stale && ReferenceEquals(source, _source) && definitionRevision == _sourceRevision)
            return false;

        Value = new AtmosConfig(source);
        _source = source;
        _sourceRevision = definitionRevision;
        _stale = false;
        return true;
    }

    /// <summary>
    ///     Copies the draft for the host to apply and counts the apply as outstanding.
    /// </summary>
    /// <returns>
    ///     The copy, which shares no list with the draft, and a token for <see cref="EndApply" />.
    /// </returns>
    /// <exception cref="InvalidOperationException">No draft exists.</exception>
    public (AtmosConfig Candidate, int Token) BeginApply()
    {
        var draft = Value ?? throw new InvalidOperationException("No configuration draft exists.");
        PendingApplies++;
        return (Clone(draft), _generation);
    }

    /// <summary>
    ///     Counts an apply as outstanding without copying the draft, for a host command that changes the
    ///     configuration itself, such as a gas registry edit.
    /// </summary>
    /// <returns>A token for <see cref="EndApply" />.</returns>
    public int BeginExternalApply()
    {
        PendingApplies++;
        return _generation;
    }

    /// <summary>
    ///     Records that the host finished an apply.
    /// </summary>
    /// <param name="token">The token from <see cref="BeginApply" /> or <see cref="BeginExternalApply" />.</param>
    /// <param name="succeeded">Whether the host applied the configuration.</param>
    /// <remarks>
    ///     A token from before the last <see cref="Reset" /> is ignored, because its project is gone.
    /// </remarks>
    public void EndApply(int token, bool succeeded)
    {
        if (token != _generation)
            return;

        PendingApplies = Math.Max(0, PendingApplies - 1);
        if (!succeeded)
            _stale = true;
    }

    /// <summary>
    ///     Copies a configuration without going through a snapshot, which would normalize values the host must see
    ///     as entered.
    /// </summary>
    public static AtmosConfig Clone(AtmosConfig source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = new AtmosConfig();
        AtmosConfigSettings.CopyScalarSettings(source, copy);
        var registry = new GasRegistry();
        foreach (var gas in source.GasRegistry)
            registry.Add(gas);

        copy.GasRegistry = registry;
        copy.SolverConfigurations = source.SolverConfigurations.ToList();
        return copy;
    }
}
