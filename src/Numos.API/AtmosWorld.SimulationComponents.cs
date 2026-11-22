using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;

namespace Numos.API;

public sealed partial class AtmosWorld
{
    /// <summary>
    ///     Every simulation component, by simulation in stable simulation-ID order, then by ordinal key.
    /// </summary>
    [NumosSerialize("SimulationComponents")]
    internal IReadOnlyList<AtmosSimulationComponentCheckpoint> SimulationComponentCheckpoints => CaptureSimulationComponents();

    private AtmosSimulationComponentCheckpoint[] CaptureSimulationComponents()
    {
        List<AtmosSimulationComponentCheckpoint>? entries = null;
        StateBinarySink sink = default;
        foreach (var simulation in _orderedSimulations)
        {
            if (!simulation.HasComponents)
                continue;

            if (entries is null)
            {
                entries = [];
                sink = new StateBinarySink();
            }

            foreach (var slot in simulation.CopyComponentSlots())
            {
                entries.Add(
                    new AtmosSimulationComponentCheckpoint(
                        simulation.Id,
                        slot.Key,
                        StateEditBaseline.Capture(slot.Instance, ref sink))
                    {
                        SchemaFingerprint = SchemaReport.FingerprintOf(slot.Instance),
                    });
            }
        }

        return entries?.ToArray() ?? [];
    }

    /// <summary>
    ///     Checks each component entry before the restore changes anything: its simulation is in the checkpoint, the
    ///     entries are in order without duplicates, the key is registered, and the payload is a well-formed record.
    /// </summary>
    private static void ValidateSimulationComponents(AtmosWorldCheckpoint checkpoint)
    {
        AtmosSimulationComponentCheckpoint? previous = null;
        foreach (var saved in checkpoint.SimulationComponents)
        {
            if (string.IsNullOrWhiteSpace(saved.Key) ||
                checkpoint.Simulations.All(simulation => simulation.Simulation != saved.Simulation))
            {
                throw new ArgumentException(
                    "The world checkpoint holds a simulation component with a blank key or for a simulation it " +
                    "doesn't list.",
                    nameof(checkpoint));
            }

            if (previous is { } prior && AtmosSimulationComponentCheckpoint.CompareOrder(prior, saved) >= 0)
            {
                throw new ArgumentException(
                    "The world checkpoint lists its simulation components twice or out of order.",
                    nameof(checkpoint));
            }

            if (!SimulationComponentCatalog.Contains(saved.Key))
            {
                throw new ArgumentException(
                    "The world checkpoint can't be restored. " + SimulationComponentCatalog.DescribeUnknown(saved.Key),
                    nameof(checkpoint));
            }

            try
            {
                SkimStatePayload(saved.State.Span);
            }
            catch (InvalidDataException exception)
            {
                throw new ArgumentException(
                    $"The world checkpoint's state for simulation component '{saved.Key}' is malformed.",
                    nameof(checkpoint),
                    exception);
            }

            previous = saved;
        }
    }

    /// <summary>
    ///     Builds the component set of every simulation the checkpoint keeps or adds. This is part of the restore phase
    ///     that can fail.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An instance the simulation already holds is restored in place, so host references to it stay valid. A
    ///         missing one is created from <see cref="SimulationComponentCatalog" />. Nothing is installed here. The
    ///         second phase swaps each set in, which drops the components the checkpoint lacks.
    ///     </para>
    ///     <para>
    ///         A failure writes the earlier state back into every instance it restored in place, then rethrows.
    ///     </para>
    /// </remarks>
    private PreparedComponents PrepareComponents(
        AtmosWorldCheckpoint checkpoint, List<PreparedGrid> grids, SchemaReport schemas)
    {
        var prepared = new PreparedComponents();
        var sink = new StateBinarySink();
        try
        {
            foreach (var grid in grids)
            {
                var slots = new SortedList<string, SimulationComponentSlot>(StringComparer.Ordinal);
                prepared.Sets.Add((grid.Simulation, slots));
                foreach (var saved in checkpoint.SimulationComponents)
                {
                    if (saved.Simulation != grid.Simulation.Id)
                        continue;

                    string owner = $"simulation component '{saved.Key}'";
                    INumosStateTarget? instance = grid.IsNew ? null : grid.Simulation.FindComponentSlot(saved.Key)?.Instance;
                    if (instance is not null)
                    {
                        schemas.Check(instance, saved.SchemaFingerprint, saved.Key, owner, true);

                        // Listed first, so a setter that throws partway is still rolled back.
                        prepared.Restored.Add((instance, StateEditBaseline.Capture(instance, ref sink), owner));
                    }
                    else if (SimulationComponentCatalog.TryCreate(saved.Key, out instance))
                    {
                        schemas.Check(instance!, saved.SchemaFingerprint, saved.Key, owner, true);
                    }
                    else
                    {
                        throw new ArgumentException(SimulationComponentCatalog.DescribeUnknown(saved.Key), nameof(checkpoint));
                    }

                    StateEditBaseline.Restore(instance!, saved.State.Span, checkpoint.Config.GasRegistry, owner);
                    slots.Add(saved.Key, new SimulationComponentSlot(saved.Key, instance!));
                }
            }
        }
        catch
        {
            RollBackComponents(prepared);
            throw;
        }

        return prepared;
    }

    /// <summary>
    ///     Writes the state captured before the restore back into each instance <see cref="PrepareComponents" />
    ///     restored in place.
    /// </summary>
    /// <remarks>
    ///     The world's configuration is still the previous one here, so the payloads resolve against its gas registry.
    ///     An instance that rejects its own earlier state is left as it is, and the original failure propagates.
    /// </remarks>
    private void RollBackComponents(PreparedComponents prepared)
    {
        foreach ((INumosStateTarget instance, byte[] previous, string owner) in prepared.Restored)
        {
            try
            {
                StateEditBaseline.Restore(instance, previous, _config.GasRegistry, owner);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    ///     Resets the <c>[NumosScratch]</c> members of every component after a world restore.
    /// </summary>
    private void ResetComponentScratch()
    {
        foreach (var simulation in _orderedSimulations)
        {
            if (!simulation.HasComponents)
                continue;

            foreach (var slot in simulation.CopyComponentSlots())
            {
                if (slot.Instance is INumosScratchTarget scratch)
                    scratch.ResetScratch();
            }
        }
    }

    /// <summary>
    ///     Records the state of every component the host created or edited since Numos last hashed it. Part of
    ///     <see cref="RecordSolverStateEdits" />, with the same caller rules.
    /// </summary>
    private void RecordComponentEdits(ref StateBinarySink sink, ref bool hasSink)
    {
        foreach (var simulation in _orderedSimulations)
        {
            if (!simulation.HasComponents)
                continue;

            foreach (var slot in simulation.CopyComponentSlots())
            {
                ulong hash = StateEditBaseline.HashHostEditable(slot.Instance);
                if (slot.HasBaseline)
                {
                    StateEditBaseline.CheckBulk(slot.Instance, slot.BulkHashes, $"simulation component '{slot.Key}'");
                    if (hash == slot.StateHash)
                        continue;
                }

                if (!hasSink)
                {
                    sink = new StateBinarySink();
                    hasSink = true;
                }

                slot.StateHash = hash;
                slot.HasBaseline = true;
                StateEditBaseline.RefreshBulk(slot.Instance, ref slot.BulkHashes);
                RecordWorldOperation(
                    new SetAtmosSimulationComponentStateOperation(
                        simulation.Id,
                        slot.Key,
                        StateEditBaseline.Capture(slot.Instance, ref sink)));
            }
        }
    }

    /// <summary>
    ///     Takes the current state of every component as the baseline the next boundary compares against.
    /// </summary>
    private void RefreshComponentHashes()
    {
        foreach (var simulation in _orderedSimulations)
        {
            if (!simulation.HasComponents)
                continue;

            foreach (var slot in simulation.CopyComponentSlots())
            {
                slot.StateHash = StateEditBaseline.HashHostEditable(slot.Instance);
                slot.HasBaseline = true;
                StateEditBaseline.RefreshBulk(slot.Instance, ref slot.BulkHashes);
            }
        }
    }

    /// <summary>
    ///     Records a removal the host made between ticks. The caller holds <see cref="Gate" />.
    /// </summary>
    /// <remarks>
    ///     A component without a baseline was created since the last boundary, which has not recorded it yet. Replay
    ///     then never creates it, so the removal records nothing either.
    /// </remarks>
    internal void RecordComponentRemoved(AtmosSimulation simulation, SimulationComponentSlot slot)
    {
        if (!_isTickExecuting && slot.HasBaseline)
            RecordWorldOperation(new RemoveAtmosSimulationComponentOperation(simulation.Id, slot.Key));
    }

    internal void Apply(SetAtmosSimulationComponentStateOperation state)
    {
        var simulation = TryGetSimulationCore(state.Simulation) ??
                         throw new ArgumentException(
                             "A recorded simulation component edit targets a simulation that is not registered.",
                             nameof(state));

        simulation.SetComponentState(state.Key, state.State.Span, _config.GasRegistry);
    }

    internal void Apply(RemoveAtmosSimulationComponentOperation remove)
    {
        var simulation = TryGetSimulationCore(remove.Simulation);
        if (simulation is null || !simulation.RemoveComponentSlot(remove.Key))
        {
            throw new ArgumentException(
                $"A recorded removal of simulation component '{remove.Key}' targets a simulation that doesn't hold it.",
                nameof(remove));
        }
    }

    /// <summary>
    ///     What <see cref="PrepareComponents" /> built: the new component set of each simulation, and the earlier
    ///     state of each instance it restored in place.
    /// </summary>
    private sealed class PreparedComponents
    {
        public List<(INumosStateTarget Instance, byte[] Previous, string Owner)> Restored { get; } = [];

        public List<(AtmosSimulation Simulation, SortedList<string, SimulationComponentSlot> Slots)> Sets { get; } = [];
    }
}
