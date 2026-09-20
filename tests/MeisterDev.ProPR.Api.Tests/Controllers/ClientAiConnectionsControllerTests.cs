// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Api.Tests.Support;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

public sealed class ClientAiConnectionsControllerTests(ClientsControllerTests.ClientsApiFactory factory)
    : IClassFixture<ClientsControllerTests.ClientsApiFactory>
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly JsonSerializerOptions ApiJsonOptions = CreateApiJsonOptions();

    // A stand-in for the credential document a Vertex profile stores. It is shaped like the real thing so the
    // save path treats it as one, and holds nothing.
    private const string ServiceAccountDocument =
        "{\"type\":\"service_account\",\"project_id\":\"meister-dev-test\",\"client_email\":\"test@example.com\"}";

    // A base URL each family's driver accepts, so a create can be exercised against every one of them. The hosts
    // are pinned by the drivers themselves — an Azure resource, an AWS region, a Vertex location — so one URL
    // cannot serve all seven.
    private static readonly Dictionary<string, string> ProbeableBaseUrlByFamily = new(StringComparer.Ordinal)
    {
        ["meisterdev/azureOpenAi"] = "https://my-openai.openai.azure.com/",
        ["meisterdev/openAi"] = "https://api.openai.com/v1",
        ["meisterdev/liteLlm"] = "https://gateway.example.com/v1",
        ["meisterdev/openAiCompatible"] = "https://llm.example.com/v1",
        ["meisterdev/anthropic"] = "https://api.anthropic.com/v1",
        ["meisterdev/awsBedrock"] = "https://bedrock-runtime.eu-central-1.amazonaws.com",
        ["meisterdev/googleVertex"] = "https://europe-west4-aiplatform.googleapis.com",
        [MultiFieldCredentialFamily.FamilyKey] = MultiFieldCredentialFamily.AcceptableBaseUrl,
    };

    // Google is the one family whose credential shapes belong to two surfaces: a key is read by the Gemini API
    // and a credential document by Vertex, and each surface refuses the other's mode.
    // A credential or wire shape as it persists: the declaring family's key joined to the mode name. The two
    // shapes the host reserves, Auto and Embeddings, belong to no family and carry no qualifier.
    private static string Shape(string family, string modeName)
    {
        return $"{family}:{modeName}";
    }

    private static string ProbeableBaseUrl(string family, string authMode)
    {
        if (family == "meisterdev/googleVertex" && authMode == Shape(family, "ApiKey"))
        {
            return "https://generativelanguage.googleapis.com";
        }

        Assert.True(
            ProbeableBaseUrlByFamily.TryGetValue(family, out var baseUrl),
            $"No probeable base URL is known for the '{family}' family.");

        return baseUrl;
    }

    private HttpClient CreateAuthorizedClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        return client;
    }

    // A model states the host-reserved automatic shape and nothing else, which a binding names when it
    // has no opinion. Every family serves it, so one payload builder covers all of them; a wire shape a family
    // owns is qualified by that family's key and belongs to no other.
    private static object BuildConfiguredModel(string remoteModelId, bool embedding = false)
    {
        return embedding
            ? new
            {
                remoteModelId,
                displayName = remoteModelId,
                operationKinds = new[] { "embedding" },
                supportedProtocolModes = new[] { "Auto", "Embeddings" },
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
                supportedProtocolModes = new[] { "Auto" },
                supportsStructuredOutput = true,
                supportsToolUse = true,
                source = "manual",
            };
    }

    private static object[] BuildBindings(
        string primaryChatModel,
        string embeddingModel,
        bool includeEffortOverrides = true,
        string protocolMode = "Auto")
    {
        var bindings = new List<object>
        {
            new { purpose = "reviewDefault", remoteModelId = primaryChatModel, protocolMode, isEnabled = true },
            new { purpose = "memoryReconsideration", remoteModelId = primaryChatModel, protocolMode = "Auto", isEnabled = true },
            new { purpose = "embeddingDefault", remoteModelId = embeddingModel, protocolMode = "Embeddings", isEnabled = true },
        };

        if (includeEffortOverrides)
        {
            bindings.InsertRange(
                1,
                [
                    new { purpose = "reviewLowEffort", remoteModelId = primaryChatModel, protocolMode = "Auto", isEnabled = true },
                    new { purpose = "reviewMediumEffort", remoteModelId = primaryChatModel, protocolMode = "Auto", isEnabled = true },
                    new { purpose = "reviewHighEffort", remoteModelId = primaryChatModel, protocolMode = "Auto", isEnabled = true },
                ]);
        }

        return bindings.ToArray();
    }

    private static object BuildCreatePayload(
        string displayName,
        IReadOnlyList<string>? chatModels = null,
        string? baseUrl = null,
        bool includeEffortOverrides = true,
        string providerKind = "meisterdev/azureOpenAi",
        string? protocolMode = null)
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
                mode = Shape(providerKind, "ApiKey"),
                apiKey = "secret-api-key",
            },
            discoveryMode = "manualOnly",
            configuredModels = resolvedChatModels
                .Select(model => BuildConfiguredModel(model))
                .Concat([BuildConfiguredModel(embeddingModel, true)]),
            purposeBindings = BuildBindings(
                resolvedChatModels[0],
                embeddingModel,
                includeEffortOverrides,
                protocolMode ?? "Auto"),
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

        foreach (var family in new[]
                 {
                     "meisterdev/azureOpenAi", "meisterdev/openAi", "meisterdev/liteLlm", "meisterdev/openAiCompatible", "meisterdev/anthropic",
                     "meisterdev/awsBedrock", "meisterdev/googleVertex"
                 })
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
            .GetProperty("protocolModes").EnumerateArray().Select(m => m.GetProperty("value").GetString()).ToList();

        Assert.Contains(Shape("meisterdev/azureOpenAi", "Responses"), shapesOf("meisterdev/azureOpenAi"));
        Assert.DoesNotContain(Shape("meisterdev/openAiCompatible", "Responses"), shapesOf("meisterdev/openAiCompatible"));
        Assert.Contains(Shape("meisterdev/openAiCompatible", "ChatCompletions"), shapesOf("meisterdev/openAiCompatible"));
    }

    // A console that kept its own catalogue of names could only name the families it shipped knowing about, so
    // every name it renders is served here: the family's own, and one per shape it speaks and authenticates with.
    [Fact]
    public async Task PermittedProviders_NamesEveryFamilyAndEveryShapeItOffers()
    {
        var client = this.CreateAuthorizedClient();

        var response = await client.GetAsync($"/clients/{ClientId}/ai-connections/permitted-providers");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ApiJsonOptions);
        var providers = payload.GetProperty("providers").EnumerateArray().ToList();

        foreach (var provider in providers)
        {
            Assert.False(string.IsNullOrWhiteSpace(provider.GetProperty("label").GetString()));

            foreach (var shape in provider.GetProperty("protocolModes").EnumerateArray())
            {
                Assert.False(string.IsNullOrWhiteSpace(shape.GetProperty("label").GetString()));
            }

            foreach (var mode in provider.GetProperty("authModes").EnumerateArray())
            {
                Assert.False(string.IsNullOrWhiteSpace(mode.GetProperty("label").GetString()));
            }
        }
    }

    // A name a family states is served as the family spelled it; one it does not state is read off the member
    // name at its word boundaries, so a shape no console has heard of still reads as words rather than as a key.
    [Fact]
    public async Task PermittedProviders_PrefersTheNameAFamilyStatesOverTheMemberName()
    {
        var client = this.CreateAuthorizedClient();

        var response = await client.GetAsync($"/clients/{ClientId}/ai-connections/permitted-providers");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ApiJsonOptions);
        var providers = payload.GetProperty("providers").EnumerateArray().ToList();

        var labelOf = (string kind, string collection, string value) => providers
            .Single(provider => provider.GetProperty("providerKind").GetString() == kind)
            .GetProperty(collection)
            .EnumerateArray()
            .Single(entry => entry.GetProperty("value").GetString() == value)
            .GetProperty("label")
            .GetString();

        // Stated by the family, because reading the member name at its word boundaries would give "Api Key".
        Assert.Equal(
            "API Key",
            labelOf("meisterdev/awsBedrock", "authModes", Shape("meisterdev/awsBedrock", "ApiKey")));

        // Not stated by any family: read off AnthropicMessages and BedrockConverse.
        Assert.Equal(
            "Anthropic Messages",
            labelOf("meisterdev/anthropic", "protocolModes", Shape("meisterdev/anthropic", "AnthropicMessages")));
        Assert.Equal(
            "Bedrock Converse",
            labelOf("meisterdev/awsBedrock", "protocolModes", Shape("meisterdev/awsBedrock", "BedrockConverse")));
    }

    // The base URL box takes a resource endpoint on one family and a regional host on another, and a required
    // query parameter on a third. The console renders family-neutral text where a family says nothing, so what a
    // family does say has to reach it.
    [Fact]
    public async Task PermittedProviders_ReportsWhatEachFamilySaysAboutTheConnectionForm()
    {
        var client = this.CreateAuthorizedClient();

        var response = await client.GetAsync($"/clients/{ClientId}/ai-connections/permitted-providers");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ApiJsonOptions);
        var providers = payload.GetProperty("providers").EnumerateArray().ToList();

        var formOf = (string kind) => providers
            .Single(provider => provider.GetProperty("providerKind").GetString() == kind)
            .GetProperty("connectionForm");

        Assert.Contains("openai.azure.com", formOf("meisterdev/azureOpenAi").GetProperty("baseUrlPlaceholder").GetString());
        Assert.Contains("region", formOf("meisterdev/awsBedrock").GetProperty("baseUrlHint").GetString(), StringComparison.Ordinal);
        Assert.Equal("project", formOf("meisterdev/googleVertex").GetProperty("requiredQueryParam").GetString());

        // A family with no opinion about a box leaves it unstated rather than borrowing another family's example.
        Assert.Equal(JsonValueKind.Null, formOf("meisterdev/anthropic").GetProperty("requiredQueryParam").ValueKind);
    }

    // The credential shapes are per family for the same reason the wire shapes are, and they differ between
    // families: an Azure resource takes a managed identity, Bedrock signs with an access key, an arbitrary
    // OpenAI-compatible server reads a bearer key and nothing else. A UI holding its own copy of that offers
    // modes the family cannot read, so the answer is served from the drivers here.
    [Fact]
    public async Task PermittedProviders_ReportsTheCredentialShapesEachFamilyReads()
    {
        var client = this.CreateAuthorizedClient();

        var response = await client.GetAsync($"/clients/{ClientId}/ai-connections/permitted-providers");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ApiJsonOptions);
        var providers = payload.GetProperty("providers").EnumerateArray().ToList();

        var modesOf = (string kind) => providers
            .Single(provider => provider.GetProperty("providerKind").GetString() == kind)
            .GetProperty("authModes").EnumerateArray().Select(mode => mode.GetProperty("value").GetString()).ToList();

        Assert.Equal(
            [Shape("meisterdev/azureOpenAi", "ApiKey"), Shape("meisterdev/azureOpenAi", "AzureIdentity")],
            modesOf("meisterdev/azureOpenAi"));
        Assert.Equal(
            [Shape("meisterdev/awsBedrock", "ApiKey")],
            modesOf("meisterdev/awsBedrock"));
        Assert.Equal([Shape("meisterdev/openAiCompatible", "ApiKey")], modesOf("meisterdev/openAiCompatible"));
    }

    // A credential is one key for most families and several values for some, and a form that assumed one input
    // could not configure the others at all. Which fields a mode needs is the driver's own answer, served here
    // beside the modes, because choosing a mode is choosing which of these to fill in.
    [Fact]
    public async Task PermittedProviders_ReportsTheCredentialFieldsEachModeNeeds()
    {
        var client = this.CreateAuthorizedClient();

        var response = await client.GetAsync($"/clients/{ClientId}/ai-connections/permitted-providers");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ApiJsonOptions);
        var providers = payload.GetProperty("providers").EnumerateArray().ToList();

        var fieldsOf = (string kind, string mode) => providers
            .Single(provider => provider.GetProperty("providerKind").GetString() == kind)
            .GetProperty("credentialFields")
            .GetProperty(mode)
            .EnumerateArray()
            .Select(field => field.GetProperty("name").GetString())
            .ToList();

        Assert.Equal(
            [
                MultiFieldCredentialFamily.PrincipalField,
                MultiFieldCredentialFamily.SecretField,
                MultiFieldCredentialFamily.ScopeField,
            ],
            fieldsOf(MultiFieldCredentialFamily.FamilyKey, MultiFieldCredentialFamily.CredentialAuth));
        Assert.Equal(
            ["serviceAccountJson"],
            fieldsOf("meisterdev/googleVertex", Shape("meisterdev/googleVertex", "GcpAdc")));
        Assert.Equal(["apiKey"], fieldsOf("meisterdev/anthropic", Shape("meisterdev/anthropic", "ApiKey")));

        // An ambient identity has nothing for an operator to enter, and a form that rendered a key box for it
        // would be asking for a credential the mode exists to avoid.
        Assert.Empty(fieldsOf("meisterdev/azureOpenAi", Shape("meisterdev/azureOpenAi", "AzureIdentity")));
    }


    // Anthropic reads its key from a header of its own and rejects a bearer token, so where the stored key ends
    // up is the difference between a working profile and a 401. The connection names the one credential shape
    // the family declares; the header is applied by the family and is not part of what was stored.
    [Fact]
    public async Task VerifyAiConnection_ForAnthropic_SendsTheStoredKeyInTheHeaderAnthropicReads()
    {
        var profile = await this.SeedProviderProfileAsync(
            "meisterdev/anthropic",
            "apiKey",
            "https://api.anthropic.com/v1",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["apiKey"] = "sk-ant-example-key" },
            "claude-sonnet-4");

        var verification = await this.VerifyAsync(profile);

        Assert.Equal(HttpStatusCode.OK, verification.StatusCode);
        var sent = factory.ProviderWire.Single();
        Assert.Equal(new Uri("https://api.anthropic.com/v1/models"), sent.Uri);
        Assert.Equal("sk-ant-example-key", sent.Header("x-api-key"));
        Assert.Null(sent.Header("Authorization"));
    }

    // Vertex is served from an add-in in the built-in directory rather than from a driver the host composes, so
    // this is the whole path: the stored credential document is read back, handed to a family loaded from a
    // file, and reaches the point where a token would be minted from it. The document seeded here carries no
    // private key, and the refusal that names it is what only that family produces — so a verification that
    // reported anything else would mean the call never arrived there.
    [Fact]
    public async Task VerifyAiConnection_ForVertex_ReachesTheAddInWithTheStoredCredentialDocument()
    {
        var profile = await this.SeedProviderProfileAsync(
            "meisterdev/googleVertex",
            "gcpAdc",
            "https://europe-west4-aiplatform.googleapis.com?project=meister-dev-test",
            CredentialFor("gcpAdc"),
            "gemini-3-pro",
            new Dictionary<string, string> { ["project"] = "meister-dev-test" });

        var verification = await this.VerifyAsync(profile);

        Assert.Equal(HttpStatusCode.OK, verification.StatusCode);
        var body = await verification.Content.ReadAsStringAsync();
        Assert.Contains("Google credential", body, StringComparison.Ordinal);

        // The credential itself never reaches a response, whatever the family says about it.
        Assert.DoesNotContain("client_email", body, StringComparison.Ordinal);
    }

    private async Task<AiConnectionDto> SeedProviderProfileAsync(
        string family,
        string authMode,
        string baseUrl,
        IReadOnlyDictionary<string, string> fields,
        string remoteModelId,
        IReadOnlyDictionary<string, string>? defaultQueryParams = null)
    {
        var client = this.CreateAuthorizedClient();
        var response = await client.PostAsJsonAsync(
            $"/clients/{ClientId}/ai-connections",
            new
            {
                displayName = $"{family} credential fields",
                providerKind = family,
                baseUrl,
                auth = new { mode = authMode, fields },
                discoveryMode = "manualOnly",
                defaultQueryParams,
                configuredModels = new[] { BuildConfiguredModel(remoteModelId) },
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);
        Assert.NotNull(created);
        return created;
    }

    // The driver reads the stored credential, not the request body that created the profile, so what leaves on
    // a verify is what survived storage. Recording starts here so a test reads only its own traffic.
    private async Task<HttpResponseMessage> VerifyAsync(AiConnectionDto profile)
    {
        factory.ProviderWire.Clear();

        return await this.CreateAuthorizedClient()
            .PostAsync($"/clients/{ClientId}/ai-connections/{profile.Id}/verify", null);
    }

    [Fact]
    public async Task CreateAiConnection_WithARequiredCredentialFieldLeftEmpty_IsRefusedNamingTheField()
    {
        var client = this.CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            $"/clients/{ClientId}/ai-connections",
            new
            {
                displayName = "A credential without its secret",
                providerKind = MultiFieldCredentialFamily.FamilyKey,
                baseUrl = MultiFieldCredentialFamily.AcceptableBaseUrl,
                auth = new
                {
                    mode = "Credential",
                    fields = new Dictionary<string, string>
                    {
                        [MultiFieldCredentialFamily.PrincipalField] = "a-principal",
                        [MultiFieldCredentialFamily.SecretField] = "   ",
                    },
                },
                discoveryMode = "manualOnly",
                configuredModels = new[] { BuildConfiguredModel("anthropic.claude-3-5-sonnet") },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            MultiFieldCredentialFamily.SecretField,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    // A value entered under a name the family does not read would sit in the row and never be sent, so the
    // operator would believe a credential is in use that is not.
    [Fact]
    public async Task CreateAiConnection_WithACredentialFieldTheFamilyDoesNotDeclare_IsRefusedNamingIt()
    {
        var client = this.CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            $"/clients/{ClientId}/ai-connections",
            new
            {
                displayName = "A credential with a field nobody reads",
                providerKind = MultiFieldCredentialFamily.FamilyKey,
                baseUrl = MultiFieldCredentialFamily.AcceptableBaseUrl,
                auth = new
                {
                    mode = "Credential",
                    fields = new Dictionary<string, string>
                    {
                        [MultiFieldCredentialFamily.PrincipalField] = "a-principal",
                        [MultiFieldCredentialFamily.SecretField] = "a-secret",
                        ["regionNobodyDeclared"] = "eu-central-1",
                    },
                },
                discoveryMode = "manualOnly",
                configuredModels = new[] { BuildConfiguredModel("anthropic.claude-3-5-sonnet") },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "regionNobodyDeclared",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    // A name is stored as it was submitted. Were it trimmed first, ' principal ' and 'principal' would
    // arrive as one entry with whichever came last silently displacing the other, so an operator could overwrite
    // a credential field by padding its name.
    [Fact]
    public async Task CreateAiConnection_WithAPaddedCredentialFieldName_IsRefusedAndDoesNotDisplaceTheRealField()
    {
        var client = this.CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            $"/clients/{ClientId}/ai-connections",
            new
            {
                displayName = "Bedrock with a padded field name",
                providerKind = MultiFieldCredentialFamily.FamilyKey,
                baseUrl = "https://bedrock-runtime.eu-central-1.amazonaws.com",
                auth = new
                {
                    mode = "Credential",
                    fields = new Dictionary<string, string>
                    {
                        [MultiFieldCredentialFamily.PrincipalField] = "a-principal",
                        [" principal "] = "displaced-the-real-one",
                        [MultiFieldCredentialFamily.SecretField] = "a-secret",
                    },
                },
                discoveryMode = "manualOnly",
                configuredModels = new[] { BuildConfiguredModel("anthropic.claude-3-5-sonnet") },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(" principal ", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // Google's two surfaces read different credentials and the URL says which, so the mode has to match. The
    // Gemini direction is the one that matters most: that surface reads its credential from a header, and a
    // profile holding a service-account document would send a private key in one.
    [Theory]
    [InlineData("https://europe-west4-aiplatform.googleapis.com", "apiKey", "Vertex AI")]
    [InlineData("https://generativelanguage.googleapis.com", "gcpAdc", "Gemini API")]
    public async Task CreateAiConnection_WhereTheModeDoesNotMatchTheGoogleSurface_IsRefusedNamingBoth(
        string baseUrl,
        string authMode,
        string surface)
    {
        var client = this.CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            $"/clients/{ClientId}/ai-connections",
            new
            {
                displayName = $"Google {authMode} on {surface}",
                providerKind = "meisterdev/googleVertex",
                baseUrl,
                auth = new { mode = authMode, fields = CredentialFor(authMode) },
                discoveryMode = "manualOnly",
                configuredModels = new[] { BuildConfiguredModel("gemini-3-pro") },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(surface, body, StringComparison.Ordinal);

        // Both shapes are named the way the form names them, which an operator has to pick between.
        Assert.Contains(
            authMode == "apiKey" ? "API Key" : "Google Application Default Credentials",
            body,
            StringComparison.Ordinal);
    }

    // A stored credential belongs to the mode it was saved under. Moving a Vertex profile onto the Gemini
    // surface and the API-key mode while leaving the credential boxes empty must not carry the service-account
    // document over as the key: the Gemini surface puts whatever it is given into the 'x-goog-api-key' header,
    // so the document's private key would leave the product in one.
    [Fact]
    public async Task UpdateAiConnection_MovingAVertexProfileToTheGeminiKeyMode_RefusesToCarryTheStoredDocument()
    {
        var client = this.CreateAuthorizedClient();
        var created = await client.PostAsJsonAsync(
            $"/clients/{ClientId}/ai-connections",
            new
            {
                displayName = "Vertex before the move",
                providerKind = "meisterdev/googleVertex",
                baseUrl = "https://europe-west4-aiplatform.googleapis.com?project=meister-dev-test",
                auth = new { mode = "gcpAdc", fields = CredentialFor("gcpAdc") },
                discoveryMode = "manualOnly",
                defaultQueryParams = new Dictionary<string, string> { ["project"] = "meister-dev-test" },
                configuredModels = new[] { BuildConfiguredModel("gemini-3-pro") },
            });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var profile = await created.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);
        Assert.NotNull(profile);

        var moved = await client.PatchAsJsonAsync(
            $"/clients/{ClientId}/ai-connections/{profile.Id}",
            new
            {
                baseUrl = "https://generativelanguage.googleapis.com",
                auth = new { mode = "apiKey" },
                defaultQueryParams = new Dictionary<string, string>(),
            });

        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
        var body = await moved.Content.ReadAsStringAsync();
        Assert.Contains("apiKey", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private_key", body, StringComparison.Ordinal);
    }

    // Editing anything other than the credential leaves its boxes empty, and the stored one has to survive that.
    // A multi-field credential is stored as an envelope, so carrying it forward as a single key would store the
    // envelope's own text and the next call would sign with nonsense.
    [Fact]
    public async Task UpdateAiConnection_WithoutReEnteringAMultiFieldCredential_KeepsTheStoredOne()
    {
        var client = this.CreateAuthorizedClient();
        var created = await client.PostAsJsonAsync(
            $"/clients/{ClientId}/ai-connections",
            new
            {
                displayName = "A multi-field credential before the rename",
                providerKind = MultiFieldCredentialFamily.FamilyKey,
                baseUrl = MultiFieldCredentialFamily.AcceptableBaseUrl,
                auth = new
                {
                    mode = "Credential",
                    fields = new Dictionary<string, string>
                    {
                        [MultiFieldCredentialFamily.PrincipalField] = "a-principal",
                        [MultiFieldCredentialFamily.SecretField] = "a-secret",
                    },
                },
                discoveryMode = "manualOnly",
                configuredModels = new[] { BuildConfiguredModel("anthropic.claude-3-5-sonnet") },
            });
        var profile = await created.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);
        Assert.NotNull(profile);

        var renamed = await client.PatchAsJsonAsync(
            $"/clients/{ClientId}/ai-connections/{profile.Id}",
            new { displayName = "A multi-field credential after the rename" });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        // Verification resolves the stored credential, so a credential lost in the round trip is refused here
        // rather than reported as a signing failure much later.
        var verification = await client.PostAsync($"/clients/{ClientId}/ai-connections/{profile.Id}/verify", null);
        Assert.Equal(HttpStatusCode.OK, verification.StatusCode);
        Assert.DoesNotContain(
            "needs an access key",
            await verification.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    // Credential material leaves the product only towards the provider. Nothing an operator entered may come
    // back on any read, whatever shape it was entered in.
    [Fact]
    public async Task AStoredCredentialFieldIsNeverReturnedByTheApi()
    {
        var client = this.CreateAuthorizedClient();
        var fields = CredentialFor("sigV4");

        var created = await client.PostAsJsonAsync(
            $"/clients/{ClientId}/ai-connections",
            new
            {
                displayName = "Bedrock credential readback",
                providerKind = MultiFieldCredentialFamily.FamilyKey,
                baseUrl = "https://bedrock-runtime.eu-central-1.amazonaws.com",
                auth = new { mode = "sigV4", fields },
                discoveryMode = "manualOnly",
                configuredModels = new[] { BuildConfiguredModel("anthropic.claude-3-5-sonnet") },
            });

        var profile = await created.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);
        Assert.NotNull(profile);

        var bodies = new List<string>
        {
            await created.Content.ReadAsStringAsync(),
            await (await client.GetAsync($"/clients/{ClientId}/ai-connections")).Content.ReadAsStringAsync(),
            await (await client.PostAsync($"/clients/{ClientId}/ai-connections/{profile.Id}/verify", null))
                .Content.ReadAsStringAsync(),
        };

        foreach (var value in fields.Values)
        {
            Assert.All(bodies, body => Assert.DoesNotContain(value, body, StringComparison.Ordinal));
        }
    }

    private static Dictionary<string, string> CredentialFor(string authMode)
    {
        return authMode switch
        {
            "sigV4" => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["accessKeyId"] = "AKIAEXAMPLEACCESSKEY",
                ["secretAccessKey"] = "wJalrXUtnFEMI-EXAMPLE-SECRET",
                ["sessionToken"] = "FwoGZXIvYXdzE-EXAMPLE-SESSION",
            },
            "Credential" => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MultiFieldCredentialFamily.PrincipalField] = "a-principal",
                [MultiFieldCredentialFamily.SecretField] = "a-secret",
                [MultiFieldCredentialFamily.ScopeField] = "a-scope",
            },
            "gcpAdc" => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["serviceAccountJson"] = ServiceAccountDocument,
            },
            _ => new Dictionary<string, string>(StringComparer.Ordinal) { ["apiKey"] = "secret-api-key" },
        };
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
            BuildCreatePayload(
                "Anthropic-shaped binding",
                protocolMode: Shape("meisterdev/anthropic", "AnthropicMessages")));

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
        Assert.Equal("meisterdev/azureOpenAi", created.ProviderKind);
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
            providerKind = "meisterdev/openAi",
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
            providerKind = "meisterdev/openAi",
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
            providerKind = "meisterdev/openAi",
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
            providerKind = "meisterdev/azureOpenAi",
            baseUrl = "https://internal.corp.example/",
            auth = new { mode = "apiKey", apiKey = "secret-api-key" },
        };

        var response = await client.PostAsJsonAsync($"/clients/{ClientId}/ai-connections/discover-models", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Azure AI host", body, StringComparison.OrdinalIgnoreCase);
    }

    // Discovery dials the provider with the supplied credential, so the tenant's policy answers for it on the
    // same terms probing does. Without this the policy is bypassed by choosing this endpoint instead: an
    // operator-supplied credential reaches an operator-supplied host the tenant forbade.
    [Fact]
    public async Task DiscoverModels_WhenTheTenantDoesNotPermitTheFamily_IsRefusedBeforeAnyProviderIsContacted()
    {
        var client = this.CreateAuthorizedClient();
        factory.ProviderWire.Clear();
        factory.ProviderPolicy = new TenantProviderPolicy(["meisterdev/azureOpenAi"], []);

        try
        {
            var response = await client.PostAsJsonAsync(
                $"/clients/{ClientId}/ai-connections/discover-models",
                new
                {
                    providerKind = "meisterdev/openAi",
                    baseUrl = "https://api.openai.com/v1",
                    auth = new { mode = "apiKey", apiKey = "secret-api-key" },
                });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Empty(factory.ProviderWire.Requests);
        }
        finally
        {
            factory.ProviderPolicy = TenantProviderPolicy.Unrestricted;
        }
    }

    [Fact]
    public async Task DiscoverModels_WhenTheTenantDoesNotPermitTheHost_IsRefusedBeforeAnyProviderIsContacted()
    {
        var client = this.CreateAuthorizedClient();
        factory.ProviderWire.Clear();
        factory.ProviderPolicy = new TenantProviderPolicy([], ["gateway.example.com"]);

        try
        {
            var response = await client.PostAsJsonAsync(
                $"/clients/{ClientId}/ai-connections/discover-models",
                new
                {
                    providerKind = "meisterdev/openAi",
                    baseUrl = "https://api.openai.com/v1",
                    auth = new { mode = "apiKey", apiKey = "secret-api-key" },
                });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(
                "gateway.example.com",
                await response.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
            Assert.Empty(factory.ProviderWire.Requests);
        }
        finally
        {
            factory.ProviderPolicy = TenantProviderPolicy.Unrestricted;
        }
    }

    // An address carrying userinfo holds a credential in a field nothing treats as one: it is stored in the
    // clear beside the profile and rendered into anything quoting the address.
    [Fact]
    public async Task DiscoverModels_WithACredentialInTheAddress_Returns400()
    {
        var client = this.CreateAuthorizedClient();
        factory.ProviderWire.Clear();

        var response = await client.PostAsJsonAsync(
            $"/clients/{ClientId}/ai-connections/discover-models",
            new
            {
                providerKind = "meisterdev/openAi",
                baseUrl = "https://someone:sk-live-0123456789@api.openai.com/v1",
                auth = new { mode = "apiKey", apiKey = "secret-api-key" },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("must not carry a credential in the address", body, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-0123456789", body, StringComparison.Ordinal);
        Assert.Empty(factory.ProviderWire.Requests);
    }

    // The operator route writes into the same protected column a provider family's own credential goes into, so
    // it is held to the same bound. Without it the only bound left is the request-body limit of the host.
    [Fact]
    public async Task CreateAiConnection_WithACredentialFieldPastTheHostBound_Returns400()
    {
        var client = this.CreateAuthorizedClient();
        var payload = new
        {
            displayName = "Oversized credential",
            providerKind = "meisterdev/openAi",
            baseUrl = "https://api.openai.com/v1",
            auth = new { mode = "apiKey", apiKey = new string('k', ProviderHostLimits.MaximumCredentialFieldLength + 1) },
            discoveryMode = "manualOnly",
            configuredModels = new[] { BuildConfiguredModel("gpt-4o"), BuildConfiguredModel("text-embedding-3-large", true) },
            purposeBindings = BuildBindings("gpt-4o", "text-embedding-3-large"),
        };

        var response = await client.PostAsJsonAsync($"/clients/{ClientId}/ai-connections", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            $"at most {ProviderHostLimits.MaximumCredentialFieldLength}",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    // A family authenticates with the credential shapes its driver declares and no others. A row carrying a mode
    // the family does not read cannot be verified: the credential would go somewhere the provider never looks,
    // and the reply would be a provider-worded rejection naming neither the mode nor the family. Refusing at
    // verification also keeps the profile out of a review, because activation requires a verified connection.
    // Such a row can only predate the rule, so the stored mode is reached directly rather than through the write
    // path, which refuses the same combination.
    [Fact]
    public async Task VerifyAiConnection_WithAStoredAuthModeTheFamilyDoesNotDeclare_Returns400NamingTheModeAndFamily()
    {
        var client = this.CreateAuthorizedClient();
        var created = await this.SeedVertexConnectionAsync("Vertex Profile (stored mode)");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            var stored = await db.AiConnectionProfiles.SingleAsync(profile => profile.Id == created.Id);
            stored.AuthMode = "meisterdev/azureOpenAi:SigV4";
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsync($"/clients/{ClientId}/ai-connections/{created.Id}/verify", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("meisterdev/azureOpenAi:SigV4", body, StringComparison.Ordinal);
        Assert.Contains("meisterdev/googleVertex", body, StringComparison.Ordinal);
    }

    // A profile whose stored provider family this build cannot name is refused before a driver is resolved for
    // it, because there is no family to resolve one from and the stored credential belongs to whichever family
    // wrote it. The console stops offering the button, but the endpoint is reachable on its own.
    [Fact]
    public async Task VerifyAiConnection_WithAStoredFamilyThisBuildCannotName_IsRefusedBeforeAnyProviderIsContacted()
    {
        var client = this.CreateAuthorizedClient();
        var created = await this.SeedVertexConnectionAsync("Vertex Profile (unknown family)");
        factory.ProviderWire.Clear();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            var stored = await db.AiConnectionProfiles.SingleAsync(profile => profile.Id == created.Id);
            stored.ProviderKind = "ArrakisIntelligence";
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsync($"/clients/{ClientId}/ai-connections/{created.Id}/verify", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "ArrakisIntelligence",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.Empty(factory.ProviderWire.Requests);
    }

    // A tenant that adds or tightens its endpoint list after a profile was saved has to be able to rely on it.
    // Verification sends the stored credential, so the host is answered for before anything is dialled — the
    // family leg alone does not cover it, because a permitted family reached at an operator-supplied base URL
    // constrains no destination.
    [Fact]
    public async Task VerifyAiConnection_WhenTheTenantNoLongerPermitsTheHost_IsRefusedBeforeAnyProviderIsContacted()
    {
        var client = this.CreateAuthorizedClient();
        var created = await this.SeedConnectionAsync("Primary Profile");
        factory.ProviderWire.Clear();
        factory.ProviderPolicy = new TenantProviderPolicy([], ["api.openai.com"]);

        try
        {
            var response = await client.PostAsync($"/clients/{ClientId}/ai-connections/{created.Id}/verify", null);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(
                "api.openai.com",
                await response.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
            Assert.Empty(factory.ProviderWire.Requests);
        }
        finally
        {
            factory.ProviderPolicy = TenantProviderPolicy.Unrestricted;
        }
    }

    // A tenant-scoped profile has no edit route of its own: the tenant surface creates, deletes and verifies.
    // The client edit route is the one place a stored credential is carried across a write, and it answers only
    // for a profile the client owns, and that keeps that carry-forward away from tenant profiles. A change
    // that let one through here would put a mode change with no re-entered credential back on the tenant path.
    [Fact]
    public async Task UpdateAiConnection_ForATenantScopedProfile_Returns404()
    {
        var client = this.CreateAuthorizedClient();
        var created = await this.SeedConnectionAsync("Inherited Profile");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            var stored = await db.AiConnectionProfiles.SingleAsync(profile => profile.Id == created.Id);
            stored.ClientId = null;
            stored.TenantId = Guid.NewGuid();
            await db.SaveChangesAsync();
        }

        var response = await client.PatchAsJsonAsync(
            $"/clients/{ClientId}/ai-connections/{created.Id}",
            new { auth = new { mode = "apiKey" } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // The same rule on the write path, which is where an operator meets it. Saving the combination would store a
    // profile that can never be verified, so it is refused while the form is still open — and the refusal names
    // the mode and the family, because neither is obvious from a provider's own authentication failure.
    [Fact]
    public async Task CreateAiConnection_WithAnAuthModeTheFamilyDoesNotDeclare_Returns400NamingTheModeAndFamily()
    {
        var client = this.CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            $"/clients/{ClientId}/ai-connections",
            new
            {
                displayName = "Vertex Profile (signed)",
                providerKind = "meisterdev/googleVertex",
                baseUrl = "https://europe-west4-aiplatform.googleapis.com",
                auth = new { mode = "sigV4", apiKey = "service-account-json" },
                discoveryMode = "manualOnly",
                configuredModels = new[] { BuildConfiguredModel("gemini-3-pro") },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // Named as it was sent: this family neither declares that shape nor supersedes its spelling.
        Assert.Contains("sigV4", body, StringComparison.Ordinal);
        Assert.Contains("meisterdev/googleVertex", body, StringComparison.Ordinal);
    }

    // An update can switch the mode as well as the family, so it is held to the same rule as a create. Without
    // this a profile saved with a declared mode could be edited into one the family cannot read.
    [Fact]
    public async Task UpdateAiConnection_SwitchingToAnAuthModeTheFamilyDoesNotDeclare_Returns400()
    {
        var created = await this.SeedVertexConnectionAsync("Vertex Profile (edited)");
        var client = this.CreateAuthorizedClient();

        var response = await client.PatchAsJsonAsync(
            $"/clients/{ClientId}/ai-connections/{created.Id}",
            new { auth = new { mode = "azureIdentity", apiKey = "service-account-json" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // Named as it was sent: this family neither declares that shape nor supersedes its spelling, so there
        // is nothing else the refusal could call it.
        Assert.Contains("azureIdentity", body, StringComparison.Ordinal);
        Assert.Contains("meisterdev/googleVertex", body, StringComparison.Ordinal);
    }

    // The refusal must narrow the write path to what the drivers declare and nothing further: every combination
    // the options endpoint offers has to be savable, or the console would present a mode the save then rejects.
    // The offer is read from the endpoint rather than restated here — the modes, and now the fields each one
    // needs — so a driver that changes either is covered without this test being edited.
    [Fact]
    public async Task CreateAiConnection_WithEveryAuthModeItsFamilyDeclares_IsAccepted()
    {
        var client = this.CreateAuthorizedClient();

        var offerResponse = await client.GetAsync($"/clients/{ClientId}/ai-connections/permitted-providers");
        var offer = await offerResponse.Content.ReadFromJsonAsync<JsonElement>(ApiJsonOptions);

        foreach (var provider in offer.GetProperty("providers").EnumerateArray())
        {
            var family = provider.GetProperty("providerKind").GetString()!;

            foreach (var authMode in provider.GetProperty("authModes")
                         .EnumerateArray()
                         .Select(mode => mode.GetProperty("value").GetString()!))
            {
                var baseUrl = ProbeableBaseUrl(family, authMode);
                var response = await client.PostAsJsonAsync(
                    $"/clients/{ClientId}/ai-connections",
                    new
                    {
                        displayName = $"{family} over {authMode}",
                        providerKind = family,
                        baseUrl,
                        auth = new
                        {
                            mode = authMode,
                            fields = FillDeclaredFields(provider, authMode),
                        },
                        discoveryMode = "manualOnly",
                        configuredModels = new[] { BuildConfiguredModel($"{family}-{authMode}-model") },
                    });

                Assert.True(
                    response.StatusCode == HttpStatusCode.Created,
                    $"'{family}' refused its declared '{authMode}' mode: {await response.Content.ReadAsStringAsync()}");
            }
        }
    }

    // Enters something for every field the family declared for one mode. What the values are does not matter to
    // a save — nothing is dialled — only that each declared name is answered.
    private static Dictionary<string, string> FillDeclaredFields(JsonElement provider, string authMode)
    {
        var declared = provider.GetProperty("credentialFields");
        if (!declared.TryGetProperty(authMode, out var fields))
        {
            return [];
        }

        return fields
            .EnumerateArray()
            .ToDictionary(
                field => field.GetProperty("name").GetString()!,
                field => field.GetProperty("name").GetString() == "serviceAccountJson"
                    ? ServiceAccountDocument
                    : $"value-for-{field.GetProperty("name").GetString()}",
                StringComparer.Ordinal);
    }

    private async Task<AiConnectionDto> SeedVertexConnectionAsync(string displayName)
    {
        var client = this.CreateAuthorizedClient();
        var response = await client.PostAsJsonAsync(
            $"/clients/{ClientId}/ai-connections",
            new
            {
                displayName,
                providerKind = "meisterdev/googleVertex",
                baseUrl = "https://europe-west4-aiplatform.googleapis.com",
                auth = new
                {
                    mode = "gcpAdc",
                    fields = new Dictionary<string, string> { ["serviceAccountJson"] = ServiceAccountDocument },
                },
                discoveryMode = "manualOnly",
                configuredModels = new[] { BuildConfiguredModel("gemini-3-pro") },
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);
        Assert.NotNull(created);
        return created;
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
                providerKind = "meisterdev/openAiCompatible",
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
                providerKind = "meisterdev/openAiCompatible",
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
                providerKind = "meisterdev/openAi",
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
            providerKind = "meisterdev/azureOpenAi",
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
