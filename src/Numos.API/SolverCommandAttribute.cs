using JetBrains.Annotations;

namespace Numos.API;

/// <summary>
///     Declares a recorded input of a class solver, as an instance partial method the <c>Numos.API</c> source
///     generator implements.
/// </summary>
/// <remarks>
///     <para>
///         Put the attribute on an instance <see langword="partial" /> <see langword="void" /> method declaration with
///         no body, in a <see langword="partial" /> class that implements <see cref="IAtmosWorldSolver" />. Every type
///         the class is nested in must be <see langword="partial" /> too. Write the body as an instance method named
///         <c>Apply{Name}</c> with the same parameter types. The method must not be generic, and no parameter can be
///         <see langword="ref" />, <see langword="out" />, <see langword="in" />, or <see langword="params" />.
///     </para>
///     <para>
///         <c>Apply{Name}</c> can take an <see cref="AtmosWorld" /> before the command's parameters. The generated
///         code then passes the world that holds the solver, live and in replay. The world is not recorded. A restore
///         or <see cref="AtmosWorldReplayTimeline.Import" /> can replace a catalog stage with a new instance, and this
///         parameter is how that instance reaches its simulations.
///     </para>
///     <para>
///         A parameter can have any type a <c>[NumosSerialize]</c> member can have: primitives, quantity aliases,
///         enums, strings, <c>Int3</c>, handles such as <see cref="AtmosCellRef" />, types with <c>[NumosSerialize]</c>
///         members, the supported collections, and gases, which the payload stores by name.
///     </para>
///     <para>
///         A call between ticks runs <c>Apply{Name}</c> and records it as one
///         <see cref="InvokeAtmosSolverCommandOperation" />. The host calls inside <c>Apply{Name}</c> apply without
///         being recorded themselves, because replay runs the command again. A call from a solver during a tick, during
///         a replay, or with no recording live applies and records nothing. While a tick runs, any other thread
///         that calls a command gets an <see cref="InvalidOperationException" /> rather than blocking.
///     </para>
///     <para>
///         Check the arguments before the first change. When <c>Apply{Name}</c> throws, Numos does not record the
///         command. The host calls it made before the throw stay applied but are missing from the recording, so replay
///         diverges. A command cannot tick, update, replay, restore, or dispose the world, change the solver layout, or
///         start, stop, or resume recording.
///     </para>
///     <para>
///         The recording stores the stage name, the command id, and the arguments. The id hashes the wire name, which
///         is the method name unless the attribute passes one. Keep the old name in the attribute when you rename the
///         method, so old recordings still replay. Each argument is stored under the id of its parameter name. Replay
///         passes <see langword="default" /> for a parameter that the recording lacks, and skips a stored argument that
///         no parameter names.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     public sealed partial class FireSolver : IAtmosWorldSolver
///     {
///         [SolverCommand]
///         public partial void Ignite(AtmosCellRef cell, float temperature);
///
///         private void ApplyIgnite(AtmosCellRef cell, float temperature)
///         {
///             if (temperature &lt;= 0f)
///                 throw new ArgumentOutOfRangeException(nameof(temperature));
///
///             _burning.Add(cell);
///         }
///
///         public void Solve(AtmosWorldSolverContext context) { /* ... */ }
///     }
///
///     fire.Ignite(cell, 900f); // between ticks: applied, then recorded as one operation
///     </code>
/// </example>
[PublicAPI]
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class SolverCommandAttribute : Attribute
{
    /// <summary>
    ///     Declares a command whose wire name is the method name.
    /// </summary>
    public SolverCommandAttribute()
    {
    }

    /// <summary>
    ///     Declares a command with an explicit wire name.
    /// </summary>
    /// <param name="wireName">
    ///     The name the command id hashes. Pass the old method name after a rename. A blank name is a build error
    ///     (<c>NUMOSCOMMAND006</c>).
    /// </param>
    public SolverCommandAttribute(string wireName)
    {
        WireName = wireName;
    }

    /// <summary>
    ///     Gets the explicit wire name, or <see langword="null" /> when the command uses its method name.
    /// </summary>
    public string? WireName { get; }
}
