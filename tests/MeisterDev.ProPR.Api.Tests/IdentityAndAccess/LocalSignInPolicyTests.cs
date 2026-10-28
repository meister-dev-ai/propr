// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

/// <summary>
///     An installation that turns off the password sign-in refuses every sign-in with a password and sets no password,
///     while personal access tokens keep working.
/// </summary>
public sealed class LocalSignInPolicyTests(LocalSignInDisabledApiFactory factory) : IClassFixture<LocalSignInDisabledApiFactory>
{
    private const string Password = "CorrectPassword1!";

    [Fact]
    public async Task PasswordSignIn_WithTheRightPasswordOfAPlatformAdministrator_IsRefused()
    {
        factory.ResetLicensing();
        var username = $"admin.{Guid.NewGuid():N}";
        var userId = await factory.SeedUserAsync(username, $"{username}@example.test", AppUserRole.Admin);
        await factory.SetLocalPasswordAsync(userId, Password);

        var response = await factory.CreateClient().PostAsJsonAsync("/auth/login", new { username, password = Password });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("local_sign_in_disabled", body.GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("accessToken", out _));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task TenantLocalSignIn_OfAMemberOfATenantThatAllowsIt_IsRefused()
    {
        factory.ResetLicensing();
        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var username = $"tenant.user.{Guid.NewGuid():N}";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp", localLoginEnabled: true);
        var userId = await factory.SeedUserAsync(username, $"{username}@acme.test");
        await factory.SetLocalPasswordAsync(userId, Password);
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantUser);

        var response = await factory.CreateClient()
            .PostAsJsonAsync($"/auth/tenants/{tenantSlug}/local-login", new { username, password = Password });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("local_sign_in_disabled", body.GetProperty("error").GetString());
        Assert.False(body.TryGetProperty("accessToken", out _));
    }

    [Fact]
    public async Task TenantLoginOptions_OfATenantThatAllowsTheLocalSignIn_OfferNoLocalSignIn()
    {
        factory.ResetLicensing();
        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        await factory.SeedTenantAsync(tenantSlug, "Acme Corp", localLoginEnabled: true);

        var response = await factory.CreateClient().GetAsync($"/auth/tenants/{tenantSlug}/providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.False(body.GetProperty("localLoginEnabled").GetBoolean());
    }

    [Fact]
    public async Task AuthOptions_OfferTheProviderForPlatformAdministratorsAndNoPasswordSignIn()
    {
        factory.ResetLicensing();

        var body = await ReadJsonAsync(await factory.CreateClient().GetAsync("/auth/options"));

        var methods = body.GetProperty("availableSignInMethods").EnumerateArray().Select(method => method.GetString()).ToList();
        Assert.DoesNotContain("password", methods);
        Assert.Equal(PlatformAdminSsoApiFactory.DisplayName, body.GetProperty("platformAdminSignIn").GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task PasswordChange_OfASignedInAccount_IsRefusedAndKeepsThePassword()
    {
        factory.ResetLicensing();
        var username = $"user.{Guid.NewGuid():N}";
        var userId = await factory.SeedUserAsync(username, $"{username}@example.test");
        await factory.SetLocalPasswordAsync(userId, Password);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/users/me/password")
        {
            Content = JsonContent.Create(new { currentPassword = Password, newPassword = "AnotherPassword2!" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateToken(userId, AppUserRole.User));

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("local_sign_in_disabled", (await ReadJsonAsync(response)).GetProperty("code").GetString());
        using var scope = factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId);
        Assert.True(scope.ServiceProvider.GetRequiredService<IPasswordHashService>().Verify(Password, user!.PasswordHash!));
    }

    [Fact]
    public async Task UserCreation_WithAPassword_IsRefused()
    {
        factory.ResetLicensing();
        var username = $"created.{Guid.NewGuid():N}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/users")
        {
            Content = JsonContent.Create(new { username, password = Password, globalRole = "Admin" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateToken(Guid.NewGuid(), AppUserRole.Admin));

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("local_sign_in_disabled", (await ReadJsonAsync(response)).GetProperty("code").GetString());
        using var scope = factory.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync(username));
    }

    [Fact]
    public async Task PersonalAccessToken_OfAPlatformAdministrator_KeepsWorking()
    {
        factory.ResetLicensing();
        var username = $"platform.{Guid.NewGuid():N}";
        var userId = await factory.SeedUserAsync(username, $"{username}@example.test", AppUserRole.Admin);
        using var creation = new HttpRequestMessage(HttpMethod.Post, "/users/me/pats")
        {
            Content = JsonContent.Create(new { label = "hosting platform" }),
        };
        creation.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateToken(userId, AppUserRole.Admin));
        var token = (await ReadJsonAsync(await factory.CreateClient().SendAsync(creation))).GetProperty("token").GetString();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Pat", token);

        var response = await client.GetAsync("/admin/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }
}
