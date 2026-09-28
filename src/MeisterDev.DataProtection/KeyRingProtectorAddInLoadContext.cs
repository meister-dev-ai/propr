// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using System.Runtime.Loader;

namespace MeisterDev.DataProtection;

/// <summary>One protector add-in's load context, resolving that add-in's dependencies from its own folder.</summary>
/// <remarks>
///     Each add-in gets its own context, so two of them carrying different versions of the same package can both
///     load. The contract assembly and the assemblies its signatures are written in are the exception and always
///     come from the default context, because their types cross the boundary between host and add-in: a
///     protector loaded into a second copy of this assembly would implement a different interface from the one
///     the host looks for.
///     <para>
///         The same holds for what the runtime supplies. The add-in's own layout is followed where it declares
///         one, and beyond that the default context answers before the add-in's folder is read, so an assembly
///         the host already carries is never loaded a second time from beside the add-in. This is the rule
///         <c>ProviderAddInLoadContext</c> applies to provider add-ins.
///     </para>
/// </remarks>
internal sealed class KeyRingProtectorAddInLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _folderPath;

    /// <summary>Creates the context for the add-in assembly at <paramref name="assemblyPath" />.</summary>
    /// <param name="assemblyPath">The full path of the add-in's own assembly.</param>
    public KeyRingProtectorAddInLoadContext(string assemblyPath)
        : base(Path.GetFileNameWithoutExtension(assemblyPath), isCollectible: false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);

        this._resolver = new AssemblyDependencyResolver(assemblyPath);
        this._folderPath = Path.GetDirectoryName(assemblyPath) ?? AppContext.BaseDirectory;
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);

        if (IsShared(assemblyName.Name))
        {
            return null;
        }

        var resolved = this._resolver.ResolveAssemblyToPath(assemblyName);
        if (resolved is not null)
        {
            return this.LoadFromAssemblyPath(resolved);
        }

        // The resolver reads the add-in's .deps.json, which does not list a framework assembly, so what
        // reaches this point is either a framework assembly or a dependency of an add-in built without dynamic
        // loading. The default context is asked first, which keeps every assembly the runtime supplies at one
        // identity however it is named: the list below cannot enumerate them, and a second copy of netstandard
        // or Microsoft.CSharp loaded from the add-in's folder would give the add-in's types a different
        // identity from the host's.
        if (DefaultContextSupplies(assemblyName))
        {
            return null;
        }

        // A file of that name beside the add-in assembly covers the add-in's own dependency. Anything still
        // unresolved falls through to the default context.
        var beside = this.PathBesideTheAddIn(assemblyName.Name);

        return beside is null ? null : this.LoadFromAssemblyPath(beside);
    }

    /// <inheritdoc />
    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var resolved = this._resolver.ResolveUnmanagedDllToPath(unmanagedDllName);

        return resolved is null ? nint.Zero : this.LoadUnmanagedDllFromPath(resolved);
    }

    /// <summary>Whether the default context already has, or can load, <paramref name="assemblyName" />.</summary>
    /// <param name="assemblyName">The assembly the add-in asked for.</param>
    private static bool DefaultContextSupplies(AssemblyName assemblyName)
    {
        try
        {
            return Default.LoadFromAssemblyName(assemblyName) is not null;
        }
        catch (Exception exception) when (exception is FileNotFoundException
                                              or FileLoadException
                                              or BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Whether <paramref name="assemblyName" /> names an assembly whose types cross the boundary between
    ///     host and add-in: the contract, and the data-protection and configuration assemblies the contract's
    ///     own signatures are written in. The add-in's declared layout is not consulted for these, because a
    ///     copy in the add-in's folder would give <c>Apply</c> parameters the host cannot pass.
    /// </summary>
    /// <param name="assemblyName">The assembly the add-in asked for.</param>
    private static bool IsShared(string? assemblyName)
    {
        return assemblyName is not null
               && (assemblyName.Equals(
                       typeof(IKeyRingProtector).Assembly.GetName().Name,
                       StringComparison.OrdinalIgnoreCase)
                   || assemblyName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                   || assemblyName.StartsWith("Microsoft.Extensions.", StringComparison.OrdinalIgnoreCase)
                   || assemblyName.StartsWith("Microsoft.AspNetCore.", StringComparison.OrdinalIgnoreCase));
    }

    // The file of that name beside the add-in assembly, or null when there is none. An assembly's simple name
    // reaches this from a reference the add-in was compiled against, so it is treated as a name and not as a
    // path: a separator or a parent segment would otherwise combine into a path outside the folder.
    private string? PathBesideTheAddIn(string? assemblyName)
    {
        if (assemblyName is not { Length: > 0 }
            || assemblyName.AsSpan().ContainsAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            || Path.IsPathRooted(assemblyName)
            || assemblyName.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var beside = Path.Combine(this._folderPath, assemblyName + ".dll");

        return File.Exists(beside) ? beside : null;
    }
}
