using Numos.CoreSim.EulerFlow;
using Numos.Viewer.Ui;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class FlowReadoutTests
{
    [Test]
    public void Describe_FormatsEveryDiagnosticWithItsUnit()
    {
        var flow = new AtmosFlowDiagnostics(
            Substeps: 3,
            SimulatedSeconds: 0.05,
            SubstepCapEngaged: false,
            MaxCourantNumber: 0.45f,
            MaxSignalSpeed: 412.5f,
            ClampedVelocityCount: 0,
            NegativeInternalEnergyCount: 0,
            NegativeMolesCount: 0,
            NonFiniteCellCount: 0);

        var rows = FlowReadout.Describe(flow);

        Assert.That(rows.Select(static row => (row.Label, row.Value)), Is.EqualTo(new[]
        {
            ("Substeps", "3"),
            ("Simulated time", "50.00 ms"),
            ("Substep cap", "Not reached"),
            ("Max Courant number", "0.450"),
            ("Max signal speed", "412.50 m/s"),
            ("Clamped velocities", "0"),
            ("Negative energies", "0"),
            ("Negative moles", "0"),
            ("Non-finite cells", "0")
        }));
        Assert.That(rows.Any(static row => row.IsWarning), Is.False);
    }

    [Test]
    public void Describe_FlagsTheRowsThatMeanTheTickDidNotFinishCleanly()
    {
        var flow = new AtmosFlowDiagnostics(
            Substeps: 64,
            SimulatedSeconds: 0.0312,
            SubstepCapEngaged: true,
            MaxCourantNumber: 1.2f,
            MaxSignalSpeed: 900f,
            ClampedVelocityCount: 2,
            NegativeInternalEnergyCount: 1,
            NegativeMolesCount: 5,
            NonFiniteCellCount: 3);

        var warnings = FlowReadout.Describe(flow).Where(static row => row.IsWarning).Select(static row => row.Label);

        Assert.That(warnings, Is.EqualTo(new[]
        {
            "Substep cap",
            "Max Courant number",
            "Clamped velocities",
            "Negative energies",
            "Negative moles",
            "Non-finite cells"
        }));
    }
}
