// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Tests.Features.Clients;

public sealed class ProviderOperationalStatusControllerTests(ClientProviderConnectionsControllerTests.ProviderConnectionsApiFactory factory)
    : IClassFixture<ClientProviderConnectionsControllerTests.ProviderConnectionsApiFactory>
{
    [Fact]
    public async Task
        GetProviderOperationalStatus_VerifiedConnectionWithoutReviewerTrigger_ReturnsWorkflowCompleteStatus()
    {
        await factory.ResetProviderStateAsync();
        var created = await factory.CreateConnectionAsync(hostBaseUrl: "https://github.com/acme/platform");
        await factory.CreateScopeAsync(created.Id, scopePath: "acme/platform", displayName: "Acme Platform");
        factory.SetDiscoveryScopes("acme/platform");

        var httpClient = factory.CreateClient();
        using (var verifyRequest = new HttpRequestMessage(
                   HttpMethod.Post,
                   $"/clients/{factory.ClientId}/provider-connections/{created.Id}/verify"))
        {
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
        Assert.Equal("workflowComplete", body.GetProperty("connections")[0].GetProperty("readinessLevel").GetString());
        Assert.Equal("healthy", body.GetProperty("connections")[0].GetProperty("health").GetString());
        Assert.Contains(
            "authenticated connection identity used for posting",
            body.GetProperty("connections")[0].GetProperty("statusReason").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "workflowComplete",
            body.GetProperty("providerFamilies")[0].GetProperty("leastReadyLevel").GetString());
    }

    [Fact]
    public async Task GetProviderOperationalStatus_GitHubAppVerificationFailure_ReturnsFailureCategoryAndReason()
    {
        await factory.ResetProviderStateAsync();
        var created = await factory.CreateConnectionAsync(
            ScmProvider.GitHub,
            "https://github.enterprise.example.com/acme/platform",
            ScmAuthenticationKind.AppInstallation,
            displayName: "GitHub App",
            secret: "-----BEGIN PRIVATE KEY-----",
            gitHubAppId: 123456,
            gitHubAppInstallationId: 789012);
        factory.SetDiscoveryFailure("GitHub App installation token request failed because permission is missing.");

        var httpClient = factory.CreateClient();
        using (var verifyRequest = new HttpRequestMessage(
                   HttpMethod.Post,
                   $"/clients/{factory.ClientId}/provider-connections/{created.Id}/verify"))
        {
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
        var connection = body.GetProperty("connections")[0];
        Assert.Equal("failed", connection.GetProperty("verificationStatus").GetString());
        Assert.Equal("failing", connection.GetProperty("health").GetString());
        Assert.Equal("authentication", connection.GetProperty("failureCategory").GetString());
        Assert.Contains(
            "granted permissions",
            connection.GetProperty("statusReason").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }
}
