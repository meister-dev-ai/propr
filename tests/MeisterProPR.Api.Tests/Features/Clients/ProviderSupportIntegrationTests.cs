// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Api.Tests.Features.Clients;

public sealed class ProviderSupportIntegrationTests(ClientProviderConnectionsControllerTests.ProviderConnectionsApiFactory factory)
    : IClassFixture<ClientProviderConnectionsControllerTests.ProviderConnectionsApiFactory>
{
    [Fact]
    public async Task GetProviderConnections_MixedProviderClient_ReturnsAllConfiguredProviderFamilies()
    {
        await factory.ResetProviderStateAsync();

        await factory.CreateConnectionAsync(
            ScmProvider.GitHub,
            "https://github.com/acme/platform",
            displayName: "Acme GitHub");
        await factory.CreateConnectionAsync(
            ScmProvider.GitLab,
            "https://gitlab.example.com/groups/acme",
            displayName: "Acme GitLab");
        await factory.CreateConnectionAsync(
            ScmProvider.Forgejo,
            "https://codeberg.org/acme",
            displayName: "Acme Forgejo");

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/clients/{factory.ClientId}/provider-connections");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientUserToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var providerFamilies = body.EnumerateArray()
            .Select(item => item.GetProperty("providerFamily").GetString())
            .Where(value => value is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("github", providerFamilies);
        Assert.Contains("gitLab", providerFamilies);
        Assert.Contains("forgejo", providerFamilies);
    }

    [Fact]
    public async Task GetProviderOperationalStatus_HostVariantSummary_DoesNotOverstateLessProvenVariant()
    {
        await factory.ResetProviderStateAsync();
        var hosted = await factory.CreateConnectionAsync(
            ScmProvider.GitHub,
            "https://github.com/acme/platform",
            displayName: "Acme GitHub Cloud");
        var selfHosted = await factory.CreateConnectionAsync(
            ScmProvider.GitHub,
            "https://github.enterprise.example.com/acme/platform",
            displayName: "Acme GitHub Enterprise");

        await factory.CreateScopeAsync(hosted.Id, scopePath: "acme/platform", displayName: "Acme Platform");
        await factory.CreateScopeAsync(selfHosted.Id, scopePath: "acme/platform", displayName: "Acme Platform");

        using (var scope = factory.Services.CreateScope())
        {
            var reviewerRepository = scope.ServiceProvider.GetRequiredService<IClientReviewerIdentityRepository>();
            await reviewerRepository.UpsertAsync(
                factory.ClientId,
                hosted.Id,
                ScmProvider.GitHub,
                "bot-1",
                "meister-bot",
                "Meister Bot",
                true,
                CancellationToken.None);
            await reviewerRepository.UpsertAsync(
                factory.ClientId,
                selfHosted.Id,
                ScmProvider.GitHub,
                "bot-2",
                "meister-bot",
                "Meister Bot",
                true,
                CancellationToken.None);
        }

        factory.SetDiscoveryScopes("acme/platform");
        var httpClient = factory.CreateClient();

        foreach (var connection in new[] { hosted, selfHosted })
        {
            using var verifyRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"/clients/{factory.ClientId}/provider-connections/{connection.Id}/verify");
            verifyRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                factory.GenerateClientAdministratorToken());

            var verifyResponse = await httpClient.SendAsync(verifyRequest);
            Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/provider-operations/status");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientUserToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var githubSummary = body.GetProperty("providerFamilies")
            .EnumerateArray()
            .First(item => string.Equals(
                item.GetProperty("providerFamily").GetString(),
                "github",
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal("onboardingReady", githubSummary.GetProperty("leastReadyLevel").GetString());
        Assert.Equal(1, githubSummary.GetProperty("workflowCompleteCount").GetInt32());
        Assert.Equal(1, githubSummary.GetProperty("onboardingReadyCount").GetInt32());
    }
}
