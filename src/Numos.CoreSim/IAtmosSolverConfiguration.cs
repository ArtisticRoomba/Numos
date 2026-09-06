namespace Numos.CoreSim;

/// <summary>
///     Supplies solver-owned settings to configuration snapshots, recording, and replay.
/// </summary>
/// <remarks>
///     Add implementations to <see cref="AtmosConfig.SolverConfigurations" />. Numos does not inspect their fields
///     or use reflection to copy them. Each implementation owns its validation, immutable snapshot, and deterministic
///     hash. Derived gas attachments and runtime workspaces do not belong in this configuration.
/// </remarks>
public interface IAtmosSolverConfiguration
{
    /// <summary>
    ///     Gets a nonempty, stable identifier unique to this solver configuration, compared ordinally.
    /// </summary>
    string Key { get; }

    /// <summary>
    ///     Validates settings against the registered gases and captures an immutable, detached configuration.
    /// </summary>
    /// <param name="gasRegistry">The normalized, read-only gas registry being applied.</param>
    /// <returns>A non-null immutable configuration with the same key. Already immutable implementations may return themselves.</returns>
    /// <remarks>
    ///     Retained builders must not be able to mutate the returned object. The snapshot must implement this method
    ///     too, since Numos can capture it again when a caller edits a copy of the applied configuration.
    /// </remarks>
    IAtmosSolverConfiguration CreateSnapshot(IGasRegistry gasRegistry);

    /// <summary>
    ///     Compares all settings that can affect solver execution, without relying on hash equality.
    /// </summary>
    /// <param name="other">Another immutable configuration.</param>
    /// <returns>True when both configurations have the same meaning; false for incompatible types or settings.</returns>
    bool SemanticallyEquals(IAtmosSolverConfiguration other);

    /// <summary>
    ///     Computes a deterministic hash of all authoritative settings.
    /// </summary>
    /// <returns>A stable 64-bit hash, independent of object identity, worker scheduling, and process-randomized hashes.</returns>
    /// <remarks>
    ///     Include exact numeric representations and meaningful collection order. Do not use <c>HashCode</c> or
    ///     <c>string.GetHashCode()</c>, which need not agree between processes. Exclude derived caches and delegates.
    /// </remarks>
    ulong ComputeStateHash();
}