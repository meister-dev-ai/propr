// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers;

/// <summary>
///     What the mention scan learns from GitLab about the projects a configuration claims, and what each way
///     the read can fail costs.
/// </summary>
public sealed class GitLabActivePrFetcherTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateTimeOffset Watermark = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheWatermarkAndTheOpenStateAreAppliedByTheHost()
    {
        var requested = new List<string>();
        var sut = CreateFetcher(request =>
        {
            requested.Add(request.RequestUri!.AbsoluteUri);
            return Respond(request, MergeRequestPage((42, Watermark.AddMinutes(5))));
        });

        var result = (await sut.GetRecentlyUpdatedPullRequestsAsync(Query())).PullRequests;

        Assert.Equal(42, Assert.Single(result).PullRequestId);
        Assert.Contains(
            requested,
            uri => uri.Contains("state=opened", StringComparison.Ordinal)
                   && uri.Contains("updated_after=", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Every other GitLab call addresses a merge request by its project-scoped number, so discovery has to
    ///     report that one. The global id would reach a different merge request entirely.
    /// </summary>
    [Fact]
    public async Task TheProjectScopedNumberIsWhatIsReported()
    {
        var sut = CreateFetcher(request => Respond(
            request,
            [
                new
                {
                    id = 900123,
                    iid = 7,
                    updated_at = Watermark.AddMinutes(5),
                    created_at = Watermark.AddMinutes(5),
                },
            ]));

        var result = (await sut.GetRecentlyUpdatedPullRequestsAsync(Query())).PullRequests;

        Assert.Equal(7, Assert.Single(result).PullRequestId);
    }

    /// <summary>A project in a nested subgroup is one path segment, or the request reaches a group instead.</summary>
    [Fact]
    public async Task ANestedSubgroupProjectPathIsSentAsOneSegment()
    {
        var requested = new List<string>();
        var sut = CreateFetcher(request =>
        {
            requested.Add(request.RequestUri!.AbsoluteUri);
            return Respond(request, MergeRequestPage((42, Watermark.AddMinutes(5))));
        });

        await sut.GetRecentlyUpdatedPullRequestsAsync(Query([new ClaimedRepositoryRef("acme/platform/services/api")]));

        Assert.Contains(
            requested,
            uri => uri.Contains("/projects/acme%2Fplatform%2Fservices%2Fapi/merge_requests", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProjectWithNoOpenMergeRequestsIsEmptyRatherThanAFailure()
    {
        var sut = CreateFetcher(request => Respond(request, MergeRequestPage()));

        var result = (await sut.GetRecentlyUpdatedPullRequestsAsync(Query())).PullRequests;

        Assert.Empty(result);
    }

    [Fact]
    public async Task AProjectThatCannotBeReadDoesNotCostTheOthersTheirScan()
    {
        var sut = CreateFetcher(request =>
            request.RequestUri!.AbsoluteUri.Contains("acme%2Fgone", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Respond(request, MergeRequestPage((42, Watermark.AddMinutes(5)))));

        var result = (await sut.GetRecentlyUpdatedPullRequestsAsync(
            Query(
            [
                new ClaimedRepositoryRef("acme/gone"),
                new ClaimedRepositoryRef("acme/platform"),
            ]))).PullRequests;

        Assert.Equal("acme/platform", Assert.Single(result).RepositoryId);
    }

    [Fact]
    public async Task ARateLimitIsAbsorbedAndStopsTheRemainingProjects()
    {
        var attempted = new List<string>();
        var sut = CreateFetcher(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            attempted.Add(uri);

            if (uri.Contains("acme%2Ftooling", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            }

            return Respond(request, MergeRequestPage((42, Watermark.AddMinutes(5))));
        });

        var result = (await sut.GetRecentlyUpdatedPullRequestsAsync(
            Query(
            [
                new ClaimedRepositoryRef("acme/platform"),
                new ClaimedRepositoryRef("acme/tooling"),
                new ClaimedRepositoryRef("acme/docs"),
            ]))).PullRequests;

        Assert.Equal("acme/platform", Assert.Single(result).RepositoryId);
        Assert.DoesNotContain(attempted, uri => uri.Contains("acme%2Fdocs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASelfManagedInstanceIsReachedAtItsOwnAddress()
    {
        var requested = new List<string>();
        var sut = CreateFetcher(
            request =>
            {
                requested.Add(request.RequestUri!.AbsoluteUri);
                return Respond(request, MergeRequestPage((42, Watermark.AddMinutes(5))));
            },
            hostBaseUrl: "https://gitlab.internal.example");

        await sut.GetRecentlyUpdatedPullRequestsAsync(Query(scopePath: "https://gitlab.internal.example"));

        Assert.Contains(
            requested,
            uri => uri.StartsWith("https://gitlab.internal.example/api/v4/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AConfigurationClaimingNothingAsksGitLabNothing()
    {
        var attempted = 0;
        var sut = CreateFetcher(request =>
        {
            attempted++;
            return Respond(request, MergeRequestPage());
        });

        var result = (await sut.GetRecentlyUpdatedPullRequestsAsync(Query([]))).PullRequests;

        Assert.Empty(result);
        Assert.Equal(0, attempted);
    }

    private static ActivePullRequestQuery Query(
        IReadOnlyList<ClaimedRepositoryRef>? repositories = null,
        string scopePath = "https://gitlab.com")
    {
        return new ActivePullRequestQuery(
            ScmProvider.GitLab,
            scopePath,
            repositories ?? [new ClaimedRepositoryRef("acme/platform")],
            Watermark,
            ClientId);
    }

    private static object[] MergeRequestPage(params (int Iid, DateTimeOffset UpdatedAt)[] mergeRequests)
    {
        return mergeRequests
            .Select(mergeRequest => (object)new
            {
                iid = mergeRequest.Iid,
                updated_at = mergeRequest.UpdatedAt,
                created_at = mergeRequest.UpdatedAt,
            })
            .ToArray();
    }

    private static HttpResponseMessage Respond(HttpRequestMessage request, object[] mergeRequests)
    {
        // Every GitLab adapter verifies the connection first, which reads the authenticated username.
        return request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal)
            ? Json(new { username = "meister-dev" })
            : Json(mergeRequests);
    }

    private static HttpResponseMessage Json<T>(T payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload)),
        };
    }

    private static GitLabActivePrFetcher CreateFetcher(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string hostBaseUrl = "https://gitlab.com")
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("GitLabProvider").Returns(new HttpClient(new StubHandler(responder)));

        var host = new ProviderHostRef(ScmProvider.GitLab, hostBaseUrl);
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalConnectionAsync(ClientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    ClientId,
                    ScmProvider.GitLab,
                    hostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitLab",
                    "provider-token",
                    true));

        return new GitLabActivePrFetcher(
            new GitLabConnectionVerifier(connections, factory),
            factory,
            NullLogger<GitLabActivePrFetcher>.Instance);
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
