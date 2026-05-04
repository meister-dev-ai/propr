// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Api.Tests.Controllers;

public sealed class ClientAiConnectionsControllerTests(ClientsControllerTests.ClientsApiFactory factory)
    : IClassFixture<ClientsControllerTests.ClientsApiFactory>
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly JsonSerializerOptions ApiJsonOptions = CreateApiJsonOptions();

    private HttpClient CreateAuthorizedClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        return client;
    }

    private static object BuildConfiguredModel(string remoteModelId, bool embedding = false)
    {
        return embedding
            ? new
            {
                remoteModelId,
                displayName = remoteModelId,
                operationKinds = new[] { "embedding" },
                supportedProtocolModes = new[] { "auto", "embeddings" },
                tokenizerName = "cl100k_base",
                maxInputTokens = 8192,
                embeddingDimensions = 3072,
                supportsStructuredOutput = false,
                supportsToolUse = false,
                source = "manual",
            }
            : new
            {
                remoteModelId,
                displayName = remoteModelId,
                operationKinds = new[] { "chat" },
                supportedProtocolModes = new[] { "auto", "responses", "chatCompletions" },
                supportsStructuredOutput = true,
                supportsToolUse = true,
                source = "manual",
            };
    }

    private static object[] BuildBindings(string primaryChatModel, string embeddingModel, bool includeEffortOverrides = true)
    {
        var bindings = new List<object>
        {
            new { purpose = "reviewDefault", remoteModelId = primaryChatModel, protocolMode = "auto", isEnabled = true },
            new { purpose = "memoryReconsideration", remoteModelId = primaryChatModel, protocolMode = "auto", isEnabled = true },
            new { purpose = "embeddingDefault", remoteModelId = embeddingModel, protocolMode = "embeddings", isEnabled = true },
        };

        if (includeEffortOverrides)
        {
            bindings.InsertRange(
                1,
                [
                    new { purpose = "reviewLowEffort", remoteModelId = primaryChatModel, protocolMode = "auto", isEnabled = true },
                    new { purpose = "reviewMediumEffort", remoteModelId = primaryChatModel, protocolMode = "auto", isEnabled = true },
                    new { purpose = "reviewHighEffort", remoteModelId = primaryChatModel, protocolMode = "auto", isEnabled = true },
                ]);
        }

        return bindings.ToArray();
    }

    private static object BuildCreatePayload(
        string displayName,
        IReadOnlyList<string>? chatModels = null,
        string? baseUrl = null,
        bool includeEffortOverrides = true)
    {
        var resolvedChatModels = chatModels is { Count: > 0 } ? chatModels : new[] { "gpt-4o" };
        var embeddingModel = "text-embedding-3-large";

        return new
        {
            displayName,
            providerKind = "azureOpenAi",
            baseUrl = baseUrl ?? "https://my-openai.openai.azure.com/",
            auth = new
            {
                mode = "apiKey",
                apiKey = "secret-api-key",
            },
            discoveryMode = "manualOnly",
            configuredModels = resolvedChatModels.Select(model => BuildConfiguredModel(model)).Concat([BuildConfiguredModel(embeddingModel, true)]),
            purposeBindings = BuildBindings(resolvedChatModels[0], embeddingModel, includeEffortOverrides),
        };
    }

    private async Task<AiConnectionDto> SeedConnectionAsync(
        string displayName,
        IReadOnlyList<string>? chatModels = null,
        bool verify = false,
        bool includeEffortOverrides = true)
    {
        var client = this.CreateAuthorizedClient();
        var response = await client.PostAsJsonAsync(
            $"/clients/{ClientId}/ai-connections",
            BuildCreatePayload(displayName, chatModels, includeEffortOverrides: includeEffortOverrides));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);
        Assert.NotNull(created);

        if (verify)
        {
            var verifyResponse = await client.PostAsync($"/clients/{ClientId}/ai-connections/{created.Id}/verify", null);
            Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        }

        return created;
    }

    [Fact]
    public async Task CreateAiConnection_WithValidPayload_Returns201WithDto()
    {
        var client = this.CreateAuthorizedClient();
        var response = await client.PostAsJsonAsync($"/clients/{ClientId}/ai-connections", BuildCreatePayload("Primary Profile", ["gpt-4o", "gpt-4.1-mini"]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);
        Assert.NotNull(created);
        Assert.Equal("Primary Profile", created.DisplayName);
        Assert.Equal("azureOpenAi", created.ProviderKind.ToString().ToCamelCase());
        Assert.Equal("https://my-openai.openai.azure.com/", created.BaseUrl);
        Assert.Equal(3, created.ConfiguredModels.Count);
        Assert.False(created.IsActive);
        Assert.Equal("neverVerified", created.Verification.Status.ToString().ToCamelCase());
    }

    [Fact]
    public async Task CreateAiConnection_WithoutCredentials_Returns401()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync($"/clients/{ClientId}/ai-connections", BuildCreatePayload("Primary Profile"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateAiConnection_OpenAiProviderWithAzureHostedEndpoint_Returns400()
    {
        var client = this.CreateAuthorizedClient();
        var payload = new
        {
            displayName = "Wrong Provider",
            providerKind = "openAi",
            baseUrl = "https://my-openai.openai.azure.com/",
            auth = new
            {
                mode = "apiKey",
                apiKey = "secret-api-key",
            },
            discoveryMode = "manualOnly",
            configuredModels = new[]
            {
                BuildConfiguredModel("gpt-4o"),
                BuildConfiguredModel("text-embedding-3-large", true),
            },
            purposeBindings = BuildBindings("gpt-4o", "text-embedding-3-large"),
        };

        var response = await client.PostAsJsonAsync($"/clients/{ClientId}/ai-connections", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("must use providerKind 'azureOpenAi'", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateAiConnection_WithProviderNeutralPayload_UpdatesConnection()
    {
        var created = await this.SeedConnectionAsync("Primary Profile", ["gpt-4o"]);
        var client = this.CreateAuthorizedClient();

        var response = await client.PatchAsJsonAsync(
            $"/clients/{ClientId}/ai-connections/{created.Id}",
            new
            {
                baseUrl = "https://updated.openai.azure.com/",
                configuredModels = new[]
                {
                    BuildConfiguredModel("gpt-4.1"),
                    BuildConfiguredModel("text-embedding-3-large", true),
                },
                purposeBindings = BuildBindings("gpt-4.1", "text-embedding-3-large"),
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);

        Assert.NotNull(updated);
        Assert.Equal("https://updated.openai.azure.com/", updated.BaseUrl);
        Assert.Contains(updated.ConfiguredModels, model => model.RemoteModelId == "gpt-4.1");
        Assert.Equal("gpt-4.1", updated.GetBoundModelId(AiPurpose.ReviewDefault));
    }

    [Fact]
    public async Task UpdateAiConnection_QualifyingEditResetsVerificationAndBlocksActivationUntilReverified()
    {
        var created = await this.SeedConnectionAsync("Primary Profile", verify: true);
        var client = this.CreateAuthorizedClient();

        var updateResponse = await client.PatchAsJsonAsync(
            $"/clients/{ClientId}/ai-connections/{created.Id}",
            new
            {
                baseUrl = "https://updated.openai.azure.com/",
                auth = new
                {
                    mode = "apiKey",
                    apiKey = "updated-secret-api-key",
                },
            });

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);
        Assert.NotNull(updated);
        Assert.Equal("neverVerified", updated.Verification.Status.ToString().ToCamelCase());

        var activateBeforeVerify = await client.PostAsync($"/clients/{ClientId}/ai-connections/{created.Id}/activate", null);
        Assert.Equal(HttpStatusCode.BadRequest, activateBeforeVerify.StatusCode);

        var verifyResponse = await client.PostAsync($"/clients/{ClientId}/ai-connections/{created.Id}/verify", null);
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);

        var activateAfterVerify = await client.PostAsync($"/clients/{ClientId}/ai-connections/{created.Id}/activate", null);
        Assert.Equal(HttpStatusCode.OK, activateAfterVerify.StatusCode);
    }

    [Fact]
    public async Task UpdateAiConnection_InvalidEndpointUrl_Returns400()
    {
        var created = await this.SeedConnectionAsync("Primary Profile");
        var client = this.CreateAuthorizedClient();

        var response = await client.PatchAsJsonAsync(
            $"/clients/{ClientId}/ai-connections/{created.Id}",
            new { baseUrl = "not-a-valid-url" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateAiConnection_OpenAiProviderWithAzureHostedEndpoint_Returns400()
    {
        var created = await this.SeedConnectionAsync("Primary Profile");
        var client = this.CreateAuthorizedClient();

        var response = await client.PatchAsJsonAsync(
            $"/clients/{ClientId}/ai-connections/{created.Id}",
            new
            {
                providerKind = "openAi",
                baseUrl = "https://project.services.ai.azure.com/api/projects/demo",
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("must use providerKind 'azureOpenAi'", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateAiConnection_MissingPurposeBindingModel_Returns400()
    {
        var created = await this.SeedConnectionAsync("Primary Profile");
        var client = this.CreateAuthorizedClient();

        var response = await client.PatchAsJsonAsync(
            $"/clients/{ClientId}/ai-connections/{created.Id}",
            new
            {
                purposeBindings = new object[]
                {
                    new { purpose = "reviewDefault", protocolMode = "auto", isEnabled = true },
                },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ActivateAiConnection_WithVerifiedProfile_Returns200AndIsActiveTrue()
    {
        var created = await this.SeedConnectionAsync("Primary Profile", verify: true);
        var client = this.CreateAuthorizedClient();

        var response = await client.PostAsync($"/clients/{ClientId}/ai-connections/{created.Id}/activate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var activated = await response.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);
        Assert.NotNull(activated);
        Assert.True(activated.IsActive);
    }

    [Fact]
    public async Task ActivateAiConnection_WithMinimalVerifiedBindings_Returns200AndIsActiveTrue()
    {
        var created = await this.SeedConnectionAsync("Primary Profile", verify: true, includeEffortOverrides: false);
        var client = this.CreateAuthorizedClient();

        var response = await client.PostAsync($"/clients/{ClientId}/ai-connections/{created.Id}/activate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var activated = await response.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);
        Assert.NotNull(activated);
        Assert.True(activated.IsActive);
    }

    [Fact]
    public async Task ActivateAiConnection_WithUnverifiedProfile_Returns400()
    {
        var created = await this.SeedConnectionAsync("Primary Profile", verify: false);
        var client = this.CreateAuthorizedClient();

        var response = await client.PostAsync($"/clients/{ClientId}/ai-connections/{created.Id}/activate", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeactivateAiConnection_WhenActive_Returns200AndIsActiveFalse()
    {
        var created = await this.SeedConnectionAsync("Primary Profile", verify: true);
        var client = this.CreateAuthorizedClient();
        await client.PostAsync($"/clients/{ClientId}/ai-connections/{created.Id}/activate", null);

        var response = await client.PostAsync($"/clients/{ClientId}/ai-connections/{created.Id}/deactivate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var deactivated = await response.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);
        Assert.NotNull(deactivated);
        Assert.False(deactivated.IsActive);
    }

    [Fact]
    public async Task DeleteAiConnection_ExistingConnection_Returns204()
    {
        var created = await this.SeedConnectionAsync("Primary Profile");
        var client = this.CreateAuthorizedClient();

        var response = await client.DeleteAsync($"/clients/{ClientId}/ai-connections/{created.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var listResponse = await client.GetAsync($"/clients/{ClientId}/ai-connections");
        var connections = await listResponse.Content.ReadFromJsonAsync<List<AiConnectionDto>>(ApiJsonOptions);
        Assert.NotNull(connections);
        Assert.DoesNotContain(connections, connection => connection.Id == created.Id);
    }

    [Fact]
    public async Task DeleteAiConnection_WithoutCredentials_Returns401()
    {
        var created = await this.SeedConnectionAsync("Primary Profile");
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync($"/clients/{ClientId}/ai-connections/{created.Id}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteAiConnection_DoesNotCorruptExistingJobAiConnectionSnapshot()
    {
        var created = await this.SeedConnectionAsync("Primary Profile");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            var job = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "proj", "repo", 42, 9001);
            job.SetAiConfig(created.Id, "gpt-4o");
            db.ReviewJobs.Add(job);
            await db.SaveChangesAsync();
        }

        var client = this.CreateAuthorizedClient();
        var response = await client.DeleteAsync($"/clients/{ClientId}/ai-connections/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var persistedJob = await verifyDb.ReviewJobs.AsNoTracking().SingleAsync();
        Assert.Equal(created.Id, persistedJob.AiConnectionId);
        Assert.Equal("gpt-4o", persistedJob.AiModel);
    }

    private static JsonSerializerOptions CreateApiJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

internal static class ClientAiConnectionsControllerTestStringExtensions
{
    public static string ToCamelCase(this string value)
    {
        if (string.IsNullOrEmpty(value) || char.IsLower(value[0]))
        {
            return value;
        }

        return char.ToLowerInvariant(value[0]) + value[1..];
    }
}
