// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

namespace MeisterDev.Ai.Providers.AddIns;

/// <summary>One add-in's load context, resolving that add-in's dependencies from that add-in's own folder.</summary>
/// <remarks>
///     <para>
///         Each add-in gets its own context so two families carrying different versions of the same third-party
///         package can both load. Loading both into one context would leave one version in force and the other
///         family running against an assembly it was not built for.
///     </para>
///     <para>
///         The assemblies in <see cref="ProviderAddInSharedAssemblies" /> are the exception and always come from
///         the default context, because their types cross the boundary between host and add-in. Returning null
///         for them is what hands the request back to the default context.
///     </para>
///     <para>
///         Two routes reach a file, and they are contained differently. The add-in's own <c>.deps.json</c>, read
///         through <see cref="AssemblyDependencyResolver" />, is the layout its author declared and is followed
///         wherever it points: a package reference resolves into the machine's NuGet cache, which an
///         add-in copied out of a build output depends on, and refusing that would leave such an add-in unable to
///         load at all. A path it resolves from outside the add-in's folder is reported instead of refused. The
///         folder probe below is the loader's own rule rather than the author's, and it is contained: it reads a
///         file beside the add-in assembly and nothing else, so a link there pointing elsewhere is not followed.
///     </para>
///     <para>
///         A context is created once, in the single discovery pass, and none is unloaded while the process runs,
///         so the context is not collectible.
///     </para>
/// </remarks>
public sealed partial class ProviderAddInLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _folderPath;
    private readonly ILogger? _logger;

    /// <summary>Creates the context for the add-in assembly at <paramref name="assemblyPath" />.</summary>
    /// <param name="assemblyPath">The full path of the add-in's own assembly.</param>
    /// <param name="logger">Where a dependency resolved from outside the add-in's folder is reported.</param>
    public ProviderAddInLoadContext(string assemblyPath, ILogger? logger = null)
        : base(Path.GetFileNameWithoutExtension(assemblyPath), isCollectible: false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);

        this._resolver = new AssemblyDependencyResolver(assemblyPath);
        this._folderPath = Path.GetDirectoryName(assemblyPath) ?? AppContext.BaseDirectory;
        this._logger = logger;
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);

        if (ProviderAddInSharedAssemblies.IsShared(assemblyName.Name))
        {
            return null;
        }

        var resolved = this._resolver.ResolveAssemblyToPath(assemblyName);
        if (resolved is not null)
        {
            this.ReportIfOutsideFolder(assemblyName.Name, resolved);
            return this.LoadFromAssemblyPath(resolved);
        }

        // The resolver reads the add-in's .deps.json, which an add-in built without dynamic loading does not
        // have. Probing the folder covers that case; it reaches no further than the folder the resolver would
        // have read, so an add-in still resolves only what sits beside it.
        var beside = this.AssemblyPathBesideTheAddIn(assemblyName.Name);

        // Anything still unresolved falls through to the default context, which is how an add-in referencing a
        // package the host already carries, at the host's version, loads without shipping a copy.
        return beside is null ? null : this.LoadFromAssemblyPath(beside);
    }

    /// <inheritdoc />
    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var resolved = this._resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (resolved is null)
        {
            return nint.Zero;
        }

        this.ReportIfOutsideFolder(unmanagedDllName, resolved);
        return this.LoadUnmanagedDllFromPath(resolved);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "The provider add-in '{AddIn}' resolved '{Dependency}' from '{Path}', which is outside its own "
                  + "folder '{Folder}'. Its .deps.json names that path.")]
    private static partial void LogDependencyOutsideFolder(
        ILogger logger,
        string addIn,
        string dependency,
        string path,
        string folder);

    // The file of that name beside the add-in assembly, or null when there is none the loader will read. An
    // assembly's simple name reaches this from the reference the add-in was compiled against, so it is treated as
    // a name and not as a path: a separator or a parent segment in it would otherwise combine into a path outside
    // the folder, and a link at the combined path would resolve to one.
    private string? AssemblyPathBesideTheAddIn(string? assemblyName)
    {
        if (assemblyName is not { Length: > 0 }
            || assemblyName.AsSpan().ContainsAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            || Path.IsPathRooted(assemblyName)
            || assemblyName.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var beside = Path.Combine(this._folderPath, assemblyName + ".dll");

        return File.Exists(beside)
               && ProviderAddInContainment.IsCandidatePathContainedInDirectory(this._folderPath, beside, isDirectory: false)
            ? beside
            : null;
    }

    private void ReportIfOutsideFolder(string? dependency, string resolvedPath)
    {
        if (this._logger is null || ProviderAddInContainment.IsCandidatePathContainedInDirectory(this._folderPath, resolvedPath, isDirectory: false))
        {
            return;
        }

        LogDependencyOutsideFolder(
            this._logger,
            this.Name ?? this._folderPath,
            dependency ?? "an unnamed assembly",
            resolvedPath,
            this._folderPath);
    }
}
