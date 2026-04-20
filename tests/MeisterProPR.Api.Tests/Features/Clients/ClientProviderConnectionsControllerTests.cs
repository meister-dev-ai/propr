// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MeisterProPR.Api.Features.Clients.Controllers;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Features.Clients.Services;
using MeisterProPR.Application.Features.Clients.Support;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Auth;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.Clients.Support;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Features.Clients;

/// <summary>Integration tests for client provider connection and reviewer identity APIs.</summary>
public sealed class ClientProviderConnectionsControllerTests(ClientProviderConnectionsControllerTests.ProviderConnectionsApiFactory factory)
    : IClassFixture<ClientProviderConnectionsControllerTests.ProviderConnectionsApiFactory>
{
    [Fact]
    public async Task GetProviderConnections_ClientUserForAssignedClient_Returns200WithConnections()
    {
        await factory.ResetProviderStateAsync();
        var created = await factory.CreateConnectionAsync();

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/clients/{factory.ClientId}/provider-connections");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientUserToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Single(body.EnumerateArray());
        Assert.Equal(created.Id, body[0].GetProperty("id").GetGuid());
        Assert.Equal("github", body[0].GetProperty("providerFamily").GetString());
        Assert.Equal(created.HostBaseUrl, body[0].GetProperty("hostBaseUrl").GetString());
    }

    [Fact]
    public async Task GetAdminProviders_Admin_ReturnsAllProvidersWithPublishDefaults()
    {
        await factory.ResetProviderStateAsync(true);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/providers");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateAdminToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var providers = body.EnumerateArray()
            .ToDictionary(entry => entry.GetProperty("providerFamily").GetString()!, StringComparer.Ordinal);

        Assert.Equal(4, providers.Count);
        Assert.True(providers["azureDevOps"].GetProperty("isEnabled").GetBoolean());
        Assert.True(providers["gitLab"].GetProperty("isEnabled").GetBoolean());
        Assert.False(providers["github"].GetProperty("isEnabled").GetBoolean());
        Assert.False(providers["forgejo"].GetProperty("isEnabled").GetBoolean());
    }

    [Fact]
    public async Task GetAdminProviders_ClientAdministrator_ReturnsProviderStatuses()
    {
        await factory.ResetProviderStateAsync();

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/providers");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(4, body.GetArrayLength());
    }

    [Fact]
    public async Task PatchAdminProvider_WhenDisabled_HidesMatchingClientConnections()
    {
        await factory.ResetProviderStateAsync();
        await factory.CreateConnectionAsync();

        var httpClient = factory.CreateClient();
        using (var disableRequest = new HttpRequestMessage(HttpMethod.Patch, "/admin/providers/github"))
        {
            disableRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                factory.GenerateAdminToken());
            disableRequest.Content = JsonContent.Create(new { isEnabled = false });

            var disableResponse = await httpClient.SendAsync(disableRequest);
            Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);
        }

        using var listRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/provider-connections");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());

        var listResponse = await httpClient.SendAsync(listRequest);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listBody = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Empty(listBody.EnumerateArray());
    }

    [Fact]
    public async Task GetProviderConnection_WhenProviderDisabled_Returns404()
    {
        await factory.ResetProviderStateAsync();
        var created = await factory.CreateConnectionAsync();

        var httpClient = factory.CreateClient();
        using (var disableRequest = new HttpRequestMessage(HttpMethod.Patch, "/admin/providers/github"))
        {
            disableRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                factory.GenerateAdminToken());
            disableRequest.Content = JsonContent.Create(new { isEnabled = false });

            var disableResponse = await httpClient.SendAsync(disableRequest);
            Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);
        }

        using var getRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/provider-connections/{created.Id}");
        getRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientUserToken());

        var getResponse = await httpClient.SendAsync(getRequest);

        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task PostProviderConnection_WhenProviderDisabled_Returns409()
    {
        await factory.ResetProviderStateAsync();

        var httpClient = factory.CreateClient();
        using (var disableRequest = new HttpRequestMessage(HttpMethod.Patch, "/admin/providers/github"))
        {
            disableRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                factory.GenerateAdminToken());
            disableRequest.Content = JsonContent.Create(new { isEnabled = false });

            var disableResponse = await httpClient.SendAsync(disableRequest);
            Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/provider-connections");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        request.Content = JsonContent.Create(
            new
            {
                providerFamily = "github",
                hostBaseUrl = "https://github.example.com",
                authenticationKind = "personalAccessToken",
                displayName = "Disabled GitHub",
                secret = "ghp_test_secret_value",
                isActive = true,
            });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains("disabled", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostProviderConnection_ClientAdministrator_Returns201AndStoresProtectedSecret()
    {
        await factory.ResetProviderStateAsync();
        var hostBaseUrl = $"https://github-{Guid.NewGuid():N}.example.com/acme/platform";
        var expectedHostBaseUrl = new Uri(hostBaseUrl).GetLeftPart(UriPartial.Authority);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/provider-connections");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        request.Content = JsonContent.Create(
            new
            {
                providerFamily = "github",
                hostBaseUrl,
                authenticationKind = "personalAccessToken",
                displayName = "Acme GitHub",
                secret = "ghp_test_secret_value",
                isActive = true,
            });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var connectionId = body.GetProperty("id").GetGuid();
        Assert.Equal(factory.ClientId, body.GetProperty("clientId").GetGuid());
        Assert.Equal("github", body.GetProperty("providerFamily").GetString());
        Assert.Equal(expectedHostBaseUrl, body.GetProperty("hostBaseUrl").GetString());
        Assert.Equal("personalAccessToken", body.GetProperty("authenticationKind").GetString());
        Assert.Equal("unknown", body.GetProperty("verificationStatus").GetString());
        Assert.False(body.TryGetProperty("secret", out _));

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var record = await dbContext.ClientScmConnections.FirstAsync(connection => connection.Id == connectionId);
        Assert.NotEqual("ghp_test_secret_value", record.EncryptedSecretMaterial);
    }

    [Fact]
    public async Task PostProviderConnection_AzureDevOpsOAuthClientCredentials_PersistsOAuthMetadata()
    {
        await factory.ResetProviderStateAsync();

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/provider-connections");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        request.Content = JsonContent.Create(
            new
            {
                providerFamily = "azureDevOps",
                hostBaseUrl = "https://dev.azure.com",
                authenticationKind = "oauthClientCredentials",
                oAuthTenantId = "contoso.onmicrosoft.com",
                oAuthClientId = "11111111-1111-1111-1111-111111111111",
                displayName = "Contoso Azure DevOps",
                secret = "azure-secret-value",
                isActive = true,
            });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var connectionId = body.GetProperty("id").GetGuid();
        Assert.Equal("azureDevOps", body.GetProperty("providerFamily").GetString());
        Assert.Equal("oauthClientCredentials", body.GetProperty("authenticationKind").GetString());
        Assert.Equal("contoso.onmicrosoft.com", body.GetProperty("oAuthTenantId").GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", body.GetProperty("oAuthClientId").GetString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var record = await dbContext.ClientScmConnections.FirstAsync(connection => connection.Id == connectionId);
        Assert.Equal("contoso.onmicrosoft.com", record.OAuthTenantId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", record.OAuthClientId);
        Assert.NotEqual("azure-secret-value", record.EncryptedSecretMaterial);
    }

    [Fact]
    public async Task PostProviderConnection_AzureDevOpsAppInstallation_Returns400()
    {
        await factory.ResetProviderStateAsync();

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/provider-connections");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        request.Content = JsonContent.Create(
            new
            {
                providerFamily = "azureDevOps",
                hostBaseUrl = "https://dev.azure.com",
                authenticationKind = "appInstallation",
                displayName = "Unsupported Azure DevOps Auth",
                secret = "azure-secret-value",
                isActive = true,
            });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains(
            nameof(CreateClientProviderConnectionRequest.AuthenticationKind),
            body.GetProperty("errors").EnumerateObject().Select(error => error.Name));
    }

    [Fact]
    public async Task PostProviderConnection_ClientUser_Returns403()
    {
        await factory.ResetProviderStateAsync();
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/provider-connections");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientUserToken());
        request.Content = JsonContent.Create(
            new
            {
                providerFamily = "github",
                hostBaseUrl = "https://github.com",
                authenticationKind = "personalAccessToken",
                displayName = "Forbidden GitHub",
                secret = "ghp_test_secret_value",
                isActive = true,
            });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PatchProviderConnection_ClientAdministrator_UpdatesConnection()
    {
        await factory.ResetProviderStateAsync();
        var created = await factory.CreateConnectionAsync();

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/clients/{factory.ClientId}/provider-connections/{created.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        request.Content = JsonContent.Create(
            new
            {
                displayName = "Renamed Connection",
                isActive = false,
            });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(created.Id, body.GetProperty("id").GetGuid());
        Assert.Equal("Renamed Connection", body.GetProperty("displayName").GetString());
        Assert.False(body.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task PatchProviderConnection_AzureDevOpsAppInstallation_Returns400()
    {
        await factory.ResetProviderStateAsync();
        var created = await factory.CreateConnectionAsync(
            ScmProvider.AzureDevOps,
            "https://dev.azure.com",
            ScmAuthenticationKind.OAuthClientCredentials,
            "contoso.onmicrosoft.com",
            "11111111-1111-1111-1111-111111111111",
            "Azure DevOps",
            "azure-secret-value");

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/clients/{factory.ClientId}/provider-connections/{created.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        request.Content = JsonContent.Create(
            new
            {
                authenticationKind = "appInstallation",
            });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains(
            nameof(CreateClientProviderConnectionRequest.AuthenticationKind),
            body.GetProperty("errors").EnumerateObject().Select(error => error.Name));
    }

    [Fact]
    public async Task PostProviderConnection_DuplicateConnection_ReturnsConflictWithSafeError()
    {
        await factory.ResetProviderStateAsync();
        const string duplicateHost = "https://github-duplicate.example.com/acme/platform";
        await factory.CreateConnectionAsync(hostBaseUrl: duplicateHost);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/provider-connections");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        request.Content = JsonContent.Create(
            new
            {
                providerFamily = "github",
                hostBaseUrl = duplicateHost,
                authenticationKind = "personalAccessToken",
                displayName = "Duplicate GitHub",
                secret = "ghp_test_secret_value",
                isActive = true,
            });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(
            "A provider connection with the same provider family and host already exists for this client.",
            body.GetProperty("error").GetString());
        Assert.DoesNotContain(
            "github-duplicate.example.com",
            body.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PatchProviderConnection_DuplicateConnection_ReturnsConflictWithSafeError()
    {
        await factory.ResetProviderStateAsync();
        var existing =
            await factory.CreateConnectionAsync(hostBaseUrl: "https://github-first.example.com/acme/platform");
        var created =
            await factory.CreateConnectionAsync(hostBaseUrl: "https://github-second.example.com/acme/platform");

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/clients/{factory.ClientId}/provider-connections/{created.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        request.Content = JsonContent.Create(
            new
            {
                hostBaseUrl = existing.HostBaseUrl,
            });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(
            "A provider connection with the same provider family and host already exists for this client.",
            body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task VerifyProviderConnection_WithRegisteredOnboardingCapabilities_Returns200AndUpdatesVerification()
    {
        await factory.ResetProviderStateAsync();
        var created = await factory.CreateConnectionAsync();
        factory.SetDiscoveryScopes("acme/platform");

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/provider-connections/{created.Id}/verify");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("verified", body.GetProperty("verificationStatus").GetString());
        Assert.Equal("onboardingReady", body.GetProperty("readinessLevel").GetString());
        Assert.Equal(JsonValueKind.String, body.GetProperty("lastVerifiedAt").ValueKind);
        Assert.True(body.GetProperty("lastVerificationError").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);
        Assert.Contains(
            body.GetProperty("missingReadinessCriteria").EnumerateArray().Select(item => item.GetString()),
            value => value is not null && value.Contains("reviewer identity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyProviderConnection_WhenDiscoveryFails_ReturnsFailureCategoryAndAuditEntry()
    {
        await factory.ResetProviderStateAsync();
        var created = await factory.CreateConnectionAsync();
        factory.SetDiscoveryFailure("Token missing read_api scope.");

        var httpClient = factory.CreateClient();
        using var verifyRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/provider-connections/{created.Id}/verify");
        verifyRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());

        var verifyResponse = await httpClient.SendAsync(verifyRequest);

        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        var verifyBody = JsonDocument.Parse(await verifyResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("failed", verifyBody.GetProperty("verificationStatus").GetString());
        Assert.Equal("authentication", verifyBody.GetProperty("lastVerificationFailureCategory").GetString());

        using var auditRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/provider-operations/audit-trail?take=5");
        auditRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientUserToken());

        var auditResponse = await httpClient.SendAsync(auditRequest);

        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        var auditBody = JsonDocument.Parse(await auditResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("connectionVerificationFailed", auditBody[0].GetProperty("eventType").GetString());
        Assert.Equal("authentication", auditBody[0].GetProperty("failureCategory").GetString());
    }

    [Fact]
    public async Task VerifyProviderConnection_AzureDevOpsAppInstallation_ReturnsFailedStatus()
    {
        await factory.ResetProviderStateAsync();
        var created = await factory.CreateConnectionAsync(
            ScmProvider.AzureDevOps,
            "https://dev.azure.com",
            ScmAuthenticationKind.AppInstallation,
            displayName: "Azure DevOps",
            secret: "azure-secret-value");
        await factory.CreateScopeAsync(
            created.Id,
            externalScopeId: "acme",
            scopePath: "https://dev.azure.com/acme",
            displayName: "Acme");

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/provider-connections/{created.Id}/verify");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("failed", body.GetProperty("verificationStatus").GetString());
        Assert.Contains(
            "only OAuth client credentials",
            body.GetProperty("lastVerificationError").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetProviderConnection_WhenWorkflowCriteriaAreSatisfied_ReturnsWorkflowCompleteReadiness()
    {
        await factory.ResetProviderStateAsync();
        var created = await factory.CreateConnectionAsync(hostBaseUrl: "https://github.com/acme/platform");
        factory.SetDiscoveryScopes("acme/platform");
        await factory.CreateScopeAsync(created.Id, scopePath: "acme/platform", displayName: "Acme Platform");

        using (var scope = factory.Services.CreateScope())
        {
            var reviewerRepository = scope.ServiceProvider.GetRequiredService<IClientReviewerIdentityRepository>();
            await reviewerRepository.UpsertAsync(
                factory.ClientId,
                created.Id,
                ScmProvider.GitHub,
                "github-reviewer-1",
                "meister-dev-bot",
                "Meister Dev Bot",
                true,
                CancellationToken.None);
        }

        var httpClient = factory.CreateClient();
        using (var verifyRequest = new HttpRequestMessage(
                   HttpMethod.Post,
                   $"/clients/{factory.ClientId}/provider-connections/{created.Id}/verify"))
        {
            verifyRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                factory.GenerateClientAdministratorToken());

            var verifyResponse = await httpClient.SendAsync(verifyRequest);
            Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/provider-connections/{created.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientUserToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("workflowComplete", body.GetProperty("readinessLevel").GetString());
        Assert.Equal("hosted", body.GetProperty("hostVariant").GetString());
        Assert.Empty(body.GetProperty("missingReadinessCriteria").EnumerateArray());
    }

    [Fact]
    public async Task GetProviderConnectionAuditTrail_ReturnsPersistedLifecycleEvents()
    {
        await factory.ResetProviderStateAsync();
        var created = await factory.CreateConnectionAsync();
        factory.SetDiscoveryScopes("acme/platform");
        var httpClient = factory.CreateClient();

        using (var patchRequest = new HttpRequestMessage(
                   HttpMethod.Patch,
                   $"/clients/{factory.ClientId}/provider-connections/{created.Id}"))
        {
            patchRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                factory.GenerateClientAdministratorToken());
            patchRequest.Content = JsonContent.Create(
                new
                {
                    displayName = "Renamed Connection",
                });

            var patchResponse = await httpClient.SendAsync(patchRequest);
            Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
        }

        using (var verifyRequest = new HttpRequestMessage(
                   HttpMethod.Post,
                   $"/clients/{factory.ClientId}/provider-connections/{created.Id}/verify"))
        {
            verifyRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                factory.GenerateClientAdministratorToken());

            var verifyResponse = await httpClient.SendAsync(verifyRequest);
            Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        }

        using (var deleteRequest = new HttpRequestMessage(
                   HttpMethod.Delete,
                   $"/clients/{factory.ClientId}/provider-connections/{created.Id}"))
        {
            deleteRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                factory.GenerateAdminToken());

            var deleteResponse = await httpClient.SendAsync(deleteRequest);
            Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        }

        using var auditRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/provider-operations/audit-trail?take=10");
        auditRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientUserToken());

        var auditResponse = await httpClient.SendAsync(auditRequest);

        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        var auditBody = JsonDocument.Parse(await auditResponse.Content.ReadAsStringAsync()).RootElement;
        var eventTypes = auditBody.EnumerateArray()
            .Select(item => item.GetProperty("eventType").GetString())
            .Where(value => value is not null)
            .Cast<string>()
            .ToList();

        Assert.Contains("connectionCreated", eventTypes);
        Assert.Contains("connectionUpdated", eventTypes);
        Assert.Contains("connectionVerified", eventTypes);
        Assert.Contains("connectionDeleted", eventTypes);
        Assert.Equal("connectionDeleted", auditBody[0].GetProperty("eventType").GetString());
    }

    [Fact]
    public async Task ResolveReviewerIdentities_RegisteredProvider_ReturnsCandidates()
    {
        await factory.ResetProviderStateAsync();
        var connection = await factory.CreateConnectionAsync();
        factory.SetResolvedReviewerCandidates(
            new ReviewerIdentity(
                new ProviderHostRef(ScmProvider.GitHub, "https://github.com"),
                "github-reviewer-1",
                "meister-dev-bot",
                "Meister Dev Bot",
                true));

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/provider-connections/{connection.Id}/reviewer-identities/resolve?search=meister");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Single(body.EnumerateArray());
        Assert.Equal("github-reviewer-1", body[0].GetProperty("externalUserId").GetString());
        Assert.Equal("meister-dev-bot", body[0].GetProperty("login").GetString());
    }

    [Fact]
    public async Task ResolveReviewerIdentities_WhenProviderResolutionFails_ReturnsConflictWithSafeError()
    {
        await factory.ResetProviderStateAsync();
        var connection = await factory.CreateConnectionAsync();
        factory.SetReviewerIdentityResolutionFailure("The provider token raw-secret-value is invalid.");

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/provider-connections/{connection.Id}/reviewer-identities/resolve?search=meister");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(
            "Reviewer identity resolution is unavailable for this provider connection.",
            body.GetProperty("error").GetString());
        Assert.DoesNotContain(
            "raw-secret-value",
            body.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PutReviewerIdentity_ClientAdministrator_PersistsSelection()
    {
        await factory.ResetProviderStateAsync();
        var connection = await factory.CreateConnectionAsync();

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/clients/{factory.ClientId}/provider-connections/{connection.Id}/reviewer-identity");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        request.Content = JsonContent.Create(
            new
            {
                externalUserId = "github-reviewer-1",
                login = "meister-dev-bot",
                displayName = "Meister Dev Bot",
                isBot = true,
            });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("github-reviewer-1", body.GetProperty("externalUserId").GetString());

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IClientReviewerIdentityRepository>();
        var persisted = await repository.GetByConnectionIdAsync(
            factory.ClientId,
            connection.Id,
            CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal("meister-dev-bot", persisted.Login);
    }

    [Fact]
    public async Task DeleteProviderConnection_Admin_Returns204AndRemovesConnection()
    {
        await factory.ResetProviderStateAsync();
        var created = await factory.CreateConnectionAsync();

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/clients/{factory.ClientId}/provider-connections/{created.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateAdminToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IClientScmConnectionRepository>();
        var persisted = await repository.GetByIdAsync(factory.ClientId, created.Id, CancellationToken.None);
        Assert.Null(persisted);
    }

    public sealed class ProviderConnectionsApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-provider-connections-jwt-secret!";

        private readonly string _dbName = $"TestDb_ProviderConnections_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();
        private readonly IScmProviderRegistry _providerRegistry = Substitute.For<IScmProviderRegistry>();

        private readonly IRepositoryDiscoveryProvider _repositoryDiscoveryProvider =
            Substitute.For<IRepositoryDiscoveryProvider>();

        private readonly IReviewerIdentityService _reviewerIdentityService = Substitute.For<IReviewerIdentityService>();

        public ProviderConnectionsApiFactory()
        {
            this._providerRegistry.IsRegistered(Arg.Any<ScmProvider>()).Returns(false);
            this._providerRegistry.IsRegistered(ScmProvider.GitHub).Returns(true);
            this._providerRegistry.IsRegistered(ScmProvider.GitLab).Returns(true);
            this._providerRegistry.IsRegistered(ScmProvider.Forgejo).Returns(true);
            this._providerRegistry.IsRegistered(ScmProvider.AzureDevOps).Returns(true);
            this._providerRegistry.GetRegisteredCapabilities(Arg.Any<ScmProvider>())
                .Returns(["repositoryDiscovery", "reviewQuery", "reviewPublication"]);
            this._providerRegistry.GetRepositoryDiscoveryProvider(ScmProvider.GitHub)
                .Returns(this._repositoryDiscoveryProvider);
            this._providerRegistry.GetReviewerIdentityService(ScmProvider.GitHub)
                .Returns(this._reviewerIdentityService);

            this.SetDiscoveryScopes("acme/platform");

            this.SetResolvedReviewerCandidates(
                new ReviewerIdentity(
                    new ProviderHostRef(ScmProvider.GitHub, "https://github.com"),
                    "github-reviewer-1",
                    "meister-dev-bot",
                    "Meister Dev Bot",
                    true));
        }

        public Guid ClientId { get; } = Guid.NewGuid();
        public Guid OtherClientId { get; } = Guid.NewGuid();
        public Guid ClientAdministratorUserId { get; } = Guid.NewGuid();
        public Guid ClientUserId { get; } = Guid.NewGuid();
        public Guid ConnectionId { get; private set; }

        public string GenerateAdminToken()
        {
            return this.GenerateToken(Guid.NewGuid(), AppUserRole.Admin);
        }

        public string GenerateClientAdministratorToken()
        {
            return this.GenerateToken(this.ClientAdministratorUserId, AppUserRole.User);
        }

        public string GenerateClientUserToken()
        {
            return this.GenerateToken(this.ClientUserId, AppUserRole.User);
        }

        public void SetResolvedReviewerCandidates(params ReviewerIdentity[] identities)
        {
            this._reviewerIdentityService
                .ResolveCandidatesAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<ProviderHostRef>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<ReviewerIdentity>>(identities));
        }

        public void SetReviewerIdentityResolutionFailure(string message)
        {
            this._reviewerIdentityService
                .ResolveCandidatesAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<ProviderHostRef>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromException<IReadOnlyList<ReviewerIdentity>>(new InvalidOperationException(message)));
        }

        public void SetDiscoveryFailure(string message)
        {
            this._repositoryDiscoveryProvider
                .ListScopesAsync(Arg.Any<Guid>(), Arg.Any<ProviderHostRef>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<IReadOnlyList<string>>(new InvalidOperationException(message)));
        }

        public void SetDiscoveryScopes(params string[] scopePaths)
        {
            this._repositoryDiscoveryProvider
                .ListScopesAsync(Arg.Any<Guid>(), Arg.Any<ProviderHostRef>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<string>>(scopePaths));
        }

        public async Task<ClientScmConnectionDto> CreateConnectionAsync(
            ScmProvider providerFamily = ScmProvider.GitHub,
            string? hostBaseUrl = null,
            ScmAuthenticationKind authenticationKind = ScmAuthenticationKind.PersonalAccessToken,
            string? oAuthTenantId = null,
            string? oAuthClientId = null,
            string displayName = "Acme GitHub",
            string secret = "ghp_default_secret",
            bool isActive = true)
        {
            var resolvedHostBaseUrl = hostBaseUrl ?? $"https://github-{Guid.NewGuid():N}.example.com/acme/platform";

            using var scope = this.Services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IClientScmConnectionRepository>();
            var created = await repository.AddAsync(
                this.ClientId,
                providerFamily,
                resolvedHostBaseUrl,
                authenticationKind,
                oAuthTenantId,
                oAuthClientId,
                displayName,
                secret,
                isActive,
                CancellationToken.None);

            this.ConnectionId = created!.Id;
            return created;
        }

        public async Task ResetProviderStateAsync(bool usePublishDefaults = false)
        {
            using var scope = this.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();

            db.ProviderActivations.RemoveRange(db.ProviderActivations);
            db.ProviderConnectionAuditEntries.RemoveRange(db.ProviderConnectionAuditEntries);
            db.ClientReviewerIdentities.RemoveRange(db.ClientReviewerIdentities);
            db.ClientScmScopes.RemoveRange(db.ClientScmScopes);
            db.ClientScmConnections.RemoveRange(db.ClientScmConnections);

            if (!usePublishDefaults)
            {
                var updatedAt = DateTimeOffset.UtcNow;
                db.ProviderActivations.AddRange(
                    Enum.GetValues<ScmProvider>()
                        .Select(provider => new ProviderActivationRecord
                        {
                            Provider = provider,
                            IsEnabled = true,
                            UpdatedAt = updatedAt,
                        }));
            }

            await db.SaveChangesAsync();
            this.SetDiscoveryScopes("acme/platform");
            this.SetResolvedReviewerCandidates(
                new ReviewerIdentity(
                    new ProviderHostRef(ScmProvider.GitHub, "https://github.com"),
                    "github-reviewer-1",
                    "meister-dev-bot",
                    "Meister Dev Bot",
                    true));
        }

        public async Task<ClientScmScopeDto> CreateScopeAsync(
            Guid connectionId,
            string scopeType = "organization",
            string externalScopeId = "acme",
            string scopePath = "acme",
            string displayName = "Acme",
            bool isEnabled = true)
        {
            using var scope = this.Services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IClientScmScopeRepository>();
            return (await repository.AddAsync(
                this.ClientId,
                connectionId,
                scopeType,
                externalScopeId,
                scopePath,
                displayName,
                isEnabled,
                CancellationToken.None))!;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;
            var clientId = this.ClientId;
            var clientAdministratorUserId = this.ClientAdministratorUserId;
            var clientUserId = this.ClientUserId;
            var providerRegistry = this._providerRegistry;

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IJwtTokenService, JwtTokenService>();
                services.AddDbContext<MeisterProPRDbContext>(options =>
                    options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddDbContextFactory<MeisterProPRDbContext>(options =>
                    options.UseInMemoryDatabase(dbName, dbRoot));

                services.AddScoped<IClientAdminService, ClientAdminService>();
                services.AddScoped<IClientScmConnectionRepository, ClientScmConnectionRepository>();
                services.AddScoped<IClientScmScopeRepository, ClientScmScopeRepository>();
                services.AddScoped<IClientReviewerIdentityRepository, ClientReviewerIdentityRepository>();
                services.AddScoped<IProviderActivationService, ProviderActivationService>();
                services.AddSingleton<IProviderReadinessProfileCatalog, StaticProviderReadinessProfileCatalog>();
                services.AddScoped<IProviderReadinessEvaluator, ProviderReadinessEvaluator>();
                services.AddScoped<IProviderOperationalStatusService, ProviderOperationalStatusService>();

                services.AddSingleton(providerRegistry);
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());
                services.AddSingleton(Substitute.For<IClientRegistry>());
                services.AddSingleton(Substitute.For<IJobRepository>());

                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                userRepo.GetUserClientRolesAsync(clientAdministratorUserId, Arg.Any<CancellationToken>())
                    .Returns(
                        Task.FromResult(
                            new Dictionary<Guid, ClientRole>
                            {
                                { clientId, ClientRole.ClientAdministrator },
                            }));
                userRepo.GetUserClientRolesAsync(clientUserId, Arg.Any<CancellationToken>())
                    .Returns(
                        Task.FromResult(
                            new Dictionary<Guid, ClientRole>
                            {
                                { clientId, ClientRole.ClientUser },
                            }));
                userRepo.GetUserClientRolesAsync(
                        Arg.Is<Guid>(id => id != clientAdministratorUserId && id != clientUserId),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                services.AddSingleton(userRepo);

                var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
                crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));
                services.AddSingleton(crawlRepo);

                var organizationScopeRepository = Substitute.For<IClientAdoOrganizationScopeRepository>();
                organizationScopeRepository.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<ClientAdoOrganizationScopeDto>>([]));
                services.AddSingleton(organizationScopeRepository);
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Clients.AddRange(
                new ClientRecord
                {
                    Id = this.ClientId,
                    DisplayName = "Provider Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                new ClientRecord
                {
                    Id = this.OtherClientId,
                    DisplayName = "Other Provider Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            db.SaveChanges();

            return host;
        }

        private string GenerateToken(Guid userId, AppUserRole role)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(
                "meisterpropr",
                "meisterpropr",
                [
                    new Claim("sub", userId.ToString()),
                    new Claim("global_role", role.ToString()),
                ],
                expires: DateTime.UtcNow.AddHours(1),
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
