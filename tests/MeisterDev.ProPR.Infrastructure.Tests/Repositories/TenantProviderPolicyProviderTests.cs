// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Covers what a stored allow-list entry this build cannot name does to the policy it is read into. The
///     load-bearing case is a tenant whose entries all stopped resolving: it has to keep refusing, and an operator
///     has to be able to find out which entry caused it.
/// </summary>
public sealed class TenantProviderPolicyProviderTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid ClientId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private const string CompatibleKey = "meisterdev/openAiCompatible";
    private const string OpenAiKey = "meisterdev/openAi";

    [Fact]
    public async Task AnEntryNoLoadedFamilyClaimsLeavesTheRestRestricting()
    {
        var sut = Sut(out _, ["OpenAiCompatible", "Acme.Llm"], registry: TwoFamiliesLoaded());

        var policy = await sut.GetForTenantAsync(TenantId);

        Assert.True(policy.IsRestricted);
        Assert.True(policy.IsAllowed(CompatibleKey));
        Assert.False(policy.IsAllowed(OpenAiKey));
        Assert.Equal(["Acme.Llm"], policy.UnresolvedProviderEntries);
    }

    // The fail-open this closes: the entries were dropped, the surviving list was empty, and an empty list was
    // read as no policy at all.
    [Fact]
    public async Task ATenantWhoseEntriesAllStoppedResolvingRefusesEveryFamily()
    {
        var sut = Sut(out _, ["Acme.Llm"]);

        var policy = await sut.GetForTenantAsync(TenantId);

        Assert.True(policy.IsRestricted);
        Assert.All(
            new[] { CompatibleKey, OpenAiKey, "Acme.Llm" },
            key => Assert.False(policy.IsAllowed(key)));
    }

    // What a family's identity migration leaves behind halfway through: one entry rewritten to the declared key,
    // the rest still holding the member name. Both spellings name the same family, so the tenant keeps refusing
    // everything it did not list. A rewrite that resolved to nothing would empty the list and open the tenant.
    [Fact]
    public async Task ATenantWhoseAllowListIsOnlyPartlyRewrittenStaysRestricted()
    {
        var sut = Sut(out _, [CompatibleKey, "OpenAi"], registry: TwoFamiliesLoaded());

        var policy = await sut.GetForTenantAsync(TenantId);

        Assert.True(policy.IsRestricted);
        Assert.True(policy.IsAllowed(CompatibleKey));
        Assert.True(policy.IsAllowed(OpenAiKey));
        Assert.False(policy.IsAllowed("meisterdev/azureOpenAi"));
        Assert.Empty(policy.UnresolvedProviderEntries);
    }

    // The same list before the rewrite reaches it, which every tenant holds until the migration runs.
    [Fact]
    public async Task ATenantWhoseAllowListIsNotYetRewrittenAllowsTheSameFamilies()
    {
        var sut = Sut(out _, ["OpenAiCompatible", "OpenAi"], registry: TwoFamiliesLoaded());

        var policy = await sut.GetForTenantAsync(TenantId);

        Assert.True(policy.IsRestricted);
        Assert.True(policy.IsAllowed(CompatibleKey));
        Assert.True(policy.IsAllowed(OpenAiKey));
        Assert.False(policy.IsAllowed("meisterdev/azureOpenAi"));
    }

    [Fact]
    public async Task ATenantThatStatedNoPolicyIsUnrestricted()
    {
        var sut = Sut(out _, []);

        var policy = await sut.GetForTenantAsync(TenantId);

        Assert.False(policy.IsRestricted);
        Assert.True(policy.IsAllowed("meisterdev/openAi"));
    }

    // The client leg resolves the owning tenant first and then reads the same row, so it has to answer the same
    // way: a client whose tenant restricts nothing it can name may use nothing.
    [Fact]
    public async Task TheClientLegAnswersAsTheTenantLegDoes()
    {
        var sut = Sut(out _, ["Acme.Llm"]);

        var policy = await sut.GetForClientAsync(ClientId);

        Assert.True(policy.IsRestricted);
        Assert.False(policy.IsAllowed("meisterdev/azureOpenAi"));
    }

    [Fact]
    public async Task TheUnresolvedEntryIsLoggedWithTheTenantOnEveryRead()
    {
        var sut = Sut(out var log, ["Acme.Llm"]);

        await sut.GetForTenantAsync(TenantId);
        await sut.GetForTenantAsync(TenantId);

        Assert.Equal(2, log.Count);
        Assert.All(
            log,
            line =>
            {
                Assert.Contains("Acme.Llm", line, StringComparison.Ordinal);
                Assert.Contains(TenantId.ToString(), line, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task AResolvableAllowListIsNotLogged()
    {
        var sut = Sut(out var log, ["OpenAiCompatible"], registry: TwoFamiliesLoaded());

        await sut.GetForTenantAsync(TenantId);

        Assert.Empty(log);
    }

    // The system tenant has no allow-list surface and is answered without a query, so nothing about it is read
    // and nothing is logged for it.
    [Fact]
    public async Task TheSystemTenantIsUnrestrictedWithoutARead()
    {
        var sut = Sut(out var log, ["Acme.Llm"]);

        var policy = await sut.GetForTenantAsync(TenantCatalog.SystemTenantId);

        Assert.False(policy.IsRestricted);
        Assert.Empty(log);
    }

    // A build that has loaded the two families whose rows the identity migration rewrites: each answers to the
    // key it declares and to the member name its rows still hold.
    private static IAiProviderDriverRegistry TwoFamiliesLoaded()
    {
        return new AiProviderRegistry(
        [
            Loaded(CompatibleKey, "OpenAiCompatible"),
            Loaded(OpenAiKey, "OpenAi"),
        ]);
    }

    private static IAiProviderDriver Loaded(string key, string supersededKey)
    {
        return DeclaringProviderFamilies.DriverFor(
            DeclaringProviderFamilies.DeclarationWith(key) with
            {
                LegacyNames = ProviderLegacyNames.FromUnqualifiedNames(
                    [supersededKey],
                    [ProviderVocabulary.Compose(key, "ApiKey")],
                    [ProviderVocabulary.Compose(key, "ChatCompletions")]),
            });
    }

    private static TenantProviderPolicyProvider Sut(
        out List<string> log,
        string[] storedProviderKinds,
        string[]? storedEndpointHosts = null,
        IAiProviderDriverRegistry? registry = null)
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using (var seed = new MeisterProPRDbContext(options))
        {
            seed.Tenants.Add(
                new TenantRecord
                {
                    Id = TenantId,
                    Slug = "acme",
                    DisplayName = "Acme Corp",
                    IsActive = true,
                    LocalLoginEnabled = true,
                    AllowedAiProviderKinds = storedProviderKinds,
                    AllowedAiEndpointHosts = storedEndpointHosts ?? [],
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            seed.Clients.Add(
                new ClientRecord
                {
                    Id = ClientId,
                    TenantId = TenantId,
                    DisplayName = "Acme client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            seed.SaveChanges();
        }

        var captured = new List<string>();
        log = captured;
        // The registry is what lets an entry naming a family by its declared key resolve. These cases are about
        // the entries that do not resolve, so it holds no family.
        return new TenantProviderPolicyProvider(
            new TestDbContextFactory(options),
            registry ?? new AiProviderRegistry([]),
            new CapturingLogger(captured));
    }

    private sealed class CapturingLogger(List<string> lines) : ILogger<TenantProviderPolicyProvider>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lines.Add(formatter(state, exception));
        }
    }
}
