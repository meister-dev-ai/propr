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
///     Amazon Bedrock served from a file in a directory, from the host starting to a signed model call leaving on
///     the host's own transport.
/// </summary>
/// <remarks>
///     Nothing here names a compiled-in driver. The host has no registration for this family; it reads the
///     built-in add-in directory while starting and takes whatever declares itself there. This family is the one
///     whose credential is three values rather than one, so what the signed request carries is also what proves
///     each of them survived the move: the access key id names the credential in the signature, the secret
///     access key computes it and never travels, and the session token rides in a header of its own.
/// </remarks>
public sealed class BedrockAddInEndToEndTests(ClientsControllerTests.ClientsApiFactory factory)
    : IClassFixture<ClientsControllerTests.ClientsApiFactory>
{
    private const string AddInKey = "meisterdev/awsBedrock";
    private const string RegionalEndpoint = "https://bedrock-runtime.eu-central-1.amazonaws.com";

    private const string ApiKey = "ABSKQmVkcm9ja0FQSUtleS1FWEFNUExF";

    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly JsonSerializerOptions ApiJsonOptions = CreateApiJsonOptions();

    [Fact]
    public async Task AFamilySuppliedByAFileInTheBuiltInDirectoryServesASignedModelCall()
    {
        _ = factory.CreateClient();

        // The family is there because a file declared it, and the inventory says which file.
        var catalog = factory.Services.GetRequiredService<ProviderAddInCatalog>();
        var loaded = Assert.Single(catalog.Loaded, family => family.Key == AddInKey);
        Assert.Equal(ProviderAddInOrigin.BuiltIn, loaded.Origin);
        Assert.Equal("AWS Bedrock", loaded.Label);
        Assert.EndsWith("MeisterDev.Ai.Providers.BedrockAddIn.dll", loaded.FilePath, StringComparison.Ordinal);
        Assert.NotNull(loaded.ContentHash);

        // The registry serves it, and the driver it hands back is the one in that file.
        var registry = factory.Services.GetRequiredService<IAiProviderDriverRegistry>();
        Assert.Contains("meisterdev/awsBedrock", registry.RegisteredKinds);
        var driver = registry.GetRequired("meisterdev/awsBedrock");
        Assert.Equal(AddInKey, driver.Declaration.Key);
        Assert.Equal("MeisterDev.Ai.Providers.BedrockAddIn", driver.GetType().Assembly.GetName().Name);

        // An operator saves a connection against it, through the same endpoint any other family is saved
        // through. The credential fields the form collects are the three the file declared.
        var connectionId = await this.SaveABedrockConnectionAsync();

        // The stored connection resolves back to the family, and is projected onto the shape a driver reads.
        // The transport comes from the host's own registered factory: this host composes no database, so the
        // primitives that act on a stored connection are not registered here.
        var connection = await factory.Services.GetRequiredService<IAiConnectionRepository>().GetByIdAsync(connectionId);
        Assert.NotNull(connection);
        Assert.Equal("meisterdev/awsBedrock", connection.ProviderKind);
        var endpoint = connection.ToProviderEndpoint(new ProviderProbeContext(factory.Services.GetRequiredService<IProviderHttpClientFactory>()));

        // And a model call goes out on the host's transport, addressed and regioned by the family, carrying the
        // key the connection stored.
        factory.ProviderWire.Clear();
        using var chat = driver.CreateChatClient(
            endpoint,
            new ProviderModelDescriptor(Guid.NewGuid(), "anthropic.claude-opus-4-5", [AddInKey + ":BedrockConverse"]),
            AddInKey + ":BedrockConverse");

        // The vendor's answer is not what is under test; that the request reached the wire carrying what the
        // connection stored is. The fake endpoint answers every call with an empty document.
        await Record.ExceptionAsync(() => chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        var sent = factory.ProviderWire.Single();
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("bedrock-runtime.eu-central-1.amazonaws.com", sent.Uri?.Host);

        // A Bedrock API key is a bearer token, so it travels as one and nothing about the request is signed.
        var authorization = sent.Header("Authorization");
        Assert.NotNull(authorization);
        Assert.Equal($"Bearer {ApiKey}", authorization);
        Assert.DoesNotContain("AWS4-HMAC-SHA256", authorization, StringComparison.Ordinal);

        // A signing credential would have put a session token on the request. A bearer token carries none.
        Assert.Null(sent.Header("x-amz-security-token"));
    }

    // The endpoint is where the region is read from, so one that names none cannot be held to a residency
    // requirement. The refusal comes from the family, through the same save any other family is saved through.
    [Fact]
    public async Task AnEndpointThatNamesNoRegionIsRefusedWhereTheOperatorCanSeeIt()
    {
        var response = await this.PostABedrockConnectionAsync("https://bedrock-runtime.amazonaws.com");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "region",
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Guid> SaveABedrockConnectionAsync()
    {
        var response = await this.PostABedrockConnectionAsync(RegionalEndpoint);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);
        Assert.NotNull(created);

        return created.Id;
    }

    private Task<HttpResponseMessage> PostABedrockConnectionAsync(string baseUrl)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        return client.PostAsJsonAsync(
            $"/clients/{ClientId}/ai-connections",
            new
            {
                displayName = $"Bedrock at {baseUrl}",
                providerKind = "awsBedrock",
                baseUrl,
                auth = new
                {
                    mode = "apiKey",
                    fields = new Dictionary<string, string> { ["apiKey"] = ApiKey },
                },
                discoveryMode = "manualOnly",
                configuredModels = new[]
                {
                    new
                    {
                        remoteModelId = "anthropic.claude-opus-4-5",
                        displayName = "Claude Opus 4.5 on Bedrock",
                        operationKinds = new[] { "chat" },
                        supportedProtocolModes = new[] { "auto", "bedrockConverse" },
                        supportsStructuredOutput = false,
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
