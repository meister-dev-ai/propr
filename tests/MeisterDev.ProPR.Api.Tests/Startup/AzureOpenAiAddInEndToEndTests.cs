// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterDev.Ai.Providers.AddIns;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.ProPR.Api.Tests.Controllers;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Api.Tests.Startup;

/// <summary>
///     Azure OpenAI served from a file in a directory, from the host starting to a model call leaving on the
///     host's own transport, and the host pin that survives the move.
/// </summary>
/// <remarks>
///     Nothing here names a compiled-in driver. The host has no registration for this family; it reads the
///     built-in add-in directory while starting and takes whatever declares itself there. The pin matters for
///     this family in particular: a profile configured for it authenticates with a resource key or a Microsoft
///     Entra token, and one pointed at a host Microsoft does not control would send that credential there.
/// </remarks>
public sealed class AzureOpenAiAddInEndToEndTests(ClientsControllerTests.ClientsApiFactory factory)
    : IClassFixture<ClientsControllerTests.ClientsApiFactory>
{
    private const string AddInKey = "meisterdev/azureOpenAi";
    private const string ResourceRoot = "https://contoso.openai.azure.com";
    private const string Deployment = "my-gpt-deployment";

    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly JsonSerializerOptions ApiJsonOptions = CreateApiJsonOptions();

    [Fact]
    public async Task AFamilySuppliedByAFileInTheBuiltInDirectoryServesAModelCall()
    {
        _ = factory.CreateClient();

        // The family is there because a file declared it, and the inventory says which file.
        var catalog = factory.Services.GetRequiredService<ProviderAddInCatalog>();
        var loaded = Assert.Single(catalog.Loaded, family => family.Key == AddInKey);
        Assert.Equal(ProviderAddInOrigin.BuiltIn, loaded.Origin);
        Assert.Equal("Azure OpenAI / AI Foundry", loaded.Label);
        Assert.EndsWith("MeisterDev.Ai.Providers.AzureOpenAiAddIn.dll", loaded.FilePath, StringComparison.Ordinal);
        Assert.NotNull(loaded.ContentHash);

        // The hosts it says it reaches are the three Azure AI suffixes, which an operator reads in the
        // inventory and what a tenant's endpoint restriction is checked against.
        Assert.Equal(
            [".openai.azure.com", ".services.ai.azure.com", ".cognitiveservices.azure.com"],
            loaded.ReachedHostPatterns);

        // The registry serves it, and the driver it hands back is the one in that file.
        var registry = factory.Services.GetRequiredService<IAiProviderDriverRegistry>();
        Assert.Contains("meisterdev/azureOpenAi", registry.RegisteredKinds);
        var driver = registry.GetRequired("meisterdev/azureOpenAi");
        Assert.Equal(AddInKey, driver.Declaration.Key);
        Assert.Equal("MeisterDev.Ai.Providers.AzureOpenAiAddIn", driver.GetType().Assembly.GetName().Name);

        // An operator saves a connection against it, through the same endpoint any other family is saved
        // through. The credential field the form collects is the one the file declared.
        var connectionId = await this.SaveAResourceConnectionAsync(ResourceRoot);

        // The stored connection resolves back to the family, and is projected onto the shape a driver reads.
        // The transport comes from the host's own registered factory: this host composes no database, so the
        // primitives that act on a stored connection are not registered here.
        var connection = await factory.Services.GetRequiredService<IAiConnectionRepository>().GetByIdAsync(connectionId);
        Assert.NotNull(connection);
        Assert.Equal("meisterdev/azureOpenAi", connection.ProviderKind);
        var endpoint = connection.ToProviderEndpoint(new ProviderProbeContext(factory.Services.GetRequiredService<IProviderHttpClientFactory>()));

        // And a model call goes out on the host's transport, addressed and authenticated by the family.
        factory.ProviderWire.Clear();
        using var chat = driver.CreateChatClient(
            endpoint,
            new ProviderModelDescriptor(Guid.NewGuid(), Deployment, [AddInKey + ":ChatCompletions"]),
            AddInKey + ":ChatCompletions");

        // The resource's answer is not what is under test; that the request reached the wire on the resource's
        // OpenAI-compatible surface is. The fake endpoint answers every call with an empty document.
        await Record.ExceptionAsync(() => chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        var sent = factory.ProviderWire.Single();
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(new Uri($"{ResourceRoot}/openai/v1/chat/completions"), sent.Uri);

        // The resource key the connection stored, read back out and put in the header the resource reads it
        // from rather than sent as a bearer, so a key and an Entra token stay distinguishable on the wire.
        Assert.Equal("azure-resource-key-for-the-add-in", sent.Header("api-key"));
        Assert.Null(sent.Header("Authorization"));
    }

    // The pin is a control over the credential, not over the address: a profile for this family carries an
    // Azure credential, and a host Microsoft does not control must never be sent one. The refusal comes from
    // the family, through the same save any other family is saved through.
    [Fact]
    public async Task AHostOutsideAzureIsRefusedWhereTheOperatorCanSeeIt()
    {
        var response = await this.PostAResourceConnectionAsync("https://contoso.openai.azure.com.evil.example");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "Azure AI host",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    // A resource name this product has never seen is accepted, which is why the declared hosts are suffixes: no
    // family can enumerate a tenant's resource names.
    [Fact]
    public async Task AResourceNameThisProductHasNeverSeenIsAccepted()
    {
        var connectionId = await this.SaveAResourceConnectionAsync("https://a-brand-new-resource.services.ai.azure.com");

        var connection = await factory.Services.GetRequiredService<IAiConnectionRepository>().GetByIdAsync(connectionId);
        Assert.NotNull(connection);
        Assert.Equal(AiConnectionAvailabilityState.Available, connection.Availability.State);
    }

    private async Task<Guid> SaveAResourceConnectionAsync(string baseUrl)
    {
        var response = await this.PostAResourceConnectionAsync(baseUrl);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);
        Assert.NotNull(created);

        return created.Id;
    }

    private Task<HttpResponseMessage> PostAResourceConnectionAsync(string baseUrl)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        return client.PostAsJsonAsync(
            $"/clients/{ClientId}/ai-connections",
            new
            {
                displayName = $"Azure OpenAI at {baseUrl}",
                providerKind = "azureOpenAi",
                baseUrl,
                auth = new
                {
                    mode = "apiKey",
                    fields = new Dictionary<string, string> { ["apiKey"] = "azure-resource-key-for-the-add-in" },
                },
                discoveryMode = "manualOnly",
                configuredModels = new[]
                {
                    new
                    {
                        remoteModelId = Deployment,
                        displayName = "A deployment on the resource",
                        operationKinds = new[] { "chat" },
                        supportedProtocolModes = new[] { "auto", "chatCompletions" },
                        supportsStructuredOutput = true,
                        supportsToolUse = true,
                        source = "manual",
                    },
                },
            });
    }

    private static JsonSerializerOptions CreateApiJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
