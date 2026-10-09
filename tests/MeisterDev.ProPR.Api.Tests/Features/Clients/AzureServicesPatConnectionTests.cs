// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterDev.ProPR.Api.Features.IdentityAndAccess.Authentication;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Features.Clients;

public sealed class AzureServicesPatConnectionTests
{
    [Fact]
    public async Task ServicesPatSwitchToOAuth_RequiresNewSecretAndMetadataAndPreservesSameKindEdits()
    {
        using var factory = new ClientProviderConnectionsControllerTests.ProviderConnectionsApiFactory();
        await factory.ResetProviderStateAsync();
        var connection = await factory.CreateConnectionAsync(
            ScmProvider.AzureDevOps, "https://dev.azure.com", ScmAuthenticationKind.PersonalAccessToken,
            secret: "synthetic-pat");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", factory.GenerateClientAdministratorToken());
        var path = $"/clients/{factory.ClientId}/provider-connections/{connection.Id}";

        using var missingSecret = await client.PatchAsJsonAsync(
            path, new
            {
                authenticationKind = "oauthClientCredentials", oAuthTenantId = "synthetic-tenant", oAuthClientId = "synthetic-client"
            });
        Assert.Equal(HttpStatusCode.BadRequest, missingSecret.StatusCode);
        var secretProblem = await missingSecret.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(secretProblem.GetProperty("errors").TryGetProperty("Secret", out _));

        using var missingMetadata = await client.PatchAsJsonAsync(
            path, new
            {
                authenticationKind = "oauthClientCredentials", secret = "synthetic-client-secret"
            });
        Assert.Equal(HttpStatusCode.BadRequest, missingMetadata.StatusCode);
        var metadataProblem = await missingMetadata.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(metadataProblem.GetProperty("errors").TryGetProperty("OAuthTenantId", out _));
        Assert.True(metadataProblem.GetProperty("errors").TryGetProperty("OAuthClientId", out _));

        using var retainedPatResponse = await client.PatchAsJsonAsync(path, new { displayName = "Retained PAT" });
        Assert.Equal(HttpStatusCode.OK, retainedPatResponse.StatusCode);
        var retainedPat = await retainedPatResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("personalAccessToken", retainedPat.GetProperty("authenticationKind").GetString());

        using var accepted = await client.PatchAsJsonAsync(
            path, new
            {
                authenticationKind = "oauthClientCredentials", secret = "synthetic-client-secret",
                oAuthTenantId = "synthetic-tenant", oAuthClientId = "synthetic-client"
            });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var retainedOAuthResponse = await client.PatchAsJsonAsync(path, new { displayName = "Retained OAuth" });
        Assert.Equal(HttpStatusCode.OK, retainedOAuthResponse.StatusCode);
        var retainedOAuth = await retainedOAuthResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("oauthClientCredentials", retainedOAuth.GetProperty("authenticationKind").GetString());
        Assert.Equal("synthetic-client", retainedOAuth.GetProperty("oAuthClientId").GetString());
        Assert.False(retainedOAuth.TryGetProperty("secret", out _));
    }

    [Theory]
    [InlineData("https://dev.azure.com/organization", "https://dev.azure.com/organization")]
    [InlineData("https://organization.visualstudio.com", "https://organization.visualstudio.com")]
    public async Task ServicesPat_MachineCallerCreatesEditsAndVerifiesWithScope(string hostBaseUrl, string organizationUrl)
    {
        using var factory = new ClientProviderConnectionsControllerTests.ProviderConnectionsApiFactory();
        factory.EnableMachineTenantTest();
        await factory.ResetProviderStateAsync();
        using var client = factory.CreateClient();
        using var services = factory.Services.CreateScope();
        var credentialService = services.ServiceProvider.GetRequiredService<TenantMachineCredentialService>();
        var issued = await credentialService.IssueAsync(factory.MachineTenantId, "PAT test", null, Guid.NewGuid(), default);
        client.DefaultRequestHeaders.Add("X-Tenant-Machine-Token", issued!.Token);
        var path = $"/clients/{factory.ClientId}/provider-connections";

        using var createdResponse = await client.PostAsJsonAsync(
            path, new
            {
                providerFamily = "azureDevOps", hostBaseUrl, authenticationKind = "personalAccessToken",
                displayName = "Services PAT", secret = "synthetic-pat", isActive = true,
            });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var connectionId = created.GetProperty("id").GetGuid();
        Assert.Equal("unknown", created.GetProperty("verificationStatus").GetString());
        Assert.False(created.TryGetProperty("secret", out _));

        using var missingScopeResponse = await client.PostAsync($"{path}/{connectionId}/verify", null);
        Assert.Equal(HttpStatusCode.OK, missingScopeResponse.StatusCode);
        var missingScope = await missingScopeResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("failed", missingScope.GetProperty("verificationStatus").GetString());
        Assert.Contains("enabled organization scope", missingScope.GetProperty("lastVerificationError").GetString());
        await factory.AzureDiscovery.DidNotReceiveWithAnyArgs().ListProjectOptionsAsync(default, default, default);

        var scope = await factory.CreateScopeAsync(connectionId, scopePath: organizationUrl);
        using var editedResponse = await client.PatchAsJsonAsync($"{path}/{connectionId}", new { displayName = "Edited Services PAT" });
        Assert.Equal(HttpStatusCode.OK, editedResponse.StatusCode);
        var edited = await editedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("personalAccessToken", edited.GetProperty("authenticationKind").GetString());

        using var verifiedResponse = await client.PostAsync($"{path}/{connectionId}/verify", null);
        Assert.Equal(HttpStatusCode.OK, verifiedResponse.StatusCode);
        var verified = await verifiedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("verified", verified.GetProperty("verificationStatus").GetString());
        await factory.AzureDiscovery.Received(1).ListProjectOptionsAsync(factory.ClientId, scope.Id, Arg.Any<CancellationToken>());

        using var foreignCreate = await client.PostAsJsonAsync(
            $"/clients/{factory.OtherClientId}/provider-connections", new
            {
                providerFamily = "azureDevOps", hostBaseUrl, authenticationKind = "personalAccessToken",
                displayName = "Foreign Services PAT", secret = "synthetic-pat", isActive = true,
            });
        Assert.Equal(HttpStatusCode.Forbidden, foreignCreate.StatusCode);
        using var foreignPatch = await client.PatchAsJsonAsync(
            $"/clients/{factory.OtherClientId}/provider-connections/{connectionId}", new { displayName = "Foreign" });
        Assert.Equal(HttpStatusCode.Forbidden, foreignPatch.StatusCode);
        using var foreignVerify = await client.PostAsync($"/clients/{factory.OtherClientId}/provider-connections/{connectionId}/verify", null);
        Assert.Equal(HttpStatusCode.Forbidden, foreignVerify.StatusCode);
    }

    [Fact]
    public async Task ServicesOAuthSwitchToPat_RequiresReplacementSecret()
    {
        using var factory = new ClientProviderConnectionsControllerTests.ProviderConnectionsApiFactory();
        await factory.ResetProviderStateAsync();
        var connection = await factory.CreateConnectionAsync(
            ScmProvider.AzureDevOps, "https://dev.azure.com", ScmAuthenticationKind.OAuthClientCredentials,
            "synthetic-tenant", "synthetic-client");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", factory.GenerateClientAdministratorToken());
        var path = $"/clients/{factory.ClientId}/provider-connections/{connection.Id}";

        using var rejected = await client.PatchAsJsonAsync(path, new { authenticationKind = "personalAccessToken" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("Secret", out _));

        using var accepted = await client.PatchAsJsonAsync(path, new { authenticationKind = "personalAccessToken", secret = "synthetic-replacement-pat" });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var updated = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("personalAccessToken", updated.GetProperty("authenticationKind").GetString());
    }
}
