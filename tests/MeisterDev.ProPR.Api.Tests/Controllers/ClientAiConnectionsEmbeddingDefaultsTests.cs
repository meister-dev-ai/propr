// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

/// <summary>
///     The values ProPR fills in for an embedding model on a write, the stated values it refuses, the vector width a
///     purpose selection and a model check report, and the catalog models a discovery offers.
/// </summary>
public sealed class ClientAiConnectionsEmbeddingDefaultsTests(ClientsControllerTests.ClientsApiFactory factory)
    : IClassFixture<ClientsControllerTests.ClientsApiFactory>
{
    private const string CompatibleKind = "meisterdev/openAiCompatible";
    private const string CompatibleBaseUrl = "https://llm.example.test/v1";

    private static readonly JsonSerializerOptions ApiJsonOptions = CreateApiJsonOptions();

    [Fact]
    public async Task CreateAiConnection_FillsTheMissingValuesOfEachEmbeddingModel()
    {
        factory.CatalogEntries.Add(CatalogEntry("acme", "acme-embed-v2", family: "acme-embed", context: 512));
        try
        {
            var response = await this.CreateCompatibleProfileAsync(
                new { remoteModelId = "openai/text-embedding-3-large", operationKinds = new[] { "embedding" } },
                new { remoteModelId = "acme-embed-v2", operationKinds = new[] { "embedding" } },
                new { remoteModelId = "nomic-embedding-text" });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var created = (await response.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions))!;

            // The table of documented models, after the vendor prefix is stripped.
            Assert.Equal(("cl100k_base", 8191, 3072), Values(created, "openai/text-embedding-3-large"));

            // The context window of the catalog, and the fallback tokenizer and width.
            Assert.Equal(("cl100k_base", 512, 1536), Values(created, "acme-embed-v2"));

            // A model nobody knows, offered as an embedding model by its name.
            Assert.Equal(("cl100k_base", 8191, 1536), Values(created, "nomic-embedding-text"));
        }
        finally
        {
            factory.CatalogEntries.Clear();
        }
    }

    [Fact]
    public async Task UpdateAiConnection_FillsTheMissingValuesOfAnEmbeddingModel()
    {
        var created = await this.CreateCompatibleProfileAsync(new { remoteModelId = "gpt-6.1-terra", operationKinds = new[] { "chat" } });
        var profile = (await created.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions))!;

        var updated = await this.CreateAuthorizedClient().PatchAsJsonAsync(
            $"/clients/{factory.ClientId}/ai-connections/{profile.Id}",
            new
            {
                configuredModels = new object[]
                {
                    new { remoteModelId = "gpt-6.1-terra", operationKinds = new[] { "chat" } },
                    new { remoteModelId = "text-embedding-3-small", operationKinds = new[] { "embedding" } },
                },
            });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var saved = (await updated.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions))!;
        Assert.Equal(("cl100k_base", 8191, 1536), Values(saved, "text-embedding-3-small"));
    }

    [Fact]
    public async Task CreateAiConnection_KeepsTheStatedValuesOfAnEmbeddingModel()
    {
        var response = await this.CreateCompatibleProfileAsync(
            new
            {
                remoteModelId = "text-embedding-3-large",
                operationKinds = new[] { "embedding" },
                tokenizerName = "o200k_base",
                maxInputTokens = 2048,
                embeddingDimensions = 1536,
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions))!;
        Assert.Equal(("o200k_base", 2048, 1536), Values(created, "text-embedding-3-large"));
    }

    // The embedding paths count input tokens with these encodings and no other, and a stated value is kept, so a
    // tokenizer they cannot use is refused on the field that states it.
    [Theory]
    [InlineData("claude")]
    [InlineData("gpt2")]
    public async Task CreateAiConnection_RefusesATokenizerTheEmbeddingPathsCannotUse(string tokenizerName)
    {
        var response = await this.CreateCompatibleProfileAsync(
            new { remoteModelId = "gpt-6.1-terra", operationKinds = new[] { "chat" } },
            new { remoteModelId = "bge-m3", operationKinds = new[] { "embedding" }, tokenizerName });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var message = Assert.Single(problem.RootElement.GetProperty("errors").GetProperty("configuredModels[1].tokenizerName").EnumerateArray())
            .GetString();
        Assert.Contains("cl100k_base", message, StringComparison.Ordinal);
        Assert.Contains("o200k_base", message, StringComparison.Ordinal);
        Assert.DoesNotContain("claude", message, StringComparison.Ordinal);
    }

    // Every embedding column stores 1536 dimensions, so only a model configured for 1536 can be selected. Without
    // a stated width the table answers 1536 for text-embedding-3-small and 3072 for text-embedding-3-large.
    // An update that leaves the models out restates the stored ones, so a stored tokenizer outside the supported
    // list does not refuse a rename. An update that states the models again is checked.
    [Fact]
    public async Task UpdateAiConnection_ChecksTheTokenizerOnlyWhenTheRequestStatesTheModels()
    {
        AiConnectionDto stored;
        using (var scope = factory.Services.CreateScope())
        {
            stored = await scope.ServiceProvider.GetRequiredService<IAiConnectionRepository>().AddAsync(
                factory.ClientId,
                new AiConnectionWriteRequestDto(
                    "Stored claude tokenizer",
                    CompatibleKind,
                    CompatibleBaseUrl,
                    "meisterdev/openAiCompatible:ApiKey",
                    AiDiscoveryMode.ManualOnly,
                    [
                        new AiConfiguredModelDto(Guid.Empty, "bge-m3", "bge-m3", [AiOperationKind.Embedding], ["Auto", "Embeddings"], "claude", 8191, 1536),
                    ],
                    [],
                    Secret: "sk-test"));
        }

        var client = this.CreateAuthorizedClient();
        var renamed = await client.PatchAsJsonAsync(
            $"/clients/{factory.ClientId}/ai-connections/{stored.Id}",
            new { displayName = "Renamed" });
        var restated = await client.PatchAsJsonAsync(
            $"/clients/{factory.ClientId}/ai-connections/{stored.Id}",
            new { configuredModels = new[] { new { remoteModelId = "bge-m3", operationKinds = new[] { "embedding" }, tokenizerName = "claude" } } });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, restated.StatusCode);
        Assert.Contains("configuredModels[0].tokenizerName", await restated.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("text-embedding-3-small", null, HttpStatusCode.OK)]
    [InlineData("text-embedding-3-large", null, HttpStatusCode.BadRequest)]
    [InlineData("bge-m3", 1024, HttpStatusCode.BadRequest)]
    public async Task SelectPurposes_AcceptsOnlyAnEmbeddingModelConfiguredFor1536Dimensions(
        string embeddingModel,
        int? embeddingDimensions,
        HttpStatusCode expected)
    {
        using (var ownershipScope = factory.Services.CreateScope())
        {
            var registry = ownershipScope.ServiceProvider.GetRequiredService<IClientRegistry>();
            registry.GetTenantIdAsync(factory.ClientId, Arg.Any<CancellationToken>()).Returns((Guid?)factory.TenantId);
        }

        var client = this.CreateAuthorizedClient();
        var created = await client.PostAsJsonAsync(
            $"/clients/{factory.ClientId}/ai-connections",
            new
            {
                displayName = $"Selection of {embeddingModel}",
                providerKind = "meisterdev/azureOpenAi",
                baseUrl = "https://my-openai.openai.azure.com/",
                auth = new { mode = "meisterdev/azureOpenAi:ApiKey", apiKey = "secret-api-key" },
                discoveryMode = "manualOnly",
                configuredModels = new object[]
                {
                    new { remoteModelId = "gpt-4o", operationKinds = new[] { "chat" } },
                    new { remoteModelId = embeddingModel, operationKinds = new[] { "embedding" }, embeddingDimensions },
                },
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var profile = (await created.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions))!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/clients/{factory.ClientId}/ai-connections/{profile.Id}/verify", null)).StatusCode);

        var chat = profile.ConfiguredModels.Single(model => model.SupportsChat);
        var embedding = profile.ConfiguredModels.Single(model => model.SupportsEmbedding);
        var response = await client.PostAsJsonAsync(
            $"/clients/{factory.ClientId}/ai-connections/select-purposes",
            new
            {
                @default = new { connectionId = profile.Id, configuredModelId = chat.Id },
                high = new { connectionId = profile.Id, configuredModelId = chat.Id },
                embedding = new { connectionId = profile.Id, configuredModelId = embedding.Id },
            });

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest)
        {
            Assert.Contains("1536 dimensions", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    // The measured length is reported whether the check passed or failed, so an operator can enter it.
    [Theory]
    [InlineData(1536, true)]
    [InlineData(3, false)]
    public async Task CheckAiModels_ReportsTheLengthOfTheReturnedVector(int returned, bool passed)
    {
        var created = await this.CreateCompatibleProfileAsync(new { remoteModelId = "bge-m3", operationKinds = new[] { "embedding" } });
        var profile = (await created.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions))!;
        var vector = string.Join(",", Enumerable.Repeat("0.1", returned));
        using var answer = factory.ProviderWire.Answer(_ => Json(
            HttpStatusCode.OK,
            $$$"""{"object":"list","data":[{"object":"embedding","index":0,"embedding":[{{{vector}}}]}],"model":"bge-m3","usage":{"prompt_tokens":3,"total_tokens":3}}"""));

        var response = await this.CreateAuthorizedClient().PostAsJsonAsync(
            $"/clients/{factory.ClientId}/ai-connections/{profile.Id}/check-models",
            new { models = new[] { new { configuredModelId = profile.ConfiguredModels[0].Id, operation = "embedding" } } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var outcome = Assert.Single(body.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal(passed, outcome.GetProperty("passed").GetBoolean());
        Assert.Equal(returned, outcome.GetProperty("returnedDimensions").GetInt32());
    }

    [Fact]
    public async Task DiscoverModels_OffersTheWholeCatalogToACompatibleEndpointBesideTheListedModels()
    {
        factory.CatalogEntries.AddRange(
        [
            CatalogEntry("openai", "gpt-4o", input: 2.5m),
            CatalogEntry("azure", "gpt-4o", input: 2.75m),
            CatalogEntry("openai", "text-embedding-3-small", family: "text-embedding", context: 8191),
            CatalogEntry("mistral", "mistral-embed", family: "mistral-embed", context: 8000),
            CatalogEntry("openai", "gpt-image-1", family: "gpt-image"),
            CatalogEntry("openai", "gpt-realtime-2.1"),
            CatalogEntry("openai", "omni-moderation-latest"),
            CatalogEntry("openai", "listed-chat"),
        ]);
        using var answer = factory.ProviderWire.Answer(request => request.Uri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, """{"data":[{"id":"listed-chat"},{"id":"acme-embedding-small"}]}""")
            : null);
        try
        {
            var response = await this.DiscoverAsync(
                CompatibleKind, CompatibleBaseUrl, new { mode = "apiKey", fields = new Dictionary<string, string> { ["apiKey"] = "sk-test" } });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var raw = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<AiModelDiscoveryResultDto>(raw, ApiJsonOptions)!;

            // A listed embedding model carries the values a save would fill in.
            var listedEmbedding = Assert.Single(result.Models, model => model.RemoteModelId == "acme-embedding-small");
            Assert.Equal(
                ("cl100k_base", (int?)8191, (int?)1536), (listedEmbedding.TokenizerName, listedEmbedding.MaxInputTokens, listedEmbedding.EmbeddingDimensions));

            // Each identifier once, without the listed one and without the image, realtime and moderation models,
            // the embedding models first.
            Assert.Contains("\"catalogModels\"", raw, StringComparison.Ordinal);
            Assert.Equal(["mistral-embed", "text-embedding-3-small", "gpt-4o"], result.CatalogModels.Select(model => model.RemoteModelId));
            Assert.All(result.CatalogModels, model => Assert.Equal(AiConfiguredModelSource.KnownCatalog, model.Source));

            var mistral = Assert.Single(result.CatalogModels, model => model.RemoteModelId == "mistral-embed");
            Assert.Equal([AiOperationKind.Embedding], mistral.OperationKinds);
            Assert.Equal(("cl100k_base", (int?)8000, (int?)1536), (mistral.TokenizerName, mistral.MaxInputTokens, mistral.EmbeddingDimensions));
            Assert.Null(Assert.Single(result.CatalogModels, model => model.RemoteModelId == "gpt-4o").InputCostPer1MUsd);
        }
        finally
        {
            factory.CatalogEntries.Clear();
        }
    }

    // When the listing fails, an operator can pick a model from the catalog models, so they are offered then too.
    // An OpenAI connection is offered the models of the OpenAI catalog provider and no other.
    [Fact]
    public async Task DiscoverModels_OffersTheFamilysCatalogModelsWhenTheListingFails()
    {
        factory.CatalogEntries.AddRange(
        [
            CatalogEntry("openai", "gpt-4o", input: 2.5m),
            CatalogEntry("openai", "text-embedding-3-large", family: "text-embedding", context: 8191),
            CatalogEntry("azure", "gpt-4.1"),
            CatalogEntry("mistral", "mistral-embed", family: "mistral-embed"),
        ]);
        using var answer = factory.ProviderWire.Answer(_ => Json(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key provided."}}"""));
        try
        {
            var response = await this.DiscoverAsync(
                "meisterdev/openAi", "https://api.openai.com/v1", new { mode = "meisterdev/openAi:ApiKey", apiKey = "sk-test" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<AiModelDiscoveryResultDto>(ApiJsonOptions))!;
            Assert.Equal("failed", result.DiscoveryStatus);
            Assert.Empty(result.Models);
            Assert.Equal(["text-embedding-3-large", "gpt-4o"], result.CatalogModels.Select(model => model.RemoteModelId));
            var embedding = result.CatalogModels[0];
            Assert.Equal(("cl100k_base", (int?)8191, (int?)3072), (embedding.TokenizerName, embedding.MaxInputTokens, embedding.EmbeddingDimensions));
            Assert.Equal(2.5m, result.CatalogModels[1].InputCostPer1MUsd);
        }
        finally
        {
            factory.CatalogEntries.Clear();
        }
    }

    [Fact]
    public async Task CreateTenantAiConnection_FillsTheMissingValuesAndRefusesAnUnsupportedTokenizer()
    {
        factory.CatalogEntries.Add(CatalogEntry("acme", "acme-embed-v2", family: "acme-embed", context: 512));
        try
        {
            var client = this.CreateAuthorizedClient();
            var created = await client.PostAsJsonAsync(
                $"/tenants/{factory.TenantId}/ai-connections",
                TenantPayload(new { remoteModelId = "acme-embed-v2", operationKinds = new[] { "embedding" } }));
            var refused = await client.PostAsJsonAsync(
                $"/tenants/{factory.TenantId}/ai-connections",
                TenantPayload(new { remoteModelId = "acme-embed-v2", operationKinds = new[] { "embedding" }, tokenizerName = "claude" }));

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var profile = (await created.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions))!;
            Assert.Equal(("cl100k_base", 512, 1536), Values(profile, "acme-embed-v2"));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("configuredModels[0].tokenizerName", out _));
        }
        finally
        {
            factory.CatalogEntries.Clear();
        }
    }

    private static object TenantPayload(object model)
    {
        return new
        {
            displayName = "Tenant embeddings",
            providerKind = CompatibleKind,
            baseUrl = CompatibleBaseUrl,
            auth = new { mode = "apiKey", fields = new Dictionary<string, string> { ["apiKey"] = "sk-test" } },
            discoveryMode = "manualOnly",
            configuredModels = new[] { model },
        };
    }

    private async Task<HttpResponseMessage> CreateCompatibleProfileAsync(params object[] configuredModels)
    {
        return await this.CreateAuthorizedClient().PostAsJsonAsync(
            $"/clients/{factory.ClientId}/ai-connections",
            new
            {
                displayName = "Embedding defaults",
                providerKind = CompatibleKind,
                baseUrl = CompatibleBaseUrl,
                auth = new { mode = "apiKey", fields = new Dictionary<string, string> { ["apiKey"] = "sk-test" } },
                discoveryMode = "manualOnly",
                configuredModels,
            });
    }

    private async Task<HttpResponseMessage> DiscoverAsync(string providerKind, string baseUrl, object auth)
    {
        return await this.CreateAuthorizedClient().PostAsJsonAsync(
            $"/clients/{factory.ClientId}/ai-connections/discover-models",
            new { providerKind, baseUrl, auth });
    }

    private HttpClient CreateAuthorizedClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        return client;
    }

    private static (string? Tokenizer, int? MaxInputTokens, int? Dimensions) Values(AiConnectionDto profile, string remoteModelId)
    {
        var model = Assert.Single(profile.ConfiguredModels, candidate => candidate.RemoteModelId == remoteModelId);
        Assert.True(model.SupportsEmbedding);
        return (model.TokenizerName, model.MaxInputTokens, model.EmbeddingDimensions);
    }

    private static AiModelCatalogEntryDto CatalogEntry(
        string providerId,
        string remoteModelId,
        string? family = null,
        int? context = null,
        decimal? input = null)
    {
        return new AiModelCatalogEntryDto(
            providerId, providerId, remoteModelId, remoteModelId, family, false, false, false, false, null, context, null,
            input, null, null, null, false, null, AiModelCatalogLayer.Global);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
    {
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static JsonSerializerOptions CreateApiJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
