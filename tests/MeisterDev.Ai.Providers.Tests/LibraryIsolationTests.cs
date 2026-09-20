// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;
using System.Reflection;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Tests;

/// <summary>
///     Guards the two rules that keep the shared runtime separable from the product that hosts it. They are cheap
///     to state and easy to break by a single convenient using directive, so they are asserted rather than
///     trusted. What the contract assembly beside it owes a family built elsewhere is asserted in
///     <see cref="ContractIsolationTests" />.
/// </summary>
public sealed class LibraryIsolationTests
{
    private static Assembly LibraryAssembly => typeof(AiProviderRegistry).Assembly;

    [Fact]
    public void Library_ReferencesNoHostAssembly()
    {
        var hostReferences = LibraryAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.StartsWith("MeisterDev.ProPR", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(hostReferences);
    }

    [Fact]
    public void Library_ReadsTheProviderVocabularyFromTheContract()
    {
        // The declaration a family states its identity in, and the token rules its key and its mode names are
        // held to, are the contract's: not the host's and not the shared runtime's. If one of them resolves out
        // of a host assembly again, the seam has leaked back.
        var contract = typeof(IAiProviderDriver).Assembly;

        Assert.Same(contract, typeof(ProviderDeclaration).Assembly);
        Assert.Same(contract, typeof(ProviderVocabulary).Assembly);
        Assert.Same(contract, typeof(ProviderDeclaredProtocolModes).Assembly);
    }

    [Fact]
    public void Library_KeepsTheSharedAuthModeDeclarationImmutableAtItsPublishedSignature()
    {
        // A driver compiled outside this repository links against the declared type of what it reads. Narrowing
        // this property to the concrete collection behind it changes the signature and fails that driver's call
        // with a missing-method error, which is the case the immutability below exists for.
        Assert.Equal(
            typeof(IReadOnlyList<string>),
            typeof(AiAuthModeSupport).GetMethod(nameof(AiAuthModeSupport.ApiKeyOnly))!.ReturnType);

        // What comes back is immutable, so a caller that could cast it back to an array would be editing what
        // the driver holding it accepts as a credential.
        Assert.IsType<ImmutableArray<string>>(AiAuthModeSupport.ApiKeyOnly("vendor/family"));
    }

    [Fact]
    public void Library_ExposesEveryPublicTypeUnderItsOwnRootNamespace()
    {
        var strays = LibraryAssembly
            .GetExportedTypes()
            .Where(t => !(t.Namespace ?? string.Empty).StartsWith("MeisterDev.Ai.Providers", StringComparison.Ordinal))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        Assert.Empty(strays);
    }
}
