// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace MeisterProPR.Api.Tests.IdentityAndAccess;

public sealed class TenantAuthControllerTests(TenantAdministrationApiFactory factory)
    : IClassFixture<TenantAdministrationApiFactory>
{
    [Fact]
    public async Task GetProviders_WhenSsoCapabilityUnavailable_ReturnsTenantLocalPolicyWithNoExternalProviders()
    {
        factory.ResetLicensing();
        factory.SetSsoCapabilityAvailability(false);

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp", true);
        await factory.SeedSsoProviderAsync(tenantId, "Acme Entra");

        var response = await factory.CreateClient().GetAsync($"/auth/tenants/{tenantSlug}/providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(tenantSlug, body.GetProperty("tenantSlug").GetString());
        Assert.True(body.GetProperty("localLoginEnabled").GetBoolean());
        Assert.Empty(body.GetProperty("providers").EnumerateArray());
    }

    [Fact]
    public async Task GetProviders_WithActiveTenant_ReturnsOnlyEnabledProvidersAndLocalPolicy()
    {
        factory.ResetLicensing();

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp", false);
        await factory.SeedSsoProviderAsync(tenantId, "Acme Entra");
        var disabledProviderId = await factory.SeedSsoProviderAsync(tenantId, "Acme GitHub", "GitHub", "Oauth2");

        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITenantSsoProviderService>();
            await service.UpdateAsync(
                tenantId,
                disabledProviderId,
                "Acme GitHub",
                "GitHub",
                "Oauth2",
                "https://github.com/login/oauth",
                "client-id",
                null,
                ["read:user"],
                ["acme.test"],
                false,
                true);
        }

        var response = await factory.CreateClient().GetAsync($"/auth/tenants/{tenantSlug}/providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(tenantSlug, body.GetProperty("tenantSlug").GetString());
        Assert.False(body.GetProperty("localLoginEnabled").GetBoolean());
        Assert.Single(body.GetProperty("providers").EnumerateArray());
        Assert.Equal("Acme Entra", body.GetProperty("providers")[0].GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task LocalLogin_WithTenantMembershipAndPassword_ReturnsSession()
    {
        factory.ResetLicensing();
        factory.SetSsoCapabilityAvailability(false, "Commercial edition is required to use single sign-on.");

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var username = $"tenant.user.{Guid.NewGuid():N}";
        var email = $"{username}@acme.test";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp");
        var userId = await factory.SeedUserAsync(username, email);
        await factory.SetLocalPasswordAsync(userId, "CorrectPassword1!");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantUser);

        var response = await factory.CreateClient()
            .PostAsJsonAsync(
                $"/auth/tenants/{tenantSlug}/local-login",
                new { username, password = "CorrectPassword1!" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("refreshToken").GetString()));

        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(body.GetProperty("accessToken").GetString());
        Assert.Equal(userId.ToString(), token.Subject);
        Assert.Equal(username, token.Claims.Single(claim => claim.Type == JwtRegisteredClaimNames.UniqueName).Value);
    }

    [Fact]
    public async Task ChallengeExternal_WhenSsoCapabilityUnavailable_ReturnsPremiumFeatureUnavailable()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();
        factory.SetSsoCapabilityAvailability(false);

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp");
        var providerId = await factory.SeedSsoProviderAsync(tenantId, "Acme Entra");

        var response = await factory.CreateClient().GetAsync($"/auth/external/challenge/{tenantSlug}/{providerId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("premium_feature_unavailable", body.GetProperty("error").GetString());
        Assert.Equal(PremiumCapabilityKey.SsoAuthentication, body.GetProperty("feature").GetString());
    }

    [Fact]
    public async Task ChallengeExternal_WithEntraProvider_BuildsAuthorizationRedirect()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp");
        const string authority = "https://login.microsoftonline.com/contoso.onmicrosoft.com/v2.0";
        const string clientId = "entra-client-id";
        var providerId = await factory.SeedSsoProviderAsync(
            tenantId,
            "Acme Entra",
            issuerOrAuthorityUrl: authority,
            clientId: clientId,
            scopes: ["openid", "profile", "email"]);

        using var client = CreateNonRedirectingClient(factory);
        var response = await client.GetAsync($"/auth/external/challenge/{tenantSlug}/{providerId}");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location;
        Assert.NotNull(location);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("login.microsoftonline.com", location.Host);
        Assert.Equal("/contoso.onmicrosoft.com/oauth2/v2.0/authorize", location.AbsolutePath);
        Assert.Equal(clientId, GetSingleQueryValue(query, "client_id"));
        Assert.Equal("code", GetSingleQueryValue(query, "response_type"));
        Assert.Equal("query", GetSingleQueryValue(query, "response_mode"));
        Assert.Equal(BuildExpectedCallbackUri(client.BaseAddress!, tenantSlug), GetSingleQueryValue(query, "redirect_uri"));
        Assert.Equal("openid profile email", GetSingleQueryValue(query, "scope"));
        Assert.False(string.IsNullOrWhiteSpace(GetSingleQueryValue(query, "state")));
    }

    [Fact]
    public async Task ChallengeExternal_WithGoogleProvider_BuildsAuthorizationRedirect()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp");
        const string clientId = "google-client-id";
        var providerId = await factory.SeedSsoProviderAsync(
            tenantId,
            "Acme Google",
            "Google",
            "Oidc",
            "https://accounts.google.com",
            clientId,
            scopes: ["openid", "profile", "email"]);

        using var client = CreateNonRedirectingClient(factory);
        var response = await client.GetAsync($"/auth/external/challenge/{tenantSlug}/{providerId}");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location;
        Assert.NotNull(location);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("accounts.google.com", location.Host);
        Assert.Equal("/o/oauth2/v2/auth", location.AbsolutePath);
        Assert.Equal(clientId, GetSingleQueryValue(query, "client_id"));
        Assert.Equal("code", GetSingleQueryValue(query, "response_type"));
        Assert.Equal(BuildExpectedCallbackUri(client.BaseAddress!, tenantSlug), GetSingleQueryValue(query, "redirect_uri"));
        Assert.Equal("openid profile email", GetSingleQueryValue(query, "scope"));
        Assert.False(string.IsNullOrWhiteSpace(GetSingleQueryValue(query, "state")));
    }

    [Fact]
    public async Task ChallengeExternal_WithGitHubProvider_BuildsAuthorizationRedirect()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp");
        const string clientId = "github-client-id";
        var providerId = await factory.SeedSsoProviderAsync(
            tenantId,
            "Acme GitHub",
            "GitHub",
            "Oauth2",
            "https://github.com",
            clientId,
            scopes: ["read:user", "user:email"]);

        using var client = CreateNonRedirectingClient(factory);
        var response = await client.GetAsync($"/auth/external/challenge/{tenantSlug}/{providerId}");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location;
        Assert.NotNull(location);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("github.com", location.Host);
        Assert.Equal("/login/oauth/authorize", location.AbsolutePath);
        Assert.Equal(clientId, GetSingleQueryValue(query, "client_id"));
        Assert.Equal(BuildExpectedCallbackUri(client.BaseAddress!, tenantSlug), GetSingleQueryValue(query, "redirect_uri"));
        Assert.Equal("read:user user:email", GetSingleQueryValue(query, "scope"));
        Assert.False(string.IsNullOrWhiteSpace(GetSingleQueryValue(query, "state")));
    }

    [Fact]
    public async Task ChallengeExternal_UsesConfiguredPublicBaseUrl_ForCallbackRedirectUri()
    {
        await using var configuredFactory = new TenantAdministrationApiFactory();
        configuredFactory.SetPublicBaseUrl("https://propr.example.test/api");
        configuredFactory.ResetLicensing();
        configuredFactory.ResetExternalAuthResponses();

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var tenantId = await configuredFactory.SeedTenantAsync(tenantSlug, "Acme Corp");
        var providerId = await configuredFactory.SeedSsoProviderAsync(
            tenantId,
            "Acme Entra",
            issuerOrAuthorityUrl: "https://login.microsoftonline.com/common/v2.0",
            clientId: "entra-client-id",
            scopes: ["openid", "profile", "email"]);

        using var client = CreateNonRedirectingClient(configuredFactory);
        var response = await client.GetAsync($"/auth/external/challenge/{tenantSlug}/{providerId}");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location;
        Assert.NotNull(location);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal(
            $"https://propr.example.test/api/auth/external/callback/{Uri.EscapeDataString(tenantSlug)}",
            GetSingleQueryValue(query, "redirect_uri"));
    }

    [Fact]
    public async Task ExternalCallback_WithVerifiedAllowedEmail_AutoCreatesUserMembershipAndIdentity()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var emailLocalPart = $"new.user.{Guid.NewGuid():N}";
        var email = $"{emailLocalPart}@acme.test";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp");
        const string clientId = "entra-client-id";
        var providerId = await factory.SeedSsoProviderAsync(
            tenantId,
            "Acme Entra",
            issuerOrAuthorityUrl: "https://login.microsoftonline.com/common/v2.0",
            clientId: clientId,
            scopes: ["openid", "profile", "email"]);

        using var client = CreateNonRedirectingClient(factory);
        var (_, state) = await StartChallengeAsync(client, tenantSlug, providerId);
        var expectedCallbackUri = BuildExpectedCallbackUri(client.BaseAddress!, tenantSlug);

        factory.QueueExternalAuthResponse(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/token", request.RequestUri?.ToString());
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            Assert.Contains($"code={Uri.EscapeDataString("entra-code-1")}", body, StringComparison.Ordinal);
            Assert.Contains($"redirect_uri={Uri.EscapeDataString(expectedCallbackUri)}", body, StringComparison.Ordinal);

            return CreateJsonResponse(
                new
                {
                    id_token = CreateOidcIdToken(
                        "https://login.microsoftonline.com/common/v2.0",
                        clientId,
                        "entra-user-1",
                        email,
                        true,
                        "New User"),
                });
        });

        var response = await factory.CreateClient().GetAsync($"/auth/external/callback/{tenantSlug}?code=entra-code-1&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var user = await dbContext.AppUsers.SingleAsync(record => record.NormalizedEmail == email.ToUpperInvariant());
        Assert.Equal(emailLocalPart, user.Username);
        Assert.Null(user.PasswordHash);
        Assert.True(await dbContext.TenantMemberships.AnyAsync(membership => membership.TenantId == tenantId && membership.UserId == user.Id));
        Assert.True(
            await dbContext.ExternalIdentities.AnyAsync(identity =>
                identity.TenantId == tenantId
                && identity.SsoProviderId == providerId
                && identity.Issuer == "https://login.microsoftonline.com/common/v2.0"
                && identity.Subject == "entra-user-1"));
    }

    [Fact]
    public async Task ExternalCallback_WithExistingExplicitLink_ReusesExistingUser()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var username = $"tenant.user.{Guid.NewGuid():N}";
        var email = $"{username}@acme.test";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp");
        const string clientId = "entra-client-id";
        var providerId = await factory.SeedSsoProviderAsync(
            tenantId,
            "Acme Entra",
            issuerOrAuthorityUrl: "https://login.microsoftonline.com/common/v2.0",
            clientId: clientId,
            scopes: ["openid", "profile", "email"]);
        var userId = await factory.SeedUserAsync(username, email);
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantUser);
        await factory.SeedExternalIdentityAsync(
            tenantId,
            userId,
            providerId,
            "https://login.microsoftonline.com/common/v2.0",
            "entra-user-1",
            email);

        using var client = CreateNonRedirectingClient(factory);
        var (_, state) = await StartChallengeAsync(client, tenantSlug, providerId);
        var expectedCallbackUri = BuildExpectedCallbackUri(client.BaseAddress!, tenantSlug);

        factory.QueueExternalAuthResponse(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/token", request.RequestUri?.ToString());
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            Assert.Contains($"code={Uri.EscapeDataString("entra-code-2")}", body, StringComparison.Ordinal);
            Assert.Contains($"redirect_uri={Uri.EscapeDataString(expectedCallbackUri)}", body, StringComparison.Ordinal);

            return CreateJsonResponse(
                new
                {
                    id_token = CreateOidcIdToken(
                        "https://login.microsoftonline.com/common/v2.0",
                        clientId,
                        "entra-user-1",
                        email,
                        true,
                        username),
                });
        });

        var response = await client.GetAsync($"/auth/external/callback/{tenantSlug}?code=entra-code-2&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var token = new JwtSecurityTokenHandler().ReadJwtToken(body.GetProperty("accessToken").GetString());
        Assert.Equal(userId.ToString(), token.Subject);
    }

    [Fact]
    public async Task ExternalCallback_WithExistingUserEmailAndNoTenantMembership_AutoCreatesMembershipAndLinksIdentity()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var username = $"tenant.user.{Guid.NewGuid():N}";
        var email = $"{username}@acme.test";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp");
        const string clientId = "entra-client-id";
        var providerId = await factory.SeedSsoProviderAsync(
            tenantId,
            "Acme Entra",
            issuerOrAuthorityUrl: "https://login.microsoftonline.com/common/v2.0",
            clientId: clientId,
            scopes: ["openid", "profile", "email"],
            autoCreateUsers: true);
        var existingUserId = await factory.SeedUserAsync(username, email);

        using var client = CreateNonRedirectingClient(factory);
        var (_, state) = await StartChallengeAsync(client, tenantSlug, providerId);
        var expectedCallbackUri = BuildExpectedCallbackUri(client.BaseAddress!, tenantSlug);

        factory.QueueExternalAuthResponse(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/token", request.RequestUri?.ToString());
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            Assert.Contains($"code={Uri.EscapeDataString("entra-code-existing-user")}", body, StringComparison.Ordinal);
            Assert.Contains($"redirect_uri={Uri.EscapeDataString(expectedCallbackUri)}", body, StringComparison.Ordinal);

            return CreateJsonResponse(
                new
                {
                    id_token = CreateOidcIdToken(
                        "https://login.microsoftonline.com/common/v2.0",
                        clientId,
                        "entra-existing-user",
                        email,
                        true,
                        username),
                });
        });

        var response = await client.GetAsync($"/auth/external/callback/{tenantSlug}?code=entra-code-existing-user&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBody = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var token = new JwtSecurityTokenHandler().ReadJwtToken(responseBody.GetProperty("accessToken").GetString());
        Assert.Equal(existingUserId.ToString(), token.Subject);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        Assert.True(await dbContext.TenantMemberships.AnyAsync(membership => membership.TenantId == tenantId && membership.UserId == existingUserId));
        Assert.True(
            await dbContext.ExternalIdentities.AnyAsync(identity =>
                identity.TenantId == tenantId
                && identity.SsoProviderId == providerId
                && identity.Subject == "entra-existing-user"
                && identity.UserId == existingUserId));
    }

    [Fact]
    public async Task ExternalCallback_WhenSsoCapabilityUnavailable_ReturnsPremiumFeatureUnavailable()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();
        factory.SetSsoCapabilityAvailability(false);

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp");
        var providerId = await factory.SeedSsoProviderAsync(tenantId, "Acme Entra");

        var response = await factory.CreateClient().GetAsync($"/auth/external/callback/{tenantSlug}?code=ignored&state=ignored");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("premium_feature_unavailable", body.GetProperty("error").GetString());
        Assert.Equal(PremiumCapabilityKey.SsoAuthentication, body.GetProperty("feature").GetString());
    }

    [Fact]
    public async Task ExternalCallback_WithInvalidState_Returns401AndDoesNotCallProvider()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp");
        var providerId = await factory.SeedSsoProviderAsync(
            tenantId,
            "Acme Entra",
            issuerOrAuthorityUrl: "https://login.microsoftonline.com/common/v2.0",
            clientId: "entra-client-id");

        var response = await factory.CreateClient()
            .GetAsync($"/auth/external/callback/{tenantSlug}?code=entra-code-invalid&state={Uri.EscapeDataString("invalid-state")}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExternalCallback_WithMatchingExistingEmailOutsideTenantMembership_AndAutoCreateDisabled_Returns401AndDoesNotCreateIdentity()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var username = $"tenant.user.{Guid.NewGuid():N}";
        var email = $"{username}@acme.test";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp");
        const string clientId = "entra-client-id";
        var providerId = await factory.SeedSsoProviderAsync(
            tenantId,
            "Acme Entra",
            issuerOrAuthorityUrl: "https://login.microsoftonline.com/common/v2.0",
            clientId: clientId,
            scopes: ["openid", "profile", "email"],
            autoCreateUsers: false);
        var existingUserId = await factory.SeedUserAsync(username, email);
        var otherTenantId = await factory.SeedTenantAsync($"globex-{Guid.NewGuid():N}", "Globex Corp");
        await factory.SeedTenantMembershipAsync(otherTenantId, existingUserId, TenantRole.TenantUser);

        using var client = CreateNonRedirectingClient(factory);
        var (_, state) = await StartChallengeAsync(client, tenantSlug, providerId);
        var expectedCallbackUri = BuildExpectedCallbackUri(client.BaseAddress!, tenantSlug);

        factory.QueueExternalAuthResponse(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/token", request.RequestUri?.ToString());
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            Assert.Contains($"code={Uri.EscapeDataString("entra-code-3")}", body, StringComparison.Ordinal);
            Assert.Contains($"redirect_uri={Uri.EscapeDataString(expectedCallbackUri)}", body, StringComparison.Ordinal);

            return CreateJsonResponse(
                new
                {
                    id_token = CreateOidcIdToken(
                        "https://login.microsoftonline.com/common/v2.0",
                        clientId,
                        "entra-user-2",
                        email,
                        true,
                        username),
                });
        });

        var response = await client.GetAsync($"/auth/external/callback/{tenantSlug}?code=entra-code-3&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var responseBody = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("auto_create_disabled", responseBody.GetProperty("error").GetString());
        Assert.Equal(
            "First-time sign-in is disabled for this provider. Ask a tenant administrator to enable sign-in for your account.",
            responseBody.GetProperty("message").GetString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        Assert.False(
            await dbContext.ExternalIdentities.AnyAsync(identity =>
                identity.TenantId == tenantId
                && identity.SsoProviderId == providerId
                && identity.Subject == "entra-user-2"));
    }

    [Fact]
    public async Task ExternalCallback_WithExistingTenantMemberAndRecreatedProvider_RelinksIdentityAndReturns200()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var username = $"tenant.user.{Guid.NewGuid():N}";
        var email = $"{username}@acme.test";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp");
        const string clientId = "entra-client-id";
        var originalProviderId = await factory.SeedSsoProviderAsync(
            tenantId,
            "Acme Entra",
            issuerOrAuthorityUrl: "https://login.microsoftonline.com/common/v2.0",
            clientId: clientId,
            scopes: ["openid", "profile", "email"]);
        var existingUserId = await factory.SeedUserAsync(username, email);
        await factory.SeedTenantMembershipAsync(tenantId, existingUserId, TenantRole.TenantUser);
        await factory.SeedExternalIdentityAsync(
            tenantId,
            existingUserId,
            originalProviderId,
            "https://login.microsoftonline.com/common/v2.0",
            "entra-user-relink",
            email);

        using (var deleteScope = factory.Services.CreateScope())
        {
            var providerService = deleteScope.ServiceProvider.GetRequiredService<ITenantSsoProviderService>();
            var deleted = await providerService.DeleteAsync(tenantId, originalProviderId, CancellationToken.None);
            Assert.True(deleted);
        }

        var recreatedProviderId = await factory.SeedSsoProviderAsync(
            tenantId,
            "Acme Entra",
            issuerOrAuthorityUrl: "https://login.microsoftonline.com/common/v2.0",
            clientId: clientId,
            scopes: ["openid", "profile", "email"]);

        using var client = CreateNonRedirectingClient(factory);
        var (_, state) = await StartChallengeAsync(client, tenantSlug, recreatedProviderId);
        var expectedCallbackUri = BuildExpectedCallbackUri(client.BaseAddress!, tenantSlug);

        factory.QueueExternalAuthResponse(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/token", request.RequestUri?.ToString());
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            Assert.Contains($"code={Uri.EscapeDataString("entra-code-relink")}", body, StringComparison.Ordinal);
            Assert.Contains($"redirect_uri={Uri.EscapeDataString(expectedCallbackUri)}", body, StringComparison.Ordinal);

            return CreateJsonResponse(
                new
                {
                    id_token = CreateOidcIdToken(
                        "https://login.microsoftonline.com/common/v2.0",
                        clientId,
                        "entra-user-relink",
                        email,
                        true,
                        username),
                });
        });

        var response = await client.GetAsync($"/auth/external/callback/{tenantSlug}?code=entra-code-relink&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var token = new JwtSecurityTokenHandler().ReadJwtToken(body.GetProperty("accessToken").GetString());
        Assert.Equal(existingUserId.ToString(), token.Subject);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        Assert.True(
            await dbContext.ExternalIdentities.AnyAsync(identity =>
                identity.TenantId == tenantId
                && identity.SsoProviderId == recreatedProviderId
                && identity.Subject == "entra-user-relink"
                && identity.UserId == existingUserId));
    }

    [Fact]
    public async Task ExternalCallback_WithDisallowedEmailDomain_Returns401AndDoesNotCreateUser()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var email = $"intruder.{Guid.NewGuid():N}@other.test";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp");
        const string clientId = "entra-client-id";
        var providerId = await factory.SeedSsoProviderAsync(
            tenantId,
            "Acme Entra",
            issuerOrAuthorityUrl: "https://login.microsoftonline.com/common/v2.0",
            clientId: clientId,
            scopes: ["openid", "profile", "email"]);

        using var client = CreateNonRedirectingClient(factory);
        var (_, state) = await StartChallengeAsync(client, tenantSlug, providerId);
        var expectedCallbackUri = BuildExpectedCallbackUri(client.BaseAddress!, tenantSlug);

        factory.QueueExternalAuthResponse(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/token", request.RequestUri?.ToString());
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            Assert.Contains($"code={Uri.EscapeDataString("entra-code-4")}", body, StringComparison.Ordinal);
            Assert.Contains($"redirect_uri={Uri.EscapeDataString(expectedCallbackUri)}", body, StringComparison.Ordinal);

            return CreateJsonResponse(
                new
                {
                    id_token = CreateOidcIdToken(
                        "https://login.microsoftonline.com/common/v2.0",
                        clientId,
                        "entra-user-3",
                        email,
                        true,
                        "Intruder"),
                });
        });

        var response = await client.GetAsync($"/auth/external/callback/{tenantSlug}?code=entra-code-4&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("disallowed_domain", body.GetProperty("error").GetString());
        Assert.Equal("Email domain 'other.test' is not allowed for this tenant sign-in provider.", body.GetProperty("message").GetString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        Assert.False(await dbContext.AppUsers.AnyAsync(record => record.NormalizedEmail == email.ToUpperInvariant()));
    }

    [Fact]
    public async Task ExternalCallback_WithGitHubAuthorizationCode_AutoCreatesUserMembershipAndIdentity()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var emailLocalPart = $"github.user.{Guid.NewGuid():N}";
        var email = $"{emailLocalPart}@acme.test";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp");
        const string clientId = "github-client-id";
        var providerId = await factory.SeedSsoProviderAsync(
            tenantId,
            "Acme GitHub",
            "GitHub",
            "Oauth2",
            "https://github.com",
            clientId,
            scopes: ["read:user", "user:email"]);

        using var client = CreateNonRedirectingClient(factory);
        var (_, state) = await StartChallengeAsync(client, tenantSlug, providerId);
        var expectedCallbackUri = BuildExpectedCallbackUri(client.BaseAddress!, tenantSlug);

        factory.QueueExternalAuthResponse(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://github.com/login/oauth/access_token", request.RequestUri?.ToString());
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            Assert.Contains($"code={Uri.EscapeDataString("github-code-1")}", body, StringComparison.Ordinal);
            Assert.Contains($"redirect_uri={Uri.EscapeDataString(expectedCallbackUri)}", body, StringComparison.Ordinal);

            return CreateJsonResponse(new { access_token = "github-access-token", token_type = "bearer", scope = "read:user,user:email" });
        });
        factory.QueueExternalAuthResponse(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.github.com/user", request.RequestUri?.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("github-access-token", request.Headers.Authorization?.Parameter);

            return CreateJsonResponse(new { id = 4242, login = "octocat", name = "GitHub User" });
        });
        factory.QueueExternalAuthResponse(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.github.com/user/emails", request.RequestUri?.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("github-access-token", request.Headers.Authorization?.Parameter);

            return CreateJsonResponse(
                new[]
                {
                    new GitHubEmailResponse(email, true, true),
                });
        });

        var response = await client.GetAsync($"/auth/external/callback/{tenantSlug}?code=github-code-1&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var user = await dbContext.AppUsers.SingleAsync(record => record.NormalizedEmail == email.ToUpperInvariant());
        Assert.Equal(emailLocalPart, user.Username);
        Assert.True(await dbContext.TenantMemberships.AnyAsync(membership => membership.TenantId == tenantId && membership.UserId == user.Id));
        Assert.True(
            await dbContext.ExternalIdentities.AnyAsync(identity =>
                identity.TenantId == tenantId
                && identity.SsoProviderId == providerId
                && identity.Issuer == "https://github.com"
                && identity.Subject == "4242"));
    }

    [Fact]
    public async Task ExternalCallback_WithEntraPreferredUsernameAndNoEmailVerifiedClaim_AutoCreatesUserMembershipAndIdentity()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var emailLocalPart = $"entra.user.{Guid.NewGuid():N}";
        var email = $"{emailLocalPart}@acme.test";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp");
        const string clientId = "entra-client-id";
        var providerId = await factory.SeedSsoProviderAsync(
            tenantId,
            "Acme Entra",
            issuerOrAuthorityUrl: "https://login.microsoftonline.com/common/v2.0",
            clientId: clientId,
            scopes: ["openid", "profile", "email"]);

        using var client = CreateNonRedirectingClient(factory);
        var (_, state) = await StartChallengeAsync(client, tenantSlug, providerId);
        var expectedCallbackUri = BuildExpectedCallbackUri(client.BaseAddress!, tenantSlug);

        factory.QueueExternalAuthResponse(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/token", request.RequestUri?.ToString());
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            Assert.Contains($"code={Uri.EscapeDataString("entra-code-implicit-verified")}", body, StringComparison.Ordinal);
            Assert.Contains($"redirect_uri={Uri.EscapeDataString(expectedCallbackUri)}", body, StringComparison.Ordinal);

            return CreateJsonResponse(
                new
                {
                    id_token = CreateOidcIdToken(
                        "https://login.microsoftonline.com/common/v2.0",
                        clientId,
                        "entra-user-implicit-verified",
                        null,
                        null,
                        "Entra User",
                        email),
                });
        });

        var response = await client.GetAsync($"/auth/external/callback/{tenantSlug}?code=entra-code-implicit-verified&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        Assert.True(await dbContext.AppUsers.AnyAsync(record => record.NormalizedEmail == email.ToUpperInvariant()));
        Assert.True(
            await dbContext.ExternalIdentities.AnyAsync(identity =>
                identity.TenantId == tenantId
                && identity.SsoProviderId == providerId
                && identity.Email == email
                && identity.EmailVerified));
    }

    private static async Task<(Uri Location, string State)> StartChallengeAsync(HttpClient client, string tenantSlug, Guid providerId)
    {
        var response = await client.GetAsync($"/auth/external/challenge/{tenantSlug}/{providerId}");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        var location = response.Headers.Location;
        Assert.NotNull(location);
        var query = QueryHelpers.ParseQuery(location.Query);
        return (location, GetSingleQueryValue(query, "state"));
    }

    private static string BuildExpectedCallbackUri(Uri baseAddress, string tenantSlug)
    {
        return new Uri(baseAddress, $"auth/external/callback/{Uri.EscapeDataString(tenantSlug)}").ToString();
    }

    private static HttpClient CreateNonRedirectingClient(TenantAdministrationApiFactory factory)
    {
        return factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
    }

    private static HttpResponseMessage CreateJsonResponse<T>(T payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(payload),
        };
    }

    private static string CreateOidcIdToken(
        string issuer,
        string audience,
        string subject,
        string? email,
        bool? emailVerified,
        string? displayName,
        string? preferredUsername = null,
        string? upn = null)
    {
        var claims = new List<Claim>
        {
            new("sub", subject),
        };

        if (emailVerified.HasValue)
        {
            claims.Add(new Claim("email_verified", emailVerified.Value ? "true" : "false"));
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            claims.Add(new Claim("email", email));
        }

        if (!string.IsNullOrWhiteSpace(preferredUsername))
        {
            claims.Add(new Claim("preferred_username", preferredUsername));
        }

        if (!string.IsNullOrWhiteSpace(upn))
        {
            claims.Add(new Claim("upn", upn));
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            claims.Add(new Claim("name", displayName));
        }

        var token = new JwtSecurityToken(issuer, audience, claims);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string GetSingleQueryValue(Dictionary<string, StringValues> query, string parameterName)
    {
        Assert.True(query.TryGetValue(parameterName, out var values), $"Missing query parameter '{parameterName}'.");
        return Assert.Single(values);
    }

    private sealed record GitHubEmailResponse(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("verified")]
        bool Verified,
        [property: JsonPropertyName("primary")]
        bool Primary);
}
