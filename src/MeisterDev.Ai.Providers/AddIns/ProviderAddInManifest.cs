// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;
using System.Reflection;
using MeisterDev.Ai.Providers.Declaration;
using Microsoft.Extensions.Logging;

namespace MeisterDev.Ai.Providers.AddIns;

/// <summary>
///     What an add-in states about itself, read out of the file with none of it executed.
/// </summary>
/// <param name="Key">The identity key every connection of this family would be stored against.</param>
/// <param name="Label">What an operator sees where the family is named.</param>
/// <param name="Version">The family's own version.</param>
/// <param name="ContractVersion">The contract version the add-in was built against.</param>
/// <param name="ReachedHosts">The hosts the add-in says it contacts.</param>
/// <param name="RequiredCapability">The premium capability it needs, or null for none.</param>
/// <param name="AssemblyName">The assembly's own name, from its manifest.</param>
/// <param name="AssemblyVersion">The assembly's own version, from its manifest.</param>
public sealed record ProviderAddInManifest(
    string Key,
    string Label,
    string Version,
    string ContractVersion,
    IReadOnlyList<string> ReachedHosts,
    string? RequiredCapability,
    string AssemblyName,
    string AssemblyVersion)
{
    private readonly IReadOnlyList<string> _reachedHosts = Held(ReachedHosts);

    /// <summary>The hosts the add-in says it contacts.</summary>
    public IReadOnlyList<string> ReachedHosts
    {
        get => this._reachedHosts;
        init => this._reachedHosts = Held(value);
    }

    private static IReadOnlyList<string> Held(IReadOnlyList<string>? value)
    {
        return value is null ? [] : value.ToImmutableArray();
    }
}

/// <summary>
///     Reads an add-in's manifest without running it.
/// </summary>
/// <remarks>
///     <para>
///         An administrator decides whether to activate an add-in before any of it has run, so what they decide
///         on has to come out of the file. <see cref="MetadataLoadContext" /> maps the assembly for reflection
///         and executes nothing in it: no module initializer, no static constructor, no driver.
///     </para>
///     <para>
///         The resolver is given the add-in's own folder and the host's runtime assemblies, because an attribute
///         argument's type has to be resolvable for the argument to be read. Nothing is resolved out of the
///         host's load context: the files are read again, in the metadata context, where they are data.
///     </para>
/// </remarks>
public static partial class ProviderAddInManifestReader
{
    /// <summary>
    ///     Reads the manifest, or says why there is none.
    /// </summary>
    /// <param name="assemblyPath">The assembly to read.</param>
    /// <param name="folderPath">The add-in's folder, whose assemblies the resolver may use.</param>
    /// <param name="logger">Records why a file could not be read.</param>
    /// <returns>
    ///     The manifest, or null with the reason. A file that is not a readable assembly and one that is but
    ///     states no attribute are different things to an administrator, so they are reported differently.
    /// </returns>
    public static (ProviderAddInManifest? Manifest, string? Unreadable) Read(
        string assemblyPath,
        string folderPath,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            using var context = new MetadataLoadContext(new PathAssemblyResolver(ResolvablePaths(folderPath)));
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var name = assembly.GetName();

            var declared = assembly
                .GetCustomAttributesData()
                .FirstOrDefault(attribute =>
                    string.Equals(
                        attribute.AttributeType.FullName,
                        typeof(ProviderAddInAttribute).FullName,
                        StringComparison.Ordinal));

            if (declared is null)
            {
                return (null, null);
            }

            return (new ProviderAddInManifest(
                Text(declared, nameof(ProviderAddInAttribute.Key)) ?? string.Empty,
                Text(declared, nameof(ProviderAddInAttribute.Label)) ?? string.Empty,
                Text(declared, nameof(ProviderAddInAttribute.Version)) ?? string.Empty,
                Text(declared, nameof(ProviderAddInAttribute.ContractVersion)) ?? string.Empty,
                Texts(declared, nameof(ProviderAddInAttribute.ReachedHosts)),
                Text(declared, nameof(ProviderAddInAttribute.RequiredCapability)),
                name.Name ?? Path.GetFileNameWithoutExtension(assemblyPath),
                name.Version?.ToString() ?? "0.0.0.0"), null);
        }
        catch (Exception failure) when (failure is IOException
                                            or UnauthorizedAccessException
                                            or BadImageFormatException
                                            or FileLoadException
                                            or ArgumentException
                                            or NotSupportedException)
        {
            LogManifestUnreadable(logger, assemblyPath, failure);

            return (null, $"The file could not be read as a .NET assembly: {failure.Message}");
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The add-in at {AssemblyPath} could not be read for its manifest; it is listed as unreadable")]
    private static partial void LogManifestUnreadable(ILogger logger, string assemblyPath, Exception failure);

    /// <summary>
    ///     The assemblies the metadata context may resolve against, one per simple name.
    /// </summary>
    /// <remarks>
    ///     Three sources, in the order they win. The add-in's own folder, for whatever it ships beside itself.
    ///     The host's own folder, because the attribute's type lives in the contract assembly and the packaging
    ///     rules keep that out of an add-in's folder, so without this the attribute is present and its arguments
    ///     cannot be decoded. And the runtime, because every type bottoms out there.
    /// </remarks>
    /// <param name="folderPath">The add-in's folder.</param>
    private static IEnumerable<string> ResolvablePaths(string folderPath)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Sources(folderPath))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
            {
                byName.TryAdd(Path.GetFileNameWithoutExtension(file), file);
            }
        }

        return byName.Values;
    }

    private static IEnumerable<string> Sources(string folderPath)
    {
        yield return folderPath;
        yield return AppContext.BaseDirectory;

        if (Path.GetDirectoryName(typeof(object).Assembly.Location) is { Length: > 0 } runtime)
        {
            yield return runtime;
        }
    }

    private static string? Text(CustomAttributeData attribute, string member)
    {
        return Argument(attribute, member)?.Value as string;
    }

    private static IReadOnlyList<string> Texts(CustomAttributeData attribute, string member)
    {
        return Argument(attribute, member)?.Value is IReadOnlyList<CustomAttributeTypedArgument> entries
            ? [.. entries.Select(entry => entry.Value as string).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)]
            : [];
    }

    private static CustomAttributeTypedArgument? Argument(CustomAttributeData attribute, string member)
    {
        foreach (var named in attribute.NamedArguments)
        {
            if (string.Equals(named.MemberName, member, StringComparison.Ordinal))
            {
                return named.TypedValue;
            }
        }

        return null;
    }
}
