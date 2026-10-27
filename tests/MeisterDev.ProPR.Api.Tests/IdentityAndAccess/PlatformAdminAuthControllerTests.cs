// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

/// <summary>
///     The sign-in of platform administrators through the identity provider configured for the installation: the
///     role in the ID token decides at every sign-in, and an existing account is never linked by its email address.
/// </summary>
public sealed class PlatformAdminAuthControllerTests(PlatformAdminSsoApiFactory factory)
    : IClassFixture<PlatformAdminSsoApiFactory>
{
    // The test host serves the API at http://localhost, and without MEISTER_PUBLIC_BASE_URL the frontend is expected on
    // the same origin.
    private const string ReturnUrl = "http://localhost/login/callback";
    private const string ChallengePath = "/auth/platform-admin/challenge?returnUrl=http%3A%2F%2Flocalhost%2Flogin%2Fcallback";
    private const string CallbackUrl = "http://localhost/auth/platform-admin/callback";

    [Fact]
    public async Task Challenge_SendsTheBrowserToTheProviderWithStateNonceAndPkce()
    {
        this.Reset();
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync(ChallengePath);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location;
        Assert.NotNull(location);
        Assert.Equal(PlatformAdminSsoApiFactory.AuthorizationEndpoint, location.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal(PlatformAdminSsoApiFactory.ClientId, Single(query, "client_id"));
        Assert.Equal("code", Single(query, "response_type"));
        Assert.Equal(CallbackUrl, Single(query, "redirect_uri"));
        Assert.Equal("openid profile email", Single(query, "scope"));
        Assert.Equal("S256", Single(query, "code_challenge_method"));
        Assert.Equal(43, Single(query, "state").Length);
        Assert.Equal(43, Single(query, "nonce").Length);
        Assert.Equal(43, Single(query, "code_challenge").Length);

        var cookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("meisterpropr_platform_admin_sign_in=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Callback_WithTheRequiredRole_CreatesAPlatformAdministratorWithoutEmailAndSignsIn()
    {
        this.Reset();
        var subject = NewSubject();
        using var browser = factory.CreateBrowser();

        var signIn = await this.SignInAsync(browser, subject, $"{subject}@meister-dev.test", [PlatformAdminSsoApiFactory.RequiredRole, "other"]);

        Assert.Equal(HttpStatusCode.Found, signIn.Response.StatusCode);
        Assert.Equal(ReturnUrl, signIn.Response.Headers.Location!.GetLeftPart(UriPartial.Path));
        Assert.Equal("Admin", GlobalRoleOf(signIn.AccessToken));
        Assert.Equal(
            AuthorizationCodeFlow.ComputeCodeChallenge(signIn.CodeVerifierSent!),
            signIn.CodeChallenge);
        Assert.Contains(
            signIn.Response.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith("meisterpropr_refresh=", StringComparison.Ordinal)
                      && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));

        var (account, link) = await this.ReadAccountAsync(subject);
        Assert.Equal(AppUserRole.Admin, account.GlobalRole);
        Assert.Null(account.PasswordHash);
        Assert.Null(account.Email);
        Assert.Null(account.NormalizedEmail);
        Assert.Null(link.TenantId);
        Assert.Null(link.SsoProviderId);
        Assert.Equal($"{subject}@meister-dev.test", link.Email);
        factory.AuditLog.Received(1)
            .PlatformAdministratorRightsGranted(account.Id, account.Username, PlatformAdminSsoApiFactory.Authority, subject);
        factory.AuditLog.Received(1)
            .SignedInThroughPlatformIdentityProvider(account.Id, account.Username, PlatformAdminSsoApiFactory.Authority, subject);
    }

    [Fact]
    public async Task Callback_WithTheRequiredRoleAgain_SignsInToTheSameAccount()
    {
        this.Reset();
        var subject = NewSubject();
        using var browser = factory.CreateBrowser();
        var first = await this.SignInAsync(browser, subject, $"{subject}@meister-dev.test", [PlatformAdminSsoApiFactory.RequiredRole]);

        var second = await this.SignInAsync(browser, subject, $"{subject}@meister-dev.test", [PlatformAdminSsoApiFactory.RequiredRole]);

        Assert.Equal(SubjectOf(first.AccessToken), SubjectOf(second.AccessToken));
        Assert.Equal("Admin", GlobalRoleOf(second.AccessToken));
        var (account, _) = await this.ReadAccountAsync(subject);
        factory.AuditLog.Received(1)
            .PlatformAdministratorRightsGranted(account.Id, account.Username, PlatformAdminSsoApiFactory.Authority, subject);
        factory.AuditLog.Received(2)
            .SignedInThroughPlatformIdentityProvider(account.Id, account.Username, PlatformAdminSsoApiFactory.Authority, subject);
    }

    [Fact]
    public async Task Callback_WithoutTheRequiredRole_RefusesAndRemovesTheRightsAndSessionsOfTheAccount()
    {
        this.Reset();
        var subject = NewSubject();
        using var browser = factory.CreateBrowser();
        await this.SignInAsync(browser, subject, $"{subject}@meister-dev.test", [PlatformAdminSsoApiFactory.RequiredRole]);
        var sessionBefore = await browser.PostAsync("/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, sessionBefore.StatusCode);

        var refused = await this.SignInAsync(browser, subject, $"{subject}@meister-dev.test", ["other"]);

        Assert.Equal("platform_role_missing", refused.Error);
        Assert.Null(refused.AccessToken);
        var (account, _) = await this.ReadAccountAsync(subject);
        Assert.Equal(AppUserRole.User, account.GlobalRole);
        Assert.True(account.IsActive);
        var sessionAfter = await browser.PostAsync("/auth/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, sessionAfter.StatusCode);
        factory.AuditLog.Received(1)
            .PlatformAdministratorRightsRevoked(account.Id, account.Username, PlatformAdminSsoApiFactory.Authority, subject);
        factory.AuditLog.Received(1)
            .PlatformIdentityProviderSignInRefused(
                account.Id,
                account.Username,
                PlatformAdminSsoApiFactory.Authority,
                subject,
                "platform_role_missing");
    }

    /// <summary>
    ///     The first refusal saves the lost rights and fails before it revokes anything, as a database failure would.
    ///     The session of the account still renews then, and the next refusal revokes it.
    /// </summary>
    [Fact]
    public async Task Callback_WithoutTheRequiredRole_FinishesTheRevocationsThatAnInterruptedRefusalLeftUndone()
    {
        this.Reset();
        var subject = NewSubject();
        var email = $"{subject}@meister-dev.test";
        using var browser = factory.CreateBrowser();
        await this.SignInAsync(browser, subject, email, [PlatformAdminSsoApiFactory.RequiredRole]);
        factory.FailNextRefreshTokenRevocation();

        await Assert.ThrowsAnyAsync<Exception>(() => this.SignInAsync(browser, subject, email, ["other"]));
        var (account, _) = await this.ReadAccountAsync(subject);
        Assert.Equal(AppUserRole.User, account.GlobalRole);
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/auth/refresh", null)).StatusCode);

        var refused = await this.SignInAsync(browser, subject, email, ["other"]);

        Assert.Equal("platform_role_missing", refused.Error);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/auth/refresh", null)).StatusCode);
    }

    /// <summary>The subject is an opaque identifier, so two subjects that differ in a space are two identities.</summary>
    [Fact]
    public async Task Callback_WithSubjectsThatDifferInASpace_ReachSeparateAccounts()
    {
        this.Reset();
        var subject = NewSubject();
        using var browser = factory.CreateBrowser();

        var first = await this.SignInAsync(browser, subject, null, [PlatformAdminSsoApiFactory.RequiredRole]);
        var second = await this.SignInAsync(browser, $" {subject}", null, [PlatformAdminSsoApiFactory.RequiredRole]);

        Assert.NotEqual(SubjectOf(first.AccessToken), SubjectOf(second.AccessToken));
        var (firstAccount, _) = await this.ReadAccountAsync(subject);
        var (secondAccount, _) = await this.ReadAccountAsync($" {subject}");
        Assert.NotEqual(firstAccount.Id, secondAccount.Id);
    }

    [Fact]
    public async Task Callback_WithASubjectOfSpacesOnly_Refuses()
    {
        this.Reset();
        using var browser = factory.CreateBrowser();

        var refused = await this.SignInAsync(browser, "   ", null, [PlatformAdminSsoApiFactory.RequiredRole]);

        Assert.Equal("missing_identity_claims", refused.Error);
        Assert.Null(refused.AccessToken);
    }

    [Fact]
    public async Task Callback_WithTheRequiredRoleAfterALoss_RestoresTheRights()
    {
        this.Reset();
        var subject = NewSubject();
        using var browser = factory.CreateBrowser();
        await this.SignInAsync(browser, subject, $"{subject}@meister-dev.test", [PlatformAdminSsoApiFactory.RequiredRole]);
        await this.SignInAsync(browser, subject, $"{subject}@meister-dev.test", []);

        var restored = await this.SignInAsync(browser, subject, $"{subject}@meister-dev.test", [PlatformAdminSsoApiFactory.RequiredRole]);

        Assert.Equal("Admin", GlobalRoleOf(restored.AccessToken));
        var (account, _) = await this.ReadAccountAsync(subject);
        Assert.Equal(AppUserRole.Admin, account.GlobalRole);
        factory.AuditLog.Received(2)
            .PlatformAdministratorRightsGranted(account.Id, account.Username, PlatformAdminSsoApiFactory.Authority, subject);
    }

    [Fact]
    public async Task Callback_WithoutTheRequiredRoleAndWithoutAccount_RefusesAndCreatesNothing()
    {
        this.Reset();
        var subject = NewSubject();
        using var browser = factory.CreateBrowser();

        var refused = await this.SignInAsync(browser, subject, $"{subject}@meister-dev.test", ["viewer"]);

        Assert.Equal("platform_role_missing", refused.Error);
        Assert.False(await this.HasLinkAsync(subject));
        factory.AuditLog.Received(1)
            .PlatformIdentityProviderSignInRefused(null, null, PlatformAdminSsoApiFactory.Authority, subject, "platform_role_missing");
    }

    [Fact]
    public async Task Callback_ForADisabledAccount_Refuses()
    {
        this.Reset();
        var subject = NewSubject();
        using var browser = factory.CreateBrowser();
        await this.SignInAsync(browser, subject, $"{subject}@meister-dev.test", [PlatformAdminSsoApiFactory.RequiredRole]);
        var (account, _) = await this.ReadAccountAsync(subject);
        await this.SetActiveAsync(account.Id, false);

        var refused = await this.SignInAsync(browser, subject, $"{subject}@meister-dev.test", [PlatformAdminSsoApiFactory.RequiredRole]);

        Assert.Equal("account_disabled", refused.Error);
        Assert.Null(refused.AccessToken);
    }

    /// <summary>
    ///     A tenant account without a password carries the same address as well, so the refusal does not depend on
    ///     which of the two accounts a lookup by email finds first.
    /// </summary>
    [Fact]
    public async Task Callback_WhenAnAccountWithALocalPasswordHasTheEmail_RefusesWithExternalLinkRequiresConfirmation()
    {
        this.Reset();
        var subject = NewSubject();
        var email = $"{subject}@meister-dev.test";
        await factory.SeedUserAsync($"tenant-{subject}", email);
        var localUserId = await factory.SeedUserAsync($"local-{subject}", email);
        await factory.SetLocalPasswordAsync(localUserId, "Local-Password-1!");
        using var browser = factory.CreateBrowser();

        var refused = await this.SignInAsync(browser, subject, email, [PlatformAdminSsoApiFactory.RequiredRole]);

        Assert.Equal("external_link_requires_confirmation", refused.Error);
        Assert.False(await this.HasLinkAsync(subject));
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var localUser = await database.AppUsers.AsNoTracking().SingleAsync(user => user.Id == localUserId);
        Assert.Equal(AppUserRole.User, localUser.GlobalRole);
    }

    [Fact]
    public async Task Callback_WhenAnAccountWithoutPasswordHasTheEmail_CreatesASeparateAccount()
    {
        this.Reset();
        var subject = NewSubject();
        var email = $"{subject}@meister-dev.test";
        var tenantUserId = await factory.SeedUserAsync($"tenant-{subject}", email);
        using var browser = factory.CreateBrowser();

        var signIn = await this.SignInAsync(browser, subject, email, [PlatformAdminSsoApiFactory.RequiredRole]);

        Assert.Equal("Admin", GlobalRoleOf(signIn.AccessToken));
        Assert.NotEqual(tenantUserId.ToString(), SubjectOf(signIn.AccessToken));
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var tenantUser = await database.AppUsers.AsNoTracking().SingleAsync(user => user.Id == tenantUserId);
        Assert.Equal(AppUserRole.User, tenantUser.GlobalRole);
        Assert.False(await database.ExternalIdentities.AnyAsync(link => link.UserId == tenantUserId));
    }

    /// <summary>
    ///     A tenant identity provider matches an existing account by its email address. The account of a platform
    ///     administrator carries none, so a tenant provider that states the same address creates a tenant account
    ///     of its own and never signs in to the administrator's account.
    /// </summary>
    [Fact]
    public async Task TenantSignIn_WithTheEmailOfAPlatformAdministrator_CreatesATenantAccountOfItsOwn()
    {
        this.Reset();
        var subject = NewSubject();
        var email = $"{subject}@acme.test";
        using var browser = factory.CreateBrowser();
        var platformSignIn = await this.SignInAsync(browser, subject, email, [PlatformAdminSsoApiFactory.RequiredRole]);
        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp");
        const string tenantIssuer = "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0";
        var providerId = await factory.SeedSsoProviderAsync(
            tenantId,
            "Acme Entra",
            issuerOrAuthorityUrl: tenantIssuer,
            clientId: "entra-client-id");
        using var tenantBrowser = factory.CreateBrowser();
        var challenge = await tenantBrowser.GetAsync($"/auth/external/challenge/{tenantSlug}/{providerId}");
        var state = Single(QueryHelpers.ParseQuery(challenge.Headers.Location!.Query), "state");
        factory.QueueExternalAuthResponse(_ => Json(new { id_token = TenantIdToken(tenantIssuer, email) }));

        var tenantSignIn = await tenantBrowser.GetAsync($"/auth/external/callback/{tenantSlug}?code=tenant-code&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.OK, tenantSignIn.StatusCode);
        var body = JsonDocument.Parse(await tenantSignIn.Content.ReadAsStringAsync()).RootElement;
        var tenantAccessToken = body.GetProperty("accessToken").GetString();
        Assert.NotEqual(SubjectOf(platformSignIn.AccessToken), SubjectOf(tenantAccessToken));
        Assert.Equal("User", GlobalRoleOf(tenantAccessToken));
    }

    /// <summary>
    ///     The provider holds a valid token answer for the sign-in, so a callback that redeemed the code before it
    ///     checked the state would sign the browser in.
    /// </summary>
    [Fact]
    public async Task Callback_WithAnotherState_RefusesWithoutAskingTheProvider()
    {
        this.Reset();
        using var browser = factory.CreateBrowser();
        var challenge = await browser.GetAsync(ChallengePath);
        Assert.Equal(HttpStatusCode.Found, challenge.StatusCode);
        var nonce = Single(QueryHelpers.ParseQuery(challenge.Headers.Location!.Query), "nonce");
        var providerAsked = false;
        factory.QueueExternalAuthResponse(_ =>
        {
            providerAsked = true;
            return Json(new { id_token = PlatformAdminSsoApiFactory.CreateIdToken(NewSubject(), null, nonce, [PlatformAdminSsoApiFactory.RequiredRole]) });
        });

        var response = await browser.GetAsync("/auth/platform-admin/callback?code=code&state=state-of-another-sign-in");

        Assert.Equal("invalid_state", Fragment(response, "error"));
        Assert.Null(Fragment(response, "accessToken"));
        Assert.False(providerAsked);
    }

    [Fact]
    public async Task Callback_WithoutTheCookieOfTheChallenge_Refuses()
    {
        this.Reset();
        using var browser = factory.CreateBrowser();
        var challenge = await browser.GetAsync(ChallengePath);
        var state = Single(QueryHelpers.ParseQuery(challenge.Headers.Location!.Query), "state");
        using var otherBrowser = factory.CreateBrowser();

        var response = await otherBrowser.GetAsync($"/auth/platform-admin/callback?code=code&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("invalid_state", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Callback_WithATokenIssuedForAnotherNonce_Refuses()
    {
        this.Reset();
        var subject = NewSubject();
        using var browser = factory.CreateBrowser();

        var refused = await this.SignInAsync(
            browser,
            subject,
            $"{subject}@meister-dev.test",
            [PlatformAdminSsoApiFactory.RequiredRole],
            "nonce-of-another-sign-in");

        Assert.Equal("invalid_nonce", refused.Error);
        Assert.False(await this.HasLinkAsync(subject));
    }

    /// <summary>
    ///     A token endpoint that answers with valid JSON that is not an object sends no ID token, so the callback refuses
    ///     the sign-in and returns the refusal to the frontend.
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"id_token\"")]
    public async Task Callback_WhenTheTokenAnswerIsNotAJsonObject_RefusesWithMissingIdToken(string tokenAnswer)
    {
        this.Reset();
        using var browser = factory.CreateBrowser();
        var challenge = await browser.GetAsync(ChallengePath);
        var state = Single(QueryHelpers.ParseQuery(challenge.Headers.Location!.Query), "state");
        factory.QueueExternalAuthResponse(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(tokenAnswer, Encoding.UTF8, "application/json"),
        });

        var response = await browser.GetAsync($"/auth/platform-admin/callback?code=code&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("missing_id_token", Fragment(response, "error"));
        Assert.Null(Fragment(response, "accessToken"));
    }

    [Fact]
    public async Task Callback_WhenTheProviderReportsAnError_ReturnsItToTheFrontend()
    {
        this.Reset();
        using var browser = factory.CreateBrowser();
        var challenge = await browser.GetAsync(ChallengePath);
        var state = Single(QueryHelpers.ParseQuery(challenge.Headers.Location!.Query), "state");

        var response = await browser.GetAsync(
            $"/auth/platform-admin/callback?error=access_denied&error_description=Denied&state={Uri.EscapeDataString(state)}");

        Assert.Equal("provider_error", Fragment(response, "error"));
        Assert.Equal("Denied", Fragment(response, "message"));
    }

    /// <summary>
    ///     The access token of a platform administrator travels in the fragment of the return address, so only the
    ///     origins that serve the user interface of the installation qualify. The origins that the CORS policy allows for
    ///     SCM browser extensions, on which anyone who publishes an extension can serve pages, and the local frontend
    ///     origins outside Development, do not.
    /// </summary>
    [Theory]
    [InlineData("https://elsewhere.example.test/login/callback")]
    [InlineData("https://publisher.gallerycdn.vsassets.io/extension/page.html")]
    [InlineData("https://dev.azure.com/organization/login/callback")]
    [InlineData("https://organization.visualstudio.com/login/callback")]
    [InlineData("http://localhost:5173/login/callback")]
    public async Task Challenge_WithAReturnUrlOnAnOriginThatDoesNotServeTheUserInterface_ReturnsBadRequest(string returnUrl)
    {
        this.Reset();
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync($"/auth/platform-admin/challenge?returnUrl={Uri.EscapeDataString(returnUrl)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(
            response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            cookie => cookie.StartsWith("meisterpropr_platform_admin_sign_in=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Challenge_WithAReturnUrlOnAnOriginInCorsOrigins_SendsTheBrowserToTheProvider()
    {
        this.Reset();
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync(
            $"/auth/platform-admin/challenge?returnUrl={Uri.EscapeDataString($"{PlatformAdminSsoApiFactory.ConsoleOrigin}/login/callback")}");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(PlatformAdminSsoApiFactory.AuthorizationEndpoint, response.Headers.Location!.GetLeftPart(UriPartial.Path));
    }

    [Fact]
    public async Task WithoutTheSsoLicense_TheProviderIsNotOfferedAndItsEndpointsAreRefused()
    {
        this.Reset();
        factory.SetSsoCapabilityAvailability(false, "A commercial license is required to use single sign-on.");
        using var browser = factory.CreateBrowser();

        var options = JsonDocument.Parse(await browser.GetStringAsync("/auth/options")).RootElement;
        var challenge = await browser.GetAsync(ChallengePath);
        var callback = await browser.GetAsync("/auth/platform-admin/callback?code=code&state=state");

        Assert.Equal(JsonValueKind.Null, options.GetProperty("platformAdminSignIn").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, challenge.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, callback.StatusCode);
        var body = JsonDocument.Parse(await challenge.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("premium_feature_unavailable", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task AuthOptions_WithTheSsoLicense_OfferTheProviderUnderItsDisplayName()
    {
        this.Reset();

        var options = JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/auth/options")).RootElement;

        Assert.Equal(
            PlatformAdminSsoApiFactory.DisplayName,
            options.GetProperty("platformAdminSignIn").GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task LocalLogin_KeepsWorkingWhileTheProviderIsConfigured()
    {
        this.Reset();
        var username = $"local-admin-{Guid.NewGuid():N}";
        var userId = await factory.SeedUserAsync(username, $"{username}@meister-dev.test", AppUserRole.Admin);
        await factory.SetLocalPasswordAsync(userId, "Local-Password-1!");

        var response = await factory.CreateClient().PostAsJsonAsync("/auth/login", new { username, password = "Local-Password-1!" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var accessToken = body.GetProperty("accessToken").GetString();
        Assert.Equal(userId.ToString(), SubjectOf(accessToken));
        Assert.Equal("Admin", GlobalRoleOf(accessToken));
    }

    private void Reset()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();
        factory.AuditLog.ClearReceivedCalls();
    }

    /// <summary>
    ///     Runs a sign-in in <paramref name="browser" />: the challenge, the token request the provider answers, and
    ///     the callback.
    /// </summary>
    private async Task<SignInResult> SignInAsync(
        HttpClient browser,
        string subject,
        string? email,
        IReadOnlyCollection<string> roles,
        string? tokenNonce = null)
    {
        var challenge = await browser.GetAsync(ChallengePath);
        Assert.Equal(HttpStatusCode.Found, challenge.StatusCode);
        var query = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
        var state = Single(query, "state");
        var nonce = Single(query, "nonce");
        var code = $"code-{Guid.NewGuid():N}";
        string? codeVerifier = null;
        factory.QueueExternalAuthResponse(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(PlatformAdminSsoApiFactory.TokenEndpoint, request.RequestUri?.ToString());
            var form = QueryHelpers.ParseQuery(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("authorization_code", Single(form, "grant_type"));
            Assert.Equal(code, Single(form, "code"));
            Assert.Equal(CallbackUrl, Single(form, "redirect_uri"));
            Assert.Equal(PlatformAdminSsoApiFactory.ClientId, Single(form, "client_id"));
            Assert.Equal(PlatformAdminSsoApiFactory.ClientSecret, Single(form, "client_secret"));
            codeVerifier = Single(form, "code_verifier");
            return Json(new { id_token = PlatformAdminSsoApiFactory.CreateIdToken(subject, email, tokenNonce ?? nonce, roles) });
        });

        var callback = await browser.GetAsync($"/auth/platform-admin/callback?code={code}&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        return new SignInResult(
            callback,
            Fragment(callback, "accessToken"),
            Fragment(callback, "error"),
            codeVerifier,
            Single(query, "code_challenge"));
    }

    private async Task<(AppUserRecord Account, ExternalIdentityRecord Link)> ReadAccountAsync(string subject)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var link = await database.ExternalIdentities.AsNoTracking()
            .SingleAsync(identity => identity.Issuer == PlatformAdminSsoApiFactory.Authority && identity.Subject == subject);
        var account = await database.AppUsers.AsNoTracking().SingleAsync(user => user.Id == link.UserId);
        return (account, link);
    }

    private async Task<bool> HasLinkAsync(string subject)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        return await database.ExternalIdentities.AnyAsync(identity => identity.Subject == subject);
    }

    private async Task SetActiveAsync(Guid userId, bool isActive)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var account = await database.AppUsers.SingleAsync(user => user.Id == userId);
        account.IsActive = isActive;
        await database.SaveChangesAsync();
    }

    private static string NewSubject()
    {
        return $"staff-{Guid.NewGuid():N}";
    }

    private static string? Fragment(HttpResponseMessage response, string name)
    {
        var location = response.Headers.Location;
        if (location is null)
        {
            return null;
        }

        var fragment = QueryHelpers.ParseQuery("?" + location.Fragment.TrimStart('#'));
        return fragment.TryGetValue(name, out var values) ? values.ToString() : null;
    }

    private static string Single(Dictionary<string, StringValues> query, string name)
    {
        Assert.True(query.TryGetValue(name, out var values), $"Missing parameter '{name}'.");
        return Assert.Single(values)!;
    }

    private static string? GlobalRoleOf(string? accessToken)
    {
        return new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims.Single(claim => claim.Type == "global_role").Value;
    }

    private static string? SubjectOf(string? accessToken)
    {
        return new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Subject;
    }

    private static HttpResponseMessage Json<T>(T payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
    }

    private static string TenantIdToken(string issuer, string email)
    {
        var token = new JwtSecurityToken(
            issuer,
            "entra-client-id",
            [new("sub", $"entra-{Guid.NewGuid():N}"), new("email", email), new("email_verified", "true")],
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: TenantAdministrationApiFactory.OidcSigningCredentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed record SignInResult(
        HttpResponseMessage Response,
        string? AccessToken,
        string? Error,
        string? CodeVerifierSent,
        string CodeChallenge);
}
