// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers;

/// <summary>
///     What the mention scan learns from GitHub about the repositories a configuration claims, and what each
///     way the read can fail costs.
/// </summary>
public sealed class GitHubActivePrFetcherTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateTimeOffset Watermark = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OnlyPullRequestsUpdatedSinceTheWatermarkAreReturned()
    {
        var requested = new List<string>();
        var sut = CreateFetcher(request =>
        {
            requested.Add(request.RequestUri!.AbsoluteUri);
            return Respond(
                request, PullRequestPage(
                    (7, Watermark.AddMinutes(30)),
                    (6, Watermark.AddMinutes(10)),
                    (5, Watermark.AddMinutes(-10))));
        });

        var discovery = await sut.GetRecentlyUpdatedPullRequestsAsync(Query());
        var result = discovery.PullRequests;

        Assert.Equal([7, 6], result.Select(pullRequest => pullRequest.PullRequestId));

        // A tick that read everything it was asked about, which is what lets the caller close its window.
        Assert.True(discovery.IsComplete);

        // Sorted newest first and stopped at the first entry past the watermark: one request, not a walk
        // through the repository's whole history.
        Assert.Single(requested, uri => uri.Contains("/pulls?", StringComparison.Ordinal));
        Assert.Contains(
            requested,
            uri => uri.Contains("state=open", StringComparison.Ordinal)
                   && uri.Contains("sort=updated", StringComparison.Ordinal)
                   && uri.Contains("direction=desc", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EachPullRequestIsReportedUnderTheClaimedIdentifier()
    {
        var sut = CreateFetcher(request => Respond(request, PullRequestPage((7, Watermark.AddMinutes(5)))));

        var result = (await sut.GetRecentlyUpdatedPullRequestsAsync(Query([new ClaimedRepositoryRef("909", "acme/platform")]))).PullRequests;

        Assert.Equal("909", Assert.Single(result).RepositoryId);
    }

    [Fact]
    public async Task ARepositoryWithNoOpenPullRequestsIsEmptyRatherThanAFailure()
    {
        var sut = CreateFetcher(request => Respond(request, PullRequestPage()));

        var result = (await sut.GetRecentlyUpdatedPullRequestsAsync(Query())).PullRequests;

        Assert.Empty(result);
    }

    [Fact]
    public async Task ARepositoryThatCannotBeReadDoesNotCostTheOthersTheirScan()
    {
        var sut = CreateFetcher(request =>
            request.RequestUri!.AbsoluteUri.Contains("/repos/acme/gone/", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Respond(request, PullRequestPage((7, Watermark.AddMinutes(5)))));

        var discovery = await sut.GetRecentlyUpdatedPullRequestsAsync(
            Query(
            [
                new ClaimedRepositoryRef("acme/gone"),
                new ClaimedRepositoryRef("acme/platform"),
            ]));

        Assert.Equal("acme/platform", Assert.Single(discovery.PullRequests).RepositoryId);

        // Reported as partial, which is what holds the caller's watermark open over the repository that could
        // not be read. A question waiting in it is waiting whether or not the others were fine.
        Assert.False(discovery.IsComplete);
    }

    /// <summary>
    ///     GitHub reports a secondary rate limit as 403 with a Retry-After, which is a wait rather than a
    ///     refusal. The scan keeps what it read and stops asking until the next tick.
    /// </summary>
    [Fact]
    public async Task ARateLimitIsAbsorbedAndStopsTheRemainingRepositories()
    {
        var attempted = new List<string>();
        var sut = CreateFetcher(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            attempted.Add(uri);

            if (uri.Contains("/repos/acme/platform/", StringComparison.Ordinal))
            {
                return Respond(request, PullRequestPage((7, Watermark.AddMinutes(5))));
            }

            if (uri.Contains("/repos/acme/tooling/", StringComparison.Ordinal))
            {
                var throttled = new HttpResponseMessage(HttpStatusCode.Forbidden);
                throttled.Headers.Add("x-ratelimit-remaining", "0");
                return throttled;
            }

            return Respond(request, PullRequestPage());
        });

        var discovery = await sut.GetRecentlyUpdatedPullRequestsAsync(
            Query(
            [
                new ClaimedRepositoryRef("acme/platform"),
                new ClaimedRepositoryRef("acme/tooling"),
                new ClaimedRepositoryRef("acme/docs"),
            ]));

        Assert.Equal("acme/platform", Assert.Single(discovery.PullRequests).RepositoryId);
        Assert.DoesNotContain(attempted, uri => uri.Contains("/repos/acme/docs/", StringComparison.Ordinal));

        // The repositories the limit cut off keep their window open, so the next tick asks about them again
        // rather than stepping over whatever was asked in it.
        Assert.False(discovery.IsComplete);
    }

    [Fact]
    public async Task AnEnterpriseServerHostIsReachedAtItsOwnAddress()
    {
        var requested = new List<string>();
        var sut = CreateFetcher(
            request =>
            {
                requested.Add(request.RequestUri!.AbsoluteUri);
                return Respond(request, PullRequestPage((7, Watermark.AddMinutes(5))));
            },
            hostBaseUrl: "https://github.enterprise.example");

        await sut.GetRecentlyUpdatedPullRequestsAsync(Query(scopePath: "https://github.enterprise.example"));

        Assert.Contains(
            requested,
            uri => uri.StartsWith("https://github.enterprise.example/api/v3/", StringComparison.Ordinal));
        Assert.DoesNotContain(requested, uri => uri.Contains("api.github.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AConfigurationClaimingNothingAsksGitHubNothing()
    {
        var attempted = 0;
        var sut = CreateFetcher(request =>
        {
            attempted++;
            return Respond(request, PullRequestPage());
        });

        var result = (await sut.GetRecentlyUpdatedPullRequestsAsync(Query([]))).PullRequests;

        Assert.Empty(result);
        Assert.Equal(0, attempted);
    }

    private static ActivePullRequestQuery Query(
        IReadOnlyList<ClaimedRepositoryRef>? repositories = null,
        string scopePath = "https://github.com")
    {
        return new ActivePullRequestQuery(
            ScmProvider.GitHub,
            scopePath,
            repositories ?? [new ClaimedRepositoryRef("acme/platform")],
            Watermark,
            ClientId);
    }

    private static object[] PullRequestPage(params (int Number, DateTimeOffset UpdatedAt)[] pullRequests)
    {
        return pullRequests
            .Select(pullRequest => (object)new
            {
                number = pullRequest.Number,
                updated_at = pullRequest.UpdatedAt,
                created_at = pullRequest.UpdatedAt,
            })
            .ToArray();
    }

    private static HttpResponseMessage Respond(HttpRequestMessage request, object[] pullRequests)
    {
        var path = request.RequestUri!.AbsolutePath;

        // Every GitHub adapter verifies the connection first, which reads the authenticated login.
        if (path.EndsWith("/user", StringComparison.Ordinal))
        {
            return Json(new { login = "meister-dev" });
        }

        // A configuration storing a numeric repository id is turned into an owner/name pair first.
        return path.Contains("/repositories/", StringComparison.Ordinal)
            ? Json(new { full_name = "acme/platform" })
            : Json(pullRequests);
    }

    private static HttpResponseMessage Json<T>(T payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload)),
        };
    }

    private static GitHubActivePrFetcher CreateFetcher(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string hostBaseUrl = "https://github.com")
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("GitHubProvider").Returns(new HttpClient(new StubHandler(responder)));

        var host = new ProviderHostRef(ScmProvider.GitHub, hostBaseUrl);
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalConnectionAsync(ClientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    ClientId,
                    ScmProvider.GitHub,
                    hostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    "provider-token",
                    true));

        return new GitHubActivePrFetcher(
            new GitHubConnectionVerifier(connections, factory),
            factory,
            NullLogger<GitHubActivePrFetcher>.Instance);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }
}
