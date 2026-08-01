// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

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

    private static object[] BuildBindings(
        string primaryChatModel,
        string embeddingModel,
        bool includeEffortOverrides = true,
        string protocolMode = "auto")
    {
        var bindings = new List<object>
        {
            new { purpose = "reviewDefault", remoteModelId = primaryChatModel, protocolMode, isEnabled = true },
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
        bool includeEffortOverrides = true,
        string providerKind = "azureOpenAi",
        string protocolMode = "auto")
    {
        var resolvedChatModels = chatModels is { Count: > 0 } ? chatModels : new[] { "gpt-4o" };
        var embeddingModel = "text-embedding-3-large";

        return new
        {
            displayName,
            providerKind,
            baseUrl = baseUrl ?? "https://my-openai.openai.azure.com/",
            auth = new
            {
                mode = "apiKey",
                apiKey = "secret-api-key",
            },
            discoveryMode = "manualOnly",
            configuredModels = resolvedChatModels.Select(model => BuildConfiguredModel(model)).Concat([BuildConfiguredModel(embeddingModel, true)]),
            purposeBindings = BuildBindings(resolvedChatModels[0], embeddingModel, includeEffortOverrides, protocolMode),
        };
    }

    // What a client may configure comes from the drivers this build composed, not from the enum: a family named
    // without a driver behind it must never be offered, or the failure only moves to review time. Every family
    // the enum names now has one, so the offered set is the whole enum — and this test is what notices if a
    // future family is named before it can be called.
    [Fact]
    public async Task PermittedProviders_OffersEveryFamilyThisBuildCanCall()
    {
        var client = this.CreateAuthorizedClient();

        var response = await client.GetAsync($"/clients/{ClientId}/ai-connections/permitted-providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ApiJsonOptions);
        var offered = payload.GetProperty("providers")
            .EnumerateArray()
            .Select(provider => provider.GetProperty("providerKind").GetString())
            .ToList();

        foreach (var family in new[] { "azureOpenAi", "openAi", "liteLlm", "openAiCompatible", "anthropic", "awsBedrock", "googleVertex" })
        {
            Assert.Contains(family, offered);
        }
    }

    // The UI must offer only wire shapes that can actually be called, and the drivers are the only place that
    // knows which those are. An OpenAI-compatible endpoint has no Responses API, so it must not be offered one.
    [Fact]
    public async Task PermittedProviders_ReportsTheWireShapesEachProviderSpeaks()
    {
        var client = this.CreateAuthorizedClient();

        var response = await client.GetAsync($"/clients/{ClientId}/ai-connections/permitted-providers");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ApiJsonOptions);
        var providers = payload.GetProperty("providers").EnumerateArray().ToList();

        var shapesOf = (string kind) => providers
            .Single(p => p.GetProperty("providerKind").GetString() == kind)
            .GetProperty("protocolModes").EnumerateArray().Select(m => m.GetString()).ToList();

        Assert.Contains("responses", shapesOf("azureOpenAi"));
        Assert.DoesNotContain("responses", shapesOf("openAiCompatible"));
        Assert.Contains("chatCompletions", shapesOf("openAiCompatible"));
    }

    // A binding asking for a shape the provider cannot speak is refused while the operator is looking at the
    // form. Before this, the driver quietly sent chat-completions instead and the provider answered with a
    // rejection that named nothing useful.
    [Fact]
    public async Task CreateAiConnection_WithAProtocolTheProviderCannotSpeak_IsRefused()
    {
        var client = this.CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            $"/clients/{ClientId}/ai-connections",
            BuildCreatePayload("Anthropic-shaped binding", protocolMode: "anthropicMessages"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("AnthropicMessages", body, StringComparison.Ordinal);
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
    public async Task DiscoverModels_WithHttpBaseUrl_Returns400()
    {
        var client = this.CreateAuthorizedClient();
        var payload = new
        {
            providerKind = "openAi",
            baseUrl = "http://api.example.com/v1",
            auth = new { mode = "apiKey", apiKey = "secret-api-key" },
        };

        var response = await client.PostAsJsonAsync($"/clients/{ClientId}/ai-connections/discover-models", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("https", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverModels_WithLinkLocalMetadataBaseUrl_Returns400()
    {
        var client = this.CreateAuthorizedClient();
        var payload = new
        {
            providerKind = "openAi",
            baseUrl = "https://169.254.169.254/latest/meta-data/",
            auth = new { mode = "apiKey", apiKey = "secret-api-key" },
        };

        var response = await client.PostAsJsonAsync($"/clients/{ClientId}/ai-connections/discover-models", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("private, loopback, or link-local", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverModels_AzureProviderWithNonAzureHost_Returns400()
    {
        var client = this.CreateAuthorizedClient();
        var payload = new
        {
            providerKind = "azureOpenAi",
            baseUrl = "https://internal.corp.example/",
            auth = new { mode = "apiKey", apiKey = "secret-api-key" },
        };

        var response = await client.PostAsJsonAsync($"/clients/{ClientId}/ai-connections/discover-models", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Azure AI host", body, StringComparison.OrdinalIgnoreCase);
    }

    // Every pricing and capability field the model editor collects has to survive the round trip. The request
    // record silently ignores JSON it has no property for, so a field the form offers but the contract omits is
    // accepted by the UI, sent, and dropped without a word.
    [Fact]
    public async Task CreateAiConnection_EveryPricingAndCapabilityFieldTheFormCollects_IsPersisted()
    {
        var client = this.CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            $"/clients/{ClientId}/ai-connections",
            new
            {
                displayName = "Priced Profile",
                providerKind = "openAiCompatible",
                baseUrl = "https://opencode.ai/zen/v1",
                auth = new { mode = "apiKey", apiKey = "secret" },
                discoveryMode = "manualOnly",
                configuredModels = new[]
                {
                    new
                    {
                        remoteModelId = "gpt-5.6-luna",
                        displayName = "gpt-5.6-luna",
                        operationKinds = new[] { "chat" },
                        supportedProtocolModes = new[] { "auto", "chatCompletions" },
                        supportsStructuredOutput = true,
                        supportsToolUse = true,
                        source = "manual",
                        inputCostPer1MUsd = 0.2m,
                        outputCostPer1MUsd = 1.2m,
                        cachedInputCostPer1MUsd = 0.05m,
                        cacheWriteCostPer1MUsd = 0.25m,
                        supportsReasoning = true,
                        supportsPromptCaching = true,
                        reasoningContentField = "reasoning_content",
                    },
                    BuildConfiguredModel("text-embedding-3-large", true),
                },
                purposeBindings = BuildBindings("gpt-5.6-luna", "text-embedding-3-large"),
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);

        var model = Assert.Single(created!.ConfiguredModels, m => m.RemoteModelId == "gpt-5.6-luna");
        Assert.Equal(0.2m, model.InputCostPer1MUsd);
        Assert.Equal(1.2m, model.OutputCostPer1MUsd);
        Assert.Equal(0.05m, model.CachedInputCostPer1MUsd);
        Assert.Equal(0.25m, model.CacheWriteCostPer1MUsd);
        Assert.True(model.SupportsReasoning);
        Assert.True(model.SupportsPromptCaching);
        Assert.Equal("reasoning_content", model.ReasoningContentField);
    }

    // An update that names no models restates the stored ones from the response shape. Anything the restatement
    // drops is written back as null, so an unrelated edit silently erases what was configured.
    [Fact]
    public async Task UpdateAiConnection_ThatNamesNoModels_LeavesTheirPricingIntact()
    {
        var client = this.CreateAuthorizedClient();
        var createResponse = await client.PostAsJsonAsync(
            $"/clients/{ClientId}/ai-connections",
            new
            {
                displayName = "Priced Profile",
                providerKind = "openAiCompatible",
                baseUrl = "https://opencode.ai/zen/v1",
                auth = new { mode = "apiKey", apiKey = "secret" },
                discoveryMode = "manualOnly",
                configuredModels = new[]
                {
                    new
                    {
                        remoteModelId = "gpt-5.6-luna",
                        displayName = "gpt-5.6-luna",
                        operationKinds = new[] { "chat" },
                        supportedProtocolModes = new[] { "auto", "chatCompletions" },
                        supportsStructuredOutput = true,
                        supportsToolUse = true,
                        source = "manual",
                        inputCostPer1MUsd = 0.2m,
                        outputCostPer1MUsd = 1.2m,
                        cacheWriteCostPer1MUsd = 0.25m,
                        supportsReasoning = true,
                        reasoningContentField = "reasoning_content",
                    },
                    BuildConfiguredModel("text-embedding-3-large", true),
                },
                purposeBindings = BuildBindings("gpt-5.6-luna", "text-embedding-3-large"),
            });

        var created = await createResponse.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);

        var response = await client.PatchAsJsonAsync(
            $"/clients/{ClientId}/ai-connections/{created!.Id}",
            new { displayName = "Renamed Profile" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);

        var model = Assert.Single(updated!.ConfiguredModels, m => m.RemoteModelId == "gpt-5.6-luna");
        Assert.Equal(0.2m, model.InputCostPer1MUsd);
        Assert.Equal(1.2m, model.OutputCostPer1MUsd);
        Assert.Equal(0.25m, model.CacheWriteCostPer1MUsd);
        Assert.True(model.SupportsReasoning);
        Assert.Equal("reasoning_content", model.ReasoningContentField);
    }

    // A client that selects its models through logical models binds no purpose to the connection itself, so its
    // profile legitimately carries no bindings. Refusing that shape made the profile unsavable: every edit came
    // back 400 about bindings the operator had deliberately not created, and the pricing they entered was lost.
    [Fact]
    public async Task UpdateAiConnection_OnAProfileWithNoPurposeBindings_SavesTheModelPricing()
    {
        var created = await this.SeedConnectionAsync("Logical Model Profile", ["gpt-5.6-luna"]);
        var client = this.CreateAuthorizedClient();

        var response = await client.PatchAsJsonAsync(
            $"/clients/{ClientId}/ai-connections/{created.Id}",
            new
            {
                configuredModels = new[]
                {
                    new
                    {
                        remoteModelId = "gpt-5.6-luna",
                        displayName = "gpt-5.6-luna",
                        operationKinds = new[] { "chat" },
                        supportedProtocolModes = new[] { "auto", "responses", "chatCompletions" },
                        supportsStructuredOutput = true,
                        supportsToolUse = true,
                        source = "manual",
                        inputCostPer1MUsd = 0.2m,
                        outputCostPer1MUsd = 1.2m,
                    },
                },
                purposeBindings = Array.Empty<object>(),
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);

        var model = Assert.Single(updated!.ConfiguredModels, m => m.RemoteModelId == "gpt-5.6-luna");
        Assert.Equal(0.2m, model.InputCostPer1MUsd);
        Assert.Equal(1.2m, model.OutputCostPer1MUsd);
        Assert.Empty(updated.PurposeBindings);
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
    public async Task CreateAiConnection_WithDisabledOptionalProRvPrefilterWithoutModel_Returns201()
    {
        var client = this.CreateAuthorizedClient();
        var payload = new
        {
            displayName = "Primary Profile",
            providerKind = "azureOpenAi",
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
            purposeBindings = new object[]
            {
                new { purpose = "reviewDefault", remoteModelId = "gpt-4o", protocolMode = "auto", isEnabled = true },
                new { purpose = "memoryReconsideration", remoteModelId = "gpt-4o", protocolMode = "auto", isEnabled = true },
                new { purpose = "embeddingDefault", remoteModelId = "text-embedding-3-large", protocolMode = "embeddings", isEnabled = true },
                new { purpose = "proRvPrefilter", protocolMode = "auto", isEnabled = false },
            },
        };

        var response = await client.PostAsJsonAsync($"/clients/{ClientId}/ai-connections", payload);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);
        Assert.NotNull(created);
        Assert.DoesNotContain(created.PurposeBindings, binding => binding.Purpose == AiPurpose.ProRVPrefilter);
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
