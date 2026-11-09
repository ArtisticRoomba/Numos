using System.Globalization;
using Numos.API;
using Numos.CoreSim;
using Numos.Maths;
using Numos.Viewer.Workspace;
using Numos.Workbench;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private void CreateSimulationProject(
        string projectName,
        int chunkWidth,
        int chunkHeight,
        int chunkDepth,
        bool includeDefaultGases,
        AtmosTransportModel transportModel = AtmosTransportModel.Euler,
        int tickRate = AtmosWorld.DefaultTickRate)
    {
        var options = new WorkbenchProjectOptions(
            projectName,
            chunkWidth,
            chunkHeight,
            chunkDepth,
            includeDefaultGases,
            transportModel,
            tickRate);

        _createProjectPending = true;
        Send(
            session =>
            {
                session.CreateProject(options);
                return true;
            },
            result =>
            {
                _createProjectPending = false;
                if (result.Exception is { } exception)
                {
                    _createProjectError = DescribeFailure(exception);
                    return;
                }

                _closeCreateProjectModal = true;
                _showTreePanel = true;
                _showPropertiesPanel = true;
            });
    }

    /// <summary>
    ///     Drops every presentation object tied to the project that closed. <see cref="UpdateHost" /> calls it on the
    ///     UI thread when the frame's project epoch changes, after the host has already replaced the world.
    /// </summary>
    private void ResetProjectPresentation()
    {
        _document.Dispose();
        _document = new ProjectDocument();
        ResetActiveSurfaceCaches();
    }

    /// <summary>
    ///     Sends a chunk edit for the active simulation and shows its outcome beside the chunk controls.
    /// </summary>
    /// <param name="edit">Runs on the host thread with the simulation id. It must capture only copied values.</param>
    /// <param name="success">The feedback on success.</param>
    /// <param name="failure">Builds the feedback from the failure message.</param>
    private void SendChunkEdit(
        Action<WorkbenchSession, AtmosSimulationId> edit,
        string success,
        Func<string, string> failure)
    {
        if (_simulation is not { } simulation)
            return;

        var id = simulation.Id;
        Send(
            session =>
            {
                edit(session, id);
                return true;
            },
            result => _document.ChunkFeedback = result.Exception is { } exception
                ? new FeedbackMessage(failure(DescribeFailure(exception)), true)
                : new FeedbackMessage(success, false));
    }

    private void AddProjectChunk(Int3 position, int roomId)
    {
        _frameSceneOnNextPresentation = true;
        string label = FormatChunkPosition(position);
        SendChunkEdit(
            (session, id) => session.AddChunk(position, roomId, id),
            Locale.GetString("message-project-add-chunk", ("position", label)),
            message => Locale.GetString(
                "message-project-add-chunk-failed",
                ("position", label),
                ("message", message)));
    }

    private void RemoveProjectChunk(AtmosChunkHandle chunk)
    {
        if (_simulation is not { } simulation)
            return;

        var id = simulation.Id;
        var position = chunk.Position;
        string label = FormatChunkPosition(position);
        Send(
            session => session.RemoveChunk(position, id),
            result => _document.ChunkFeedback = result switch
            {
                { Exception: { } exception } => new FeedbackMessage(
                    Locale.GetString(
                        "message-project-remove-chunk-failed",
                        ("position", label),
                        ("message", DescribeFailure(exception))),
                    true),
                { Value: true } => new FeedbackMessage(
                    Locale.GetString("message-project-remove-chunk", ("position", label)),
                    false),
                _ => new FeedbackMessage(
                    Locale.GetString("message-project-remove-chunk-not-found", ("position", label)),
                    true)
            });
    }

    private void SealProjectChunk(AtmosChunkHandle chunk)
    {
        var position = chunk.Position;
        string label = FormatChunkPosition(position);
        SendChunkEdit(
            (session, id) => session.SealChunk(position, id),
            Locale.GetString("message-project-seal-chunk", ("position", label)),
            message => Locale.GetString(
                "message-project-seal-chunk-failed",
                ("position", label),
                ("message", message)));
    }

    private void UnsleepProjectChunk(AtmosChunkHandle chunk)
    {
        var position = chunk.Position;
        SendChunkEdit(
            (session, id) => session.WakeChunk(position, id),
            Locale.GetString("message-project-unsleep-chunk", ("position", FormatChunkPosition(position))),
            static message => message);
    }

    private void FillProjectChunk(AtmosChunkHandle chunk, int classification)
    {
        var position = chunk.Position;
        string label = FormatChunkPosition(position);
        SendChunkEdit(
            (session, id) => session.FillChunk(position, classification, id),
            Locale.GetString(
                "message-project-fill-chunk",
                ("position", label),
                ("classification", classification)),
            message => Locale.GetString(
                "message-project-fill-chunk-failed",
                ("position", label),
                ("message", message)));
    }

    private void AddProjectGas(GasProperties gas)
    {
        if (!_frame.HasProject)
            return;

        var configDraft = _document.ConfigDraft;
        int token = configDraft.BeginExternalApply();
        Send(
            session => session.AddGas(gas),
            result =>
            {
                configDraft.EndApply(token, result.Succeeded);
                _document.NewGasFeedback = result.Exception is { } exception
                    ? new FeedbackMessage(DescribeFailure(exception), true)
                    : new FeedbackMessage(
                        Locale.GetString("message-project-add-gas", ("name", gas.Name), ("id", result.Value)),
                        false);
            });
    }

    private void RemoveProjectGas(int gasId)
    {
        if (!_frame.HasProject || gasId < 0 || gasId >= Gases.Count)
            return;

        string name = Gases[gasId].Name;
        var configDraft = _document.ConfigDraft;
        int token = configDraft.BeginExternalApply();
        Send(
            session =>
            {
                session.RemoveGas(gasId);
                return true;
            },
            result =>
            {
                configDraft.EndApply(token, result.Succeeded);
                _document.GasRegistryFeedback = result.Exception is { } exception
                    ? new FeedbackMessage(DescribeFailure(exception), true)
                    : new FeedbackMessage(Locale.GetString("message-project-remove-gas", ("name", name)), false);
            });
    }

    private void InjectProjectGas(
        AtmosChunkHandle chunk,
        int x,
        int y,
        int z,
        int gasId,
        float moles,
        float temperature)
    {
        if (_simulation is not { } simulation)
            return;

        if (gasId < 0 || gasId >= Gases.Count)
        {
            _document.InjectionFeedback = Report(Locale.GetString("message-project-inject-gas-not-selected"), true);
            return;
        }

        var id = simulation.Id;
        var position = chunk.Position;
        string success = Locale.GetString(
            "message-project-inject-gas",
            ("moles", $"{moles:G}"),
            ("name", Gases[gasId].Name),
            ("pos", new Int3(x, y, z).ToString()));

        Send(
            session =>
            {
                var cell = session.GetCell(position, x, y, z, id);
                session.AddVoxelGas(cell, gasId, moles, temperature, id);
                return true;
            },
            result => _document.InjectionFeedback = result.Exception is { } exception
                ? new FeedbackMessage(
                    Locale.GetString("message-project-inject-gas-failed", ("message", DescribeFailure(exception))),
                    true)
                : new FeedbackMessage(success, false));
    }

    private void IgniteProjectSource(
        AtmosChunkHandle chunk,
        int x,
        int y,
        int z,
        float temperature,
        float volume)
    {
        if (_simulation is not { } simulation)
            return;

        var id = simulation.Id;
        var position = chunk.Position;
        string success = Locale.GetString(
            "message-project-ignite-source-success",
            ("pos", new Int3(x, y, z).ToString()),
            ("chunk", FormatChunkPosition(position)),
            ("temperature", temperature.ToString("G", CultureInfo.InvariantCulture)));

        Send(
            session =>
            {
                session.IgniteVoxel(session.GetCell(position, x, y, z, id), temperature, volume, id);
                return true;
            },
            result => _document.VoxelFeedback = result.Exception is { } exception
                ? new FeedbackMessage(DescribeFailure(exception), true)
                : Report(success, false));
    }

    /// <summary>
    ///     Logs <paramref name="message" /> and returns it for display beside the control that caused it.
    /// </summary>
    private FeedbackMessage Report(string message, bool isError)
    {
        SetProjectMessage(message, isError);
        return new FeedbackMessage(message, isError);
    }
}
