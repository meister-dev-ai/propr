// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.AddIns;

/// <summary>The two directories a discovery pass reads, in the order it reads them.</summary>
/// <param name="BuiltIn">
///     The directory the deployed image carries. Its path is fixed by the image and is not configurable.
/// </param>
/// <param name="External">The directory an operator supplies add-ins in.</param>
/// <remarks>
///     Two directories rather than one, because mounting a volume over a populated directory hides that
///     directory's contents in both podman and docker. A single directory would let one mount remove every
///     family the image ships, leave every connection unresolvable, and report nothing as failed because nothing
///     was found.
/// </remarks>
public sealed record ProviderAddInDirectories(string BuiltIn, string External)
{
    /// <summary>The configuration key naming the external directory.</summary>
    public const string ExternalDirectoryKey = "AI_PLUGIN_DIRECTORY";

    /// <summary>The external directory used when the configuration key is absent.</summary>
    public const string DefaultExternalDirectory = "plugins";

    /// <summary>The name of the built-in directory, beside the application's own assemblies.</summary>
    public const string BuiltInDirectoryName = "provider-add-ins";

    /// <summary>Resolves both directories to absolute paths.</summary>
    /// <param name="configuredExternalDirectory">
    ///     The configured external directory, or null or blank when none was configured.
    /// </param>
    /// <param name="contentRootPath">The base a relative external directory is resolved against.</param>
    /// <remarks>
    ///     <para>
    ///         The built-in directory sits beside the application's own assemblies, so it is fixed by whatever
    ///         built the deployment and needs no configuration. An operator cannot move it, and that makes
    ///         a shipped family updatable only by a product release.
    ///     </para>
    ///     <para>
    ///         A relative external directory is resolved against the content root and an absolute one is taken as
    ///         given. Resolved here rather than at each use, so the value means the same thing whatever the
    ///         process's working directory happens to be when it starts. The key has a default, so what gates
    ///         external loading is the contents of the directory rather than whether the key was set: placing an
    ///         assembly there already needs the filesystem access that replacing any shipped binary would need.
    ///     </para>
    /// </remarks>
    public static ProviderAddInDirectories Resolve(string? configuredExternalDirectory, string contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var configured = string.IsNullOrWhiteSpace(configuredExternalDirectory)
            ? DefaultExternalDirectory
            : configuredExternalDirectory.Trim();

        return new ProviderAddInDirectories(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, BuiltInDirectoryName)),
            Path.GetFullPath(Path.Combine(Path.GetFullPath(contentRootPath), configured)));
    }

    /// <summary>The directory <paramref name="origin" /> names.</summary>
    /// <param name="origin">Which of the two directories to take.</param>
    public string For(ProviderAddInOrigin origin)
    {
        return origin == ProviderAddInOrigin.BuiltIn ? this.BuiltIn : this.External;
    }
}
