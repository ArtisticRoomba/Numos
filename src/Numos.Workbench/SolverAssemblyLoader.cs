using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Numos.API;

namespace Numos.Workbench;

/// <summary>
///     What happened to one file that <see cref="SolverAssemblyLoader.Load" /> looked at.
/// </summary>
public enum SolverAssemblyStatus
{
    /// <summary>
    ///     The loader loaded the assembly and ran its module initializer.
    /// </summary>
    Loaded,

    /// <summary>
    ///     The process already held an assembly with the same name. The loader ran that assembly's module initializer
    ///     and loaded nothing new.
    /// </summary>
    AlreadyLoaded,

    /// <summary>
    ///     The file is not a .NET assembly, or it has no generated <c>NumosModule</c> type. The loader did not load it.
    /// </summary>
    Skipped,

    /// <summary>
    ///     Loading the assembly or running its module initializer threw. The assembly can stay loaded, because the
    ///     default load context never unloads.
    /// </summary>
    Failed,
}

/// <summary>
///     One file that <see cref="SolverAssemblyLoader.Load" /> looked at, and what its module initializer registered.
/// </summary>
/// <param name="Path">The full path of the file.</param>
/// <param name="AssemblyName">The simple assembly name, or <see langword="null" /> when the file is not an assembly.</param>
/// <param name="Status">What happened to the file.</param>
/// <param name="Solvers">The <see cref="AtmosSolverCatalog" /> names that appeared, in ordinal order.</param>
/// <param name="ChunkFields">
///     The keys of the <see cref="ChunkField{T}" /> static fields and properties the assembly declares, in ordinal
///     order. Only a <see cref="SolverAssemblyStatus.Loaded" /> assembly lists them.
/// </param>
/// <param name="Components">The <see cref="SimulationComponentCatalog" /> keys that appeared, in ordinal order.</param>
/// <param name="Message">Why the file was skipped or failed, or <see langword="null" />.</param>
public sealed record SolverAssemblyResult(
    string Path,
    string? AssemblyName,
    SolverAssemblyStatus Status,
    IReadOnlyList<string> Solvers,
    IReadOnlyList<string> ChunkFields,
    IReadOnlyList<string> Components,
    string? Message);

/// <summary>
///     The outcome of one <see cref="SolverAssemblyLoader.Load" /> call.
/// </summary>
/// <param name="Path">The full path that was named.</param>
/// <param name="IsFolder">Whether <paramref name="Path" /> named a folder.</param>
/// <param name="Error">
///     Why nothing could load: loading is unsupported, the path does not exist, or it names no Numos assembly.
///     <see langword="null" /> when the path names at least one Numos assembly, even one that failed.
/// </param>
/// <param name="Assemblies">Every file the loader looked at, in ordinal path order.</param>
public sealed record SolverAssemblyLoadReport(
    string Path,
    bool IsFolder,
    string? Error,
    IReadOnlyList<SolverAssemblyResult> Assemblies)
{
    /// <summary>
    ///     Gets whether no assembly failed and at least one loaded or was already loaded.
    /// </summary>
    public bool Succeeded => Error == null && Assemblies.All(static result => result.Status != SolverAssemblyStatus.Failed);

    /// <summary>
    ///     Describes the report in English for a log or a console.
    /// </summary>
    /// <returns>
    ///     The error, then one line per loaded, already loaded, or failed assembly, then the number of skipped files.
    /// </returns>
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();
        if (Error != null)
            lines.Add($"Could not load solvers from '{Path}': {Error}");

        int skipped = 0;
        foreach (var result in Assemblies)
        {
            switch (result.Status)
            {
                case SolverAssemblyStatus.Loaded:
                    lines.Add($"Loaded {result.AssemblyName} from '{result.Path}': {DescribeRegistrations(result)}.");
                    break;
                case SolverAssemblyStatus.AlreadyLoaded:
                    lines.Add($"{result.AssemblyName} was already loaded: {DescribeRegistrations(result)}.");
                    break;
                case SolverAssemblyStatus.Failed:
                    lines.Add($"Could not load {result.AssemblyName} from '{result.Path}': {result.Message}");
                    break;
                default:
                    skipped++;
                    break;
            }
        }

        // A named file that is skipped is already the error line.
        if (skipped > 0 && IsFolder)
            lines.Add(skipped == 1 ? "Skipped 1 file that is not a Numos assembly." : $"Skipped {skipped} files that are not Numos assemblies.");

        return lines;
    }

    private static string DescribeRegistrations(SolverAssemblyResult result)
    {
        return $"solvers {List(result.Solvers)}; chunk fields {List(result.ChunkFields)}; components {List(result.Components)}";

        static string List(IReadOnlyList<string> names) => names.Count == 0 ? "none" : string.Join(", ", names);
    }
}

/// <summary>
///     Loads assemblies that hold custom solvers, chunk components, and simulation components, so a tool can use them
///     from <see cref="AtmosSolverCatalog" /> and <see cref="SimulationComponentCatalog" />.
/// </summary>
/// <remarks>
///     <para>
///         The loader loads each assembly into the default <see cref="AssemblyLoadContext" /> and runs its module
///         initializer, which registers its solvers, chunk fields, and components. An assembly stays loaded for the
///         life of the process. Load only assemblies the user named. Opening a replay file must never load one, because
///         a file names solvers and cannot vouch for the code behind a name.
///     </para>
///     <para>
///         Loading needs the JIT and an untrimmed build. Under NativeAOT or trimming, <see cref="IsSupported" /> is
///         <see langword="false" /> and <see cref="Load" /> returns an error without touching the file.
///     </para>
///     <para>
///         A dependency that the default context cannot find is looked up next to the assemblies loaded so far. Every
///         member is safe to call from one thread at a time.
///     </para>
/// </remarks>
public sealed class SolverAssemblyLoader
{
    /// <summary>
    ///     The name of the type that <c>Numos.API.Generators</c> emits in every assembly with registrations.
    /// </summary>
    private const string ModuleTypeName = "NumosModule";

    /// <summary>
    ///     The <see cref="SolverAssemblyLoadReport.Error" /> of every load when <see cref="IsSupported" /> is
    ///     <see langword="false" />.
    /// </summary>
    public const string UnsupportedMessage =
        "This build cannot load solver assemblies, because it is compiled ahead of time (NativeAOT) or trimmed. " +
        "Run a JIT build, such as a Viewer release archive or `dotnet run --project src/Numos.Viewer`, to use them.";

    private static readonly Lock ProbeGate = new();
    private static readonly List<string> ProbeDirectories = [];
    private static bool _resolvingHooked;

    private readonly bool _enabled;

    /// <summary>
    ///     Creates a loader that loads when the runtime can run loaded code.
    /// </summary>
    public SolverAssemblyLoader() : this(true)
    {
    }

    /// <summary>
    ///     Creates a loader that can be turned off, so a test can take the unsupported branch on the JIT.
    /// </summary>
    /// <param name="enabled">
    ///     <see langword="false" /> to refuse every load. <see langword="true" /> still refuses under NativeAOT or
    ///     trimming.
    /// </param>
    public SolverAssemblyLoader(bool enabled)
    {
        _enabled = enabled;
    }

    /// <summary>
    ///     Gets whether <see cref="Load" /> can load assemblies.
    /// </summary>
    public bool IsSupported => _enabled && IsDynamicLoadingAvailable;

    /// <summary>
    ///     Gets whether the runtime can run code from an assembly it loads at run time.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         NativeAOT has no JIT, so <see cref="RuntimeFeature.IsDynamicCodeSupported" /> is <see langword="false" />
    ///         there. A trimmed build can lack members that a loaded assembly calls, so loading is off there too.
    ///     </para>
    ///     <para>
    ///         The trimmer treats a <see cref="FeatureGuardAttribute" /> property for
    ///         <see cref="RequiresUnreferencedCodeAttribute" /> as <see langword="false" /> in every trimmed build, with or
    ///         without NativeAOT, and removes the code it guards. The analyzer cannot see that from the property body, so
    ///         IL4000 is disabled for this one property.
    ///     </para>
    /// </remarks>
#pragma warning disable IL4000 // The trimmer substitutes false for a RequiresUnreferencedCode guard. See the remarks.
    [FeatureGuard(typeof(RequiresUnreferencedCodeAttribute))]
    [FeatureGuard(typeof(RequiresDynamicCodeAttribute))]
    private static bool IsDynamicLoadingAvailable => RuntimeFeature.IsDynamicCodeSupported;
#pragma warning restore IL4000

    /// <summary>
    ///     Loads one assembly, or every Numos assembly in a folder.
    /// </summary>
    /// <param name="path">A `.dll` file, or a folder whose top-level `.dll` files are loaded in ordinal order.</param>
    /// <returns>What appeared in the catalogs, per file.</returns>
    /// <remarks>
    ///     <para>
    ///         The loader reads each file's metadata first. It loads only a .NET assembly with a generated
    ///         <c>NumosModule</c> type. A named file without one is an error. In a folder, such a file is
    ///         <see cref="SolverAssemblyStatus.Skipped" />, so the copies of <c>Numos.API</c> and other dependencies that
    ///         a build puts next to a mod do no harm.
    ///     </para>
    ///     <para>
    ///         The solver and component lists are the difference between the catalogs before and after the module
    ///         initializer runs. Two assemblies that register the same name with different types make the second one
    ///         fail.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path" /> is blank.</exception>
    public SolverAssemblyLoadReport Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = System.IO.Path.GetFullPath(path);
        // IsSupported already includes the static guard, but the analyzer only sees a guard read directly.
        if (!IsSupported || !IsDynamicLoadingAvailable)
            return new SolverAssemblyLoadReport(fullPath, false, UnsupportedMessage, []);

        if (Directory.Exists(fullPath))
            return LoadFolder(fullPath);

        if (!File.Exists(fullPath))
            return new SolverAssemblyLoadReport(fullPath, false, $"'{fullPath}' does not exist.", []);

        var result = LoadFile(fullPath);
        string? error = result.Status == SolverAssemblyStatus.Skipped
            ? $"'{fullPath}' is not a Numos assembly: {result.Message}"
            : null;

        return new SolverAssemblyLoadReport(fullPath, false, error, [result]);
    }

    [RequiresUnreferencedCode("A loaded assembly can use members that trimming removed.")]
    [RequiresDynamicCode("A loaded assembly needs the JIT.")]
    private static SolverAssemblyLoadReport LoadFolder(string folder)
    {
        string[] files = Directory.GetFiles(folder, "*.dll", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);
        var results = new List<SolverAssemblyResult>(files.Length);
        foreach (string file in files)
            results.Add(LoadFile(file));

        string? error = results.Any(static result => result.Status != SolverAssemblyStatus.Skipped)
            ? null
            : $"'{folder}' holds no Numos assembly.";

        return new SolverAssemblyLoadReport(folder, true, error, results);
    }

    [RequiresUnreferencedCode("A loaded assembly can use members that trimming removed.")]
    [RequiresDynamicCode("A loaded assembly needs the JIT.")]
    private static SolverAssemblyResult LoadFile(string file)
    {
        if (!TryReadIdentity(file, out string? name, out string? skipped))
            return Result(file, name, SolverAssemblyStatus.Skipped, skipped);

        var existing = FindLoaded(name!);
        var solversBefore = AtmosSolverCatalog.Names;
        var componentsBefore = SimulationComponentCatalog.Keys;
        Assembly? assembly = existing;
        try
        {
            if (assembly == null)
            {
                AddProbeDirectory(System.IO.Path.GetDirectoryName(file)!);
                assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
            }

            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
            string[] chunkFields = existing == null ? FindChunkFieldKeys(assembly) : [];
            return new SolverAssemblyResult(
                file,
                name,
                existing == null ? SolverAssemblyStatus.Loaded : SolverAssemblyStatus.AlreadyLoaded,
                Appeared(solversBefore, AtmosSolverCatalog.Names),
                chunkFields,
                Appeared(componentsBefore, SimulationComponentCatalog.Keys),
                null);
        }
        catch (Exception exception)
        {
            // A module initializer failure arrives wrapped, and the inner exception names the conflict.
            var cause = exception is TypeInitializationException { InnerException: { } inner } ? inner : exception;
            return new SolverAssemblyResult(
                file,
                name,
                SolverAssemblyStatus.Failed,
                Appeared(solversBefore, AtmosSolverCatalog.Names),
                [],
                Appeared(componentsBefore, SimulationComponentCatalog.Keys),
                cause.Message);
        }
    }

    /// <summary>
    ///     Reads the assembly name and looks for the generated module type without loading the file.
    /// </summary>
    private static bool TryReadIdentity(string file, out string? name, out string? reason)
    {
        name = null;
        reason = null;
        try
        {
            using var stream = File.OpenRead(file);
            using var reader = new PEReader(stream);
            if (!reader.HasMetadata)
            {
                reason = "the file is not a .NET assembly.";
                return false;
            }

            var metadata = reader.GetMetadataReader();
            if (!metadata.IsAssembly)
            {
                reason = "the file is a module without an assembly manifest.";
                return false;
            }

            name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            foreach (var handle in metadata.TypeDefinitions)
            {
                if (metadata.StringComparer.Equals(metadata.GetTypeDefinition(handle).Name, ModuleTypeName))
                    return true;
            }

            reason = "it has no generated NumosModule type, so it registers no solvers, chunk fields, or components.";
            return false;
        }
        catch (BadImageFormatException)
        {
            reason = "the file is not a .NET assembly.";
            return false;
        }
        catch (IOException exception)
        {
            reason = exception.Message;
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            reason = exception.Message;
            return false;
        }
    }

    private static Assembly? FindLoaded(string name)
    {
        foreach (var assembly in AssemblyLoadContext.Default.Assemblies)
        {
            if (string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                return assembly;
        }

        return null;
    }

    /// <summary>
    ///     Reads the key of every <see cref="ChunkField{T}" /> that a static field or property of the assembly holds.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The chunk field registry has no public list of keys, so the loader cannot diff it like the catalogs. The
    ///         generated holders keep each field in a static member, and the module initializer has already created
    ///         them. A field that the assembly registers without keeping it is missing from the list.
    ///     </para>
    ///     <para>
    ///         Reading a member runs its type's static constructor. The list leaves out a type that fails to load or
    ///         initialize, because the assembly itself loaded and its registrations stand.
    ///     </para>
    /// </remarks>
    [RequiresUnreferencedCode("Reads static members of a loaded assembly through reflection.")]
    private static string[] FindChunkFieldKeys(Assembly assembly)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            types = exception.Types.Where(static type => type != null).ToArray()!;
        }

        foreach (var type in types)
        {
            if (type.ContainsGenericParameters)
                continue;

            try
            {
                AddChunkFieldKeys(type, keys);
            }
            catch (Exception exception) when (exception is TypeLoadException or TypeInitializationException or
                                                  FileNotFoundException or FileLoadException or
                                                  TargetInvocationException)
            {
            }
        }

        return [..keys];
    }

    [RequiresUnreferencedCode("Reads static members of a loaded assembly through reflection.")]
    private static void AddChunkFieldKeys(Type type, SortedSet<string> keys)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.DeclaredOnly;

        foreach (var field in type.GetFields(flags))
        {
            if (IsChunkField(field.FieldType) && ReadKey(field.GetValue(null)) is { } key)
                keys.Add(key);
        }

        foreach (var property in type.GetProperties(flags))
        {
            if (IsChunkField(property.PropertyType) && property.GetIndexParameters().Length == 0 &&
                property.GetMethod != null && ReadKey(property.GetValue(null)) is { } key)
            {
                keys.Add(key);
            }
        }
    }

    private static bool IsChunkField(Type type)
    {
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ChunkField<>);
    }

    [RequiresUnreferencedCode("Reads the Key property of a closed ChunkField<T> through reflection.")]
    private static string? ReadKey(object? field)
    {
        return field?.GetType().GetProperty(nameof(ChunkField<int>.Key))?.GetValue(field) as string;
    }

    private static string[] Appeared(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var known = new HashSet<string>(before, StringComparer.Ordinal);
        return after.Where(name => !known.Contains(name)).ToArray();
    }

    private static SolverAssemblyResult Result(string file, string? name, SolverAssemblyStatus status, string? message)
    {
        return new SolverAssemblyResult(file, name, status, [], [], [], message);
    }

    /// <summary>
    ///     Lets the default context find a mod's own dependencies next to it.
    /// </summary>
    [RequiresUnreferencedCode("A loaded assembly can use members that trimming removed.")]
    private static void AddProbeDirectory(string directory)
    {
        lock (ProbeGate)
        {
            if (!ProbeDirectories.Contains(directory, StringComparer.Ordinal))
                ProbeDirectories.Add(directory);

            if (_resolvingHooked)
                return;

            // Inside the lock, so a second load that sees the flag cannot run before the handler is attached.
            AssemblyLoadContext.Default.Resolving += ResolveFromProbeDirectories;
            _resolvingHooked = true;
        }
    }

    [RequiresUnreferencedCode("A loaded assembly can use members that trimming removed.")]
    private static Assembly? ResolveFromProbeDirectories(AssemblyLoadContext context, AssemblyName name)
    {
        string[] directories;
        lock (ProbeGate)
            directories = [..ProbeDirectories];

        foreach (string directory in directories)
        {
            string candidate = System.IO.Path.Combine(directory, name.Name + ".dll");
            if (File.Exists(candidate))
                return context.LoadFromAssemblyPath(candidate);
        }

        return null;
    }
}
