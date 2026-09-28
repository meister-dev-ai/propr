// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.DataProtection.AzureKeyVaultAddIn.Tests;

/// <summary>
///     The shipped add-in read from a directory, the way an image holds it, so selecting it needs no rebuild.
/// </summary>
public sealed class AzureKeyVaultAddInDiscoveryTests
{
    /// <summary>The assemblies the add-in folder has to carry beside the add-in.</summary>
    private static readonly string[] RequiredAssemblies =
    [
        "MeisterDev.DataProtection.AzureKeyVaultAddIn",
        "Azure.Core",
        "Azure.Security.KeyVault.Keys",
        "Azure.Extensions.AspNetCore.DataProtection.Keys",
        "Azure.Extensions.AspNetCore.DataProtection.Blobs",
        "Azure.Storage.Blobs",
        "Azure.Storage.Common",
    ];

    /// <summary>Where the build stages the add-in, a folder named after the assembly in it.</summary>
    private static string AddInDirectory =>
        Path.Combine(AppContext.BaseDirectory, "add-in-builds", "MeisterDev.DataProtection.AzureKeyVaultAddIn");

    [Fact]
    public void TheAddInIsSelectableByNameFromTheAddInDirectory()
    {
        var protector = KeyRingProtectorCatalog.Find(
            AzureKeyVaultKeyRingProtector.ProtectorName,
            AddInDirectory);

        Assert.NotNull(protector);
        Assert.Equal(AzureKeyVaultKeyRingProtector.ProtectorName, protector.Name);

        // The one that answered is the file in that directory, and not an equally named type this test project
        // already carries.
        Assert.Equal(AddInDirectory, Path.GetDirectoryName(protector.GetType().Assembly.Location));
    }

    [Fact]
    public void TheSharedSetUpSelectsTheAddInAndReportsWhatItNeeds()
    {
        // The protector the lookup answers with, held from here on, so what the set-up below reports is
        // traced to this instance and not to whatever a second lookup would return.
        var selected = KeyRingProtectorCatalog.Find(AzureKeyVaultKeyRingProtector.ProtectorName, AddInDirectory);
        Assert.NotNull(selected);
        Assert.Equal(AddInDirectory, Path.GetDirectoryName(selected.GetType().Assembly.Location));

        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [MeisterDataProtectionExtensions.ProtectorKey] =
                        AzureKeyVaultKeyRingProtector.ProtectorName,
                    [MeisterDataProtectionExtensions.AddInDirectoryKey] = AddInDirectory,
                })
            .Build();

        // Reached through the loader, the add-in reports its missing setting as the start-up failure a
        // compiled-in protector's would be.
        var failure = Assert.Throws<InvalidOperationException>(() => services.AddMeisterDataProtection(configuration));

        Assert.Contains(
            AzureKeyVaultKeyRingProtector.KeyIdentifierKey,
            failure.Message,
            StringComparison.Ordinal);

        // The held protector reports the same thing when it is applied, so the failure above is the one that
        // type produces and not another protector's.
        var applied = Assert.Throws<InvalidOperationException>(() => selected.Apply(new ServiceCollection().AddDataProtection(), configuration));

        Assert.Equal(failure.Message, applied.Message);
    }

    /// <summary>
    ///     The contract assembly names no Azure reference of its own, so a host compiling against it takes on
    ///     none. What the running host has loaded is decided by what else it references and is not asserted
    ///     here.
    /// </summary>
    [Fact]
    public void TheContractAssemblyReferencesNothingFromAzure()
    {
        var referenced = typeof(IKeyRingProtector).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty);

        Assert.DoesNotContain(
            referenced,
            name => name.StartsWith("Azure.", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The libraries the add-in needs beside it, which the folder a deployment holds has to carry.</summary>
    /// <remarks>
    ///     The test project references the add-in directly, so the default load context can supply an assembly
    ///     the folder is missing and the loader test would still pass. Each one is therefore checked as a file
    ///     in the folder.
    /// </remarks>
    public static TheoryData<string> RequiredAssemblyNames => [.. RequiredAssemblies];

    [Theory]
    [MemberData(nameof(RequiredAssemblyNames))]
    public void TheAddInFolderCarriesTheAssemblyItNeeds(string assemblyName)
    {
        Assert.True(
            File.Exists(Path.Combine(AddInDirectory, assemblyName + ".dll")),
            $"'{assemblyName}.dll' is not in the staged add-in folder.");
    }

    // The protector authenticates with the default Azure credential chain, so the assembly defining those
    // types has to be in the folder. It is named through the type because the types have moved between
    // packages, and a host that carries a copy of that assembly would hide a folder that lacks one.
    [Fact]
    public void TheAddInFolderCarriesTheAssemblyDefiningTheAzureCredential()
    {
        var credentialAssembly = typeof(DefaultAzureCredential).Assembly.GetName().Name + ".dll";

        Assert.True(
            File.Exists(Path.Combine(AddInDirectory, credentialAssembly)),
            $"'{credentialAssembly}' is not in the staged add-in folder.");
    }

    // The folder is what a deployment ships, so an Azure library nothing in the list accounts for is a
    // dependency that arrived without anyone deciding to ship it.
    //
    // The list carries no Azure.Identity entry because the folder holds no Azure.Identity.dll: the credential
    // types in that namespace are defined by Azure.Core 1.61, which the add-in's two packages bring. The test
    // above asks the folder for the assembly that defines DefaultAzureCredential and is answered by
    // Azure.Core for the same reason.
    [Fact]
    public void TheAddInFolderCarriesNoAzureLibraryBeyondTheOnesItNeeds()
    {
        var expected = RequiredAssemblies
            .Where(name => name.StartsWith("Azure.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var present = Directory.EnumerateFiles(AddInDirectory, "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name!.StartsWith("Azure.", StringComparison.OrdinalIgnoreCase));

        Assert.All(present, name => Assert.Contains(name!, expected));
    }

    [Fact]
    public void TheAddInFolderCarriesNoCopyOfTheContract()
    {
        var contract = typeof(IKeyRingProtector).Assembly.GetName().Name + ".dll";

        Assert.False(File.Exists(Path.Combine(AddInDirectory, contract)));
    }
}
