using System.Text.Json.Serialization;
using Numos.Headless.Diagnostics;

namespace Numos.Headless.Protocol;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(HeadlessRequest))]
[JsonSerializable(typeof(HeadlessResponse))]
[JsonSerializable(typeof(SimulationStateReport))]
internal sealed partial class HeadlessJsonContext : JsonSerializerContext;