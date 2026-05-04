// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterProPR.Api.Tests.Controllers;

namespace MeisterProPR.Api.Tests.Features.Clients;

public sealed class ClientsModuleIntegrationTests(ClientsControllerTests.ClientsApiFactory factory)
    : IClassFixture<ClientsControllerTests.ClientsApiFactory>
{
    [Fact]
    public async Task CreateClient_ThenGetClient_ReturnsSanitizedPayload()
    {
        var http = factory.CreateClient();
        var displayName = $"Module Client {Guid.NewGuid():N}";

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/clients");
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        createRequest.Content = JsonContent.Create(new { displayName, tenantId = factory.TenantId });

        var createResponse = await http.SendAsync(createRequest);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.False(created.TryGetProperty("key", out _));
        var clientId = created.GetProperty("id").GetGuid();

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/clients/{clientId}");
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var getResponse = await http.SendAsync(getRequest);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var payload = await getResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", payload, StringComparison.OrdinalIgnoreCase);
        var body = JsonDocument.Parse(payload).RootElement;
        Assert.Equal(displayName, body.GetProperty("displayName").GetString());
        Assert.False(body.TryGetProperty("hasAdoCredentials", out _));
    }

    [Fact]
    public async Task PutAdoCredentials_LegacyEndpoint_ReturnsNotFound_AndClientPayloadStaysProviderOnly()
    {
        var http = factory.CreateClient();

        using var putRequest = new HttpRequestMessage(HttpMethod.Put, $"/clients/{factory.ClientId}/ado-credentials");
        putRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        putRequest.Content = JsonContent.Create(
            new
            {
                tenantId = "tenant-id-abc",
                clientId = "client-id-abc",
                secret = "super-secret-value",
            });
        var putResponse = await http.SendAsync(putRequest);

        Assert.Equal(HttpStatusCode.NotFound, putResponse.StatusCode);

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/clients/{factory.ClientId}");
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var getResponse = await http.SendAsync(getRequest);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var payload = await getResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("super-secret-value", payload, StringComparison.Ordinal);
        var body = JsonDocument.Parse(payload).RootElement;
        Assert.False(body.TryGetProperty("hasAdoCredentials", out _));
    }
}

public sealed class ClientsAiConnectionsModuleIntegrationTests(ClientsControllerTests.ClientsApiFactory factory)
    : IClassFixture<ClientsControllerTests.ClientsApiFactory>
{
    [Fact]
    public async Task CreateAiConnection_ThenListConnections_ReturnsPersistedSanitizedConnection()
    {
        var http = factory.CreateClient();
        var displayName = $"Module Connection {Guid.NewGuid():N}";

        using var createRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/ai-connections");
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        createRequest.Content = JsonContent.Create(
            new
            {
                displayName,
                providerKind = "azureOpenAi",
                baseUrl = "https://azure.example/openai/",
                auth = new
                {
                    mode = "apiKey",
                    apiKey = "top-secret",
                },
                discoveryMode = "manualOnly",
                configuredModels = new object[]
                {
                    new
                    {
                        remoteModelId = "gpt-4o",
                        displayName = "gpt-4o",
                        operationKinds = new[] { "chat" },
                        supportedProtocolModes = new[] { "auto", "responses", "chatCompletions" },
                        supportsStructuredOutput = true,
                        supportsToolUse = true,
                        source = "manual",
                    },
                    new
                    {
                        remoteModelId = "text-embedding-3-large",
                        displayName = "text-embedding-3-large",
                        operationKinds = new[] { "embedding" },
                        supportedProtocolModes = new[] { "auto", "embeddings" },
                        tokenizerName = "cl100k_base",
                        maxInputTokens = 8192,
                        embeddingDimensions = 3072,
                        supportsStructuredOutput = false,
                        supportsToolUse = false,
                        source = "manual",
                    },
                },
                purposeBindings = new object[]
                {
                    new { purpose = "reviewDefault", remoteModelId = "gpt-4o", protocolMode = "auto", isEnabled = true },
                    new { purpose = "reviewLowEffort", remoteModelId = "gpt-4o", protocolMode = "auto", isEnabled = true },
                    new { purpose = "reviewMediumEffort", remoteModelId = "gpt-4o", protocolMode = "auto", isEnabled = true },
                    new { purpose = "reviewHighEffort", remoteModelId = "gpt-4o", protocolMode = "auto", isEnabled = true },
                    new { purpose = "memoryReconsideration", remoteModelId = "gpt-4o", protocolMode = "auto", isEnabled = true },
                    new { purpose = "embeddingDefault", remoteModelId = "text-embedding-3-large", protocolMode = "embeddings", isEnabled = true },
                },
            });

        var createResponse = await http.SendAsync(createRequest);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.False(created.TryGetProperty("apiKey", out _));

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, $"/clients/{factory.ClientId}/ai-connections");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var listResponse = await http.SendAsync(listRequest);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var payload = await listResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sk-test-secret", payload, StringComparison.Ordinal);
        var items = JsonDocument.Parse(payload).RootElement.EnumerateArray().ToList();
        Assert.Contains(
            items,
            item => string.Equals(item.GetProperty("displayName").GetString(), displayName, StringComparison.Ordinal));
    }
}
