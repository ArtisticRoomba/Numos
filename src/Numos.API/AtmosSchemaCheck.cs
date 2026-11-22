using Numos.CoreSim.State.Advanced;

namespace Numos.API;

/// <summary>
///     Selects what a world restore does when a class solver or simulation component was saved with another state
///     schema than its registered type has now.
/// </summary>
/// <remarks>
///     The restore compares each entry's <see cref="AtmosWorldSolverStateCheckpoint.SchemaFingerprint" /> or
///     <see cref="AtmosSimulationComponentCheckpoint.SchemaFingerprint" /> with the type's
///     <see cref="INumosStateSchema.SchemaFingerprint" />. An entry that recorded no fingerprint never differs.
/// </remarks>
public enum AtmosSchemaCheck
{
    /// <summary>
    ///     Restore the entry anyway and report its name. The generated reader skips fields it does not know and leaves
    ///     missing members at their current value.
    /// </summary>
    Tolerant,

    /// <summary>
    ///     Fail the restore with an <see cref="InvalidOperationException" /> that names the type. The world stays as it
    ///     was.
    /// </summary>
    Strict,
}
