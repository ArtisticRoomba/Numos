using Numos.CoreSim;
using Numos.CoreSim.Replay;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;

namespace Numos.Serialization;

/// <summary>
///     Reads and writes a single value with <see cref="NumosSerializeAttribute" /> members, such as a solver
///     configuration preset, outside a world archive.
/// </summary>
/// <remarks>
///     <para>
///         The stream holds a small header (magic, container version, the type's <see cref="INumosState{TSelf}.StateKey" />)
///         followed by the same field payload a solver configuration has inside a world archive, so there's one wire
///         format rather than two. Fields carry ids and lengths, so a file written before a field was added still loads
///         with that field at its default, and ids the reader doesn't know are skipped.
///     </para>
///     <para>
///         Only types with a constant <c>Key</c> property (every solver configuration has one) can be saved this way;
///         the key is how a load knows the file holds the type it asked for.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     NumosStateSerializer.Write(stream, fireSettings);
///     stream.Position = 0;
///     FireSettings loaded = NumosStateSerializer.Read&lt;FireSettings&gt;(stream, world.Config.GasRegistry);
///     </code>
/// </example>
public static class NumosStateSerializer
{
    private const ushort ContainerVersion = 1;
    private static readonly byte[] Magic = [0x4e, 0x55, 0x4d, 0x53, 0x54, 0x0d, 0x0a, 0x1a];

    /// <summary>
    ///     Writes one value and leaves the destination open.
    /// </summary>
    /// <param name="destination">Writable destination.</param>
    /// <param name="value">The value to write.</param>
    /// <typeparam name="T">A serializable type with a constant <c>Key</c>, such as a solver configuration.</typeparam>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination" /> is not writable.</exception>
    /// <exception cref="InvalidOperationException"><typeparamref name="T" /> declares no key.</exception>
    public static void Write<T>(Stream destination, T value) where T : INumosState<T>
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(value);
        if (!destination.CanWrite)
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));

        string key = RequireKey<T>();
        var sink = new StateBinarySink();
        T.WriteState(ref sink, value);

        using var writer = new BinaryWriter(destination, AtmosReplayCodec.Utf8, true);
        writer.Write(Magic);
        writer.Write(ContainerVersion);
        AtmosReplayCodec.WriteString(writer, key);
        writer.Write((ulong)sink.WrittenSpan.Length);
        writer.Write(sink.WrittenSpan);
    }

    /// <summary>
    ///     Reads one value written by <see cref="Write{T}" />, leaving the source positioned just after it.
    /// </summary>
    /// <param name="source">Readable source.</param>
    /// <param name="gases">
    ///     Resolves <see cref="GasProperties" /> references by name. Needed only when the value references gases.
    /// </param>
    /// <param name="options">
    ///     Optional limits for untrusted input. <see cref="NumosReplayReadOptions.MaxPayloadBytes" /> and
    ///     <see cref="NumosReplayReadOptions.MaxStringBytes" /> apply; the rest are for world replays.
    /// </param>
    /// <typeparam name="T">A serializable type with a constant <c>Key</c>, such as a solver configuration.</typeparam>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="source" /> is not readable.</exception>
    /// <exception cref="InvalidOperationException">
    ///     <typeparamref name="T" /> declares no key, or the value references a gas and <paramref name="gases" /> is
    ///     <see langword="null" />.
    /// </exception>
    /// <exception cref="InvalidDataException">
    ///     The data is malformed, holds a different type's key, or references a gas <paramref name="gases" /> lacks.
    /// </exception>
    public static T Read<T>(Stream source, IGasRegistry? gases = null, NumosReplayReadOptions? options = null)
        where T : INumosState<T>
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("The source stream must be readable.", nameof(source));

        options ??= new NumosReplayReadOptions();
        if (options.MaxPayloadBytes <= 0 || options.MaxStringBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(options));

        string expected = RequireKey<T>();
        using var reader = new BinaryReader(source, AtmosReplayCodec.Utf8, true);
        try
        {
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
                throw new InvalidDataException("The stream is not a Numos state file.");

            if (reader.ReadUInt16() != ContainerVersion)
                throw new InvalidDataException("The Numos state file version is unsupported.");

            string key = AtmosReplayCodec.ReadString(reader, options.MaxStringBytes);
            if (key != expected)
                throw new InvalidDataException($"The Numos state file holds '{key}', not '{expected}' ({typeof(T)}).");

            ulong length = reader.ReadUInt64();
            if (length > (ulong)Math.Min(options.MaxPayloadBytes, Array.MaxLength))
                throw new InvalidDataException("The Numos state file exceeds the configured payload limit.");

            byte[] payload = AtmosReplayCodec.ReadPayload(reader, (int)length);
            var state = new StateReader(payload, options.MaxStringBytes);
            T value = T.ReadState(ref state, gases);
            if (!state.IsAtEnd)
                throw new InvalidDataException("The Numos state file has trailing payload data.");

            return value;
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("The Numos state file is truncated.", exception);
        }
    }

    private static string RequireKey<T>() where T : INumosState<T>
    {
        return T.StateKey ??
               throw new InvalidOperationException(
                   $"{typeof(T)} can't be saved on its own because it has no constant Key property.");
    }
}
