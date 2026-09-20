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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Api.Tests.Startup;

/// <summary>
///     A LiteLLM gateway served from a file in a directory, from the host starting to a model call leaving on the
///     host's own transport, and what the installation-wide private-egress opt-in does to a gateway on a private
///     address now that the family is an add-in.
/// </summary>
/// <remarks>
///     Nothing here names a compiled-in driver. The host has no registration for this family; it reads the
///     built-in add-in directory while starting and takes whatever declares itself there. The egress opt-in is
///     the reason this family matters: a gateway is usually inside the operator's own network, and moving the
///     family behind the add-in boundary must not change which addresses an installation admits.
/// </remarks>
public sealed class LiteLlmAddInEndToEndTests(ClientsControllerTests.ClientsApiFactory factory)
    : IClassFixture<ClientsControllerTests.ClientsApiFactory>
{
    private const string AddInKey = "meisterdev/liteLlm";
    private const string PublicGateway = "https://gateway.example.com/v1";
    private const string PrivateGateway = "https://10.0.0.5:4000/v1";

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
        Assert.Equal("LiteLLM", loaded.Label);
        Assert.EndsWith("MeisterDev.Ai.Providers.LiteLlmAddIn.dll", loaded.FilePath, StringComparison.Ordinal);
        Assert.NotNull(loaded.ContentHash);

        // The registry serves it, and the driver it hands back is the one in that file.
        var registry = factory.Services.GetRequiredService<IAiProviderDriverRegistry>();
        Assert.Contains("meisterdev/liteLlm", registry.RegisteredKinds);
        var driver = registry.GetRequired("meisterdev/liteLlm");
        Assert.Equal(AddInKey, driver.Declaration.Key);
        Assert.Equal("MeisterDev.Ai.Providers.LiteLlmAddIn", driver.GetType().Assembly.GetName().Name);

        // An operator saves a connection against it, through the same endpoint any other family is saved
        // through. The credential fields the form collects are the ones the file declared.
        var connectionId = await this.SaveAGatewayConnectionAsync(factory, PublicGateway);

        // The stored connection resolves back to the family, and is projected onto the shape a driver reads.
        // The transport comes from the host's own registered factory: this host composes no database, so the
        // primitives that act on a stored connection are not registered here.
        var connection = await factory.Services.GetRequiredService<IAiConnectionRepository>().GetByIdAsync(connectionId);
        Assert.NotNull(connection);
        Assert.Equal("meisterdev/liteLlm", connection.ProviderKind);
        var endpoint = connection.ToProviderEndpoint(new ProviderProbeContext(factory.Services.GetRequiredService<IProviderHttpClientFactory>()));

        // And a model call goes out on the host's transport, addressed and authenticated by the family.
        factory.ProviderWire.Clear();
        using var chat = driver.CreateChatClient(
            endpoint,
            new ProviderModelDescriptor(Guid.NewGuid(), "gpt-5", [AddInKey + ":ChatCompletions"]),
            AddInKey + ":ChatCompletions");

        // The gateway's answer is not what is under test; that the request reached the wire against the address
        // the operator configured is. The fake endpoint answers every call with an empty document.
        await Record.ExceptionAsync(() => chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        var sent = factory.ProviderWire.Single();
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(new Uri($"{PublicGateway}/chat/completions"), sent.Uri);

        // The virtual key the connection stored, read back out and put where a gateway reads it.
        Assert.Equal("Bearer virtual-key-for-the-add-in", sent.Header("Authorization"));
    }

    // The installation decides, and it decides the same way it did when this family was compiled in: the address
    // is refused before the family is asked, in the installation's own words.
    [Fact]
    public async Task AGatewayOnAPrivateAddressIsRefusedWhereTheInstallationDidNotOptIn()
    {
        var response = await this.PostAGatewayConnectionAsync(factory, PrivateGateway);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "must not target a private, loopback, or link-local address",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    // The same address on an installation that opted in. A host of its own, because it holds a store of its own:
    // a host derived from the fixture would write the fixture's connections as well as its own.
    [Fact]
    public async Task AGatewayOnAPrivateAddressIsAdmittedWhereTheInstallationOptedIn()
    {
        using var optedIn = new ClientsControllerTests.ClientsApiFactory();
        using var permitted = optedIn.WithWebHostBuilder(builder => builder.UseSetting("AI_ALLOW_PRIVATE_EGRESS", "true"));

        var connectionId = await this.SaveAGatewayConnectionAsync(permitted, PrivateGateway);

        var connection = await permitted.Services.GetRequiredService<IAiConnectionRepository>().GetByIdAsync(connectionId);
        Assert.NotNull(connection);
        Assert.Equal(PrivateGateway, connection.BaseUrl);
        Assert.Equal(AiConnectionAvailabilityState.Available, connection.Availability.State);
    }

    private async Task<Guid> SaveAGatewayConnectionAsync(WebApplicationFactory<Program> host, string baseUrl)
    {
        var response = await this.PostAGatewayConnectionAsync(host, baseUrl);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);
        Assert.NotNull(created);

        return created.Id;
    }

    // A host derived for one setting is composed from the same builder, so it validates a token the fixture
    // issued and there is no second signing key to carry.
    private Task<HttpResponseMessage> PostAGatewayConnectionAsync(
        WebApplicationFactory<Program> host,
        string baseUrl)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        return client.PostAsJsonAsync(
            $"/clients/{ClientId}/ai-connections",
            new
            {
                displayName = $"LiteLLM at {baseUrl}",
                providerKind = "liteLlm",
                baseUrl,
                auth = new
                {
                    mode = "apiKey",
                    fields = new Dictionary<string, string> { ["apiKey"] = "virtual-key-for-the-add-in" },
                },
                discoveryMode = "manualOnly",
                configuredModels = new[]
                {
                    new
                    {
                        remoteModelId = "gpt-5",
                        displayName = "GPT-5 behind the gateway",
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
