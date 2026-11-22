using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;

namespace Numos.API;

public sealed partial class AtmosWorld
{
    // Set while a solver command's Apply method runs between ticks, live or replayed. The calls that tick, update,
    // replay, restore, or dispose the world check it. So do solver layout changes and the calls that start, stop, or
    // resume recording. Replay could not reproduce any of them inside a command.
    [NumosIgnore]
    private bool _isRunningSolverCommand;

    /// <summary>
    ///     What <see cref="BeginSolverCommand" /> took, for <see cref="CompleteSolverCommand" /> and
    ///     <see cref="EndSolverCommand" />.
    /// </summary>
    /// <param name="Stage">The stage name the command records.</param>
    /// <param name="Records">Whether the command is recorded when it completes.</param>
    /// <param name="Nested">
    ///     Whether the call runs inside a tick, a replay, or another command. Such a call holds only <see cref="Gate" />
    ///     and changes no flag.
    /// </param>
    /// <param name="Gates">The gates the call holds after <see cref="Gate" />, or <see langword="null" /> when nested.</param>
    internal readonly record struct SolverCommandFrame(
        string Stage,
        bool Records,
        bool Nested,
        object[]? Gates);

    /// <summary>
    ///     Starts a command call on <paramref name="solver" />. The caller ends it with <see cref="EndSolverCommand" />
    ///     on the same thread.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Between ticks the call records pending solver-state edits first, so they land before the command. It
    ///         then takes every gate a tick takes, in the order <see cref="_mixtureGate" /> documents. No host call on
    ///         another thread can then land between two of the command's mutations, which replay applies in one place.
    ///     </para>
    ///     <para>
    ///         The call sets <see cref="_isApplyingWorldOperation" />. Grid, chunk-data, world, and mixture operations
    ///         check it before they record, and so does the Dangerous access count. The command's own host calls
    ///         therefore apply without being recorded.
    ///     </para>
    /// </remarks>
    internal SolverCommandFrame BeginSolverCommand(IAtmosWorldSolver solver)
    {
        // The tick thread holds the gate until its workers finish, so a worker that waited for it would never wake.
        if (_isTickExecuting && !Monitor.IsEntered(Gate))
        {
            throw new InvalidOperationException(
                "While a world tick runs, only the tick's own thread may call a solver command.");
        }

        Monitor.Enter(Gate);
        try
        {
            ThrowIfDisposed();
            string stage = Solvers.FindStageName(solver) ?? throw AtmosSolverCommandScope.NotRegistered(solver);

            // Replay reruns a solver's work, and the outer operation already records a nested command.
            if (_isTickExecuting || _isApplyingWorldOperation)
                return new SolverCommandFrame(stage, false, true, null);

            RecordSolverStateEdits();
            object[] gates = EnterCommandGates();
            bool records;
            lock (_recordingGate)
            {
                records = _isWorldRecording;
            }

            _isApplyingWorldOperation = true;
            _isRunningSolverCommand = true;
            return new SolverCommandFrame(stage, records, false, gates);
        }
        catch
        {
            Monitor.Exit(Gate);
            throw;
        }
    }

    /// <summary>
    ///     Records a command whose <c>Apply{Name}</c> returned, then takes fresh solver-state baselines.
    /// </summary>
    /// <remarks>
    ///     The baselines absorb the command's writes to <c>[NumosSerialize]</c> members. Replay makes the same writes
    ///     when it runs the command, so recording them again as a state edit would apply them twice.
    /// </remarks>
    internal void CompleteSolverCommand(in SolverCommandFrame frame, int command, byte[]? payload)
    {
        if (frame.Nested)
            return;

        _isApplyingWorldOperation = false;
        _isRunningSolverCommand = false;
        if (frame.Records && payload is not null)
            RecordWorldOperation(new InvokeAtmosSolverCommandOperation(frame.Stage, command, payload));

        if (_isWorldRecording)
            RefreshSolverStateHashes();
    }

    /// <summary>
    ///     Ends a command call, also when <c>Apply{Name}</c> threw, and releases the gates in reverse order.
    /// </summary>
    internal void EndSolverCommand(in SolverCommandFrame frame)
    {
        try
        {
            if (frame.Nested)
                return;

            _isApplyingWorldOperation = false;
            _isRunningSolverCommand = false;
            ExitCommandGates(frame.Gates!);
        }
        finally
        {
            Monitor.Exit(Gate);
        }
    }

    /// <summary>
    ///     Runs a recorded command on the stage it names.
    /// </summary>
    /// <remarks>
    ///     Replay holds <see cref="Gate" /> and <see cref="_mixtureGate" /> and has set
    ///     <see cref="_isApplyingWorldOperation" />, so the command's host calls record nothing.
    /// </remarks>
    internal void Apply(InvokeAtmosSolverCommandOperation invoke)
    {
        var registration = Solvers.Find(invoke.Solver);
        string command = NumosState.GetFieldName(invoke.Command);
        if (registration?.CustomSolver is not IAtmosSolverCommands commands)
        {
            string reason = registration is null ? "no stage has that name" : "the stage has no solver commands";
            throw new InvalidOperationException(
                $"The recorded solver command '{command}' targets stage '{invoke.Solver}', but {reason}.");
        }

        bool running = _isRunningSolverCommand;
        _isRunningSolverCommand = true;
        try
        {
            if (!commands.InvokeCommand(invoke.Command, invoke.Payload.Span, _config.GasRegistry))
            {
                throw new InvalidOperationException(
                    $"The recorded solver command '{command}' targets stage '{invoke.Solver}', but " +
                    $"{registration.CustomSolver.GetType().FullName} has no command with id {invoke.Command}.");
            }
        }
        finally
        {
            _isRunningSolverCommand = running;
        }
    }

    /// <summary>
    ///     Rejects a call that replay could not reproduce from inside a command.
    /// </summary>
    private void ThrowIfRunningSolverCommand(string operation)
    {
        if (_isRunningSolverCommand)
            throw new InvalidOperationException($"A solver command cannot {operation}.");
    }

    /// <summary>
    ///     Takes every gate after <see cref="Gate" /> that a tick takes, and returns them in the order taken.
    /// </summary>
    /// <remarks>
    ///     The gate objects are kept rather than looked up again on exit. A command can destroy a simulation, and a
    ///     disposed simulation no longer hands out its kernel.
    /// </remarks>
    private object[] EnterCommandGates()
    {
        AtmosSimulation[] simulations = _orderedSimulations;
        var gates = new object[2 + 2 * simulations.Length];
        gates[0] = _mixtureGate;
        for (int index = 0; index < simulations.Length; index++)
        {
            gates[1 + index] = simulations[index].MixtureGate;
            gates[1 + simulations.Length + index] = simulations[index].Kernel.StateGate;
        }

        gates[^1] = GasMixtures.StateGate;
        int entered = 0;
        try
        {
            for (; entered < gates.Length; entered++)
                Monitor.Enter(gates[entered]);

            return gates;
        }
        catch
        {
            for (int index = entered - 1; index >= 0; index--)
                Monitor.Exit(gates[index]);

            throw;
        }
    }

    private static void ExitCommandGates(object[] gates)
    {
        for (int index = gates.Length - 1; index >= 0; index--)
            Monitor.Exit(gates[index]);
    }
}
