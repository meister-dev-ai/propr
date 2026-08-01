// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.DTOs.AzureDevOps;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Queries.ResolvePullRequest;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

/// <summary>
///     Verifies the pull-request resolution endpoint over HTTP: routing, query binding, authentication, and
///     the response shape a browser extension consumes.
/// </summary>
public sealed class PullRequestResolutionControllerTests(ControllerSmokeTests.SmokeFactory factory)
    : IClassFixture<ControllerSmokeTests.SmokeFactory>
{
    private static readonly Guid ClientId = Guid.Parse("7e2456e5-f799-4aea-b749-9bf543308780");

    // The API serialises enums as camel-cased strings, so a reader using default options would reject the
    // response. Matching that here is also a check that the shape a browser extension receives is stable.
    private static readonly JsonSerializerOptions ResponseJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public async Task Resolve_WithoutCredentials_Returns401()
    {
        var httpClient = factory.CreateClient();

        var response = await httpClient.GetAsync(
            "/pull-requests/resolve?hostBaseUrl=https://dev.azure.com&scopePath=meister-dev"
            + "&repositoryName=meister-propr&pullRequestNumber=182");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Resolve_WithAdminToken_ReturnsTheCoordinatesAReviewJobWouldCarry()
    {
        this.StubCrawlConfigurations(AdoConfiguration());
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/pull-requests/resolve?hostBaseUrl=https://dev.azure.com&scopePath=meister-dev"
            + "&repositoryName=meister-propr&pullRequestNumber=182");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ResolvePullRequestResultDto>(ResponseJson);
        var match = Assert.Single(payload!.Matches);
        Assert.Equal(ClientId, match.ClientId);
        Assert.Equal("https://dev.azure.com/meister-dev", match.ProviderScopePath);
        Assert.Equal("5cda05b9-bbfa-4c44-88e9-16aa900515d2", match.ProviderProjectKey);
        Assert.Equal("c39fd3f3-e84b-4d01-84df-57964de91bc8", match.RepositoryId);
        Assert.Equal(182, match.PullRequestId);
    }

    [Fact]
    public async Task Resolve_WhenNothingCoversTheRepository_Returns200WithNoMatches()
    {
        // An uncovered repository is a normal answer the caller renders as its own state, not a 404 it has to
        // disambiguate from a routing or credential failure.
        this.StubCrawlConfigurations(AdoConfiguration());
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/pull-requests/resolve?hostBaseUrl=https://dev.azure.com&scopePath=meister-dev"
            + "&repositoryName=not-covered&pullRequestNumber=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ResolvePullRequestResultDto>(ResponseJson);
        Assert.Empty(payload!.Matches);
    }

    [Theory]
    [InlineData("?scopePath=meister-dev&repositoryName=meister-propr&pullRequestNumber=182")]
    [InlineData("?hostBaseUrl=https://dev.azure.com&scopePath=meister-dev&pullRequestNumber=182")]
    [InlineData("?hostBaseUrl=https://dev.azure.com&scopePath=meister-dev&repositoryName=meister-propr")]
    [InlineData("?hostBaseUrl=https://dev.azure.com&repositoryName=meister-propr&pullRequestNumber=0")]
    public async Task Resolve_WithIncompleteAddress_Returns400(string queryString)
    {
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/pull-requests/resolve" + queryString);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private void StubCrawlConfigurations(params CrawlConfigurationDto[] configurations)
    {
        var repository = factory.Services.GetRequiredService<ICrawlConfigurationRepository>();
        repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(configurations);
        repository
            .GetByClientIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(configurations);
    }

    private static CrawlConfigurationDto AdoConfiguration()
    {
        return new CrawlConfigurationDto(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ClientId,
            ScmProvider.AzureDevOps,
            "https://dev.azure.com/meister-dev",
            "5cda05b9-bbfa-4c44-88e9-16aa900515d2",
            300,
            true,
            DateTimeOffset.UnixEpoch,
            [
                new CrawlRepoFilterDto(
                    Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    "meister-propr",
                    [],
                    new CanonicalSourceReferenceDto("azureDevOps", "c39fd3f3-e84b-4d01-84df-57964de91bc8"),
                    "meister-propr"),
            ]);
    }
}
