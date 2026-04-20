// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MeisterProPR.Api.Tests.Features.Clients;

public sealed class ProviderOperationalStatusControllerTests(ClientProviderConnectionsControllerTests.ProviderConnectionsApiFactory factory)
    : IClassFixture<ClientProviderConnectionsControllerTests.ProviderConnectionsApiFactory>
{
    [Fact]
    public async Task
        GetProviderOperationalStatus_VerifiedConnectionWithoutWorkflowCriteria_ReturnsOnboardingReadyStatus()
    {
        await factory.ResetProviderStateAsync();
        var created = await factory.CreateConnectionAsync(hostBaseUrl: "https://github.com/acme/platform");
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
        Assert.Equal("onboardingReady", body.GetProperty("connections")[0].GetProperty("readinessLevel").GetString());
        Assert.Equal("degraded", body.GetProperty("connections")[0].GetProperty("health").GetString());
        Assert.Equal(
            "onboardingReady",
            body.GetProperty("providerFamilies")[0].GetProperty("leastReadyLevel").GetString());
    }
}
