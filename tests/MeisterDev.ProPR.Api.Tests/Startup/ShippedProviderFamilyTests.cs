// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using System.Collections.Immutable;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Api.Tests.Startup;

/// <summary>
///     What every provider family this product ships declares about itself, read off the composed host.
/// </summary>
/// <remarks>
///     <para>
///         Each family is now an assembly of its own, loaded from the built-in add-in directory, so the host is
///         the only place all of them exist together. That is what these checks need: a per-family test in a
///         family's own project cannot notice two families drifting apart, and the rules below are about the set
///         rather than about any one of them.
///     </para>
///     <para>
///         The shared driver checks are not repeated here. Each family runs those in its own build and the host
///         runs them again over every family it loads, skipping one that fails. What is here is what those checks
///         do not cover: the spellings a family supersedes, which decide whether a stored row still resolves, and
///         the affordances none of the shipped families claims.
///     </para>
/// </remarks>
public sealed class ShippedProviderFamilyTests : IClassFixture<ShippedProviderFamilyTests.ProviderHostFixture>
{
    // The identity each shipped family declares, and the spelling its connections were stored under before it
    // did. Both are pinned because both are stored: a key that changes reinterprets every row carrying it, and a
    // superseded spelling that is dropped quarantines every row still carrying that.
    private static readonly IReadOnlyDictionary<string, string> SupersededIdentityByKey =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["meisterdev/azureOpenAi"] = "AzureOpenAi",
            ["meisterdev/openAi"] = "OpenAi",
            ["meisterdev/liteLlm"] = "LiteLlm",
            ["meisterdev/openAiCompatible"] = "OpenAiCompatible",
            ["meisterdev/anthropic"] = "Anthropic",
            ["meisterdev/awsBedrock"] = "AwsBedrock",
            ["meisterdev/googleVertex"] = "GoogleVertex",
        };

    private readonly IAiProviderDriverRegistry _registry;

    public ShippedProviderFamilyTests(ProviderHostFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        this._registry = fixture.Registry;
    }

    [Fact]
    public void EveryFamilyDeclaresAWellFormedIdentityKey()
    {
        Assert.All(
            this.Families(),
            driver => Assert.True(ProviderDeclaration.IsValidIdentityKey(driver.Declaration.Key), driver.Declaration.Key));
    }

    // The set is what the host serves, and every one of its members is an identity connections are stored
    // against, so the pinned map has to cover exactly the families that are loaded.
    [Fact]
    public void TheLoadedFamiliesAreTheOnesWhoseStoredIdentitiesArePinned()
    {
        Assert.Equal(
            SupersededIdentityByKey.Keys.Order(StringComparer.Ordinal),
            this._registry.RegisteredKinds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryFamilyDeclaresALabelAVersionAndTheContractItWasBuiltAgainst()
    {
        Assert.All(
            this.Families(),
            driver =>
            {
                var declaration = driver.Declaration;

                Assert.False(string.IsNullOrWhiteSpace(declaration.Label));
                Assert.False(string.IsNullOrWhiteSpace(declaration.Version));
                Assert.Equal(ProviderContract.Version, declaration.ContractVersion);
            });
    }

    // The alias exists so a row written before the family declared its current names still resolves. A family
    // that declared no legacy name would quarantine every connection it already has. The spelling each family's
    // rows held before it declared a key is pinned as a literal, because it is a stored contract: dropping one
    // quarantines every connection still carrying it.
    [Fact]
    public void EveryFamilyDeclaresTheStoredSpellingsItSupersedes()
    {
        Assert.All(
            this.Families(),
            driver =>
            {
                var declaration = driver.Declaration;
                var legacy = declaration.LegacyNames;

                Assert.True(SupersededIdentityByKey.TryGetValue(declaration.Key, out var superseded), declaration.Key);
                Assert.Contains(superseded, legacy.Keys);
                // A mode the family owns persists qualified by its key, and the spelling it supersedes is that
                // mode name on its own, which its rows held. A host-reserved shape carries no qualifier
                // and supersedes nothing.
                Assert.All(
                    driver.SupportedAuthModes,
                    mode => Assert.Equal(
                        ProviderVocabulary.Split(mode).ModeName,
                        legacy.AuthModeSpellings[mode]));
                Assert.All(
                    driver.SupportedProtocolModes.Where(mode => !ProviderDeclaredProtocolModes.IsReserved(mode)),
                    mode => Assert.Equal(
                        ProviderVocabulary.Split(mode).ModeName,
                        legacy.ProtocolModeSpellings[mode]));
                Assert.All(
                    driver.SupportedProtocolModes.Where(ProviderDeclaredProtocolModes.IsReserved),
                    mode => Assert.DoesNotContain(mode, legacy.ProtocolModeSpellings.Keys));
            });
    }

    // An administrator activating an add-in decides on what its assembly states, and the host refuses one whose
    // driver then declares something else. A shipped family is not gated, so nothing would catch the two
    // drifting apart here; this does. These families are also the worked examples an author copies, so what
    // they state has to be what the contract asks for.
    [Fact]
    public void EveryFamilyStatesInItsAssemblyWhatItsDriverDeclares()
    {
        foreach (var driver in this.Families())
        {
            var declaration = driver.Declaration;
            var stated = driver.GetType().Assembly.GetCustomAttribute<ProviderAddInAttribute>();

            Assert.True(
                stated is not null,
                $"'{declaration.Key}' declares no {nameof(ProviderAddInAttribute)}, so a host could not read what "
                + "it is without running it.");

            Assert.Equal(declaration.Key, stated!.Key);
            Assert.Equal(declaration.Label, stated.Label);
            Assert.Equal(declaration.Version, stated.Version);
            Assert.Equal(declaration.ContractVersion, stated.ContractVersion);
            Assert.Equal(declaration.RequiredCapabilityKey, stated.RequiredCapability);
            Assert.Equal(
                declaration.ReachedHostPatterns.Order(StringComparer.Ordinal),
                stated.ReachedHosts.Order(StringComparer.Ordinal));
        }
    }

    // The shipped declarations are what an installation actually runs, and they are read together rather than
    // one at a time: a spelling two families share would leave a stored identity naming neither.
    [Fact]
    public void EveryFamilyResolvesEveryValueItsOwnRowsHold()
    {
        foreach (var driver in this.Families())
        {
            var declaration = driver.Declaration;
            var providerKind = declaration.Key;

            // Every connection stored before the family moved holds the spelling it supersedes, and both resolve.
            Assert.True(this._registry.ResolveIdentity(SupersededIdentityByKey[providerKind]).TryGetKey(out _));
            Assert.True(this._registry.ResolveIdentity(declaration.Key).TryGetKey(out _));

            // Both the qualified value a row holds after the family's migration and the unqualified spelling it
            // held before resolve, and that keeps a connection working across that migration.
            foreach (var mode in declaration.SupportedAuthModes)
            {
                Assert.True(this._registry.ResolveAuthMode(providerKind, mode).TryGetValue(out _));
                Assert.True(
                    this._registry
                        .ResolveAuthMode(providerKind, ProviderVocabulary.Split(mode).ModeName)
                        .TryGetValue(out _));
            }

            foreach (var mode in declaration.ProtocolModes.Supported)
            {
                Assert.True(this._registry.ResolveProtocolMode(providerKind, mode).TryGetValue(out _));
                Assert.True(
                    this._registry
                        .ResolveProtocolMode(providerKind, ProviderVocabulary.Split(mode).ModeName)
                        .TryGetValue(out _));
            }
        }
    }

    // The vocabulary members are what the host offers an operator, so a declaration that disagreed with the
    // driver would offer a credential shape the driver cannot read.
    [Fact]
    public void TheDeclaredVocabularyIsWhatEveryFamilyAnswersWith()
    {
        Assert.All(
            this.Families(),
            driver =>
            {
                Assert.Equal(driver.SupportedAuthModes, driver.Declaration.SupportedAuthModes);
                Assert.Equal(driver.SupportedProtocolModes, driver.Declaration.ProtocolModes.Supported);
                Assert.Equal(
                    driver.CredentialFields.OrderBy(entry => entry.Key).Select(entry => entry.Key),
                    driver.Declaration.CredentialFields.OrderBy(entry => entry.Key).Select(entry => entry.Key));
            });
    }

    // Auto is what a binding says when it has no opinion, and no family owns it or Embeddings: a family that
    // claimed one would be claiming a name every other family's rows also hold.
    [Fact]
    public void EveryFamilyHonoursTheReservedProtocolMembersWithoutOwningThem()
    {
        Assert.All(
            this.Families(),
            driver =>
            {
                var modes = driver.Declaration.ProtocolModes;

                Assert.Contains(ProviderDeclaredProtocolModes.Auto, modes.ReservedHonoured);
                Assert.Empty(modes.Owned.Intersect(ProviderDeclaredProtocolModes.Reserved));
                Assert.Equal(modes.Supported.Count, modes.Owned.Count + modes.ReservedHonoured.Count);
            });
    }

    // A declared pattern is checked against a tenant's endpoint list, and a bare host that was meant as a suffix
    // matches nothing while a suffix that was meant as a host admits every subdomain of it.
    [Fact]
    public void EveryDeclaredHostPatternIsAHostOrASuffixOfOne()
    {
        foreach (var driver in this.Families())
        {
            foreach (var pattern in driver.Declaration.ReachedHostPatterns)
            {
                Assert.False(string.IsNullOrWhiteSpace(pattern));
                Assert.DoesNotContain("/", pattern, StringComparison.Ordinal);
                Assert.DoesNotContain(":", pattern, StringComparison.Ordinal);
                Assert.Contains(".", pattern.TrimStart('.'), StringComparison.Ordinal);
            }
        }
    }

    // The shared checks are constructed from this, so a family that declared an endpoint its own rules refuse
    // would be measured against a target it was always going to reject.
    [Fact]
    public void TheDeclaredConformanceEndpointIsOneTheFamilyAccepts()
    {
        Assert.All(
            this.Families(),
            driver =>
            {
                var inputs = driver.Declaration.ConformanceInputs;

                // The mode the checks read this family's credential fields against has to be one it declares,
                // or they read the fields of a mode nothing can be configured with.
                Assert.Contains(inputs.CredentialAuthMode, driver.SupportedAuthModes);
                Assert.NotEmpty(inputs.RecordedUsagePayload);
            });
    }

    // None of the shipped families holds a grant that can be revoked, opens a socket, needs the operator's
    // browser beside the host, or is gated on a licence. Asserted so making one of them do so is a deliberate
    // edit.
    [Fact]
    public void NoShippedFamilyDeclaresAnActionOrAnythingThatNeedsOne()
    {
        Assert.All(
            this.Families(),
            driver =>
            {
                var declaration = driver.Declaration;

                Assert.Empty(declaration.Actions);
                Assert.Null(declaration.InvocationWindow);
                Assert.False(declaration.OpensListener);
                Assert.False(declaration.RequiresBrowserCoLocation);
                Assert.False(declaration.HasCredentialHealth);
                Assert.Null(declaration.RequiredCapabilityKey);
            });
    }

    // A declaration is a process-wide instance and every add-in shares the process, so a collection behind a
    // read-only interface that a caller can cast back to what is under it is a way to edit what another family
    // declares for the life of the host. The legacy spellings decide which stored rows a family resolves, and
    // the hosts and credential shapes decide what an operator is offered, so all of them are rules.
    [Fact]
    public void NoFamilysDeclaredListsCanBeEditedThroughACast()
    {
        Assert.All(
            this.Families(),
            driver =>
            {
                var declaration = driver.Declaration;

                Assert.IsType<ImmutableArray<string>>(declaration.LegacyNames.Keys);
                Assert.IsType<ImmutableDictionary<string, string>>(declaration.LegacyNames.AuthModeSpellings);
                Assert.IsType<ImmutableDictionary<string, string>>(declaration.LegacyNames.ProtocolModeSpellings);

                Assert.IsType<ImmutableArray<ProviderDeclaredAuthMode>>(declaration.AuthModes);
                Assert.IsType<ImmutableArray<ProviderDeclaredField>>(declaration.Fields);
                Assert.IsType<ImmutableArray<ProviderDeclaredAction>>(declaration.Actions);
                Assert.IsType<ImmutableArray<string>>(declaration.ReachedHostPatterns);
            });
    }

    private IReadOnlyList<IAiProviderDriver> Families()
    {
        return [.. this._registry.RegisteredKinds.Select(this._registry.GetRequired)];
    }

    /// <summary>
    ///     One composed host for the whole class, because every case reads the same registry and starting a host
    ///     per case would read the add-in directory once per case.
    /// </summary>
    public sealed class ProviderHostFixture : IDisposable
    {
        private readonly ApiHostFactory _factory = new();

        public ProviderHostFixture()
        {
            _ = this._factory.CreateClient();

            this.Registry = this._factory.Services.GetRequiredService<IAiProviderDriverRegistry>();
        }

        /// <summary>The families the composed host loaded from the built-in add-in directory.</summary>
        public IAiProviderDriverRegistry Registry { get; }

        /// <inheritdoc />
        public void Dispose()
        {
            this._factory.Dispose();
        }

        /// <summary>The API host as a test composes it: no database and no background workers.</summary>
        private sealed class ApiHostFactory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
                builder.UseSetting("MEISTER_JWT_SECRET", "test-shipped-provider-family-secret-32");
            }
        }
    }
}
