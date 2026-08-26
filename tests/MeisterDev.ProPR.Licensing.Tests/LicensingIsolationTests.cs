// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace MeisterDev.ProPR.Licensing.Tests;

/// <summary>
///     Guards the rule that the trust anchor has one source. The anchor is compiled into this assembly from a
///     committed file, so the assembly must not be able to reach a configuration key, an environment variable, a
///     file on disk, or a network endpoint that could supply a different one.
/// </summary>
public sealed class LicensingIsolationTests
{
    private static Assembly LicensingAssembly => typeof(LicenseTrustAnchor).Assembly;

    [Fact]
    public void TheLicensingAssembly_ReferencesOnlyBaseLibraries()
    {
        var outsideReferences = LicensingAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => !name.StartsWith("System.", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(outsideReferences);
    }

    // The assembly-level rule cannot see these. An environment read, a file read, and an HTTP call come from
    // base libraries the assembly also uses for other reasons, so the types themselves have to be named. The
    // streams the embedded resource is read through stay allowed, which is why the file-system entries are
    // listed one by one rather than as a namespace.
    [Fact]
    public void TheLicensingAssembly_UsesNoConfigurationEnvironmentFileSystemOrNetworkType()
    {
        var referencedTypes = ReferencedTypeNames();

        Assert.Contains("System.Security.Cryptography.X509Certificates.X509Certificate2", referencedTypes);
        Assert.Empty(referencedTypes.Where(IsConfigurationEnvironmentFileSystemOrNetworkType).Distinct(StringComparer.Ordinal));
    }

    private static bool IsConfigurationEnvironmentFileSystemOrNetworkType(string typeName)
        => typeName is "System.Environment" or "System.AppContext" or "System.IO.File" or "System.IO.FileInfo"
               or "System.IO.FileStream" or "System.IO.Directory" or "System.IO.DirectoryInfo" or "System.IO.Path"
           || typeName.StartsWith("System.Net.", StringComparison.Ordinal)
           || typeName.StartsWith("System.Configuration.", StringComparison.Ordinal)
           || typeName.StartsWith("System.Data.", StringComparison.Ordinal);

    /// <summary>
    ///     Every type the assembly names from outside itself, read from its own metadata because reflection
    ///     reports referenced assemblies but not the types taken from them.
    /// </summary>
    private static IReadOnlyList<string> ReferencedTypeNames()
    {
        using var assemblyFile = File.OpenRead(LicensingAssembly.Location);
        using var peReader = new PEReader(assemblyFile);
        var metadata = peReader.GetMetadataReader();
        var names = new List<string>(metadata.TypeReferences.Count);

        foreach (var handle in metadata.TypeReferences)
        {
            var reference = metadata.GetTypeReference(handle);
            var typeNamespace = metadata.GetString(reference.Namespace);
            var typeName = metadata.GetString(reference.Name);

            names.Add(string.IsNullOrEmpty(typeNamespace) ? typeName : $"{typeNamespace}.{typeName}");
        }

        return names;
    }
}
