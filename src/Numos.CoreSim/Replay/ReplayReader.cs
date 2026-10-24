using Numos.CoreSim.State;

namespace Numos.CoreSim.Replay;

/// <summary>
///     A <see cref="BinaryReader" /> over replay payloads that also carries what decoding them needs from the host.
/// </summary>
/// <remarks>
///     Operation readers are generated with a fixed <c>(BinaryReader, int maxStringBytes)</c> shape. The solver
///     configuration registry therefore rides on the reader instead of being threaded through every generated call. A
///     plain <see cref="BinaryReader" /> still works and decodes only the built-in configurations.
/// </remarks>
internal sealed class ReplayReader(Stream input, SolverConfigurationTypes solverConfigurations)
    : BinaryReader(input, AtmosReplayCodec.Utf8, true)
{
    /// <summary>
    ///     Gets the registry that maps saved solver configuration keys to their types.
    /// </summary>
    public SolverConfigurationTypes SolverConfigurations { get; } = solverConfigurations;
}
