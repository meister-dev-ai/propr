// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Frozen;

namespace MeisterDev.Ai.Providers.Conformance;

/// <summary>
///     The assemblies a host and every add-in have to resolve from one place: the contract, the model
///     abstractions the contract's own signatures are written in, and this kit.
/// </summary>
/// <remarks>
///     <para>
///         The types in the first two cross the boundary between host and add-in, so both sides must hold the
///         same assembly instance. If an add-in's folder carries a copy, the dependency resolver finds it there
///         and the add-in's driver ends up implementing a different <c>IAiProviderDriver</c> than the host's.
///         Nothing about that reports a failure on its own: the assembly loads, the contract-version check
///         passes, and the family is absent from the registry while the inventory says nothing failed.
///     </para>
///     <para>
///         The kit is listed with them because an add-in references it to run these checks in its own build,
///         and a build-time reference that reaches the add-in's output is a second copy of the same kind.
///     </para>
///     <para>
///         The same names are listed as <c>ProviderAddInSharedAssembly</c> items in
///         <c>MeisterDev.Ai.Providers.Abstractions/build/MeisterDev.Ai.Providers.Abstractions.props</c>, which
///         makes an add-in's own build fail before it is ever deployed. The two lists say the same thing to
///         two different audiences and are changed together.
///     </para>
/// </remarks>
public static class ConformancePackaging
{
    /// <summary>The simple assembly names, without the <c>.dll</c> extension.</summary>
    public static readonly FrozenSet<string> SharedAssemblyNames = new[]
        {
            "MeisterDev.Ai.Providers.Abstractions",
            "MeisterDev.Ai.Providers.Conformance",
            "Microsoft.Extensions.AI.Abstractions",
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="assemblyName" /> names an assembly that comes from the host.</summary>
    /// <param name="assemblyName">The simple assembly name to check.</param>
    public static bool IsShared(string? assemblyName)
    {
        return assemblyName is not null && SharedAssemblyNames.Contains(assemblyName);
    }

    /// <summary>The shared assemblies an add-in folder ships a copy of, which is none for a correct one.</summary>
    /// <param name="folderPath">The folder holding the add-in and its dependencies.</param>
    /// <remarks>
    ///     Matched by file name rather than by reading each file, because the failure is a file sitting where the
    ///     resolver will find it and the resolver matches on name too.
    /// </remarks>
    public static IReadOnlyList<string> FindShippedCopies(string folderPath)
    {
        return
        [
            .. SharedAssemblyNames
                .Select(name => Path.Combine(folderPath, name + ".dll"))
                .Where(File.Exists)
                .Order(StringComparer.Ordinal),
        ];
    }
}
