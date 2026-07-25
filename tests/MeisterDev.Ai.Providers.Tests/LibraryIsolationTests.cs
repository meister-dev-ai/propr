// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Tests;

/// <summary>
///     Guards the two rules that keep this library separable from the product that hosts it. They are cheap to
///     state and easy to break by a single convenient using directive, so they are asserted rather than trusted.
/// </summary>
public sealed class LibraryIsolationTests
{
    private static Assembly LibraryAssembly => typeof(AiProviderKind).Assembly;

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
    public void Library_OwnsTheProviderEnums()
    {
        // The provider, protocol, and auth enums are the library's contract, not the host's. If one of them
        // resolves out of a host assembly again, the seam has leaked back.
        Assert.Same(LibraryAssembly, typeof(AiProviderKind).Assembly);
        Assert.Same(LibraryAssembly, typeof(AiProtocolMode).Assembly);
        Assert.Same(LibraryAssembly, typeof(AiAuthMode).Assembly);
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
