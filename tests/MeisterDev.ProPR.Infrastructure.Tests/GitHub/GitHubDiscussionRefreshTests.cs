// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using MeisterDev.ProPR.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.GitHub;

public sealed class GitHubDiscussionRefreshTests
{
    private const string RepositoryId = "123456789";

    [Fact]
    public async Task DiscussionOnlyRefreshOfAStateKeyedByTheNumericRepositoryIdReadsThePullRequest()
    {
        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var requested = new ConcurrentQueue<string>();
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient("GitHubProvider").Returns(_ => new HttpClient(
            new StubHttpMessageHandler(request =>
            {
                requested.Enqueue(request.RequestUri!.AbsolutePath);
                return request.RequestUri.AbsolutePath switch
                {
                    "/user" => Json(new { login = "meister-dev" }),
                    "/repositories/" + RepositoryId => Json(new { id = long.Parse(RepositoryId), full_name = "acme/widgets" }),
                    "/repos/acme/widgets/pulls/7" => Json(
                        new
                        {
                            title = "Widgets", state = "open", merged_at = (string?)null,
                            head = new { @ref = "feature", sha = "head-sha" }, @base = new { @ref = "main", sha = "base-sha" },
                            requested_reviewers = Array.Empty<object>(),
                        }),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                };
            })));
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalConnectionByIdAsync(clientId, connectionId, Arg.Any<CancellationToken>()).Returns(
            new ClientScmConnectionCredentialDto(
                connectionId, clientId, ScmProvider.GitHub, "https://github.com", ScmAuthenticationKind.PersonalAccessToken, "GitHub", "ghp_test",
                true));
        connections.GetByIdAsync(clientId, connectionId, Arg.Any<CancellationToken>()).Returns(
            new ClientScmConnectionDto(
                connectionId, clientId, ScmProvider.GitHub, "https://github.com", ScmAuthenticationKind.PersonalAccessToken, "GitHub", true,
                "verified", null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        var registry = LocalScmPolicies.CreateRuntimeSubstitute();
        registry.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(call => LocalScmPolicies.Registry.GetReviewSourcePolicy(call.Arg<ScmProvider>()));
        registry.GetCodeReviewQueryService(ScmProvider.GitHub)
            .Returns(new GitHubCodeReviewQueryService(new GitHubConnectionVerifier(connections, http), http));

        // A GitHub target is keyed by the numeric repository id that discovery, webhooks and by-coordinates intake capture.
        var state = new DiscussionSynchronizationState
        {
            Id = Guid.NewGuid(), ClientId = clientId, ConnectionId = connectionId, Provider = ScmProvider.GitHub,
            ProviderScopePath = "https://github.com", ProviderProjectKey = "acme", RepositoryId = RepositoryId, PullRequestId = 7,
            RequestedGeneration = 1, FullRefreshRequested = true,
        };
        var store = Substitute.For<IDiscussionSynchronizationStore>();
        var lease = Substitute.For<IDiscussionSynchronizationLease>();
        lease.State.Returns(state);
        lease.ObservedAt.Returns(DateTimeOffset.UtcNow);
        store.TryAcquireAsync(
                Arg.Any<PullRequestThreadSnapshotTarget>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(lease);
        var reader = Substitute.For<IPullRequestThreadSnapshotReader>();
        reader.ReadAsync(Arg.Any<PullRequestThreadSnapshotTarget>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<CancellationToken>())
            .Returns(new PullRequestThreadSnapshot(DateTimeOffset.UtcNow, [], [], true));
        var service = new DiscussionSynchronizationService(
            store, reader, connections, registry, NullLogger<DiscussionSynchronizationService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new DiscussionSynchronizationOptions()));

        // The refresh worker rebuilds the repository from the stored target, which holds no repository name.
        var repository = registry.GetReviewSourcePolicy(state.Provider).CreateCapturedRepository(
            state.ProviderScopePath, state.RepositoryId, state.ProviderProjectKey, state.RepositoryId);
        var observation = await service.ObserveAsync(
            new PullRequestSynchronizationRequest
            {
                ActivationSource = PullRequestActivationSource.Crawl, SummaryLabel = "discussion refresh", ClientId = clientId,
                ProviderScopePath = state.ProviderScopePath, ProviderProjectKey = state.ProviderProjectKey, RepositoryId = state.RepositoryId,
                PullRequestId = state.PullRequestId, PullRequestStatus = PrStatus.Active, Provider = state.Provider, Host = repository.Host,
                ConnectionId = connectionId, Repository = repository,
                CodeReview = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "7", 7),
                AllowReviewSubmission = false, DiscussionOnly = true,
            });

        Assert.Equal(PrStatus.Active, observation.ObservedPullRequestStatus);
        Assert.Equal("head-sha", observation.ObservedReviewRevision?.HeadSha);
        Assert.DoesNotContain("/repos/acme/" + RepositoryId + "/pulls/7", requested);
    }

    private static HttpResponseMessage Json<T>(T payload) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload)) };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
