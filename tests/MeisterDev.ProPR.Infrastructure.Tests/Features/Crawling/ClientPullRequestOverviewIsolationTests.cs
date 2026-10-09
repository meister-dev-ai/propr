// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Crawling.Configuration;
using MeisterDev.ProPR.Infrastructure.Features.Crawling.Configuration.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Crawling;

public sealed partial class ClientPullRequestOverviewTests
{
    private TestDbContextFactory IsolationFactory()
    {
        Assert.True(fixture.IsAvailable, "Cache isolation tests require an isolated PostgreSQL fixture.");
        return new(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).Options);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("m:metadata")]
    public async Task EqualObservationKeysKeepClientContentOwnersAndCooldownsIndependent(string key)
    {
        var clock = new FakeTimeProvider();
        var store = new ClientPullRequestOverviewStore(IsolationFactory(), clock);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var provenance = new OverviewCacheScope(Guid.NewGuid(), "equal-source");
        var first = await store.AcquireAsync(a, key, "equal-fingerprint", clock.GetUtcNow(), default, provenance);
        Assert.NotNull(first.Owner);
        Assert.True(await store.CompleteAsync(a, key, first.Owner.Value, "equal-fingerprint", "client-a", null, clock.GetUtcNow(), default));
        clock.Advance(TimeSpan.FromSeconds(5));
        var second = await store.AcquireAsync(b, key, "equal-fingerprint", clock.GetUtcNow(), default, provenance);
        Assert.NotNull(second.Owner);
        Assert.NotEqual(first.Owner, second.Owner);
        Assert.Null(second.Content);
        Assert.Equal(TimeSpan.FromSeconds(5), second.NextRefreshAt - first.NextRefreshAt);
        Assert.False(await store.CompleteAsync(b, key, first.Owner.Value, "equal-fingerprint", "foreign", null, clock.GetUtcNow(), default));
        Assert.False(await store.CompleteAsync(a, key, second.Owner.Value, "equal-fingerprint", "foreign", null, clock.GetUtcNow(), default));
        Assert.True(await store.CompleteAsync(b, key, second.Owner.Value, "equal-fingerprint", "client-b", null, clock.GetUtcNow(), default));
        var retainedA = await store.AcquireAsync(a, key, "equal-fingerprint", clock.GetUtcNow(), default, provenance);
        var retainedB = await store.AcquireAsync(b, key, "equal-fingerprint", clock.GetUtcNow(), default, provenance);
        Assert.Equal("client-a", retainedA.Content);
        Assert.Equal("client-b", retainedB.Content);
        Assert.Null(retainedA.Owner);
        Assert.Null(retainedB.Owner);
        Assert.Equal(first.NextRefreshAt, retainedA.NextRefreshAt);
        Assert.Equal(second.NextRefreshAt, retainedB.NextRefreshAt);
        clock.Advance(TimeSpan.FromSeconds(55));
        Assert.NotNull((await store.AcquireAsync(a, key, "equal-fingerprint", clock.GetUtcNow(), default, provenance)).Owner);
        Assert.Null((await store.AcquireAsync(b, key, "equal-fingerprint", clock.GetUtcNow(), default, provenance)).Owner);
    }

    [Fact]
    public async Task EqualGenerationAndPageKeysKeepEveryReadPathClientScoped()
    {
        var clock = new FakeTimeProvider();
        var store = new ClientPullRequestOverviewStore(IsolationFactory(), clock);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var generation = Guid.NewGuid();
        const string contentA = "{\"attemptedSources\":1,\"marker\":\"client-a\"}";
        const string contentB = "{\"attemptedSources\":1,\"marker\":\"client-b\"}";
        var now = clock.GetUtcNow();
        Assert.True(await store.SaveGenerationAsync(a, generation, "binding", contentA, now, default));
        Assert.True(await store.SaveGenerationAsync(b, generation, "binding", contentB, now, default));
        foreach (var (client, content) in new[] { (a, contentA), (b, contentB) })
        {
            Assert.Equal(content, await store.ReadGenerationAsync(client, generation, "binding", now, default));
            Assert.Equal(content, (await store.ReadGenerationStateAsync(client, generation, "binding", now, default))!.Content);
            Assert.Equal(content, await store.ReadLatestGenerationAsync(client, "binding", now, default));
            var pageKey = $"page:{generation:N}:25:1";
            Assert.True(await store.SavePageAsync(client, pageKey, "binding", content, now.AddMinutes(10), now, default));
            Assert.Equal(content, await store.ReadPageAsync(client, pageKey, "binding", now, default));
            Assert.Null(await store.ReadPageAsync(client, pageKey, "other-binding", now, default));
        }

        Assert.Null(await store.ReadGenerationAsync(foreign, generation, "binding", now, default));
        Assert.Null(await store.ReadGenerationStateAsync(foreign, generation, "binding", now, default));
        Assert.Null(await store.ReadLatestGenerationAsync(foreign, "binding", now, default));
        Assert.Null(await store.ReadPageAsync(foreign, $"page:{generation:N}:25:1", "binding", now, default));
        var sharedA = await store.GetOrCreateGenerationAsync(a, "equivalent-binding", "same-equivalence", contentA, now.AddMinutes(15), now, default);
        var sharedB = await store.GetOrCreateGenerationAsync(b, "equivalent-binding", "same-equivalence", contentB, now.AddMinutes(15), now, default);
        Assert.NotEqual(sharedA!.Id, sharedB!.Id);
        Assert.Equal(
            contentA,
            (await store.GetOrCreateGenerationAsync(a, "equivalent-binding", "same-equivalence", "replacement", now.AddMinutes(15), now, default))!.Content);
        Assert.Equal(
            contentB,
            (await store.GetOrCreateGenerationAsync(b, "equivalent-binding", "same-equivalence", "replacement", now.AddMinutes(15), now, default))!.Content);
    }

    [Theory]
    [InlineData("accessDenied")]
    [InlineData("authenticationDenied")]
    public async Task DenialPreservesAnotherClientsMatchingObservationsInvalidationGenerationsAndPages(string failure)
    {
        var clock = new FakeTimeProvider();
        var store = new ClientPullRequestOverviewStore(IsolationFactory(), clock);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var scope = new OverviewCacheScope(Guid.NewGuid(), "same-source");
        var generation = Guid.NewGuid();
        var pageKey = $"page:{generation:N}:25:1";
        var now = clock.GetUtcNow();
        await using (var seed = IsolationFactory().CreateDbContext())
        {
            seed.ClientPullRequestOverviewCache.Add(
                new ClientPullRequestOverviewCacheRecord
                {
                    ClientId = b, Key = "invalidation", Kind = "invalidation", Fingerprint = "client-b-version", ExpiresAt = DateTimeOffset.MaxValue
                });
            await seed.SaveChangesAsync();
        }

        foreach (var client in new[] { a, b })
        {
            foreach (var key in new[] { "same-source", "m:same-metadata" })
            {
                var claim = await store.AcquireAsync(client, key, "fingerprint", now, default, scope);
                Assert.True(
                    await store.CompleteAsync(client, key, claim.Owner!.Value, "fingerprint", client == a ? "client-a" : "client-b", null, now, default));
            }

            var generationContent = client == a ? "{\"attemptedSources\":1,\"marker\":\"client-a\"}" : "{\"attemptedSources\":1,\"marker\":\"client-b\"}";
            Assert.True(await store.SaveGenerationAsync(client, generation, "binding", generationContent, now, default));
            Assert.True(await store.SavePageAsync(client, pageKey, "binding", client == a ? "page-a" : "page-b", now.AddMinutes(15), now, default));
        }

        var denial = await store.AcquireAsync(a, "denial", "fingerprint", now, default, scope);
        Assert.True(await store.CompleteAsync(a, "denial", denial.Owner!.Value, "fingerprint", null, failure, now, default));
        Assert.NotNull(await store.ReadInvalidationAsync(a, default));
        Assert.Equal("client-b-version", await store.ReadInvalidationAsync(b, default));
        Assert.Null(await store.ReadGenerationAsync(a, generation, "binding", now, default));
        Assert.Null(await store.ReadPageAsync(a, pageKey, "binding", now, default));
        Assert.NotNull(await store.ReadGenerationStateAsync(b, generation, "binding", now, default));
        Assert.Equal("{\"attemptedSources\":1,\"marker\":\"client-b\"}", await store.ReadLatestGenerationAsync(b, "binding", now, default));
        Assert.Equal("page-b", await store.ReadPageAsync(b, pageKey, "binding", now, default));
        foreach (var key in new[] { "same-source", "m:same-metadata" })
        {
            Assert.Null((await store.AcquireAsync(a, key, "fingerprint", now, default, scope)).Content);
            var retained = await store.AcquireAsync(b, key, "fingerprint", now, default, scope);
            Assert.Equal("client-b", retained.Content);
            Assert.Null(retained.Failure);
            Assert.Null(retained.Owner);
            Assert.Equal(now.AddSeconds(60), retained.NextRefreshAt);
        }
    }

    [Fact]
    public async Task CleanupClearsAndDeletesOnlyTheWritingClientsExpiredMatchingKeys()
    {
        var factory = IsolationFactory();
        var clock = new FakeTimeProvider();
        var now = clock.GetUtcNow();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await using (var seed = factory.CreateDbContext())
        {
            foreach (var client in new[] { a, b })
            {
                foreach (var kind in new[] { "source", "metadata", "generation", "page" })
                {
                    seed.ClientPullRequestOverviewCache.Add(
                        new ClientPullRequestOverviewCacheRecord
                        {
                            ClientId = client, Key = "same-" + kind, Kind = kind, Fingerprint = "fingerprint",
                            Content = client == a ? "expired-a" : "expired-b", ContentBytes = 9,
                            ObservedAt = now.AddMinutes(-16), ExpiresAt = now.AddMinutes(-1),
                            Owner = kind is "source" or "metadata" ? Guid.NewGuid() : null,
                            LeaseUntil = kind is "source" or "metadata" ? now.AddSeconds(10) : null,
                            NextRefreshAt = now.AddSeconds(60)
                        });
                }
            }

            await seed.SaveChangesAsync();
        }

        var store = new ClientPullRequestOverviewStore(factory, clock);
        await store.AcquireAsync(a, "cleanup-trigger", "fingerprint", now, default);
        await using var db = factory.CreateDbContext();
        var retainedA = await db.ClientPullRequestOverviewCache.AsNoTracking().Where(row => row.ClientId == a && row.Key.StartsWith("same-")).ToArrayAsync();
        Assert.Equal(2, retainedA.Length);
        Assert.All(
            retainedA, row =>
            {
                Assert.Null(row.Content);
                Assert.Equal(0, row.ContentBytes);
                Assert.Null(row.ObservedAt);
                Assert.NotNull(row.Owner);
                Assert.Equal(now.AddSeconds(60), row.NextRefreshAt);
            });
        var retainedB = await db.ClientPullRequestOverviewCache.AsNoTracking().Where(row => row.ClientId == b).ToArrayAsync();
        Assert.Equal(4, retainedB.Length);
        Assert.All(
            retainedB, row =>
            {
                Assert.Equal("expired-b", row.Content);
                Assert.Equal(9, row.ContentBytes);
                Assert.Equal(now.AddMinutes(-16), row.ObservedAt);
                Assert.Equal(now.AddMinutes(-1), row.ExpiresAt);
                Assert.Equal(now.AddSeconds(60), row.NextRefreshAt);
            });
    }

    [Fact]
    public async Task ValidWarmedCursorCannotBeReplayedByAnotherClientWithAuthorizedSources()
    {
        await using var setup = new IsolationServiceSetup(IsolationFactory());
        var pageA = await setup.ReadAsync(setup.A);
        var pageB = await setup.ReadAsync(setup.B);
        Assert.Equal("client-a", Assert.Single(pageA.Items).Title);
        Assert.Equal("client-b", Assert.Single(pageB.Items).Title);
        Assert.NotEmpty(pageA.Cursor);
        Assert.NotEmpty(pageB.Cursor);
        Assert.NotEqual(pageA.Cursor, pageB.Cursor);
        var exception = await Assert.ThrowsAsync<ClientPullRequestOverviewException>(() => setup.ReadAsync(setup.B, pageA.Cursor));
        Assert.Equal("obsolete", exception.Kind);
        var retainedB = await setup.ReadAsync(setup.B, pageB.Cursor);
        Assert.Equal("client-b", Assert.Single(retainedB.Items).Title);
        Assert.Equal(2, Assert.Single(retainedB.Items).Metadata!.TotalComments);
        Assert.Equal(pageB.Cursor, retainedB.Cursor);
        Assert.Equal("client-a", Assert.Single((await setup.ReadAsync(setup.A, pageA.Cursor)).Items).Title);
        Assert.Equal(2, setup.Discovery.ReceivedCalls().Count());
        Assert.Equal(2, setup.Metadata.ReceivedCalls().Count());
    }

    [Theory]
    [InlineData("grant", "accessDenied")]
    [InlineData("target", "accessDenied")]
    [InlineData("targetOwner", "accessDenied")]
    [InlineData("connectionOwner", "accessDenied")]
    [InlineData("connectionInactive", "accessDenied")]
    [InlineData("connectionUnverified", "accessDenied")]
    [InlineData("tenant", "obsolete")]
    public async Task WarmedPagesRecheckAuthorityBeforeReturningCachedContent(string mutation, string expected)
    {
        await using var setup = new IsolationServiceSetup(IsolationFactory());
        var warmed = await setup.ReadAsync(setup.A);
        Assert.Equal("client-a", Assert.Single((await setup.ReadAsync(setup.A, warmed.Cursor)).Items).Title);
        setup.Revoke(mutation);
        var exception = await Assert.ThrowsAsync<ClientPullRequestOverviewException>(() => setup.ReadAsync(setup.A, warmed.Cursor));
        Assert.Equal(expected, exception.Kind);
        Assert.Single(setup.Discovery.ReceivedCalls());
        Assert.Single(setup.Metadata.ReceivedCalls());
    }

    private sealed class IsolationServiceSetup : IAsyncDisposable
    {
        private readonly ServiceProvider root;
        private readonly Dictionary<Guid, Guid> tenants = [];
        private readonly Dictionary<Guid, CrawlConfigurationDto> targets = [];
        private readonly Dictionary<Guid, ClientScmConnectionDto> connections = [];
        private readonly Dictionary<Guid, ClientScmScopeDto> grants = [];
        public Guid A { get; } = Guid.NewGuid();
        public Guid B { get; } = Guid.NewGuid();
        public IReviewDiscoveryProvider Discovery { get; } = Substitute.For<IReviewDiscoveryProvider>();
        public IReviewOverviewProvider Metadata { get; } = Substitute.For<IReviewOverviewProvider>();

        public IsolationServiceSetup(TestDbContextFactory factory)
        {
            var clock = new FakeTimeProvider();
            var now = clock.GetUtcNow();
            var clients = Substitute.For<IClientRegistry>();
            var configurations = Substitute.For<ICrawlConfigurationRepository>();
            var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
            var scopes = Substitute.For<IClientScmScopeRepository>();
            foreach (var client in new[] { A, B })
            {
                tenants[client] = Guid.NewGuid();
                var connection = new ClientScmConnectionDto(
                    Guid.NewGuid(), client, ScmProvider.GitHub, "https://github.com",
                    ScmAuthenticationKind.PersonalAccessToken, "SCM", true, "verified", now, null, null, now, now);
                connections[client] = connection;
                targets[client] = new(
                    Guid.NewGuid(), client, ScmProvider.GitHub, "https://github.com", "team", 60, false, now,
                    [new(Guid.NewGuid(), "repo", [], new("GitHub", "repo-1"))]);
                grants[client] = new(Guid.NewGuid(), client, connection.Id, "repository", "repo-1", "team/repo", "repo", "verified", true, now, null, now, now);
                clients.GetTenantIdAsync(client, Arg.Any<CancellationToken>()).Returns(_ => tenants[client]);
                configurations.GetReviewTargetPolicySnapshotAsync(targets[client].Id, client, Arg.Any<CancellationToken>()).Returns(_ => targets[client]);
                connectionRepository.GetByIdAsync(client, connection.Id, Arg.Any<CancellationToken>()).Returns(_ => connections[client]);
                scopes.GetByConnectionIdAsync(client, connection.Id, Arg.Any<CancellationToken>()).Returns(_ => new[] { grants[client] });
                Discovery.ListOpenReviewsAsync(client, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>())
                    .Returns(call => (IReadOnlyList<ReviewDiscoveryItemDto>)new[]
                    {
                        new ReviewDiscoveryItemDto(
                            ScmProvider.GitHub, call.ArgAt<RepositoryRef>(1),
                            new(call.ArgAt<RepositoryRef>(1), CodeReviewPlatformKind.PullRequest, "1", 1),
                            CodeReviewState.Open, null, null, client == A ? "client-a" : "client-b", null, "feature", "main")
                    });
                Metadata.GetOverviewAsync(client, Arg.Any<CodeReviewRef>(), Arg.Any<ReviewDiscoveryContext>(), Arg.Any<CancellationToken>())
                    .Returns(new ReviewOverviewDto(client == A ? 1 : 2, null, null, true, false));
            }

            var providers = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
            providers.GetReviewSourcePolicy(ScmProvider.GitHub).Returns(ReviewSourcePolicies.Get(ScmProvider.GitHub));
            providers.GetReviewDiscoveryProvider(ScmProvider.GitHub).Returns(Discovery);
            providers.GetReviewOverviewProvider(ScmProvider.GitHub).Returns(Metadata);
            root = new ServiceCollection().AddSingleton<IDbContextFactory<MeisterProPRDbContext>>(factory)
                .AddSingleton<TimeProvider>(clock).AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider())
                .AddSingleton(clients).AddSingleton(configurations).AddSingleton(connectionRepository).AddSingleton(scopes).AddSingleton(providers)
                .AddScoped<IClientPullRequestOverviewStore, ClientPullRequestOverviewStore>().AddScoped<ClientPullRequestOverviewService>()
                .BuildServiceProvider();
        }

        public async Task<ClientPullRequestOverviewPage> ReadAsync(Guid client, string? cursor = null)
        {
            await using var scope = root.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>()
                .ReadAsync(client, new([new(targets[client].Id, connections[client].Id)], "same-binding", cursor), default);
        }

        public void Revoke(string mutation)
        {
            switch (mutation)
            {
                case "grant": grants[A] = grants[A] with { IsEnabled = false }; break;
                case "target": targets[A] = targets[A] with { ReviewTargetLifecycle = ReviewTargetLifecycle.Disabled }; break;
                case "targetOwner": targets[A] = targets[A] with { ClientId = B }; break;
                case "connectionOwner": connections[A] = connections[A] with { ClientId = B }; break;
                case "connectionInactive": connections[A] = connections[A] with { IsActive = false }; break;
                case "connectionUnverified": connections[A] = connections[A] with { VerificationStatus = "unverified" }; break;
                case "tenant": tenants[A] = Guid.NewGuid(); break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        }

        public ValueTask DisposeAsync() => root.DisposeAsync();
    }
}
