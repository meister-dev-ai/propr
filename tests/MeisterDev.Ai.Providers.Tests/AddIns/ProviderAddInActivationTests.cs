// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.AddIns;
using MeisterDev.Ai.Providers.Declaration;

namespace MeisterDev.Ai.Providers.Tests.AddIns;

/// <summary>
///     What the host does with an add-in nobody has activated.
/// </summary>
/// <remarks>
///     The property the gate exists for is that none of an unactivated add-in runs: not a module initializer,
///     not a static constructor, not a driver. An administrator decides on what the file states about itself,
///     which is read out of the assembly's metadata, and the decision is bound to the bytes.
/// </remarks>
public sealed class ProviderAddInActivationTests
{
    [Fact]
    public void AnAddInNobodyActivatedIsDescribedAndNotLoaded()
    {
        using var external = StagedAddInDirectory.Create();
        var addIn = external.Install(StagedAddInDirectory.Example);

        var catalog = Load(external.Path, NothingActivated.Instance);

        Assert.Empty(catalog.Loaded);
        Assert.Empty(catalog.Drivers);
        Assert.Empty(catalog.Rejected);

        var held = Assert.Single(catalog.Awaiting);
        Assert.Equal(addIn, held.FilePath);
        Assert.Equal(ProviderAddInOrigin.External, held.Origin);
        Assert.NotNull(held.ContentHash);
    }

    // What the administrator decides on, read with none of the add-in executed: who it says it is, what it says
    // it contacts, and the capability it says it needs.
    [Fact]
    public void WhatItStatesAboutItselfIsReadFromTheFile()
    {
        using var external = StagedAddInDirectory.Create();
        external.Install(StagedAddInDirectory.Example);

        var held = Assert.Single(Load(external.Path, NothingActivated.Instance).Awaiting);

        Assert.Equal("example/provider", held.Key);
        Assert.Equal("Example provider", held.Label);
        Assert.Equal("1.0", held.Version);
        Assert.Equal(ProviderContract.Version, held.ContractVersion);
        Assert.Equal(["api.example.com", ".example.com"], held.ReachedHosts);
        Assert.Equal("example-connections", held.RequiredCapability);
        Assert.True(held.CanBeActivated);
    }

    // The property the whole gate rests on: none of an unactivated add-in runs.
    //
    // The non-conforming fixture carries a module initializer that writes a file beside itself the first time
    // any of its code runs. A module initializer runs on the first access to the module, which a load causes and
    // a metadata-only read does not, so the marker's absence is the property and not a proxy for it. Asserting
    // against the process's loaded assemblies would not do: other tests load the same fixture into their own
    // contexts, and those stay listed.
    [Fact]
    public void ReadingWhatItStatesRunsNoneOfIt()
    {
        using var external = StagedAddInDirectory.Create();
        var addIn = external.Install(StagedAddInDirectory.NonConforming);
        var marker = Path.Combine(Path.GetDirectoryName(addIn)!, "this-add-in-ran");

        var held = Assert.Single(Load(external.Path, NothingActivated.Instance).Awaiting);

        Assert.Equal("example/nonconforming", held.Key);
        Assert.False(File.Exists(marker), "the add-in ran while being read for what it states");
    }

    // The other half of it: an activated add-in is loaded, so the same marker does appear. Without this the
    // assertion above would pass for a marker the fixture never writes.
    [Fact]
    public void ActivatingItRunsIt()
    {
        using var external = StagedAddInDirectory.Create();
        var addIn = external.Install(StagedAddInDirectory.NonConforming);
        var marker = Path.Combine(Path.GetDirectoryName(addIn)!, "this-add-in-ran");

        var hash = Assert.Single(Load(external.Path, NothingActivated.Instance).Awaiting).ContentHash;
        Load(external.Path, new OnlyTheseActivated(hash!));

        Assert.True(File.Exists(marker), "the add-in was activated and none of it ran");
    }

    [Fact]
    public void AnAddInTheAdministratorActivatedIsLoaded()
    {
        using var external = StagedAddInDirectory.Create();
        external.Install(StagedAddInDirectory.Example);

        var hash = Assert.Single(Load(external.Path, NothingActivated.Instance).Awaiting).ContentHash;
        var catalog = Load(external.Path, new OnlyTheseActivated(hash!));

        Assert.Empty(catalog.Awaiting);
        Assert.Equal("example/provider", Assert.Single(catalog.Loaded).Key);
        Assert.Single(catalog.Drivers);
    }

    // Activation is bound to the bytes, so replacing the file leaves the new ones unactivated. The hash of some
    // other add-in stands in for a previously activated version of this one.
    [Fact]
    public void ActivatingOneVersionDoesNotActivateAnother()
    {
        using var external = StagedAddInDirectory.Create();
        external.Install(StagedAddInDirectory.Example);

        var catalog = Load(external.Path, new OnlyTheseActivated("0000000000000000000000000000000000000000000000000000000000000000"));

        Assert.Empty(catalog.Loaded);
        Assert.Single(catalog.Awaiting);
    }

    // An add-in built against another contract is shown with the reason, rather than offered for activation and
    // then failing when it is loaded.
    [Fact]
    public void AnAddInBuiltAgainstAnotherContractCannotBeActivated()
    {
        using var external = StagedAddInDirectory.Create();
        external.Install(StagedAddInDirectory.Stale);

        var held = Assert.Single(Load(external.Path, NothingActivated.Instance).Awaiting);

        Assert.False(held.CanBeActivated);
        Assert.Contains("0.9", held.Refusal!, StringComparison.Ordinal);
        Assert.Contains(ProviderContract.Version, held.Refusal!, StringComparison.Ordinal);
    }

    // An add-in that states no manifest says nothing an administrator could decide on, so it is listed with that
    // as its reason instead of being hidden.
    [Fact]
    public void AnAddInStatingNoManifestCannotBeActivated()
    {
        using var external = StagedAddInDirectory.Create();
        external.Install(StagedAddInDirectory.TwoFamilies);

        var held = Assert.Single(Load(external.Path, NothingActivated.Instance).Awaiting);

        Assert.False(held.CanBeActivated);
        Assert.Null(held.Key);
        Assert.Contains(nameof(ProviderAddInAttribute), held.Refusal!, StringComparison.Ordinal);
    }

    // The directory the image carries needs no activation: its bytes ship with the host, and replacing one means
    // replacing a shipped binary.
    [Fact]
    public void TheBuiltInDirectoryIsNotGated()
    {
        using var builtIn = StagedAddInDirectory.Create();
        builtIn.Install(StagedAddInDirectory.Example);

        var catalog = ProviderAddInLoader.Load(
            new ProviderAddInDirectories(builtIn.Path, StagedAddInDirectory.Missing()),
            [],
            new RecordingLogger(),
            NothingActivated.Instance);

        Assert.Empty(catalog.Awaiting);
        Assert.Equal("example/provider", Assert.Single(catalog.Loaded).Key);
    }

    private static ProviderAddInCatalog Load(string externalDirectory, IProviderAddInActivations activations)
    {
        return ProviderAddInLoader.Load(
            new ProviderAddInDirectories(StagedAddInDirectory.Missing(), externalDirectory),
            [],
            new RecordingLogger(),
            activations);
    }

    private sealed class NothingActivated : IProviderAddInActivations
    {
        public static NothingActivated Instance { get; } = new();

        public bool IsActivated(string? contentHash) => false;
    }

    private sealed class OnlyTheseActivated(params string[] hashes) : IProviderAddInActivations
    {
        public bool IsActivated(string? contentHash) =>
            contentHash is not null && hashes.Contains(contentHash, StringComparer.Ordinal);
    }
}
