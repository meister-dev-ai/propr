// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers;

/// <summary>
///     What the mention scan learns from a Forgejo instance about the repositories a configuration claims, and
///     what each way the read can fail costs.
/// </summary>
public sealed class ForgejoActivePrFetcherTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateTimeOffset Watermark = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Host = "https://forgejo.example";

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
                    (5, Watermark.AddMinutes(-10))));
        });

        var result = (await sut.GetRecentlyUpdatedPullRequestsAsync(Query())).PullRequests;

        Assert.Equal(7, Assert.Single(result).PullRequestId);
        Assert.Contains(
            requested,
            uri => uri.Contains("state=open", StringComparison.Ordinal)
                   && uri.Contains("sort=recentupdate", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A whole page with nothing new ends the read, rather than the first older entry on it, so an
    ///     instance whose version orders the listing differently still yields what is newer on that page.
    /// </summary>
    [Fact]
    public async Task AnEntryOlderThanTheWatermarkDoesNotHideANewerOneBehindIt()
    {
        var sut = CreateFetcher(request => Respond(
            request, PullRequestPage(
                (5, Watermark.AddMinutes(-10)),
                (7, Watermark.AddMinutes(30)))));

        var result = (await sut.GetRecentlyUpdatedPullRequestsAsync(Query())).PullRequests;

        Assert.Equal(7, Assert.Single(result).PullRequestId);
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

        var result = (await sut.GetRecentlyUpdatedPullRequestsAsync(
            Query(
            [
                new ClaimedRepositoryRef("acme/gone"),
                new ClaimedRepositoryRef("acme/platform"),
            ]))).PullRequests;

        Assert.Equal("acme/platform", Assert.Single(result).RepositoryId);
    }

    [Fact]
    public async Task ARateLimitIsAbsorbedAndStopsTheRemainingRepositories()
    {
        var attempted = new List<string>();
        var sut = CreateFetcher(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            attempted.Add(uri);

            if (uri.Contains("/repos/acme/tooling/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            }

            return Respond(request, PullRequestPage((7, Watermark.AddMinutes(5))));
        });

        var result = (await sut.GetRecentlyUpdatedPullRequestsAsync(
            Query(
            [
                new ClaimedRepositoryRef("acme/platform"),
                new ClaimedRepositoryRef("acme/tooling"),
                new ClaimedRepositoryRef("acme/docs"),
            ]))).PullRequests;

        Assert.Equal("acme/platform", Assert.Single(result).RepositoryId);
        Assert.DoesNotContain(attempted, uri => uri.Contains("/repos/acme/docs/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheInstanceIsReachedAtTheHostFromTheConnection()
    {
        var requested = new List<string>();
        var sut = CreateFetcher(request =>
        {
            requested.Add(request.RequestUri!.AbsoluteUri);
            return Respond(request, PullRequestPage((7, Watermark.AddMinutes(5))));
        });

        await sut.GetRecentlyUpdatedPullRequestsAsync(Query());

        Assert.Contains(requested, uri => uri.StartsWith($"{Host}/api/v1/", StringComparison.Ordinal));
    }

    /// <summary>There is no default Forgejo host, so nothing is asked without one.</summary>
    [Fact]
    public async Task WithoutAHostNothingIsAsked()
    {
        var attempted = 0;
        var sut = CreateFetcher(request =>
        {
            attempted++;
            return Respond(request, PullRequestPage());
        });

        var result = (await sut.GetRecentlyUpdatedPullRequestsAsync(Query(scopePath: string.Empty))).PullRequests;

        Assert.Empty(result);
        Assert.Equal(0, attempted);
    }

    [Fact]
    public async Task AConfigurationClaimingNothingAsksTheInstanceNothing()
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

    /// <summary>
    ///     A certificate this installation does not trust otherwise surfaces as a bare transport failure,
    ///     which sends an operator looking at the network instead of at the instance's certificate authority.
    /// </summary>
    [Fact]
    public async Task AnUntrustedCertificateIsReportedAsWhatItIs()
    {
        var failures = new List<string>();
        var logger = new CapturingLogger<ForgejoActivePrFetcher>(failures);
        var sut = CreateFetcher(
            _ => throw new HttpRequestException(
                "The SSL connection could not be established.",
                new AuthenticationException("The remote certificate is invalid.")),
            logger);

        var result = (await sut.GetRecentlyUpdatedPullRequestsAsync(Query())).PullRequests;

        Assert.Empty(result);
        Assert.Contains(failures, message => message.Contains("certificate", StringComparison.OrdinalIgnoreCase));
    }

    private static ActivePullRequestQuery Query(
        IReadOnlyList<ClaimedRepositoryRef>? repositories = null,
        string scopePath = Host)
    {
        return new ActivePullRequestQuery(
            ScmProvider.Forgejo,
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

        // Every Forgejo adapter verifies the connection first, which reads the authenticated login.
        if (path.EndsWith("/user", StringComparison.Ordinal))
        {
            return Json(new { login = "meister-dev" });
        }

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

    private static ForgejoActivePrFetcher CreateFetcher(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        ILogger<ForgejoActivePrFetcher>? logger = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("ForgejoProvider").Returns(new HttpClient(new StubHandler(responder)));

        var host = new ProviderHostRef(ScmProvider.Forgejo, Host);
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalConnectionAsync(ClientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    ClientId,
                    ScmProvider.Forgejo,
                    Host,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "Forgejo",
                    "provider-token",
                    true));

        return new ForgejoActivePrFetcher(
            new ForgejoConnectionVerifier(connections, factory),
            factory,
            logger ?? NullLogger<ForgejoActivePrFetcher>.Instance);
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

    /// <summary>Keeps the rendered message and the failure behind it, which is where the reason lands.</summary>
    private sealed class CapturingLogger<T>(List<string> messages) : ILogger<T>
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
            ArgumentNullException.ThrowIfNull(formatter);
            messages.Add($"{formatter(state, exception)} {exception?.Message}");
        }
    }
}
