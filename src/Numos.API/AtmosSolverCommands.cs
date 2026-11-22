using System.ComponentModel;
using JetBrains.Annotations;
using Numos.CoreSim;

namespace Numos.API;

/// <summary>
///     Implemented by the source generator for a class solver with <see cref="SolverCommandAttribute" /> methods, so
///     replay can run a recorded command.
/// </summary>
/// <remarks>
///     Do not implement it by hand. The generated command methods and <see cref="InvokeCommand" /> must agree on every
///     id and payload, and the generator guarantees that.
/// </remarks>
[PublicAPI]
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IAtmosSolverCommands
{
    /// <summary>
    ///     Decodes a recorded payload and runs the matching <c>Apply{Name}</c> method.
    /// </summary>
    /// <param name="command">The command id, which hashes the command's wire name.</param>
    /// <param name="payload">The arguments as the command method recorded them.</param>
    /// <param name="gases">Resolves gas arguments by name.</param>
    /// <returns><see langword="false" /> when the solver has no command with id <paramref name="command" />.</returns>
    /// <exception cref="InvalidDataException">The payload is malformed or names a gas <paramref name="gases" /> lacks.</exception>
    bool InvokeCommand(int command, ReadOnlySpan<byte> payload, IGasRegistry gases);
}

/// <summary>
///     Brackets one call of a generated solver command: it takes the world's gates, suppresses recording of the host
///     calls inside, and records the command itself.
/// </summary>
/// <remarks>
///     <para>
///         Generated code uses this type. Do not call it by hand. A generated command method calls
///         <see cref="Enter" />, encodes its arguments when <see cref="Records" /> is set, runs <c>Apply{Name}</c>,
///         and calls <see cref="Complete" />. <see cref="Dispose" /> runs last, also when <c>Apply{Name}</c> throws.
///     </para>
///     <para>
///         A command that throws records nothing. The host calls it made before the exception are not recorded
///         either, so a command must validate before it mutates.
///     </para>
/// </remarks>
[PublicAPI]
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct AtmosSolverCommandScope : IDisposable
{
    private readonly AtmosWorld? _world;
    private readonly AtmosWorld.SolverCommandFrame _frame;

    private AtmosSolverCommandScope(AtmosWorld world, AtmosWorld.SolverCommandFrame frame)
    {
        _world = world;
        _frame = frame;
    }

    /// <summary>
    ///     Gets whether <see cref="Complete" /> records the command, so the caller must encode its arguments.
    /// </summary>
    /// <remarks>
    ///     <see langword="false" /> during a tick, a replay, another command, or with no recording live.
    /// </remarks>
    public bool Records => _frame.Records;

    /// <summary>
    ///     Gets the world that holds the solver, which an <c>Apply{Name}</c> method with a leading
    ///     <see cref="AtmosWorld" /> parameter receives.
    /// </summary>
    /// <remarks>
    ///     A solver that a restore or <see cref="AtmosWorldReplayTimeline.Import" /> created from the catalog has no
    ///     other reference to its world. The replay dispatch enters a scope for that reason too. Replay already holds
    ///     the world gate, so that scope is nested. It takes the gate again and records nothing.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The scope is <see langword="default" />.</exception>
    public AtmosWorld World => _world ?? throw new InvalidOperationException("The solver command scope was not entered.");

    /// <summary>
    ///     Starts a command call on <paramref name="solver" />.
    /// </summary>
    /// <param name="solver">The solver whose command runs.</param>
    /// <returns>The scope, which the caller disposes.</returns>
    /// <remarks>
    ///     Between ticks this first records pending host edits of <c>[NumosSerialize]</c> solver state, as a tick
    ///     does, so those edits land before the command.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="solver" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">
    ///     No live world has <paramref name="solver" /> as a stage. Or a tick is running and the caller is not the
    ///     tick's thread, which would otherwise wait forever for a gate the tick holds.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The world that last registered <paramref name="solver" /> is disposed.</exception>
    public static AtmosSolverCommandScope Enter(IAtmosWorldSolver solver)
    {
        ArgumentNullException.ThrowIfNull(solver);
        AtmosWorld world = AtmosWorldSolverPipeline.FindOwner(solver) ?? throw NotRegistered(solver);
        return new AtmosSolverCommandScope(world, world.BeginSolverCommand(solver));
    }

    /// <summary>
    ///     Ends a command whose <c>Apply{Name}</c> returned, and records it when <see cref="Records" /> is set.
    /// </summary>
    /// <param name="command">The command id.</param>
    /// <param name="payload">The encoded arguments, or <see langword="null" /> when <see cref="Records" /> is not set.</param>
    public void Complete(int command, byte[]? payload)
    {
        _world?.CompleteSolverCommand(_frame, command, payload);
    }

    /// <summary>
    ///     Restores recording and releases the gates <see cref="Enter" /> took.
    /// </summary>
    public void Dispose()
    {
        _world?.EndSolverCommand(_frame);
    }

    internal static InvalidOperationException NotRegistered(IAtmosWorldSolver solver)
    {
        return new InvalidOperationException(
            $"This {solver.GetType().FullName} instance is not a stage of any world. Register it, or add it with " +
            "Solvers.Add, before calling its solver commands.");
    }
}
