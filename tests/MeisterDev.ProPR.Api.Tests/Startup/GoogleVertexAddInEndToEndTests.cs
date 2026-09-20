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
///     A provider family served from a file in a directory, from the host starting to a model call leaving on
///     the host's own transport.
/// </summary>
/// <remarks>
///     Nothing here names a compiled-in driver. The host has no registration for this family; it reads the
///     built-in add-in directory while starting, takes whatever declares itself there, and everything below —
///     the family appearing in the inventory, an operator saving a connection against it, a stored credential
///     coming back out, and a chat call reaching the vendor's address — runs through what it found.
/// </remarks>
public sealed class GoogleVertexAddInEndToEndTests(ClientsControllerTests.ClientsApiFactory factory)
    : IClassFixture<ClientsControllerTests.ClientsApiFactory>
{
    private const string AddInKey = "meisterdev/googleVertex";
    private const string GeminiBaseUrl = "https://generativelanguage.googleapis.com";

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
        Assert.Equal("Google Gemini / Vertex AI", loaded.Label);
        Assert.EndsWith("MeisterDev.Ai.Providers.GoogleVertexAddIn.dll", loaded.FilePath, StringComparison.Ordinal);
        Assert.NotNull(loaded.ContentHash);

        // The registry serves it, and the driver it hands back is the one in that file.
        var registry = factory.Services.GetRequiredService<IAiProviderDriverRegistry>();
        Assert.Contains("meisterdev/googleVertex", registry.RegisteredKinds);
        var driver = registry.GetRequired("meisterdev/googleVertex");
        Assert.Equal(AddInKey, driver.Declaration.Key);
        Assert.Equal(
            "MeisterDev.Ai.Providers.GoogleVertexAddIn",
            driver.GetType().Assembly.GetName().Name);

        // An operator saves a connection against it, through the same endpoint any other family is saved
        // through. The credential fields the form collects are the ones the file declared.
        var connectionId = await this.SaveAGeminiConnectionAsync();

        // The stored connection resolves back to the family, and is projected onto the shape a driver reads.
        // The transport comes from the host's own registered factory: this host composes no database, so the
        // primitives that act on a stored connection are not registered here, and the seam that hands those to a
        // driver is covered where they are.
        var connection = await factory.Services.GetRequiredService<IAiConnectionRepository>().GetByIdAsync(connectionId);
        Assert.NotNull(connection);
        Assert.Equal("meisterdev/googleVertex", connection.ProviderKind);
        var endpoint = connection.ToProviderEndpoint(new ProviderProbeContext(factory.Services.GetRequiredService<IProviderHttpClientFactory>()));

        // And a model call goes out on the host's transport, addressed and authenticated by the family.
        factory.ProviderWire.Clear();
        using var chat = driver.CreateChatClient(
            endpoint,
            new ProviderModelDescriptor(Guid.NewGuid(), "gemini-3-pro", [AddInKey + ":GoogleGenerateContent"]),
            AddInKey + ":GoogleGenerateContent");

        // The vendor's answer is not what is under test; that the request reached the wire in the family's own
        // shape is. The fake endpoint answers every call with an empty document, which this protocol reads as a
        // response carrying no candidate.
        await Record.ExceptionAsync(() => chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        var sent = factory.ProviderWire.Single();
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(
            new Uri($"{GeminiBaseUrl}/v1beta/models/gemini-3-pro:generateContent"),
            sent.Uri);

        // The credential the connection stored, read back out and put where this family's vendor reads it.
        Assert.Equal("gemini-key-for-the-add-in", sent.Header("x-goog-api-key"));
    }

    private async Task<Guid> SaveAGeminiConnectionAsync()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await client.PostAsJsonAsync(
            $"/clients/{ClientId}/ai-connections",
            new
            {
                displayName = "Gemini from the add-in",
                providerKind = "googleVertex",
                baseUrl = GeminiBaseUrl,
                auth = new
                {
                    mode = "apiKey",
                    fields = new Dictionary<string, string> { ["apiKey"] = "gemini-key-for-the-add-in" },
                },
                discoveryMode = "manualOnly",
                configuredModels = new[]
                {
                    new
                    {
                        remoteModelId = "gemini-3-pro",
                        displayName = "Gemini 3 Pro",
                        operationKinds = new[] { "chat" },
                        supportedProtocolModes = new[] { "auto", "googleGenerateContent" },
                        supportsStructuredOutput = true,
                        supportsToolUse = true,
                        source = "manual",
                    },
                },
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AiConnectionDto>(ApiJsonOptions);
        Assert.NotNull(created);

        return created.Id;
    }

    private static JsonSerializerOptions CreateApiJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
