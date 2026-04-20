// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Api.Tests.Features.Clients;

/// <summary>Integration tests for client provider scope APIs.</summary>
public sealed class ClientProviderScopesControllerTests(ClientProviderConnectionsControllerTests.ProviderConnectionsApiFactory factory)
    : IClassFixture<ClientProviderConnectionsControllerTests.ProviderConnectionsApiFactory>
{
    [Fact]
    public async Task GetProviderScopes_ClientUserForAssignedClient_Returns200WithScopes()
    {
        await factory.ResetProviderStateAsync();

        var connection = await factory.CreateConnectionAsync();
        var created = await factory.CreateScopeAsync(connection.Id);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/provider-connections/{connection.Id}/scopes");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientUserToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Single(body.EnumerateArray());
        Assert.Equal(created.Id, body[0].GetProperty("id").GetGuid());
        Assert.Equal("organization", body[0].GetProperty("scopeType").GetString());
        Assert.Equal("acme", body[0].GetProperty("scopePath").GetString());
    }

    [Fact]
    public async Task PostProviderScope_ClientAdministrator_Returns201AndPersists()
    {
        await factory.ResetProviderStateAsync();

        var connection = await factory.CreateConnectionAsync();

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/provider-connections/{connection.Id}/scopes");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        request.Content = JsonContent.Create(
            new
            {
                scopeType = "organization",
                externalScopeId = "acme-labs",
                scopePath = "acme-labs",
                displayName = "Acme Labs",
                isEnabled = true,
            });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var scopeId = body.GetProperty("id").GetGuid();
        Assert.Equal(factory.ClientId, body.GetProperty("clientId").GetGuid());
        Assert.Equal(connection.Id, body.GetProperty("connectionId").GetGuid());
        Assert.Equal("Acme Labs", body.GetProperty("displayName").GetString());
        Assert.Equal("unknown", body.GetProperty("verificationStatus").GetString());

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IClientScmScopeRepository>();
        var persisted = await repository.GetByIdAsync(factory.ClientId, connection.Id, scopeId, CancellationToken.None);
        Assert.NotNull(persisted);
    }

    [Fact]
    public async Task PatchProviderScope_ClientAdministrator_UpdatesScope()
    {
        await factory.ResetProviderStateAsync();

        var connection = await factory.CreateConnectionAsync();
        var created = await factory.CreateScopeAsync(connection.Id);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/clients/{factory.ClientId}/provider-connections/{connection.Id}/scopes/{created.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        request.Content = JsonContent.Create(
            new
            {
                displayName = "Renamed Scope",
                isEnabled = false,
            });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(created.Id, body.GetProperty("id").GetGuid());
        Assert.Equal("Renamed Scope", body.GetProperty("displayName").GetString());
        Assert.False(body.GetProperty("isEnabled").GetBoolean());
    }

    [Fact]
    public async Task PostProviderScope_DuplicateScope_ReturnsConflictWithSafeError()
    {
        await factory.ResetProviderStateAsync();

        var connection = await factory.CreateConnectionAsync();
        await factory.CreateScopeAsync(
            connection.Id,
            externalScopeId: "acme-labs",
            scopePath: "acme-labs",
            displayName: "Acme Labs");

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/provider-connections/{connection.Id}/scopes");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        request.Content = JsonContent.Create(
            new
            {
                scopeType = "organization",
                externalScopeId = "acme-labs",
                scopePath = "acme-labs",
                displayName = "Duplicate Acme Labs",
                isEnabled = true,
            });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(
            "A provider scope with the same external scope already exists for this connection.",
            body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DeleteProviderScope_Admin_Returns204AndRemovesScope()
    {
        await factory.ResetProviderStateAsync();

        var connection = await factory.CreateConnectionAsync();
        var created = await factory.CreateScopeAsync(connection.Id);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/clients/{factory.ClientId}/provider-connections/{connection.Id}/scopes/{created.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateAdminToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IClientScmScopeRepository>();
        var persisted = await repository.GetByIdAsync(
            factory.ClientId,
            connection.Id,
            created.Id,
            CancellationToken.None);
        Assert.Null(persisted);
    }
}
