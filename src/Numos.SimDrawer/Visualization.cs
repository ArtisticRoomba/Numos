using System.Collections.ObjectModel;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;

namespace Numos.SimDrawer;

/// <summary>
///     Describes how a visualization legend is presented.
/// </summary>
public enum VisualizationLegendKind
{
    /// <summary>
    ///     Uses a continuous color gradient.
    /// </summary>
    Gradient,
    /// <summary>
    ///     Uses discrete categories.
    /// </summary>
    Categories
}

/// <summary>
///     Defines one entry in a visualization legend.
/// </summary>
/// <param name="Label">Display label.</param>
/// <param name="Color">Associated color.</param>
/// <param name="Value">Optional represented value.</param>
public readonly record struct VisualizationLegendEntry(
    string Label,
    ColorRgba Color,
    float? Value = null);

/// <summary>
///     Defines the numeric range used by a visualization.
/// </summary>
/// <param name="Minimum">Minimum mapped value.</param>
/// <param name="Maximum">Maximum mapped value.</param>
/// <param name="Resolution">Number of quantization steps.</param>
public readonly record struct VisualizationRange(float Minimum, float Maximum, int Resolution = 32)
{
    /// <summary>
    ///     Gets the default normalized range.
    /// </summary>
    public static VisualizationRange Default => new(0f, 1f);
}

/// <summary>
///     Describes the legend for a visualization.
/// </summary>
public sealed class VisualizationLegend
{
    /// <summary>
    ///     Creates a visualization legend.
    /// </summary>
    /// <param name="title">Legend title.</param>
    /// <param name="units">Units displayed by the legend.</param>
    /// <param name="kind">Legend presentation kind.</param>
    /// <param name="entries">Legend entries.</param>
    public VisualizationLegend(
        string title,
        string units,
        VisualizationLegendKind kind,
        IEnumerable<VisualizationLegendEntry> entries)
    {
        Title = title;
        Units = units;
        Kind = kind;
        Entries = new ReadOnlyCollection<VisualizationLegendEntry>(entries.ToArray());
    }

    /// <summary>
    ///     Gets the legend title.
    /// </summary>
    public string Title { get; }

    /// <summary>
    ///     Gets the displayed units.
    /// </summary>
    public string Units { get; }

    /// <summary>
    ///     Gets the legend presentation kind.
    /// </summary>
    public VisualizationLegendKind Kind { get; }

    /// <summary>
    ///     Gets the legend entries.
    /// </summary>
    public IReadOnlyList<VisualizationLegendEntry> Entries { get; }

    /// <summary>
    ///     Gets the numeric range represented by the legend.
    /// </summary>
    public VisualizationRange Range { get; internal set; } = VisualizationRange.Default;
}

/// <summary>
///     Describes a selected visualization and its legend.
/// </summary>
/// <param name="Id">Stable visualization ID.</param>
/// <param name="DisplayName">Display name.</param>
/// <param name="Legend">Visualization legend.</param>
public sealed record VisualizationDescriptor(
    string Id,
    string DisplayName,
    VisualizationLegend Legend)
{
    /// <summary>
    ///     Gets the numeric range used by the visualization.
    /// </summary>
    public VisualizationRange Range { get; init; } = VisualizationRange.Default;
}

/// <summary>
///     Provides IDs for built-in visualization methods.
/// </summary>
public static class BuiltInVisualizationIds
{
    /// <summary>
    ///     Gets the temperature visualization ID.
    /// </summary>
    public const string Temperature = "temperature";
    /// <summary>
    ///     Gets the pressure visualization ID.
    /// </summary>
    public const string Pressure = "pressure";
    /// <summary>
    ///     Gets the gas-composition visualization ID.
    /// </summary>
    public const string GasComposition = "gas-composition";
    /// <summary>
    ///     Gets the active-air visualization ID.
    /// </summary>
    public const string ActiveOnly = "active-only";
    /// <summary>
    ///     Gets the voxel-classification visualization ID.
    /// </summary>
    public const string VoxelClassification = "voxel-classification";
}

/// <summary>
///     Read-only gas-channel access for one voxel while a presentation frame is being built.
/// </summary>
public readonly struct VoxelGasData
{
    private readonly GasSnapshot[]? _channels;
    private readonly ushort _localIndex;

    internal VoxelGasData(GasSnapshot[] channels, ushort localIndex)
    {
        _channels = channels;
        _localIndex = localIndex;
    }

    /// <summary>
    ///     Gets the number of gas channels.
    /// </summary>
    public int Count => _channels?.Length ?? 0;

    /// <summary>
    ///     Gets a gas ID by channel index.
    /// </summary>
    /// <param name="channel">Gas-channel index.</param>
    /// <returns>The gas registry ID.</returns>
    public int GetGasId(int channel)
    {
        if (_channels == null || (uint)channel >= (uint)_channels.Length)
            throw new ArgumentOutOfRangeException(nameof(channel));

        return _channels[channel].GasId;
    }

    /// <summary>
    ///     Gets the sampled moles for a gas channel.
    /// </summary>
    /// <param name="channel">Gas-channel index.</param>
    /// <returns>The sampled moles.</returns>
    public float GetMoles(int channel)
    {
        if (_channels == null || (uint)channel >= (uint)_channels.Length)
            throw new ArgumentOutOfRangeException(nameof(channel));

        return _channels[channel].Moles[_localIndex];
    }
}

/// <summary>
///     Backend-independent values passed to a visualization method for one voxel.
/// </summary>
/// <param name="Temperature">Temperature in kelvins (K).</param>
/// <param name="Pressure">Pressure in pascals (Pa).</param>
/// <param name="TotalMoles">Total gas amount in moles (mol).</param>
/// <param name="LocalIndex">Local voxel index.</param>
/// <param name="RoomId">Voxel room ID.</param>
/// <param name="PrimaryGasId">Primary gas registry ID.</param>
/// <param name="Gases">Gas-channel access data.</param>
public readonly record struct VoxelSample(
    ushort LocalIndex,
    int RoomId,
    float Temperature,
    float Pressure,
    float TotalMoles,
    int PrimaryGasId,
    VoxelGasData Gases)
{
    /// <summary>
    ///     Gets the numeric range used for mapping this sample.
    /// </summary>
    public VisualizationRange Range { get; init; } = VisualizationRange.Default;
}

/// <summary>
///     Identifies source data required by a visualization.
/// </summary>
[Flags]
public enum VisualizationDataRequirements
{
    /// <summary>
    ///     Requires no additional source data.
    /// </summary>
    None = 0,
    /// <summary>
    ///     Requires temperature data.
    /// </summary>
    Temperature = 1 << 0,
    /// <summary>
    ///     Requires pressure data.
    /// </summary>
    Pressure = 1 << 1,
    /// <summary>
    ///     Requires gas data.
    /// </summary>
    Gases = 1 << 2,
    /// <summary>
    ///     Requires every supported source field.
    /// </summary>
    All = Temperature | Pressure | Gases
}

/// <summary>
///     Defines the voxel domain considered by a visualization.
/// </summary>
public enum VisualizationCellDomain
{
    /// <summary>
    ///     Includes air cells only.
    /// </summary>
    AirCells,
    /// <summary>
    ///     Includes every cell.
    /// </summary>
    AllCells
}

/// <summary>
///     Extensible policy that maps simulation values to visibility and color.
/// </summary>
public interface IVisualizationMethod
{
    /// <summary>
    ///     Gets the stable visualization ID.
    /// </summary>
    string Id { get; }

    /// <summary>
    ///     Gets the display name.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    ///     Changes whenever settings that affect visibility or mapped colors change.
    /// </summary>
    ulong MappingRevision => 0;

    /// <summary>
    ///     Expensive source fields that must be summarized while mapping every voxel.
    ///     Custom visualizations default to all fields for compatibility and correctness.
    /// </summary>
    VisualizationDataRequirements RequiredData => VisualizationDataRequirements.All;

    /// <summary>
    ///     Cell classifications offered to this method. Topology visualizations can opt into
    ///     solids and void cells instead of inheriting the default air-only domain.
    /// </summary>
    VisualizationCellDomain CellDomain => VisualizationCellDomain.AirCells;

    /// <summary>
    ///     Attempts to map a voxel sample to a display color.
    /// </summary>
    /// <param name="sample">Source voxel sample.</param>
    /// <param name="color">Mapped color.</param>
    /// <returns>Whether the voxel should be visible.</returns>
    bool TryGetColor(in VoxelSample sample, out ColorRgba color);

    /// <summary>
    ///     Creates a legend for the active gas IDs.
    /// </summary>
    /// <param name="activeGasIds">Active gas registry IDs.</param>
    /// <returns>The visualization legend.</returns>
    VisualizationLegend CreateLegend(IReadOnlyCollection<int> activeGasIds);

    /// <summary>
    ///     Creates a legend for the active gas IDs and numeric range.
    /// </summary>
    /// <param name="activeGasIds">Active gas registry IDs.</param>
    /// <param name="range">Numeric visualization range.</param>
    /// <returns>The visualization legend.</returns>
    VisualizationLegend CreateLegend(
        IReadOnlyCollection<int> activeGasIds,
        VisualizationRange range)
    {
        return CreateLegend(activeGasIds);
    }
}

/// <summary>
///     Ordered registry used by the presentation builder and UI.
/// </summary>
public sealed class VisualizationRegistry
{
    private readonly Dictionary<string, IVisualizationMethod> _byId =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<IVisualizationMethod> _methods = [];
    private readonly ReadOnlyCollection<IVisualizationMethod> _readOnlyMethods;

    /// <summary>
    ///     Creates an empty visualization registry.
    /// </summary>
    public VisualizationRegistry()
    {
        _readOnlyMethods = _methods.AsReadOnly();
    }

    /// <summary>
    ///     Gets registered visualization methods in order.
    /// </summary>
    public IReadOnlyList<IVisualizationMethod> Methods => _readOnlyMethods;

    /// <summary>
    ///     Registers a visualization method.
    /// </summary>
    /// <param name="method">Method to register.</param>
    public void Register(IVisualizationMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (string.IsNullOrWhiteSpace(method.Id))
            throw new ArgumentException("A visualization method must have a non-empty ID.", nameof(method));

        if (!_byId.TryAdd(method.Id, method))
            throw new InvalidOperationException($"A visualization method with ID '{method.Id}' is already registered.");

        _methods.Add(method);
    }

    /// <summary>
    ///     Gets a registered visualization method by ID.
    /// </summary>
    /// <param name="id">Visualization ID.</param>
    /// <returns>The registered method.</returns>
    public IVisualizationMethod GetRequired(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (_byId.TryGetValue(id, out var method))
            return method;

        throw new KeyNotFoundException($"No visualization method with ID '{id}' is registered.");
    }

    /// <summary>
    ///     Creates a registry containing built-in visualizations.
    /// </summary>
    /// <param name="config">Simulation configuration.</param>
    /// <returns>The configured registry.</returns>
    public static VisualizationRegistry CreateDefault(AtmosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var registry = new VisualizationRegistry();
        registry.Register(new TemperatureVisualization());
        registry.Register(new PressureVisualization());
        registry.Register(new GasCompositionVisualization(config));
        registry.Register(new ActiveOnlyVisualization(config));
        registry.Register(new VoxelClassificationVisualization());
        return registry;
    }

    private static float Normalize(float value, VisualizationRange range)
    {
        if (!float.IsFinite(value))
            return value > 0f ? 1f : 0f;

        if (range.Maximum <= range.Minimum)
            return 0.5f;

        float normalized = Math.Clamp((value - range.Minimum) / (range.Maximum - range.Minimum), 0f, 1f);
        int resolution = Math.Max(range.Resolution, 1);
        return resolution == 1
            ? 0.5f
            : MathF.Round(normalized * (resolution - 1)) / (resolution - 1);
    }

    /// <summary>
    ///     Gets the stable display color for a gas ID.
    /// </summary>
    /// <param name="gasId">Gas registry ID.</param>
    /// <returns>The assigned color.</returns>
    public static ColorRgba ColorForGasId(int gasId)
    {
        uint hash = unchecked((uint)gasId) * 2654435761u;
        float hue = (hash & 0xFFFF) / 65535f;
        return HsvToRgb(hue, 0.72f, 0.9f);
    }

    private static ColorRgba HsvToRgb(float hue, float saturation, float value)
    {
        float scaled = hue * 6f;
        int sector = (int)MathF.Floor(scaled) % 6;
        float fraction = scaled - MathF.Floor(scaled);
        float p = value * (1f - saturation);
        float q = value * (1f - fraction * saturation);
        float t = value * (1f - (1f - fraction) * saturation);

        return sector switch
        {
            0 => new ColorRgba(value, t, p),
            1 => new ColorRgba(q, value, p),
            2 => new ColorRgba(p, value, t),
            3 => new ColorRgba(p, q, value),
            4 => new ColorRgba(t, p, value),
            _ => new ColorRgba(value, p, q)
        };
    }

    private sealed class VoxelClassificationVisualization : IVisualizationMethod
    {
        private readonly static ColorRgba UnassignedColor = new(0.35f, 0.35f, 0.38f);
        private readonly static ColorRgba SolidColor = new(0.18f, 0.18f, 0.2f);
        private readonly static ColorRgba VoidColor = new(0.04f, 0.04f, 0.05f);

        public string Id => BuiltInVisualizationIds.VoxelClassification;

        public string DisplayName => "Voxel Classification";

        public VisualizationDataRequirements RequiredData => VisualizationDataRequirements.None;

        public VisualizationCellDomain CellDomain => VisualizationCellDomain.AllCells;

        public bool TryGetColor(in VoxelSample sample, out ColorRgba color)
        {
            color = sample.RoomId switch
            {
                VoxelClassification.RoomSolid => SolidColor,
                VoxelClassification.RoomVoid => VoidColor,
                VoxelClassification.RoomUnassigned => UnassignedColor,
                _ => ColorForRoom(sample.RoomId)
            };

            return true;
        }

        public VisualizationLegend CreateLegend(IReadOnlyCollection<int> activeGasIds)
        {
            return new VisualizationLegend(
                "Voxel classification",
                "",
                VisualizationLegendKind.Categories,
                [
                    new VisualizationLegendEntry("Solid (-2)", SolidColor, VoxelClassification.RoomSolid),
                    new VisualizationLegendEntry("Void (-1)", VoidColor, VoxelClassification.RoomVoid),
                    new VisualizationLegendEntry("Unassigned (0)", UnassignedColor, VoxelClassification.RoomUnassigned),
                    new VisualizationLegendEntry("Room IDs (positive)", ColorForRoom(1))
                ]);
        }

        private static ColorRgba ColorForRoom(int roomId)
        {
            unchecked
            {
                uint hash = (uint)roomId * 2654435761u;
                hash ^= hash >> 16;
                float hue = hash % 360u / 360f;
                const float saturation = 0.72f;
                const float value = 0.9f;
                float scaled = hue * 6f;
                int sector = (int)scaled;
                float fraction = scaled - sector;
                float p = value * (1f - saturation);
                float q = value * (1f - saturation * fraction);
                float t = value * (1f - saturation * (1f - fraction));
                return (sector % 6) switch
                {
                    0 => new ColorRgba(value, t, p),
                    1 => new ColorRgba(q, value, p),
                    2 => new ColorRgba(p, value, t),
                    3 => new ColorRgba(p, q, value),
                    4 => new ColorRgba(t, p, value),
                    _ => new ColorRgba(value, p, q)
                };
            }
        }
    }

    private sealed class TemperatureVisualization : IVisualizationMethod
    {
        private readonly static ColorRgba Cold = new(0f, 0f, 1f);
        private readonly static ColorRgba Temperate = new(1f, 1f, 0f);
        private readonly static ColorRgba Hot = new(1f, 0f, 0f);

        public string Id => BuiltInVisualizationIds.Temperature;

        public string DisplayName => "Temperature";

        public VisualizationDataRequirements RequiredData => VisualizationDataRequirements.Temperature;

        public bool TryGetColor(in VoxelSample sample, out ColorRgba color)
        {
            float normalized = Normalize(sample.Temperature, sample.Range);
            color = normalized < 0.5f
                ? ColorRgba.Lerp(Cold, Temperate, normalized * 2f)
                : ColorRgba.Lerp(Temperate, Hot, (normalized - 0.5f) * 2f);

            return true;
        }

        public VisualizationLegend CreateLegend(IReadOnlyCollection<int> activeGasIds)
        {
            return CreateLegend(activeGasIds, VisualizationRange.Default);
        }

        public VisualizationLegend CreateLegend(
            IReadOnlyCollection<int> activeGasIds,
            VisualizationRange range)
        {
            float midpoint = range.Minimum + (range.Maximum - range.Minimum) * 0.5f;
            return new VisualizationLegend(
                "Temperature",
                "K",
                VisualizationLegendKind.Gradient,
                [
                    new VisualizationLegendEntry($"{range.Minimum:G6} K", Cold, range.Minimum),
                    new VisualizationLegendEntry($"{midpoint:G6} K", Temperate, midpoint),
                    new VisualizationLegendEntry($"{range.Maximum:G6} K", Hot, range.Maximum)
                ]);
        }
    }

    private sealed class PressureVisualization : IVisualizationMethod
    {
        public string Id => BuiltInVisualizationIds.Pressure;

        public string DisplayName => "Pressure";

        public VisualizationDataRequirements RequiredData => VisualizationDataRequirements.Pressure;

        public bool TryGetColor(in VoxelSample sample, out ColorRgba color)
        {
            float normalized = Normalize(sample.Pressure, sample.Range);
            color = new ColorRgba(normalized, normalized, normalized);
            return true;
        }

        public VisualizationLegend CreateLegend(IReadOnlyCollection<int> activeGasIds)
        {
            return CreateLegend(activeGasIds, VisualizationRange.Default);
        }

        public VisualizationLegend CreateLegend(
            IReadOnlyCollection<int> activeGasIds,
            VisualizationRange range)
        {
            float midpoint = range.Minimum + (range.Maximum - range.Minimum) * 0.5f;
            return new VisualizationLegend(
                "Pressure",
                "simulation units",
                VisualizationLegendKind.Gradient,
                [
                    new VisualizationLegendEntry($"{range.Minimum:G6}", new ColorRgba(0f, 0f, 0f), range.Minimum),
                    new VisualizationLegendEntry($"{midpoint:G6}", new ColorRgba(0.5f, 0.5f, 0.5f), midpoint),
                    new VisualizationLegendEntry($"{range.Maximum:G6}", new ColorRgba(1f, 1f, 1f), range.Maximum)
                ]);
        }
    }

    private sealed class GasCompositionVisualization(AtmosConfig config) : IVisualizationMethod
    {
        public string Id => BuiltInVisualizationIds.GasComposition;

        public string DisplayName => "Gas Composition";

        public ulong MappingRevision
        {
            get
            {
                const ulong offsetBasis = 14695981039346656037UL;
                const ulong prime = 1099511628211UL;
                ulong hash = offsetBasis;
                for (int gasId = 0; gasId < config.GasRegistry.Count; gasId++)
                {
                    hash ^= unchecked((uint)gasId);
                    hash *= prime;
                    string name = config.GasRegistry[gasId].Name ?? string.Empty;
                    foreach (char character in name)
                    {
                        hash ^= character;
                        hash *= prime;
                    }
                }

                hash ^= unchecked((uint)config.GasRegistry.Count);
                return hash * prime;
            }
        }

        public VisualizationDataRequirements RequiredData => VisualizationDataRequirements.Gases;

        public bool TryGetColor(in VoxelSample sample, out ColorRgba color)
        {
            color = sample.PrimaryGasId < 0
                ? new ColorRgba(0.12f, 0.12f, 0.14f)
                : ColorForGasId(sample.PrimaryGasId);

            return true;
        }

        public VisualizationLegend CreateLegend(IReadOnlyCollection<int> activeGasIds)
        {
            var ids = new SortedSet<int>(activeGasIds);
            for (int gasId = 0; gasId < config.GasRegistry.Count; gasId++)
                ids.Add(gasId);

            var entries = new List<VisualizationLegendEntry>
            {
                new("No gas", new ColorRgba(0.12f, 0.12f, 0.14f))
            };

            foreach (int gasId in ids)
            {
                string label = gasId >= 0 && gasId < config.GasRegistry.Count
                    ? config.GasRegistry[gasId].Name
                    : $"Gas {gasId}";

                entries.Add(new VisualizationLegendEntry(label, ColorForGasId(gasId), gasId));
            }

            return new VisualizationLegend("Primary gas", "", VisualizationLegendKind.Categories, entries);
        }
    }

    private sealed class ActiveOnlyVisualization(AtmosConfig config) : IVisualizationMethod
    {
        private readonly static ColorRgba ActiveColor = new(0.3f, 0.7f, 1f);

        public string Id => BuiltInVisualizationIds.ActiveOnly;

        public string DisplayName => "Active Air";

        public ulong MappingRevision => BitConverter.SingleToUInt32Bits(config.VacuumThreshold);

        public VisualizationDataRequirements RequiredData => VisualizationDataRequirements.Pressure;

        public bool TryGetColor(in VoxelSample sample, out ColorRgba color)
        {
            color = ActiveColor;
            return float.IsFinite(sample.Pressure) && sample.Pressure > config.VacuumThreshold;
        }

        public VisualizationLegend CreateLegend(IReadOnlyCollection<int> activeGasIds)
        {
            return new VisualizationLegend(
                "Active air",
                $"> {config.VacuumThreshold:G} pressure",
                VisualizationLegendKind.Categories,
                [new VisualizationLegendEntry("Visible", ActiveColor)]);
        }
    }
}