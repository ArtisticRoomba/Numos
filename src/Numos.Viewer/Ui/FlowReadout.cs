using System.Globalization;
using Numos.CoreSim.EulerFlow;

namespace Numos.Viewer.Ui;

/// <summary>
///     One line of the Flow readout.
/// </summary>
/// <param name="Label">Row label.</param>
/// <param name="Value">Formatted value.</param>
/// <param name="IsWarning">Whether the value deserves the caution colour.</param>
internal readonly record struct FlowReadoutRow(string Label, string Value, bool IsWarning);

/// <summary>
///     Turns <see cref="AtmosFlowDiagnostics" /> into labelled rows for the simulation panel, kept out of the ImGui
///     code so the formatting and the warning rules can be tested.
/// </summary>
internal static class FlowReadout
{
    /// <summary>
    ///     Describes one tick's diagnostics. A counter is flagged as a warning when it is nonzero, and the substep
    ///     cap when it engaged, since each of those means the stage did not finish the tick cleanly.
    /// </summary>
    /// <param name="flow">Diagnostics from <see cref="Numos.API.AtmosSimulation.LastFlowDiagnostics" />.</param>
    public static IReadOnlyList<FlowReadoutRow> Describe(in AtmosFlowDiagnostics flow)
    {
        return
        [
            new FlowReadoutRow("Substeps", Count(flow.Substeps), false),
            new FlowReadoutRow("Simulated time", QuantityFormat.Milliseconds(flow.SimulatedSeconds), false),
            new FlowReadoutRow("Substep cap", flow.SubstepCapEngaged ? "Engaged" : "Not reached", flow.SubstepCapEngaged),
            new FlowReadoutRow("Max Courant number", QuantityFormat.Format(flow.MaxCourantNumber, "", 3), flow.MaxCourantNumber > 1f),
            new FlowReadoutRow("Max signal speed", QuantityFormat.Speed(flow.MaxSignalSpeed), false),
            new FlowReadoutRow("Clamped velocities", Count(flow.ClampedVelocityCount), flow.ClampedVelocityCount > 0),
            new FlowReadoutRow("Negative energies", Count(flow.NegativeInternalEnergyCount), flow.NegativeInternalEnergyCount > 0),
            new FlowReadoutRow("Negative moles", Count(flow.NegativeMolesCount), flow.NegativeMolesCount > 0)
        ];
    }

    private static string Count(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
