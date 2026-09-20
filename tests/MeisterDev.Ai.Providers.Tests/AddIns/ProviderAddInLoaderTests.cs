// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Runtime.Loader;
using System.Security.Cryptography;
using MeisterDev.Ai.Providers.AddIns;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.ExampleAddIn;
using MeisterDev.Ai.Providers.Tests.Runtime;
using MeisterDev.Ai.Providers.Transport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeisterDev.Ai.Providers.Tests.AddIns;

/// <summary>
///     The one discovery pass, run against add-ins built the way a deployment holds them.
/// </summary>
/// <remarks>
///     Every case here is a file on disk and a real load, because what this pass is for is the difference
///     between an assembly and a file that looks like one. A stand-in driver would pass every check the loader
///     makes without any of them having been exercised.
/// </remarks>
public sealed class ProviderAddInLoaderTests
{
    /// <summary>The identity the example add-in declares, which a driver built into the host also claims.</summary>
    private const string ExampleFamily = "example/provider";

    [Fact]
    public void AnAddInInTheBuiltInDirectoryIsLoadedAndItsDriverServesAModelCall()
    {
        using var builtIn = StagedAddInDirectory.Create();
        var assemblyPath = builtIn.Install(StagedAddInDirectory.Example);

        var catalog = Load(builtIn.Path, StagedAddInDirectory.Missing());

        var family = Assert.Single(catalog.Loaded);
        Assert.Equal("example/provider", family.Key);
        Assert.Equal("Example provider", family.Label);
        Assert.Equal(assemblyPath, family.FilePath);
        Assert.Equal(ProviderAddInOrigin.BuiltIn, family.Origin);
        Assert.Empty(catalog.Rejected);

        // The registry indexes it and its chat client answers, which a review needs of a family.
        var registry = new AiProviderRegistry(catalog.Drivers);
        var response = ServeOneCall(registry.GetRequired(ExampleFamily));

        // The answer names the model the call was bound to. That distinguishes the add-in's own client from
        // the compiled-in family that serves the same provider.
        Assert.Contains("example-model", response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddInInTheExternalDirectoryIsLoaded()
    {
        using var external = StagedAddInDirectory.Create();
        external.Install(StagedAddInDirectory.Example);

        var catalog = Load(StagedAddInDirectory.Missing(), external.Path);

        var family = Assert.Single(catalog.Loaded);
        Assert.Equal(ProviderAddInOrigin.External, family.Origin);
        Assert.Single(catalog.Drivers);
    }

    // A stock deployment has no external directory. Reported once so an operator who mounted one and mistyped
    // the path can see that, and not a failure because having none is the normal case.
    [Fact]
    public void AnAbsentDirectoryIsReportedOnceAndLoadsNothing()
    {
        var logger = new RecordingLogger();
        var absent = StagedAddInDirectory.Missing();

        var catalog = ProviderAddInLoader.Load(
            new ProviderAddInDirectories(absent, absent),
            [],
            logger);

        Assert.Empty(catalog.Loaded);
        Assert.Empty(catalog.Rejected);
        Assert.Equal(2, logger.Entries.Count(entry => entry.Message.Contains(absent, StringComparison.Ordinal)));
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Information, entry.Level));
    }

    [Fact]
    public void AnAbsentBuiltInDirectoryDoesNotStopTheExternalOneLoading()
    {
        using var external = StagedAddInDirectory.Create();
        external.Install(StagedAddInDirectory.Example);

        var catalog = Load(StagedAddInDirectory.Missing(), external.Path);

        Assert.Single(catalog.Loaded);
    }

    // The built-in directory holds what the deployment was tested with, so a copy in the external directory does
    // not replace it. An operator who patched a shipped family sees the copy listed as a duplicate instead.
    [Fact]
    public void AnExternalCopyOfABuiltInIdentityIsSkippedAsADuplicateNamingBothFiles()
    {
        using var builtIn = StagedAddInDirectory.Create();
        using var external = StagedAddInDirectory.Create();
        var shipped = builtIn.Install(StagedAddInDirectory.Example);
        var patched = external.Install(StagedAddInDirectory.Example);

        var catalog = Load(builtIn.Path, external.Path);

        Assert.Equal(shipped, Assert.Single(catalog.Loaded).FilePath);
        var skipped = Assert.Single(catalog.Rejected);
        Assert.Equal(ProviderAddInRejectionCategory.Duplicate, skipped.Category);
        Assert.Equal(patched, skipped.FilePath);
        Assert.Contains(shipped, skipped.Reason, StringComparison.Ordinal);
        Assert.Contains("example/provider", skipped.Reason, StringComparison.Ordinal);
    }

    // Inside one directory neither copy has precedence, so resolving it would come down to the order the
    // filesystem listed them in. Both are skipped instead.
    [Fact]
    public void TwoCopiesOfOneIdentityInOneDirectoryAreBothSkipped()
    {
        using var external = StagedAddInDirectory.Create();
        var first = external.Install(StagedAddInDirectory.Example, "first");
        var second = external.Install(StagedAddInDirectory.Example, "second");

        var catalog = Load(StagedAddInDirectory.Missing(), external.Path);

        Assert.Empty(catalog.Loaded);
        Assert.Equal(2, catalog.Rejected.Count);
        Assert.All(
            catalog.Rejected, skip =>
                Assert.Equal(ProviderAddInRejectionCategory.Duplicate, skip.Category));
        Assert.Contains(catalog.Rejected, skip => skip.FilePath == first && skip.Reason.Contains(second, StringComparison.Ordinal));
        Assert.Contains(catalog.Rejected, skip => skip.FilePath == second && skip.Reason.Contains(first, StringComparison.Ordinal));
    }

    // The registry refuses a composition with two drivers for one family by throwing, which is right for code
    // the team compiled and wrong for a file an operator dropped in a directory. The loader decides it first.
    [Fact]
    public void AnAddInClaimingAFamilyTheHostAlreadyServesIsSkippedAndTheHostKeepsItsOwn()
    {
        using var external = StagedAddInDirectory.Create();
        var addIn = external.Install(StagedAddInDirectory.Example);
        var hostDriver = new CompiledInDriver();

        var catalog = ProviderAddInLoader.Load(
            new ProviderAddInDirectories(StagedAddInDirectory.Missing(), external.Path),
            [hostDriver],
            new RecordingLogger());

        Assert.Empty(catalog.Loaded);
        var skipped = Assert.Single(catalog.Rejected);
        Assert.Equal(ProviderAddInRejectionCategory.Duplicate, skipped.Category);
        Assert.Equal(addIn, skipped.FilePath);
        Assert.Contains(nameof(CompiledInDriver), skipped.Reason, StringComparison.Ordinal);

        // The composition the host would build from this stays valid, which is the point of deciding it here.
        var registry = new AiProviderRegistry([hostDriver, .. catalog.Drivers]);
        Assert.True(registry.IsRegistered(ExampleFamily));
    }

    [Theory]
    [InlineData("MeisterDev.Ai.Providers.Abstractions.dll")]
    [InlineData("Microsoft.Extensions.AI.Abstractions.dll")]
    public void AFolderShippingACopyOfASharedAssemblyIsSkippedAsMisPackagedNamingTheFile(string shadowed)
    {
        using var external = StagedAddInDirectory.Create();
        var addIn = external.Install(StagedAddInDirectory.Example);
        var copy = external.PutFile(StagedAddInDirectory.Example, shadowed);

        var catalog = Load(StagedAddInDirectory.Missing(), external.Path);

        Assert.Empty(catalog.Loaded);
        var skipped = Assert.Single(catalog.Rejected);
        Assert.Equal(ProviderAddInRejectionCategory.MisPackaged, skipped.Category);
        Assert.Equal(addIn, skipped.FilePath);
        Assert.Contains(copy, skipped.Reason, StringComparison.Ordinal);
    }

    // Checked before the load rather than after it, because once a second copy has been resolved into an
    // add-in's context the family is absent from the registry and nothing about the load reports why.
    [Fact]
    public void AMisPackagedAddInIsNeverLoaded()
    {
        using var external = StagedAddInDirectory.Create();
        external.Install(StagedAddInDirectory.Example, "never-loaded");
        external.PutFile("never-loaded", "MeisterDev.Ai.Providers.Abstractions.dll");

        var catalog = Load(StagedAddInDirectory.Missing(), external.Path);

        Assert.Equal(ProviderAddInRejectionCategory.MisPackaged, Assert.Single(catalog.Rejected).Category);
        Assert.DoesNotContain(
            AssemblyLoadContext.All,
            context => string.Equals(context.Name, "never-loaded", StringComparison.Ordinal));
    }

    [Fact]
    public void AFamilyDeclaringAnotherContractVersionIsSkippedNamingBothVersionsAndTheFile()
    {
        using var external = StagedAddInDirectory.Create();
        var addIn = external.Install(StagedAddInDirectory.Stale);

        var catalog = Load(StagedAddInDirectory.Missing(), external.Path);

        Assert.Empty(catalog.Loaded);
        var skipped = Assert.Single(catalog.Rejected);
        Assert.Equal(ProviderAddInRejectionCategory.VersionMismatch, skipped.Category);
        Assert.Equal(addIn, skipped.FilePath);
        Assert.Contains("0.9", skipped.Reason, StringComparison.Ordinal);
        Assert.Contains(Declaration.ProviderContract.Version, skipped.Reason, StringComparison.Ordinal);
    }

    // A family that states nothing is not a family that states the right thing, so it is refused the same way
    // rather than read as if it had agreed.
    [Fact]
    public void AFamilyDeclaringNoContractVersionIsSkippedTheSameWay()
    {
        using var external = StagedAddInDirectory.Create();
        external.Install(StagedAddInDirectory.Stale);

        // A blank version rather than an unset one: the declaration member is required, so a family that
        // states nothing states a blank string. Also a value that is set on every platform, which an empty
        // string is not.
        var catalog = WithStaleAddInDeclaring(
            " ", () =>
                Load(StagedAddInDirectory.Missing(), external.Path));

        var skipped = Assert.Single(catalog.Rejected);
        Assert.Equal(ProviderAddInRejectionCategory.VersionMismatch, skipped.Category);
        Assert.Contains("no contract version", skipped.Reason, StringComparison.Ordinal);
    }

    // A family can be well formed in every way the load path can see and still be one an operator does not want
    // serving reviews. The checks are what says so, and they run on the host as well as in the author's build
    // because an author who never ran them leaves the host as the first thing that looks.
    [Fact]
    public void AFamilyThatFailsADriverCheckIsSkippedNamingTheCheck()
    {
        using var external = StagedAddInDirectory.Create();
        var addIn = external.Install(StagedAddInDirectory.NonConforming);

        var catalog = Load(StagedAddInDirectory.Missing(), external.Path);

        Assert.Empty(catalog.Loaded);
        Assert.Empty(catalog.Drivers);
        var skipped = Assert.Single(catalog.Rejected);
        Assert.Equal(ProviderAddInRejectionCategory.NonConforming, skipped.Category);
        Assert.Equal(addIn, skipped.FilePath);
        Assert.Equal("example/nonconforming", skipped.Key);
        Assert.Contains("declared-field-references", skipped.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AFamilyThatFailsADriverCheckDoesNotRemoveOneThatPassesThem()
    {
        using var external = StagedAddInDirectory.Create();
        external.Install(StagedAddInDirectory.Example);
        external.Install(StagedAddInDirectory.NonConforming);

        var catalog = Load(StagedAddInDirectory.Missing(), external.Path);

        Assert.Equal("example/provider", Assert.Single(catalog.Loaded).Key);
        Assert.Equal(ProviderAddInRejectionCategory.NonConforming, Assert.Single(catalog.Rejected).Category);
    }

    [Fact]
    public void AFamilyTheHostRefusesDoesNotRemoveOneItAccepts()
    {
        using var external = StagedAddInDirectory.Create();
        external.Install(StagedAddInDirectory.Example);
        external.Install(StagedAddInDirectory.Stale);

        var catalog = Load(StagedAddInDirectory.Missing(), external.Path);

        Assert.Equal("example/provider", Assert.Single(catalog.Loaded).Key);
        Assert.Equal(ProviderAddInRejectionCategory.VersionMismatch, Assert.Single(catalog.Rejected).Category);
    }

    [Fact]
    public void ACorruptAssemblyIsSkippedAndThePassCarriesOn()
    {
        using var external = StagedAddInDirectory.Create();
        external.Install(StagedAddInDirectory.Example);
        var corrupt = external.PutFile("corrupt", "corrupt.dll", [0x00, 0x01, 0x02, 0x03]);

        var catalog = Load(StagedAddInDirectory.Missing(), external.Path);

        Assert.Equal("example/provider", Assert.Single(catalog.Loaded).Key);
        var skipped = Assert.Single(catalog.Rejected);
        Assert.Equal(ProviderAddInRejectionCategory.Failed, skipped.Category);
        Assert.Equal(corrupt, skipped.FilePath);
        Assert.NotEmpty(skipped.Reason);
    }

    [Fact]
    public void AnAssemblyExposingNoProviderFamilyIsRecordedAsFailed()
    {
        using var external = StagedAddInDirectory.Create();
        var noFamily = external.PutFile(
            "Microsoft.Extensions.Logging.Abstractions",
            "Microsoft.Extensions.Logging.Abstractions.dll");

        var catalog = Load(StagedAddInDirectory.Missing(), external.Path);

        Assert.Empty(catalog.Loaded);
        var skipped = Assert.Single(catalog.Rejected);
        Assert.Equal(ProviderAddInRejectionCategory.Failed, skipped.Category);
        Assert.Equal(noFamily, skipped.FilePath);
        Assert.Contains(nameof(IAiProviderDriver), skipped.Reason, StringComparison.Ordinal);
    }

    // One add-in, one family: the identity, the file path and the content hash the inventory reports are one
    // record, and an assembly carrying several families has no single answer for any of them.
    [Fact]
    public void AnAssemblyExposingMoreThanOneProviderFamilyIsRecordedAsFailed()
    {
        using var external = StagedAddInDirectory.Create();
        var several = external.Install(StagedAddInDirectory.TwoFamilies);

        var catalog = Load(StagedAddInDirectory.Missing(), external.Path);

        Assert.Empty(catalog.Loaded);
        var skipped = Assert.Single(catalog.Rejected);
        Assert.Equal(ProviderAddInRejectionCategory.Failed, skipped.Category);
        Assert.Equal(several, skipped.FilePath);
        Assert.Contains("one provider family", skipped.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderWithNoAssemblyNamedAfterItIsRecordedAsFailed()
    {
        using var external = StagedAddInDirectory.Create();
        external.PutFile("mis-named", "something-else.dll", [0x00]);

        var catalog = Load(StagedAddInDirectory.Missing(), external.Path);

        var skipped = Assert.Single(catalog.Rejected);
        Assert.Equal(ProviderAddInRejectionCategory.Failed, skipped.Category);
        Assert.Contains("mis-named.dll", skipped.Reason, StringComparison.Ordinal);
    }

    // An operator who drops a bare assembly into the directory gets a record saying why nothing happened,
    // rather than a family that never appears and no explanation. Loading it is refused because an add-in's
    // folder is what its dependencies resolve from, and the directory root is shared by every add-in in it.
    [Fact]
    public void AnAssemblyLyingLooseInTheDirectoryIsRecordedAndNotLoaded()
    {
        using var external = StagedAddInDirectory.Create();
        var loose = Path.Combine(external.Path, "MeisterDev.Ai.Providers.ExampleAddIn.dll");
        File.Copy(
            external.Install(StagedAddInDirectory.Example, "staging"),
            loose);
        Directory.Delete(Path.Combine(external.Path, "staging"), recursive: true);

        var catalog = Load(StagedAddInDirectory.Missing(), external.Path);

        Assert.Empty(catalog.Loaded);
        var skipped = Assert.Single(catalog.Rejected);
        Assert.Equal(ProviderAddInRejectionCategory.Failed, skipped.Category);
        Assert.Equal(loose, skipped.FilePath);
        Assert.Contains("own folder", skipped.Reason, StringComparison.Ordinal);
    }

    // The directory is what an operator trusts when they put a file in it, so the loader reads nothing outside
    // it. A link is the way a file outside would otherwise be reached.
    [Fact]
    public void AFolderLinkedToSomewhereOutsideTheDirectoryIsRefused()
    {
        using var external = StagedAddInDirectory.Create();
        using var elsewhere = StagedAddInDirectory.Create();
        elsewhere.Install(StagedAddInDirectory.Example, "smuggled");
        var link = Path.Combine(external.Path, "smuggled");
        Directory.CreateSymbolicLink(link, Path.Combine(elsewhere.Path, "smuggled"));

        var catalog = Load(StagedAddInDirectory.Missing(), external.Path);

        Assert.Empty(catalog.Loaded);
        var skipped = Assert.Single(catalog.Rejected);
        Assert.Equal(ProviderAddInRejectionCategory.Failed, skipped.Category);
        Assert.Contains("outside", skipped.Reason, StringComparison.Ordinal);
    }

    // The hash in the inventory describes the bytes that were loaded. Nothing here can prove the negative on
    // its own, so what is asserted is that the hash recorded beside a loaded add-in is the hash of the file at
    // the path it names.
    [Fact]
    public void TheRecordedHashIsTheHashOfTheAssemblyThatWasLoaded()
    {
        using var external = StagedAddInDirectory.Create();
        external.Install(StagedAddInDirectory.Example, "example");

        var catalog = Load(StagedAddInDirectory.Missing(), external.Path);

        var loaded = Assert.Single(catalog.Loaded);
        using var stream = File.OpenRead(loaded.FilePath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(stream)), loaded.ContentHash);
    }

    [Fact]
    public void WithEveryAssemblyUnloadableNothingThrowsAndNothingLoads()
    {
        using var builtIn = StagedAddInDirectory.Create();
        using var external = StagedAddInDirectory.Create();
        builtIn.PutFile("first", "first.dll", [0x4D, 0x5A, 0x00]);
        external.PutFile("second", "second.dll", [0x00]);

        var catalog = Load(builtIn.Path, external.Path);

        Assert.Empty(catalog.Loaded);
        Assert.Empty(catalog.Drivers);
        Assert.Equal(2, catalog.Rejected.Count);
    }

    // One event, not none and not two: the log is where the condition is visible before anyone opens the
    // inventory, and a repeated line makes a single bad file look like several.
    [Fact]
    public void EverySkipProducesExactlyOneLogEventCarryingThePathTheCategoryAndTheReason()
    {
        using var external = StagedAddInDirectory.Create();
        external.Install(StagedAddInDirectory.Stale);
        var corrupt = external.PutFile("corrupt", "corrupt.dll", [0x00]);
        var logger = new RecordingLogger();

        var catalog = ProviderAddInLoader.Load(
            new ProviderAddInDirectories(StagedAddInDirectory.Missing(), external.Path),
            [],
            logger);

        Assert.Equal(2, catalog.Rejected.Count);
        foreach (var skipped in catalog.Rejected)
        {
            var events = logger.Entries
                .Where(entry => entry.Message.Contains(skipped.FilePath, StringComparison.Ordinal))
                .ToList();

            var reported = Assert.Single(events);
            Assert.Equal(LogLevel.Warning, reported.Level);
            Assert.Contains(ProviderAddInNames.Format(skipped.Category), reported.Message, StringComparison.Ordinal);
            Assert.Contains(skipped.Reason, reported.Message, StringComparison.Ordinal);
        }

        Assert.Contains(logger.Entries, entry => entry.Message.Contains(corrupt, StringComparison.Ordinal));
    }

    // The hash is what lets an operator compare a running installation against what they shipped. It is the
    // hash of the file as it was read, which is the only claim the host can make about an add-in's contents.
    [Fact]
    public void ALoadedAddInRecordsTheHashOfTheFileItWasReadFrom()
    {
        using var external = StagedAddInDirectory.Create();
        var assemblyPath = external.Install(StagedAddInDirectory.Example);

        var catalog = Load(StagedAddInDirectory.Missing(), external.Path);

        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assemblyPath))),
            Assert.Single(catalog.Loaded).ContentHash);
    }

    [Fact]
    public void ASkippedAssemblyRecordsItsHashToo()
    {
        using var external = StagedAddInDirectory.Create();
        var assemblyPath = external.Install(StagedAddInDirectory.Stale);

        var catalog = Load(StagedAddInDirectory.Missing(), external.Path);

        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assemblyPath))),
            Assert.Single(catalog.Rejected).ContentHash);
    }

    // The pass runs once. Deleting an add-in from a mounted volume under a running host therefore changes
    // neither what the host serves nor what it reports, which the inventory has to say on its face.
    [Fact]
    public void DeletingAnAssemblyAfterThePassChangesNothingItRecordedOrServes()
    {
        using var external = StagedAddInDirectory.Create();
        var assemblyPath = external.Install(StagedAddInDirectory.Example);
        var catalog = Load(StagedAddInDirectory.Missing(), external.Path);
        var registry = new AiProviderRegistry(catalog.Drivers);

        File.Delete(assemblyPath);

        Assert.Equal(assemblyPath, Assert.Single(catalog.Loaded).FilePath);
        Assert.Contains("example-model", ServeOneCall(registry.GetRequired(ExampleFamily)).Text, StringComparison.Ordinal);
    }

    private static ProviderAddInCatalog Load(string builtInDirectory, string externalDirectory)
    {
        return ProviderAddInLoader.Load(
            new ProviderAddInDirectories(builtInDirectory, externalDirectory),
            [],
            new RecordingLogger());
    }

    /// <summary>Runs <paramref name="load" /> while the stale add-in declares <paramref name="version" />.</summary>
    /// <remarks>
    ///     A family's declared contract version is a constant in its own assembly, so the two cases the loader
    ///     separates need two built assemblies unless the fixture can be told what to declare. Which version the
    ///     host accepts is not settable, because that is the contract this build carries.
    /// </remarks>
    private static ProviderAddInCatalog WithStaleAddInDeclaring(string version, Func<ProviderAddInCatalog> load)
    {
        const string Variable = "PROPR_TEST_STALE_ADDIN_CONTRACT_VERSION";
        var previous = Environment.GetEnvironmentVariable(Variable);
        Environment.SetEnvironmentVariable(Variable, version);
        try
        {
            return load();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, previous);
        }
    }

    private static ChatResponse ServeOneCall(IAiProviderDriver driver)
    {
        using var client = driver.CreateChatClient(
            new ProviderEndpoint(
                ExampleFamily,
                "https://api.example.com/v1",
                ExampleProviderDriver.ApiKeyAuth,
                "a-key"),
            new ProviderModelDescriptor(
                Guid.NewGuid(),
                "example-model",
                [ExampleProviderDriver.ChatCompletionsProtocol]),
            ExampleProviderDriver.ChatCompletionsProtocol);

        return client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]).GetAwaiter().GetResult();
    }

    /// <summary>
    ///     A driver compiled into the host for the same family the example add-in declares.
    /// </summary>
    /// <remarks>
    ///     Stated here rather than taken from a shipped family, because what the loader decides is about an
    ///     identity being claimed twice and not about which family claimed it.
    /// </remarks>
    private sealed class CompiledInDriver : StubDriver
    {
        public override ProviderDeclaration Declaration { get; } = new()
        {
            Key = ExampleFamily,
            Label = "Compiled in",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            AuthModes = [new ProviderDeclaredAuthMode(ExampleFamily + ":ApiKey", [AiCredentialFieldSupport.ApiKey])],
            ProtocolModes = new ProviderDeclaredProtocolModes([ExampleFamily + ":ChatCompletions"]),
            ConformanceInputs = new ProviderConformanceInputs(ExampleFamily + ":ApiKey"),
        };
    }
}
