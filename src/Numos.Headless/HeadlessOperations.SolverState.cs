using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Numos.API;
using Numos.Headless.Protocol;
using Numos.Workbench;

namespace Numos.Headless;

internal sealed partial class HeadlessOperations
{
    // Catalog solvers and the state members of class solvers and simulation components.

    private CommandExecution ListSolverCatalog()
    {
        var entries = _session.GetSolverCatalog()
            .Select(static entry => new CatalogSolverInfo
            {
                Name = entry.Name,
                Before = entry.Before,
                After = entry.After,
                RunsPerSecond = entry.RunsPerSecond > 0 ? entry.RunsPerSecond : null,
                Interval = entry.Interval > 0 ? entry.Interval : null,
                Added = entry.IsAdded
            })
            .ToArray();

        return Result(new CommandResult { SolverCatalog = entries });
    }

    private CommandExecution AddSolver(HeadlessRequest request)
    {
        string name = RequireCatalogName(request.Name);
        int interval = request.Interval ?? 0;
        if (interval < 0)
            throw new HeadlessRequestException("invalidRequest", "interval must be zero or more.");

        _session.AddSolver(name, request.Before, request.After, interval);
        return Result(new CommandResult { Changed = true, Solvers = SolverReports() });
    }

    private CommandExecution RemoveSolver(HeadlessRequest request)
    {
        string name = RequireCatalogName(request.Name);
        bool removed = _session.RemoveSolver(name).Applied;
        return Result(new CommandResult { Changed = removed, Solvers = SolverReports() });
    }

    private CommandExecution GetSolverState(HeadlessRequest request)
    {
        string solver = RequireSolverName(request.Solver);
        var members = RequireSolverMembers(solver);
        return Result(new CommandResult { Members = members.Select(MemberInfo).ToArray() });
    }

    private CommandExecution SetSolverState(HeadlessRequest request)
    {
        string solver = RequireSolverName(request.Solver);
        string member = RequireMemberName(request.Member);
        string? text = ValueText(request.Value);
        var before = RequireWritable(RequireSolverMembers(solver), member, $"solver '{solver}'");
        var after = SetValue(() => _session.SetSolverState(solver, member, text));
        return Result(new CommandResult { Changed = after.Text != before.Text, Member = MemberInfo(after) });
    }

    private CommandExecution GetSimulationComponents(HeadlessRequest request)
    {
        var components = _session.GetSimulationComponents(RequireTarget(request.Simulation).Id)
            .Select(static component => new SimulationComponentInfo
            {
                Key = component.Key,
                Members = component.Members.Select(MemberInfo).ToArray()
            })
            .ToArray();

        return Result(new CommandResult { Components = components });
    }

    private CommandExecution SetSimulationComponentState(HeadlessRequest request)
    {
        var simulation = RequireTarget(request.Simulation).Id;
        if (string.IsNullOrWhiteSpace(request.Key))
            throw Missing("key");

        string key = request.Key;
        string member = RequireMemberName(request.Member);
        string? text = ValueText(request.Value);
        var component = _session.GetSimulationComponents(simulation).FirstOrDefault(candidate => candidate.Key == key) ??
                        throw new HeadlessRequestException(
                            "componentNotFound",
                            $"The simulation holds no component '{key}'.");

        var before = RequireWritable(component.Members, member, $"simulation component '{key}'");
        var after = SetValue(() => _session.SetSimulationComponentState(key, member, text, simulation));
        return Result(new CommandResult { Changed = after.Text != before.Text, Member = MemberInfo(after) });
    }

    private static string RequireCatalogName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw Missing("name");

        if (!AtmosSolverCatalog.TryGet(name, out _))
        {
            throw new HeadlessRequestException(
                "solverNotFound",
                $"The solver catalog has no solver named '{name}'. listSolverCatalog lists the names.");
        }

        return name;
    }

    private static string RequireSolverName(string? solver)
    {
        return string.IsNullOrWhiteSpace(solver) ? throw Missing("solver") : solver;
    }

    private static string RequireMemberName(string? member)
    {
        return string.IsNullOrWhiteSpace(member) ? throw Missing("member") : member;
    }

    private IReadOnlyList<WorkbenchStateMember> RequireSolverMembers(string solver)
    {
        if (_session.TryGetSolverState(solver, out var members))
            return members;

        throw new HeadlessRequestException(
            "solverNotFound",
            $"No stage named '{solver}' holds a class solver.");
    }

    private static WorkbenchStateMember RequireWritable(
        IReadOnlyList<WorkbenchStateMember> members, string member, string owner)
    {
        var found = members.FirstOrDefault(candidate => candidate.Name == member) ??
                    throw new HeadlessRequestException("memberNotFound", $"The {owner} has no state member '{member}'.");

        if (!found.CanWrite)
        {
            throw new HeadlessRequestException(
                "memberReadOnly",
                $"'{member}' of the {owner} is read-only to tools. Change it through a solver command.");
        }

        return found;
    }

    /// <summary>
    ///     Runs a write whose checks already passed, so an <see cref="ArgumentException" /> can only mean the value does
    ///     not fit the member.
    /// </summary>
    private static WorkbenchStateMember SetValue(Func<WorkbenchStateMember> write)
    {
        try
        {
            return write();
        }
        catch (ArgumentException exception)
        {
            throw new HeadlessRequestException("invalidValue", exception.Message);
        }
    }

    /// <summary>
    ///     Turns a JSON value into the invariant text the session parses. A number keeps its exact digits.
    /// </summary>
    private static string? ValueText(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Undefined => throw Missing("value"),
            JsonValueKind.Null => null,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => throw new HeadlessRequestException(
                "invalidRequest",
                "value must be a number, a boolean, a string, or null.")
        };
    }

    private static StateMemberInfo MemberInfo(WorkbenchStateMember member)
    {
        return new StateMemberInfo
        {
            Name = member.Name,
            WireName = member.WireName,
            Type = member.TypeName,
            Unit = member.Unit,
            CanWrite = member.CanWrite,
            Scalar = member.IsScalar,
            Value = ValueJson(member)
        };
    }

    /// <summary>
    ///     Writes a scalar as its JSON value and anything else as its summary text. A float that is not finite becomes
    ///     a string, as everywhere else in the protocol.
    /// </summary>
    private static JsonElement ValueJson(WorkbenchStateMember member)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            switch (member.IsScalar ? member.Value : member.Text)
            {
                case null:
                    writer.WriteNullValue();
                    break;
                case bool flag:
                    writer.WriteBooleanValue(flag);
                    break;
                case float single when float.IsFinite(single):
                    writer.WriteNumberValue(single);
                    break;
                case double real when double.IsFinite(real):
                    writer.WriteNumberValue(real);
                    break;
                case ulong unsigned:
                    writer.WriteNumberValue(unsigned);
                    break;
                case byte or sbyte or short or ushort or int or uint or long:
                    writer.WriteNumberValue(Convert.ToInt64(member.Value, CultureInfo.InvariantCulture));
                    break;
                default:
                    writer.WriteStringValue(member.Text);
                    break;
            }
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
