// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Crawling.Configuration.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Crawling.Configuration;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Microsoft.Extensions.Time.Testing;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Crawling;

[Collection("PostgresIntegration4")]
public sealed partial class ClientPullRequestOverviewTests(PostgresContainerFixture fixture)
{
    [SkippableTheory]
    [InlineData(1, "success")]
    [InlineData(30, "success")]
    [InlineData(1, "cancellation")]
    [InlineData(1, "rotation")]
    [InlineData(1, "lifecycle")]
    [InlineData(1, "scope")]
    [InlineData(1, "policy")]
    [InlineData(1, "tenant")]
    [InlineData(1, "sourceFailure")]
    [InlineData(1, "metadataFailure")]
    [InlineData(1, "incomplete")]
    [InlineData(1, "stale")]
    [InlineData(1, "reuse")]
    [InlineData(1, "revision")]
    [InlineData(1, "coverage")]
    [InlineData(1, "sourceDenial")]
    [InlineData(1, "metadataDenial")]
    [InlineData(1, "nullSource")]
    [InlineData(1, "duplicate")]
    [InlineData(1, "tie")]
    [InlineData(2, "tie")]
    [InlineData(1, "capacityAuthorization")]
    [InlineData(1, "capacityTime")]
    [InlineData(2, "authenticationDenial")]
    [InlineData(1, "provenance")]
    [InlineData(1, "coverageCaller")]
    [InlineData(1, "cursorless")]
    [InlineData(100, "responseCapacity")]
    [InlineData(1, "originalListing")]
    [InlineData(1, "originalMetadata")]
    [InlineData(1, "sourceHang")]
    [InlineData(1, "metadataHang")]
    [InlineData(1, "sourceLate")]
    [InlineData(1, "metadataLate")]
    [InlineData(30, "persistedDeadline")]
    [InlineData(1, "cursorResurrection")]
    [InlineData(30, "persistedCapacity")]
    [InlineData(30, "persistedAuthority")]
    [InlineData(1, "graphqlForbidden")]
    [InlineData(1, "graphqlRateLimited")]
    public async Task IndependentServicesShareProviderRefreshAndReturnMetadataInThePage(int reviewCount, string scenario)
    {
        if (scenario is "graphqlForbidden" or "graphqlRateLimited")
        {
            Assert.True(fixture.IsAvailable, "The shared-cache regression requires an isolated PostgreSQL fixture.");
        }
        else
        {
            fixture.SkipIfUnavailable();
        }

        var client = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var otherConnectionId = Guid.NewGuid();
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).Options);
        var clients = Substitute.For<IClientRegistry>();
        var tenant = Guid.NewGuid();
        clients.GetTenantIdAsync(client, Arg.Any<CancellationToken>()).Returns(_ => tenant);
        var configurations = Substitute.For<ICrawlConfigurationRepository>();
        Action? authorityCompleted = null;
        var target = new CrawlConfigurationDto(
            targetId, client, ScmProvider.GitHub, "https://github.com", "team", 60, false,
            DateTimeOffset.UtcNow, [new(Guid.NewGuid(), "repo", [], new("GitHub", "repo-1"))]);
        configurations.GetReviewTargetPolicySnapshotAsync(Arg.Any<Guid>(), client, Arg.Any<CancellationToken>()).Returns(call =>
        {
            authorityCompleted?.Invoke();
            return
                scenario is "duplicate" or "responseCapacity"
                    ? target with { Id = call.ArgAt<Guid>(0) }
                    : scenario is "coverage" or "coverageCaller" or "authenticationDenial"
                        ? target with
                        {
                            Id = call.ArgAt<Guid>(0),
                            RepoFilters = [target.RepoFilters[0] with { CanonicalSourceRef = new("GitHub", call.ArgAt<Guid>(0).ToString()) }]
                        }
                        : target;
        });
        var connections = Substitute.For<IClientScmConnectionRepository>();
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(now);
        var connection = new ClientScmConnectionDto(
            connectionId, client, ScmProvider.GitHub, "https://github.com", ScmAuthenticationKind.PersonalAccessToken,
            "GitHub", true, "verified", now, null, null, now, now);
        connections.GetByIdAsync(client, connectionId, Arg.Any<CancellationToken>()).Returns(_ => connection);
        var connectionIds = Enumerable.Range(0, 9).Select(_ => Guid.NewGuid()).Order().ToArray();
        if (scenario is "authenticationDenial" or "provenance" or "sourceHang" or "metadataHang" or "sourceLate" or "metadataLate")
        {
            connections.GetByIdAsync(client, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call => connection with { Id = call.ArgAt<Guid>(1) });
        }

        var scopes = Substitute.For<IClientScmScopeRepository>();
        var scope = new ClientScmScopeDto(
            Guid.NewGuid(), client, connectionId, "repository", "repo-1", "team/repo", "repo", "verified", true, now, null, now, now);
        var targetIds = scenario is "coverage" or "coverageCaller" or "duplicate" or "responseCapacity" or "authenticationDenial"
            ? Enumerable.Range(0, scenario == "authenticationDenial" ? 3 : scenario == "responseCapacity" ? 100 : 10).Select(_ => Guid.NewGuid()).Order()
                .ToArray()
            : [targetId];
        scopes.GetByConnectionIdAsync(client, connectionId, Arg.Any<CancellationToken>()).Returns(_ => scenario is "coverage" or "coverageCaller"
            ? targetIds.Select(id => scope with { ExternalScopeId = id.ToString() }).ToArray()
            : new[] { scope });
        if (scenario == "authenticationDenial")
        {
            scopes.GetByConnectionIdAsync(client, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
                targetIds.Where((_, index) => (index < 2) == (call.ArgAt<Guid>(1) == connectionId))
                    .Select(id => scope with { ConnectionId = call.ArgAt<Guid>(1), ExternalScopeId = id.ToString() }).ToArray());
        }

        if (scenario is "provenance" or "sourceHang" or "metadataHang" or "sourceLate" or "metadataLate")
        {
            scopes.GetByConnectionIdAsync(client, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(call => new[] { scope with { ConnectionId = call.ArgAt<Guid>(1) } });
        }

        var discovery = Substitute.For<IReviewDiscoveryProvider>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshed = false;
        var crossExpiry = 0;
        var empty = false;
        var hanging = new TaskCompletionSource<IReadOnlyList<ReviewDiscoveryItemDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hangingMetadata = new TaskCompletionSource<ReviewOverviewDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerHung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken providerToken = default;
        discovery.ListOpenReviewsAsync(client, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>())
            .Returns(async call =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(call.ArgAt<CancellationToken>(3));
                if (scenario is "sourceHang" or "sourceLate" && call.ArgAt<ReviewDiscoveryContext>(4).ConnectionId == connectionId)
                {
                    providerToken = call.ArgAt<CancellationToken>(3);
                    providerHung.TrySetResult();
                    return await hanging.Task;
                }

                if (scenario == "originalListing" && refreshed)
                {
                    throw new HttpRequestException("Provider unavailable.");
                }

                if (scenario == "sourceFailure")
                {
                    throw new HttpRequestException("Provider unavailable.");
                }

                if (scenario == "sourceDenial" && refreshed)
                {
                    throw new HttpRequestException("Provider denied.", null, System.Net.HttpStatusCode.Unauthorized);
                }

                var repository = call.ArgAt<RepositoryRef>(1);
                if (scenario == "provenance" && empty && call.ArgAt<ReviewDiscoveryContext>(4).ConnectionId != connectionIds[8])
                {
                    return (IReadOnlyList<ReviewDiscoveryItemDto>)[];
                }

                if (Interlocked.Exchange(ref crossExpiry, 0) == 1)
                {
                    clock.Advance(TimeSpan.FromSeconds(2));
                }

                if (scenario == "coverage" && refreshed && repository.ExternalRepositoryId == targetIds[0].ToString())
                {
                    return (IReadOnlyList<ReviewDiscoveryItemDto>)[];
                }

                if (scenario == "coverage" && refreshed && repository.ExternalRepositoryId == targetIds[1].ToString())
                {
                    throw new HttpRequestException("Provider unavailable.");
                }

                if (scenario == "tie")
                {
                    var item = new ReviewDiscoveryItemDto(
                        ScmProvider.GitHub, repository, new(repository, CodeReviewPlatformKind.PullRequest, "1", 1),
                        CodeReviewState.Open, null, null, "Update", null, "a", "main");
                    return (IReadOnlyList<ReviewDiscoveryItemDto>)(reviewCount == 1
                        ? new[] { item with { SourceBranch = "z" }, item }
                        : new[] { item, item with { SourceBranch = "z" } });
                }

                return (IReadOnlyList<ReviewDiscoveryItemDto>)Enumerable.Range(1, reviewCount).Select(number => new ReviewDiscoveryItemDto(
                    ScmProvider.GitHub, repository, new(repository, CodeReviewPlatformKind.PullRequest, number.ToString(), number),
                    CodeReviewState.Open,
                    scenario is "revision" or "duplicate" or "provenance"
                        ? new(refreshed ? "head-2" : "head-1", "base", null, refreshed ? "2" : "1", null)
                        : scenario == "responseCapacity" && call.ArgAt<ReviewDiscoveryContext>(4).ConnectionId == connectionId &&
                          repository.ExternalRepositoryId == "repo-1" && refreshed
                            ? new(new string('\uffff', 128), "base", null, new string('\uffff', 128), null)
                            : null,
                    null, scenario == "responseCapacity" && refreshed ? new string('\uffff', 512) : "Update",
                    scenario == "responseCapacity" && refreshed ? "https://github.com/" + new string('a', 2029) : null,
                    scenario == "responseCapacity" && refreshed ? new string('\uffff', 512) : "feature",
                    scenario == "responseCapacity" && refreshed ? new string('\uffff', 512) : "main",
                    scenario == "responseCapacity" && refreshed ? new string('\uffff', 256) : null)).ToArray();
            });
        var metadata = Substitute.For<IReviewOverviewProvider>();
        metadata.GetOverviewAsync(client, Arg.Any<CodeReviewRef>(), Arg.Any<ReviewDiscoveryContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
                scenario == "authenticationDenial" && refreshed && call.ArgAt<ReviewDiscoveryContext>(2).ConnectionId == connectionId
                    ? Task.FromException<ReviewOverviewDto>(new HttpRequestException("Provider denied.", null, System.Net.HttpStatusCode.Unauthorized))
                    : scenario == "metadataDenial" && refreshed
                        ? Task.FromException<ReviewOverviewDto>(new HttpRequestException("Provider denied.", null, System.Net.HttpStatusCode.Forbidden))
                        : scenario == "metadataFailure"
                            ? Task.FromException<ReviewOverviewDto>(new HttpRequestException("Provider unavailable."))
                            : Task.FromResult(
                                new ReviewOverviewDto(
                                    scenario is "revision" or "duplicate" && refreshed ? 9 : 0, null, null, scenario != "incomplete", false)));
        if (scenario == "provenance")
        {
            metadata.GetOverviewAsync(client, Arg.Any<CodeReviewRef>(), Arg.Any<ReviewDiscoveryContext>(), Arg.Any<CancellationToken>())
                .Returns(call => new ReviewOverviewDto(
                    call.ArgAt<ReviewDiscoveryContext>(2).ConnectionId == connectionIds[8] ? 5 : 9, null, null, true, false));
        }

        if (scenario is "originalMetadata" or "persistedDeadline" or "cursorResurrection" or "persistedCapacity" or "persistedAuthority")
        {
            metadata.GetOverviewAsync(client, Arg.Any<CodeReviewRef>(), Arg.Any<ReviewDiscoveryContext>(), Arg.Any<CancellationToken>())
                .Returns(call => refreshed && (scenario == "originalMetadata" || scenario == "cursorResurrection" || call.ArgAt<CodeReviewRef>(1).Number > 5)
                    ? Task.FromException<ReviewOverviewDto>(new HttpRequestException("Provider unavailable."))
                    : AdvanceMetadata());
        }

        Task<ReviewOverviewDto> AdvanceMetadata()
        {
            if (scenario == "persistedDeadline" && refreshed)
            {
                clock.Advance(TimeSpan.FromSeconds(2));
            }

            return Task.FromResult(new ReviewOverviewDto(7, null, null, true, false));
        }

        if (scenario is "metadataHang" or "metadataLate")
        {
            metadata.GetOverviewAsync(client, Arg.Any<CodeReviewRef>(), Arg.Any<ReviewDiscoveryContext>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    if (call.ArgAt<ReviewDiscoveryContext>(2).ConnectionId != connectionId)
                    {
                        return Task.FromResult(new ReviewOverviewDto(0, null, null, true, false));
                    }

                    providerToken = call.ArgAt<CancellationToken>(3);
                    providerHung.TrySetResult();
                    return hangingMetadata.Task;
                });
        }

        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        registry.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(call => ReviewSourcePolicies.Get(call.Arg<ScmProvider>()));
        registry.GetReviewDiscoveryProvider(ScmProvider.GitHub).Returns(discovery);
        registry.GetReviewOverviewProvider(ScmProvider.GitHub).Returns(metadata);
        var graphqlRequests = 0;
        if (scenario is "graphqlForbidden" or "graphqlRateLimited")
        {
            connections.GetOperationalConnectionByIdAsync(client, connectionId, Arg.Any<CancellationToken>()).Returns(
                new ClientScmConnectionCredentialDto(
                    connectionId, client, ScmProvider.GitHub, "https://github.com",
                    ScmAuthenticationKind.PersonalAccessToken, "Fixture", "fixture-token", true));
            var httpFactory = Substitute.For<IHttpClientFactory>();
            httpFactory.CreateClient("GitHubProvider").Returns(
                new HttpClient(
                    new GraphQlHandler(httpRequest =>
                    {
                        object payload;
                        if (httpRequest.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal))
                        {
                            payload = new { login = "fixture" };
                        }
                        else if (httpRequest.RequestUri.AbsolutePath.EndsWith("/graphql", StringComparison.Ordinal))
                        {
                            graphqlRequests++;
                            payload = refreshed
                                ? new
                                {
                                    errors = new[]
                                    {
                                        new { type = scenario == "graphqlForbidden" ? "FORBIDDEN" : "RATE_LIMITED", message = "private fixture-token" }
                                    }
                                }
                                : (object)new
                                {
                                    data = new
                                    {
                                        repository = new
                                        {
                                            pullRequest = new { reviewThreads = new { nodes = Array.Empty<object>(), pageInfo = new { hasNextPage = false } } }
                                        }
                                    }
                                };
                        }
                        else
                        {
                            payload = Array.Empty<object>();
                        }

                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
                    })));
            registry.GetReviewOverviewProvider(ScmProvider.GitHub)
                .Returns(new GitHubReviewOverviewProvider(new GitHubConnectionVerifier(connections, httpFactory), httpFactory));
        }

        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<MeisterProPRDbContext>>(_ => new TestDbContextFactory(
            new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, options => options.UseVector()).Options));
        services.AddSingleton<IClientPullRequestOverviewStore>(provider =>
        {
            var durable = new ClientPullRequestOverviewStore(provider.GetRequiredService<IDbContextFactory<MeisterProPRDbContext>>(), clock);
            if (scenario is "persistedCapacity" or "persistedAuthority")
            {
                return new RefusingStore(
                    durable, () => { }, false, () =>
                    {
                        if (scenario == "persistedCapacity")
                        {
                            clock.Advance(TimeSpan.FromSeconds(2));
                        }
                        else
                        {
                            authorityCompleted = () =>
                            {
                                authorityCompleted = null;
                                clock.Advance(TimeSpan.FromSeconds(2));
                            };
                        }
                    }, scenario == "persistedCapacity");
            }

            return scenario is "capacityAuthorization" or "capacityTime"
                ? new RefusingStore(
                    durable, () =>
                    {
                        if (scenario == "capacityTime")
                        {
                            clock.Advance(TimeSpan.FromMinutes(16));
                        }
                        else
                        {
                            target = target with { ReviewTargetLifecycle = ReviewTargetLifecycle.Disabled };
                        }
                    })
                : durable;
        });
        services.AddSingleton(clients);
        services.AddSingleton(configurations);
        services.AddSingleton(connections);
        services.AddSingleton(scopes);
        services.AddSingleton(registry);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddScoped<ClientPullRequestOverviewService>();
        await using var root = services.BuildServiceProvider();
        await using var otherRoot = services.BuildServiceProvider();
        using var scopeA = root.CreateScope();
        using var scopeB = otherRoot.CreateScope();
        Assert.NotSame(
            scopeA.ServiceProvider.GetRequiredService<IClientPullRequestOverviewStore>(),
            scopeB.ServiceProvider.GetRequiredService<IClientPullRequestOverviewStore>());
        Assert.NotSame(
            scopeA.ServiceProvider.GetRequiredService<IDbContextFactory<MeisterProPRDbContext>>(),
            scopeB.ServiceProvider.GetRequiredService<IDbContextFactory<MeisterProPRDbContext>>());
        var request = new ClientPullRequestOverviewRequest(
            targetIds.Select((id, index) => new ClientPullRequestOverviewSource(
                id,
                scenario == "authenticationDenial" && index == 2 ? otherConnectionId : connectionId)).ToArray(), "grant-a");
        if (scenario == "provenance")
        {
            request = request with { Sources = connectionIds.Select(id => new ClientPullRequestOverviewSource(targetId, id)).ToArray() };
        }

        if (scenario == "cursorless")
        {
            release.SetResult();
            await Assert.ThrowsAsync<ClientPullRequestOverviewException>(() => scopeA.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>()
                .ReadAsync(client, request with { Page = 2 }, default));
            Assert.Empty(discovery.ReceivedCalls());
            return;
        }

        if (scenario == "nullSource")
        {
            await Assert.ThrowsAsync<ClientPullRequestOverviewException>(() => scopeA.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>()
                .ReadAsync(client, request with { Sources = [null!] }, default));
            Assert.Empty(discovery.ReceivedCalls());
            Assert.Empty(clients.ReceivedCalls());
            await using var nullDatabase = factory.CreateDbContext();
            Assert.False(await nullDatabase.ClientPullRequestOverviewCache.AnyAsync(row => row.ClientId == client));
            return;
        }

        using var cancellation = new CancellationTokenSource();
        var first = scopeA.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>().ReadAsync(client, request, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (scenario is "sourceHang" or "metadataHang" or "sourceLate" or "metadataLate")
        {
            release.SetResult();
            await providerHung.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var independent = await scopeB.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>()
                .ReadAsync(client, request with { Sources = [new(targetId, otherConnectionId)] }, default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, independent.TotalRows);
            Assert.False(first.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(15));
            ClientPullRequestOverviewPage completed;
            try
            {
                completed = await first.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                // Release the fixture's arbitrary provider task even when the owned wait regression fails.
                if (scenario.EndsWith("Late", StringComparison.Ordinal))
                {
                    hanging.TrySetResult([]);
                    hangingMetadata.TrySetResult(new ReviewOverviewDto(999, null, null, true, false));
                }
                else
                {
                    hanging.TrySetException(new HttpRequestException("Late provider failure."));
                    hangingMetadata.TrySetException(new HttpRequestException("Late provider failure."));
                }
            }

            Assert.True(providerToken.IsCancellationRequested);
            if (scenario.StartsWith("source", StringComparison.Ordinal))
            {
                Assert.Equal("timeout", Assert.Single(completed.Sources).FailureKind);
            }
            else
            {
                Assert.Equal("unavailable", Assert.Single(completed.Items).MetadataStatus);
            }

            var retry = await scopeB.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>().ReadAsync(client, request, default);
            Assert.Equal(completed.TotalRows, retry.TotalRows);
            if (scenario.StartsWith("metadata", StringComparison.Ordinal))
            {
                Assert.Null(Assert.Single(retry.Items).Metadata);
            }

            await discovery.Received(2).ListOpenReviewsAsync(
                client, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>());
            if (scenario.StartsWith("metadata", StringComparison.Ordinal))
            {
                await metadata.Received(2).GetOverviewAsync(client, Arg.Any<CodeReviewRef>(), Arg.Any<ReviewDiscoveryContext>(), Arg.Any<CancellationToken>());
            }

            return;
        }

        var second = scopeB.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>()
            .ReadAsync(client, request with { Binding = "grant-b" }, default);
        if (scenario == "rotation")
        {
            connection = connection with { UpdatedAt = now.AddSeconds(1) };
        }

        if (scenario == "lifecycle")
        {
            target = target with { ReviewTargetLifecycle = ReviewTargetLifecycle.Disabled };
        }

        if (scenario == "scope")
        {
            scope = scope with { IsEnabled = false, UpdatedAt = now.AddSeconds(1) };
        }

        if (scenario == "policy")
        {
            target = target with { ReviewTargetRevision = 2, RepoFilters = [target.RepoFilters[0] with { TargetBranchPatterns = ["release/*"] }] };
        }

        if (scenario == "tenant")
        {
            tenant = Guid.NewGuid();
        }

        if (scenario == "cancellation")
        {
            cancellation.Cancel();
        }

        release.SetResult();
        if (scenario is "rotation" or "lifecycle" or "scope" or "policy" or "tenant" or "capacityAuthorization" or "capacityTime")
        {
            await Assert.ThrowsAsync<ClientPullRequestOverviewException>(() => first);
            await Assert.ThrowsAsync<ClientPullRequestOverviewException>(() => second);
            return;
        }

        ClientPullRequestOverviewPage[] pages;
        if (scenario == "cancellation")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            pages = [await second];
        }
        else
        {
            pages = await Task.WhenAll(first, second);
        }

        if (scenario is "graphqlForbidden" or "graphqlRateLimited")
        {
            Assert.Equal(0, Assert.Single(pages[0].Items).Metadata!.TotalComments);
            Assert.Equal(1, graphqlRequests);
            var service = scopeA.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>();
            var other = scopeB.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>();
            var store = scopeA.ServiceProvider.GetRequiredService<IClientPullRequestOverviewStore>();
            var invalidation = await store.ReadInvalidationAsync(client, default);
            clock.Advance(TimeSpan.FromSeconds(61));
            refreshed = true;
            var failed = await service.ReadAsync(client, request with { Reload = true }, default);
            var row = Assert.Single(failed.Items);
            Assert.Equal("unavailable", row.MetadataStatus);
            if (scenario == "graphqlForbidden")
            {
                Assert.Null(row.Metadata);
                Assert.NotEqual(invalidation, await store.ReadInvalidationAsync(client, default));
                await Assert.ThrowsAsync<ClientPullRequestOverviewException>(() => other.ReadAsync(client, request with { Cursor = pages[0].Cursor }, default));
            }
            else
            {
                Assert.Equal(0, row.Metadata!.TotalComments);
                Assert.Equal(invalidation, await store.ReadInvalidationAsync(client, default));
                Assert.Equal(
                    0, Assert.Single((await other.ReadAsync(client, request with { Cursor = pages[0].Cursor }, default)).Items).Metadata!.TotalComments);
            }

            await using (var db = factory.CreateDbContext())
            {
                var failure = await db.ClientPullRequestOverviewCache.SingleAsync(entry => entry.ClientId == client && entry.Kind == "metadata");
                Assert.Equal(scenario == "graphqlForbidden" ? "accessDenied" : "throttled", failure.Failure);
                Assert.DoesNotContain("fixture-token", failure.Content ?? "");
                Assert.DoesNotContain("private", failure.Content ?? "");
            }

            await other.ReadAsync(client, request with { Reload = true }, default);
            Assert.Equal(2, graphqlRequests);
            clock.Advance(TimeSpan.FromSeconds(61));
            refreshed = false;
            Assert.Equal(0, Assert.Single((await other.ReadAsync(client, request with { Reload = true }, default)).Items).Metadata!.TotalComments);
            Assert.Equal(3, graphqlRequests);
            return;
        }

        if (scenario is "persistedDeadline" or "cursorResurrection" or "persistedCapacity" or "persistedAuthority")
        {
            var service = scopeA.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>();
            clock.Advance(TimeSpan.FromSeconds(870));
            refreshed = true;
            var limited = await service.ReadAsync(client, request with { Reload = true }, default);
            Assert.True(limited.ExpiresAt <= now.AddMinutes(15));
            if (scenario != "cursorResurrection")
            {
                clock.Advance(TimeSpan.FromSeconds(29));
                await Assert.ThrowsAsync<ClientPullRequestOverviewException>(() => service.ReadAsync(
                    client, request with { Cursor = limited.Cursor, Page = 2 }, default));
                if (scenario != "persistedAuthority")
                {
                    await using var db = factory.CreateDbContext();
                    Assert.False(
                        await db.ClientPullRequestOverviewCache.AnyAsync(row => row.ClientId == client && row.Kind == "page" && row.Key.EndsWith(":2")));
                }
            }
            else
            {
                clock.Advance(TimeSpan.FromSeconds(31));
                var replacement = await service.ReadAsync(client, request, default);
                Assert.NotEmpty(replacement.Cursor);
                await Assert.ThrowsAsync<ClientPullRequestOverviewException>(() => service.ReadAsync(
                    client, request with { Cursor = limited.Cursor }, default));
            }

            return;
        }

        if (scenario is "originalListing" or "originalMetadata")
        {
            var service = scopeA.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>();
            var original = pages[0].Items[0];
            clock.Advance(TimeSpan.FromMinutes(14));
            refreshed = true;
            var retained = await service.ReadAsync(client, request with { Reload = true }, default);
            Assert.Equal(scenario == "originalListing" ? original.ListedAt : clock.GetUtcNow(), retained.Items[0].ListedAt);
            if (scenario == "originalMetadata")
            {
                Assert.Equal(original.MetadataAt, retained.Items[0].MetadataAt);
            }

            Assert.True(retained.ExpiresAt <= now.AddMinutes(15));
            var immutable = await service.ReadAsync(client, request with { Cursor = retained.Cursor }, default);
            Assert.Equal(retained.TotalRows, immutable.TotalRows);
            Assert.Equal(retained.Items[0].ListedAt, immutable.Items[0].ListedAt);
            Assert.Equal(retained.Items[0].MetadataAt, immutable.Items[0].MetadataAt);
            Assert.Equal(retained.Items[0].Metadata, immutable.Items[0].Metadata);
            clock.Advance(TimeSpan.FromMinutes(1));
            await Assert.ThrowsAsync<ClientPullRequestOverviewException>(() => service.ReadAsync(client, request with { Cursor = retained.Cursor }, default));
            return;
        }

        if (scenario == "provenance")
        {
            var service = scopeA.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>();
            clock.Advance(TimeSpan.FromSeconds(10));
            var complete = await service.ReadAsync(client, request with { Cursor = pages[0].Cursor, LoadMore = true }, default);
            var original = clock.GetUtcNow();
            clock.Advance(TimeSpan.FromSeconds(60));
            refreshed = true;
            var newer = await service.ReadAsync(client, request with { Cursor = complete.Cursor, Reload = true }, default);
            Assert.Equal("head-2", Assert.Single(newer.Items).HeadSha);
            clock.Advance(TimeSpan.FromSeconds(61));
            empty = true;
            var survived = await service.ReadAsync(client, request with { Cursor = newer.Cursor, Reload = true }, default);
            var row = Assert.Single(survived.Items);
            Assert.Equal("head-1", row.HeadSha);
            Assert.Equal(original, row.ListedAt);
            Assert.Equal(connectionIds[8], row.ConnectionId);
            Assert.Equal(5, row.Metadata!.TotalComments);
            Assert.Equal(new ClientPullRequestOverviewSource(targetId, connectionIds[8]), Assert.Single(row.Associations));
            Assert.Equal(original, survived.Sources.Single(source => source.ConnectionId == connectionIds[8]).ListedAt);
            return;
        }

        if (scenario == "coverageCaller")
        {
            var service = scopeA.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>();
            var complete = await service.ReadAsync(client, request with { Cursor = pages[0].Cursor, LoadMore = true }, default);
            clock.Advance(TimeSpan.FromSeconds(61));
            var narrow = await scopeB.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>().ReadAsync(client, request, default);
            Assert.Equal(8, narrow.TotalRows);
            var reload = await service.ReadAsync(client, request with { Cursor = complete.Cursor, Reload = true }, default);
            Assert.Equal(10, reload.TotalRows);
            Assert.Equal(10, reload.Sources.Count);
            return;
        }

        if (scenario == "responseCapacity")
        {
            var service = scopeA.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>();
            var cumulative = pages[0];
            while (cumulative.NextCursor is not null)
            {
                // Only the final four sources contain large text; all values remain within provider row bounds.
                refreshed = cumulative.AttemptedSources >= 96;
                clock.Advance(TimeSpan.FromSeconds(1));
                cumulative = await service.ReadAsync(client, request with { Cursor = cumulative.Cursor, LoadMore = true }, default);
            }

            var capacity = await service.ReadAsync(client, request with { Cursor = cumulative.Cursor, PageSize = 100 }, default);
            Assert.NotEmpty(cumulative.Cursor);
            Assert.All(cumulative.Items, row => Assert.Equal(100, row.Associations.Count));
            var oversized = cumulative with
            {
                PageSize = 100, Items = Enumerable.Range(1, 100).Select(number => cumulative.Items[0] with { Number = number }).ToArray()
            };
            Assert.True(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                    oversized, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)).Length > 2 * 1024 * 1024);
            Assert.Equal("capacity", capacity.Status);
            Assert.Null(capacity.TotalRows);
            Assert.Empty(capacity.Items);
            Assert.Equal("", capacity.Cursor);
            Assert.Null(capacity.NextCursor);
            Assert.Equal(100, capacity.Sources.Count);
            Assert.NotNull(capacity.NextRefreshAt);
            Assert.InRange(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                    capacity, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)).Length, 1, 2 * 1024 * 1024);
            await using var capacityDatabase = factory.CreateDbContext();
            var retained = await capacityDatabase.ClientPullRequestOverviewCache.Where(record => record.ClientId == client).ToArrayAsync();
            Assert.InRange(retained.Sum(record => (long)record.ContentBytes + 1024), 1, 64 * 1024 * 1024);
            Assert.All(retained.Where(record => record.Kind == "generation"), record => Assert.InRange(record.ContentBytes, 1, 16 * 1024 * 1024));
            return;
        }

        if (scenario == "tie")
        {
            Assert.All(pages, page => Assert.Equal("a", Assert.Single(page.Items).SourceBranch));
            return;
        }

        if (scenario == "authenticationDenial")
        {
            Assert.Equal(6, pages[0].TotalRows);
            clock.Advance(TimeSpan.FromSeconds(61));
            refreshed = true;
            var service = scopeA.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>();
            var other = scopeB.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>();
            var denied = await service.ReadAsync(client, request with { Reload = true }, default);
            Assert.Equal(2, denied.TotalRows);
            Assert.All(denied.Items, row => Assert.Equal(otherConnectionId, row.ConnectionId));
            Assert.All(denied.Items, row => Assert.Equal(0, row.Metadata!.TotalComments));
            await Assert.ThrowsAsync<ClientPullRequestOverviewException>(() => other.ReadAsync(client, request with { Cursor = pages[0].Cursor }, default));
            var cooldown = await other.ReadAsync(client, request with { Reload = true }, default);
            Assert.Equal(2, cooldown.TotalRows);
            Assert.All(cooldown.Items, row => Assert.Equal(otherConnectionId, row.ConnectionId));
            clock.Advance(TimeSpan.FromSeconds(61));
            refreshed = false;
            Assert.Equal(6, (await other.ReadAsync(client, request with { Reload = true }, default)).TotalRows);
            await Assert.ThrowsAsync<ClientPullRequestOverviewException>(() => service.ReadAsync(client, request with { Cursor = pages[0].Cursor }, default));
            return;
        }

        if (scenario == "duplicate")
        {
            clock.Advance(TimeSpan.FromSeconds(61));
            refreshed = true;
            var merged = await scopeB.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>()
                .ReadAsync(client, request with { Cursor = pages[0].Cursor, LoadMore = true }, default);
            var row = Assert.Single(merged.Items);
            Assert.Equal("head-2", row.HeadSha);
            Assert.Equal("2", row.ProviderRevisionId);
            Assert.Equal(10, row.Associations.Count);
            Assert.Equal(9, row.Metadata!.TotalComments);
            clock.Advance(TimeSpan.FromSeconds(61));
            refreshed = false;
            var reload = await scopeA.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>()
                .ReadAsync(client, request with { Cursor = merged.Cursor, Reload = true }, default);
            Assert.Equal("head-1", Assert.Single(reload.Items).HeadSha);
            Assert.Equal(10, reload.Items[0].Associations.Count);
            return;
        }

        if (scenario == "coverage")
        {
            var service = scopeA.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>();
            var complete = await service.ReadAsync(client, request with { Cursor = pages[0].Cursor, LoadMore = true }, default);
            Assert.Equal(10, complete.TotalRows);
            Assert.Equal(10, complete.AttemptedSources);
            clock.Advance(TimeSpan.FromSeconds(61));
            refreshed = true;
            var reload = await service.ReadAsync(client, request with { Cursor = complete.Cursor, Reload = true }, default);
            Assert.Equal(9, reload.TotalRows);
            Assert.Equal(8, reload.AttemptedSources);
            Assert.Equal(10, reload.Sources.Count);
            Assert.DoesNotContain(reload.Items, row => row.TargetId == targetIds[0]);
            Assert.Contains(reload.Items, row => row.TargetId == targetIds[1]);
            Assert.Equal("failed", reload.Sources.Single(source => source.TargetId == targetIds[1]).Status);
            Assert.Equal(2, reload.Sources.Count(source => source.Status == "pending"));
            var continued = await service.ReadAsync(client, request with { Cursor = reload.Cursor, LoadMore = true }, default);
            Assert.Equal(9, continued.TotalRows);
            Assert.Equal(10, continued.Sources.Count);
            Assert.Equal(10, continued.AttemptedSources);
            clock.Advance(TimeSpan.FromMinutes(14) - TimeSpan.FromSeconds(2));
            crossExpiry = 1;
            var afterExpiry = await service.ReadAsync(client, request with { Reload = true }, default);
            Assert.Equal(8, afterExpiry.Sources.Count);
            Assert.Equal(6, afterExpiry.TotalRows);
            Assert.DoesNotContain(afterExpiry.Items, row => row.TargetId == targetIds[1]);
            Assert.True(afterExpiry.ExpiresAt > clock.GetUtcNow());
            await using var retainedDatabase = factory.CreateDbContext();
            var persisted = await retainedDatabase.ClientPullRequestOverviewCache.SingleAsync(record => record.ClientId == client
                                                                                                        && record.Kind == "generation" &&
                                                                                                        record.ObservedAt == afterExpiry.CreatedAt);
            Assert.InRange((persisted.ExpiresAt - afterExpiry.ExpiresAt).Ticks, -9, 9);
            return;
        }

        if (scenario == "sourceFailure")
        {
            Assert.All(
                pages, page =>
                {
                    Assert.Empty(page.Items);
                    Assert.Equal("failed", Assert.Single(page.Sources).Status);
                });
            var retry = await scopeA.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>().ReadAsync(
                client, request with { Binding = "filter-c", Reload = true }, default);
            Assert.Empty(retry.Items);
            await discovery.Received(1).ListOpenReviewsAsync(
                client, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>());
            return;
        }

        Assert.All(
            pages, page =>
            {
                Assert.Equal(Math.Min(25, reviewCount), page.Items.Count);
                Assert.All(
                    page.Items, row =>
                    {
                        if (scenario == "metadataFailure")
                        {
                            Assert.Null(row.Metadata);
                            Assert.Equal("unavailable", row.MetadataStatus);
                        }
                        else
                        {
                            Assert.Equal(0, row.Metadata!.TotalComments);
                            Assert.Equal(scenario == "incomplete" ? "incomplete" : "available", row.MetadataStatus);
                        }
                    });
            });
        await discovery.Received(1).ListOpenReviewsAsync(
            client, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>());
        await metadata.Received(Math.Min(25, reviewCount)).GetOverviewAsync(
            client, Arg.Any<CodeReviewRef>(), Arg.Any<ReviewDiscoveryContext>(), Arg.Any<CancellationToken>());
        if (scenario == "reuse")
        {
            for (var index = 0; index < 5; index++)
            {
                await scopeB.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>().ReadAsync(client, request, default);
            }

            await using var db = factory.CreateDbContext();
            Assert.Equal(2, await db.ClientPullRequestOverviewCache.CountAsync(row => row.ClientId == client && row.Kind == "generation"));
            await discovery.Received(1).ListOpenReviewsAsync(
                client, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>());
        }

        if (scenario == "revision")
        {
            clock.Advance(TimeSpan.FromSeconds(61));
            refreshed = true;
            var service = scopeA.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>();
            var newer = await service.ReadAsync(client, request with { Reload = true }, default);
            Assert.Equal("head-2", Assert.Single(newer.Items).HeadSha);
            Assert.Equal(9, newer.Items[0].Metadata!.TotalComments);
            var older = await service.ReadAsync(client, request with { Cursor = pages[0].Cursor }, default);
            Assert.Equal("head-1", Assert.Single(older.Items).HeadSha);
            Assert.Equal(0, older.Items[0].Metadata!.TotalComments);
            await metadata.Received(2).GetOverviewAsync(client, Arg.Any<CodeReviewRef>(), Arg.Any<ReviewDiscoveryContext>(), Arg.Any<CancellationToken>());
        }

        if (scenario is "sourceDenial" or "metadataDenial")
        {
            clock.Advance(TimeSpan.FromSeconds(61));
            refreshed = true;
            var service = scopeA.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>();
            var other = scopeB.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>();
            var denied = await service.ReadAsync(client, request with { Reload = true }, default);
            if (scenario == "sourceDenial")
            {
                Assert.Empty(denied.Items);
            }
            else
            {
                Assert.Null(Assert.Single(denied.Items).Metadata);
            }

            await Assert.ThrowsAsync<ClientPullRequestOverviewException>(() => other.ReadAsync(client, request with { Cursor = pages[0].Cursor }, default));
            var cooldown = await other.ReadAsync(client, request with { Reload = true }, default);
            if (scenario == "sourceDenial")
            {
                Assert.Empty(cooldown.Items);
            }
            else
            {
                Assert.Null(Assert.Single(cooldown.Items).Metadata);
            }

            clock.Advance(TimeSpan.FromSeconds(61));
            refreshed = false;
            var recovered = await other.ReadAsync(client, request with { Reload = true }, default);
            Assert.Equal(0, Assert.Single(recovered.Items).Metadata!.TotalComments);
            await Assert.ThrowsAsync<ClientPullRequestOverviewException>(() => service.ReadAsync(client, request with { Cursor = pages[0].Cursor }, default));
        }

        if (scenario == "stale")
        {
            clock.Advance(TimeSpan.FromSeconds(61));
            var navigation = request with { Cursor = pages[0].Cursor };
            var stale = await scopeA.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>().ReadAsync(client, navigation, default);
            Assert.True(stale.IsStale);
            Assert.Equal(pages[0].Items[0].MetadataAt, stale.Items[0].MetadataAt);
            clock.Advance(TimeSpan.FromMinutes(15));
            await Assert.ThrowsAsync<ClientPullRequestOverviewException>(() =>
                scopeA.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>().ReadAsync(client, navigation, default));
        }

        if (reviewCount > 25)
        {
            var next = request with { Cursor = pages[0].Cursor, Page = 2 };
            var pageTwo = await Task.WhenAll(
                scopeA.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>().ReadAsync(client, next, default),
                scopeB.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>().ReadAsync(client, next, default));
            Assert.All(pageTwo, page => Assert.Equal(5, page.Items.Count));
            await discovery.Received(1).ListOpenReviewsAsync(
                client, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>());
            await metadata.Received(30).GetOverviewAsync(client, Arg.Any<CodeReviewRef>(), Arg.Any<ReviewDiscoveryContext>(), Arg.Any<CancellationToken>());
        }
    }

    private sealed class GraphQlHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    [SkippableTheory]
    [InlineData("source")]
    [InlineData("m:metadata")]
    public async Task ActiveMaintenanceClearsExpiredPayloadWhileKeepingOwnerAndCooldown(string key)
    {
        fixture.SkipIfUnavailable();
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).Options);
        var clock = new FakeTimeProvider();
        var store = new ClientPullRequestOverviewStore(factory, clock);
        var client = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var original = await store.AcquireAsync(client, key, "revision", now, default);
        Assert.True(await store.CompleteAsync(client, key, original.Owner!.Value, "revision", "original", null, now, default));
        clock.SetUtcNow(now.AddMinutes(14).AddSeconds(-2));
        var retry = await store.AcquireAsync(client, key, "revision", now.AddMinutes(14).AddSeconds(-2), default);
        Assert.True(await store.CompleteAsync(client, key, retry.Owner!.Value, "revision", null, "unavailable", now.AddMinutes(14).AddSeconds(-2), default));
        clock.SetUtcNow(now.AddMinutes(15).AddSeconds(-1));
        var owner = await store.AcquireAsync(client, key, "revision", now.AddMinutes(15).AddSeconds(-1), default);
        Assert.NotNull(owner.Owner);
        clock.SetUtcNow(now.AddMinutes(15));
        await store.AcquireAsync(client, "maintenance", "revision", now.AddMinutes(15), default);
        await using (var db = factory.CreateDbContext())
        {
            var expired = await db.ClientPullRequestOverviewCache.SingleAsync(row => row.ClientId == client && row.Key == key);
            Assert.Null(expired.Content);
            Assert.Equal(0, expired.ContentBytes);
            Assert.Null(expired.ObservedAt);
            Assert.Equal(owner.Owner, expired.Owner);
            Assert.InRange((owner.NextRefreshAt - expired.NextRefreshAt).Ticks, -9, 9);
        }

        Assert.True(await store.CompleteAsync(client, key, owner.Owner!.Value, "revision", null, "timeout", now.AddMinutes(15), default));
        clock.SetUtcNow(now.AddMinutes(15).AddSeconds(1));
        var cooled = await store.AcquireAsync(client, key, "revision", now.AddMinutes(15).AddSeconds(1), default);
        Assert.Null(cooled.Owner);
        Assert.Null(cooled.Content);
        Assert.Equal("timeout", cooled.Failure);
    }

    [SkippableFact]
    public async Task RefusedDuplicateWritesCommitExpiredCleanupWithoutChangingExistingContent()
    {
        fixture.SkipIfUnavailable();
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).Options);
        var clock = new FakeTimeProvider();
        var store = new ClientPullRequestOverviewStore(factory, clock);
        var client = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var generation = Guid.NewGuid();
        Assert.True(await store.SaveGenerationAsync(client, generation, "current", "original", now, default));
        await using (var seed = factory.CreateDbContext())
        {
            seed.ClientPullRequestOverviewCache.Add(
                new()
                {
                    ClientId = client, Key = Guid.NewGuid().ToString("N"), Kind = "generation", Fingerprint = "expired", Content = "{}",
                    ContentBytes = 2, ObservedAt = now.AddMinutes(-16), ExpiresAt = now.AddMinutes(-1), NextRefreshAt = now.AddMinutes(-15)
                });
            await seed.SaveChangesAsync();
        }

        Assert.False(await store.SaveGenerationAsync(client, generation, "current", "replacement", now, default));
        await using var db = factory.CreateDbContext();
        Assert.False(await db.ClientPullRequestOverviewCache.AnyAsync(row => row.ClientId == client && row.Fingerprint == "expired"));
        Assert.Equal("original", await store.ReadGenerationAsync(client, generation, "current", now, default));
    }

    [SkippableFact]
    public async Task GenerationAndClientStorageBudgetsRejectExcessWithoutAffectingOtherClients()
    {
        fixture.SkipIfUnavailable();
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).Options);
        var store = new ClientPullRequestOverviewStore(factory, TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        var client = Guid.NewGuid();
        Assert.False(await store.SaveGenerationAsync(client, Guid.NewGuid(), "binding", new string('a', 16 * 1024 * 1024 + 1), now, default));
        var first = Guid.NewGuid();
        Assert.True(await store.SaveGenerationAsync(client, first, "binding", "{}", now, default));
        for (var index = 1; index < 128; index++)
        {
            Assert.True(await store.SaveGenerationAsync(client, Guid.NewGuid(), "binding", "{}", now, default));
        }

        Assert.False(await store.SaveGenerationAsync(client, Guid.NewGuid(), "binding", "{}", now, default));
        Assert.Equal("{}", await store.ReadGenerationAsync(client, first, "binding", now, default));
        var largeClient = Guid.NewGuid();
        var largeContent = new string('a', 15 * 1024 * 1024);
        for (var index = 0; index < 4; index++)
        {
            Assert.True(await store.SaveGenerationAsync(largeClient, Guid.NewGuid(), "binding", largeContent, now, default));
        }

        Assert.False(await store.SaveGenerationAsync(largeClient, Guid.NewGuid(), "binding", new string('a', 4 * 1024 * 1024), now, default));
        Assert.Equal("{}", await store.ReadGenerationAsync(client, first, "binding", now, default));
        var independent = Guid.NewGuid();
        var independentGeneration = Guid.NewGuid();
        Assert.True(await store.SaveGenerationAsync(independent, independentGeneration, "binding", "{}", now, default));
        Assert.Equal("{}", await store.ReadGenerationAsync(independent, independentGeneration, "binding", now, default));
    }

    [SkippableFact]
    public async Task IndependentStoresCoalesceRefreshAndFenceExpiredOwners()
    {
        fixture.SkipIfUnavailable();
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).Options);
        var clock = new FakeTimeProvider();
        var first = new ClientPullRequestOverviewStore(factory, clock);
        var second = new ClientPullRequestOverviewStore(factory, clock);
        var client = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var claims = await Task.WhenAll(
            first.AcquireAsync(client, "source", "revision-1", now, default),
            second.AcquireAsync(client, "source", "revision-1", now, default));
        Assert.Single(claims, claim => claim.Owner.HasValue);
        var owner = claims.Single(claim => claim.Owner.HasValue).Owner!.Value;
        clock.SetUtcNow(now.AddSeconds(21));
        var takeover = await second.AcquireAsync(client, "source", "revision-1", now.AddSeconds(21), default);
        Assert.NotNull(takeover.Owner);
        Assert.False(await first.CompleteAsync(client, "source", owner, "revision-1", "{}", null, now, default));
        Assert.True(await second.CompleteAsync(client, "source", takeover.Owner!.Value, "revision-1", "{}", null, now.AddSeconds(21), default));
        clock.SetUtcNow(now.AddSeconds(22));
        var cached = await first.AcquireAsync(client, "source", "revision-1", now.AddSeconds(22), default);
        Assert.Null(cached.Owner);
        Assert.Equal("{}", cached.Content);
    }

    [SkippableTheory]
    [InlineData("authenticationDenied")]
    [InlineData("accessDenied")]
    public async Task DenialsFenceAffectedKeysAndPreserveUnrelatedObservations(string failure)
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).Options;
        var clock = new FakeTimeProvider();
        var first = new ClientPullRequestOverviewStore(new TestDbContextFactory(options), clock);
        var second = new ClientPullRequestOverviewStore(new TestDbContextFactory(options), clock);
        var client = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var scope = new OverviewCacheScope(connection, "source-a");
        var sibling = new OverviewCacheScope(connection, "source-b");
        var unrelated = new OverviewCacheScope(Guid.NewGuid(), "source-c");
        var now = clock.GetUtcNow();
        foreach (var (key, provenance) in new[]
                     { ("source-a", scope), ("source-b", sibling), ("m:old-revision", scope), ("source-c", unrelated), ("m:unrelated", unrelated) })
        {
            var claim = await first.AcquireAsync(client, key, "fingerprint", now, default, provenance);
            Assert.True(await first.CompleteAsync(client, key, claim.Owner!.Value, "fingerprint", "original", null, now, default));
        }

        var stale = await first.AcquireAsync(client, "m:late", "fingerprint", now, default, scope);
        var denial = await second.AcquireAsync(client, "m:denial", "fingerprint", now, default, scope);
        Assert.True(await second.CompleteAsync(client, "m:denial", denial.Owner!.Value, "fingerprint", null, failure, now, default));
        var version = await second.ReadInvalidationAsync(client, default);
        Assert.NotNull(version);
        Assert.False(await first.CompleteAsync(client, "m:late", stale.Owner!.Value, "fingerprint", "late", null, now, default));
        Assert.False(await first.CompleteAsync(client, "m:late", stale.Owner!.Value, "fingerprint", null, "authenticationDenied", now, default));
        Assert.Equal(version, await first.ReadInvalidationAsync(client, default));
        clock.SetUtcNow(now.AddSeconds(1));
        var listing = await first.AcquireAsync(client, "source-a", "fingerprint", now.AddSeconds(1), default, scope);
        Assert.Equal(failure == "authenticationDenied" ? null : "original", listing.Content);
        Assert.Equal(
            failure == "authenticationDenied" ? null : "original",
            (await first.AcquireAsync(client, "source-b", "fingerprint", now.AddSeconds(1), default, sibling)).Content);
        Assert.Null((await first.AcquireAsync(client, "m:old-revision", "fingerprint", now.AddSeconds(1), default, scope)).Content);
        var newlyKeyed = await first.AcquireAsync(client, "m:new-revision", "fingerprint", now.AddSeconds(1), default, scope);
        Assert.Null(newlyKeyed.Owner);
        Assert.Null(newlyKeyed.Content);
        Assert.Equal("original", (await first.AcquireAsync(client, "source-c", "fingerprint", now.AddSeconds(1), default, unrelated)).Content);
        Assert.Equal("original", (await first.AcquireAsync(client, "m:unrelated", "fingerprint", now.AddSeconds(1), default, unrelated)).Content);
        clock.SetUtcNow(now.AddMinutes(16));
        await second.AcquireAsync(client, "cleanup", "fingerprint", now.AddMinutes(16), default);
        Assert.Equal(version, await first.ReadInvalidationAsync(client, default));
    }

    [SkippableTheory]
    [InlineData("generation")]
    [InlineData("page")]
    [InlineData("source")]
    public async Task CapacityRefusalsCommitBoundedCleanupWithoutPartialContent(string kind)
    {
        fixture.SkipIfUnavailable();
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).Options);
        var store = new ClientPullRequestOverviewStore(factory, TimeProvider.System);
        var client = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var db = factory.CreateDbContext())
        {
            db.ClientPullRequestOverviewCache.AddRange(
                Enumerable.Range(0, 257).Select(index =>
                    new MeisterDev.ProPR.Infrastructure.Data.Models.ClientPullRequestOverviewCacheRecord
                    {
                        ClientId = client, Key = "expired:" + index, Kind = "page", Fingerprint = "expired",
                        ExpiresAt = now.AddSeconds(-1),
                    }));
            if (kind == "source")
            {
                db.ClientPullRequestOverviewCache.AddRange(
                    Enumerable.Range(0, 12800).Select(index =>
                        new MeisterDev.ProPR.Infrastructure.Data.Models.ClientPullRequestOverviewCacheRecord
                        {
                            ClientId = client, Key = "source:" + index, Kind = "source", Fingerprint = "current",
                            ExpiresAt = now.AddMinutes(15), NextRefreshAt = now.AddSeconds(60),
                        }));
            }

            await db.SaveChangesAsync();
        }

        if (kind == "generation")
        {
            Assert.False(await store.SaveGenerationAsync(client, Guid.NewGuid(), "current", new string('a', 16 * 1024 * 1024 + 1), now, default));
        }
        else if (kind == "page")
        {
            Assert.False(await store.SavePageAsync(client, "new", "current", new string('a', 2 * 1024 * 1024 + 1), now.AddMinutes(15), now, default));
        }
        else
        {
            Assert.Equal("capacity", (await store.AcquireAsync(client, "new", "current", now, default)).Failure);
        }

        await using var read = factory.CreateDbContext();
        Assert.Equal(1, await read.ClientPullRequestOverviewCache.CountAsync(row => row.ClientId == client && row.Fingerprint == "expired"));
        Assert.False(await read.ClientPullRequestOverviewCache.AnyAsync(row => row.ClientId == client && row.Key == "new"));
        Assert.Equal(
            kind == "source" ? 12800 : 0, await read.ClientPullRequestOverviewCache.CountAsync(row => row.ClientId == client && row.Fingerprint == "current"));
    }

    [SkippableFact]
    public async Task FailureAndChangedRevisionConsumeTheLogicalSourceCooldown()
    {
        fixture.SkipIfUnavailable();
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).Options);
        var clock = new FakeTimeProvider();
        var store = new ClientPullRequestOverviewStore(factory, clock);
        var client = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var claim = await store.AcquireAsync(client, "source", "revision-1", now, default);
        Assert.True(await store.CompleteAsync(client, "source", claim.Owner!.Value, "revision-1", null, "unavailable", now, default));
        clock.SetUtcNow(now.AddSeconds(1));
        var cooldown = await store.AcquireAsync(client, "source", "revision-2", now.AddSeconds(1), default);
        Assert.Null(cooldown.Owner);
        Assert.Null(cooldown.Content);
        Assert.InRange((cooldown.NextRefreshAt - now.AddSeconds(60)).Ticks, -9, 9);
        clock.SetUtcNow(now.AddSeconds(61));
        var renewed = await store.AcquireAsync(client, "source", "revision-2", now.AddSeconds(61), default);
        Assert.NotNull(renewed.Owner);
        Assert.True(await store.CompleteAsync(client, "source", renewed.Owner!.Value, "revision-2", "{}", null, now.AddSeconds(61), default));
        clock.SetUtcNow(now.AddSeconds(122));
        var failedRefresh = await store.AcquireAsync(client, "source", "revision-2", now.AddSeconds(122), default);
        Assert.True(await store.CompleteAsync(client, "source", failedRefresh.Owner!.Value, "revision-2", null, "unavailable", now.AddSeconds(122), default));
        clock.SetUtcNow(now.AddSeconds(123));
        var retained = await store.AcquireAsync(client, "source", "revision-2", now.AddSeconds(123), default);
        Assert.Equal("{}", retained.Content);
        Assert.Equal("unavailable", retained.Failure);
        Assert.InRange((retained.ObservedAt!.Value - now.AddSeconds(61)).Ticks, -9, 9);
    }

    [SkippableFact]
    public async Task GenerationsAreImmutableOwnedAndExpireAfterFifteenMinutes()
    {
        fixture.SkipIfUnavailable();
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).Options);
        var clock = new FakeTimeProvider();
        var store = new ClientPullRequestOverviewStore(factory, clock);
        var client = Guid.NewGuid();
        var generation = Guid.NewGuid();
        var now = clock.GetUtcNow();
        Assert.True(await store.SaveGenerationAsync(client, generation, "binding", "{\"items\":[]}", now, default));
        Assert.False(await store.SaveGenerationAsync(client, generation, "binding", "changed", now, default));
        Assert.NotNull(await store.ReadGenerationAsync(client, generation, "binding", now.AddMinutes(14), default));
        Assert.Null(await store.ReadGenerationAsync(Guid.NewGuid(), generation, "binding", now, default));
        Assert.Null(await store.ReadGenerationAsync(client, generation, "changed", now, default));
        Assert.Null(await store.ReadGenerationAsync(client, generation, "binding", now.AddMinutes(15), default));
    }

    [SkippableTheory]
    [InlineData("acquire")]
    [InlineData("complete")]
    [InlineData("page")]
    public async Task ContendedCacheDecisionsUseConfiguredClockAfterLock(string operation)
    {
        fixture.SkipIfUnavailable();
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).Options);
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(now);
        using var services = new ServiceCollection().AddSingleton<IDbContextFactory<MeisterProPRDbContext>>(factory)
            .AddSingleton<TimeProvider>(clock).BuildServiceProvider();
        var store = ActivatorUtilities.CreateInstance<ClientPullRequestOverviewStore>(services);
        var client = Guid.NewGuid();
        var claim = await store.AcquireAsync(client, "existing", "binding", now, default);
        var generation = Guid.NewGuid();
        Assert.True(await store.SaveGenerationAsync(client, generation, "binding", "{}", now, default, now.AddSeconds(20)));
        await using var blocker = factory.CreateDbContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({'p' + client.ToString("N")}, 0))");
        var pending = operation switch
        {
            "complete" => store.CompleteAsync(client, "existing", claim.Owner!.Value, "binding", "late", null, now, default),
            "page" => store.SavePageAsync(client, $"page:{generation:N}:25:1", "binding", "{}", now.AddSeconds(20), now, default),
            _ => Acquire()
        };

        async Task<bool> Acquire()
        {
            var acquired = await store.AcquireAsync(client, "new", "binding", now, default);
            Assert.Equal(clock.GetUtcNow().AddSeconds(20), acquired.LeaseUntil);
            Assert.Equal(clock.GetUtcNow().AddSeconds(60), acquired.NextRefreshAt);
            return true;
        }

        await using var observer = factory.CreateDbContext();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await observer.Database.SqlQuery<int>(
                       $"SELECT count(*)::integer AS \"Value\" FROM pg_stat_activity WHERE wait_event = 'advisory' AND query LIKE '%hashtextextended%'")
                   .AnyAsync(count => count > 0, deadline.Token))
        {
            await Task.Delay(20, deadline.Token);
        }

        clock.Advance(TimeSpan.FromSeconds(21));
        await transaction.CommitAsync();
        Assert.Equal(operation == "acquire", await pending);
    }

    [SkippableFact]
    public async Task PagePublicationRequiresLiveOwnedGeneration()
    {
        fixture.SkipIfUnavailable();
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).Options);
        var store = new ClientPullRequestOverviewStore(factory, TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        var client = Guid.NewGuid();
        Assert.False(await store.SavePageAsync(client, $"page:{Guid.NewGuid():N}:25:1", "binding", "{}", now.AddMinutes(15), now, default));
        await using var db = factory.CreateDbContext();
        Assert.False(await db.ClientPullRequestOverviewCache.AnyAsync(row => row.ClientId == client && row.Kind == "page"));
    }

    [SkippableFact]
    public async Task EquivalentActiveGenerationsShareOneIncarnationAndReplacementCannotRestoreExpiredCursor()
    {
        fixture.SkipIfUnavailable();
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).Options);
        var clock = new FakeTimeProvider();
        var first = new ClientPullRequestOverviewStore(factory, clock);
        var second = new ClientPullRequestOverviewStore(factory, clock);
        var client = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var shared = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(index => (index % 2 == 0 ? first : second)
                .GetOrCreateGenerationAsync(client, "binding", "equivalent", "immutable", now.AddMinutes(15), now, default)));
        var generation = Assert.Single(shared.Select(item => item!.Id).Distinct());
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(1, await db.ClientPullRequestOverviewCache.CountAsync(row => row.ClientId == client && row.Kind == "generation"));
        }

        Assert.False(await first.SavePageAsync(client, $"page:{generation:N}:25:1", "foreign", "{}", now.AddMinutes(15), now, default));
        Assert.False(await first.SavePageAsync(Guid.NewGuid(), $"page:{generation:N}:25:1", "binding", "{}", now.AddMinutes(15), now, default));
        Assert.True(await first.SavePageAsync(client, $"page:{generation:N}:25:1", "binding", "{}", now.AddSeconds(30), now, default));
        var persisted = await first.ReadGenerationStateAsync(client, generation, "binding", now, default);
        Assert.Equal("immutable", persisted!.Content);
        Assert.Equal(now.AddSeconds(30), persisted.ExpiresAt);
        var retained = await second.GetOrCreateGenerationAsync(client, "binding", "equivalent", "replacement", now.AddMinutes(15), now, default);
        Assert.Equal(persisted, retained);
        clock.Advance(TimeSpan.FromSeconds(31));
        var replacement = await first.GetOrCreateGenerationAsync(client, "binding", "equivalent", "new metadata", now.AddMinutes(15), now, default);
        Assert.NotEqual(generation, replacement!.Id);
        Assert.Null(await second.ReadGenerationStateAsync(client, generation, "binding", now, default));
        Assert.Equal("new metadata", replacement.Content);
        await using var read = factory.CreateDbContext();
        Assert.Equal(1, await read.ClientPullRequestOverviewCache.CountAsync(row => row.ClientId == client && row.Kind == "generation"));
    }

    private sealed class RefusingStore(
        IClientPullRequestOverviewStore inner,
        Action mutate,
        bool refuseGeneration = true,
        Action? afterPage = null,
        bool refusePage = false) : IClientPullRequestOverviewStore
    {
        public Task<string?> ReadInvalidationAsync(Guid client, CancellationToken ct) => inner.ReadInvalidationAsync(client, ct);

        public Task<OverviewSourceClaim> AcquireAsync(
            Guid client, string key, string fingerprint, DateTimeOffset now, CancellationToken ct, OverviewCacheScope? scope = null) =>
            inner.AcquireAsync(client, key, fingerprint, now, ct, scope);

        public Task<bool> CompleteAsync(
            Guid client, string key, Guid owner, string fingerprint, string? content, string? failure, DateTimeOffset now, CancellationToken ct) =>
            inner.CompleteAsync(client, key, owner, fingerprint, content, failure, now, ct);

        public Task<bool> SaveGenerationAsync(
            Guid client, Guid generation, string binding, string content, DateTimeOffset now, CancellationToken ct, DateTimeOffset? expiresAt = null)
        {
            mutate();
            return Task.FromResult(false);
        }

        public Task<string?> ReadGenerationAsync(Guid client, Guid generation, string binding, DateTimeOffset now, CancellationToken ct) =>
            inner.ReadGenerationAsync(client, generation, binding, now, ct);

        public Task<OverviewGeneration?> ReadGenerationStateAsync(Guid client, Guid generation, string binding, DateTimeOffset now, CancellationToken ct) =>
            inner.ReadGenerationStateAsync(client, generation, binding, now, ct);

        public Task<OverviewGeneration?> GetOrCreateGenerationAsync(
            Guid client, string binding, string equivalence, string content, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken ct)
        {
            if (!refuseGeneration)
            {
                return inner.GetOrCreateGenerationAsync(client, binding, equivalence, content, expiresAt, now, ct);
            }

            mutate();
            return Task.FromResult<OverviewGeneration?>(null);
        }

        public Task<string?> ReadLatestGenerationAsync(Guid client, string binding, DateTimeOffset now, CancellationToken ct) =>
            inner.ReadLatestGenerationAsync(client, binding, now, ct);

        public async Task<bool> SavePageAsync(
            Guid client, string key, string binding, string content, DateTimeOffset expiry, DateTimeOffset now, CancellationToken ct)
        {
            if (key.EndsWith(":2") && refusePage)
            {
                afterPage?.Invoke();
                return false;
            }

            var result = await inner.SavePageAsync(client, key, binding, content, expiry, now, ct);
            if (key.EndsWith(":2"))
            {
                afterPage?.Invoke();
            }

            return result;
        }

        public Task<string?> ReadPageAsync(Guid client, string key, string binding, DateTimeOffset now, CancellationToken ct) =>
            inner.ReadPageAsync(client, key, binding, now, ct);
    }
}
