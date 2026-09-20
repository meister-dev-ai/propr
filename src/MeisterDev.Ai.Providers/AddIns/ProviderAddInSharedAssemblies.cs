// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Frozen;
using MeisterDev.Ai.Providers.Conformance;

namespace MeisterDev.Ai.Providers.AddIns;

/// <summary>
///     The assemblies the host and every add-in have to resolve from one place: the contract, the model
///     abstractions the contract's own signatures are written in, and the conformance kit.
/// </summary>
/// <remarks>
///     <para>
///         These types cross the boundary between host and add-in, so both sides must hold the same assembly
///         instance. If an add-in's folder carries a copy, the dependency resolver finds it there and the
///         add-in's driver ends up implementing a different <c>IAiProviderDriver</c> than the host's. Nothing
///         about that reports a failure on its own: the assembly loads, the contract-version check passes, and
///         the family is absent from the registry while the inventory says nothing failed. The loader therefore
///         refuses a folder holding one, and an add-in's own load context never resolves one from its folder.
///     </para>
///     <para>
///         The names are the kit's, so the rule an add-in's build is measured against and the rule the loader
///         applies cannot drift apart. This type is where the host reaches them.
///     </para>
/// </remarks>
public static class ProviderAddInSharedAssemblies
{
    /// <summary>The simple assembly names, without the <c>.dll</c> extension.</summary>
    public static FrozenSet<string> Names => ConformancePackaging.SharedAssemblyNames;

    /// <summary>Whether <paramref name="assemblyName" /> names an assembly that comes from the host.</summary>
    /// <param name="assemblyName">The simple assembly name to check.</param>
    public static bool IsShared(string? assemblyName)
    {
        return ConformancePackaging.IsShared(assemblyName);
    }

    /// <summary>The shared assemblies an add-in folder ships a copy of, which is none for a correct one.</summary>
    /// <param name="folderPath">The folder holding the add-in and its dependencies.</param>
    public static IReadOnlyList<string> FindShippedCopies(string folderPath)
    {
        return ConformancePackaging.FindShippedCopies(folderPath);
    }
}
