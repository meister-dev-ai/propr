// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

/// <summary>
///     The sign-in of provisioned users through the identity provider for provisioned users: it reaches only an account
///     that the hosting platform provisioned for the issuer and the subject, the account reads the clients the platform
///     gave it and nothing else, and the protection of the flow is the protection of the other sign-ins.
/// </summary>
public sealed class ProvisionedUserSignInTests(ProvisionedUserApiFactory factory) : IClassFixture<ProvisionedUserApiFactory>
{
    // The test host serves the API at http://localhost, and without MEISTER_PUBLIC_BASE_URL the frontend is expected on
    // the same origin.
    private const string ReturnUrl = "http://localhost/login/callback";
    private const string CallbackUrl = "http://localhost/auth/provisioned-user/callback";
    private const string FlowCookieName = "meisterpropr_provisioned_user_sign_in";

    private static readonly string ChallengePath = $"/auth/provisioned-user/challenge?returnUrl={Uri.EscapeDataString(ReturnUrl)}";

    [Fact]
    public async Task Callback_ForASubjectThatThePlatformDidNotProvision_RefusesWithAFixedMessageAndCreatesNoAccount()
    {
        this.Reset();
        var subject = NewSubject();
        var accountsBefore = await this.CountAccountsAsync();
        using var browser = factory.CreateBrowser();

        var signIn = await this.SignInAsync(browser, subject, $"{subject}@customer.test");

        Assert.Equal(ReturnUrl, signIn.Response.Headers.Location!.GetLeftPart(UriPartial.Path));
        Assert.Null(signIn.AccessToken);
        Assert.Equal("no_access", signIn.Error);
        Assert.Equal(ProvisionedUserSignInService.NoAccessMessage, signIn.Message);
        Assert.Equal(accountsBefore, await this.CountAccountsAsync());
        factory.AuditLog.Received(1).ProvisionedIdentityProviderSignInRefused(
            null,
            null,
            ProvisionedUserApiFactory.Authority,
            subject,
            "not_provisioned");
    }

    /// <summary>
    ///     An account with the email address of the identity exists, a local account and a tenant account alike. The
    ///     sign-in never looks an account up by its email address, so it signs in to neither and links neither.
    /// </summary>
    [Fact]
    public async Task Callback_ForAnUnprovisionedSubjectWithTheEmailAddressOfAnAccount_SignsInToNoAccount()
    {
        this.Reset();
        var subject = NewSubject();
        var email = $"{subject}@customer.test";
        var localUserId = await factory.SeedUserAsync($"local-{subject}", email);
        await factory.SetLocalPasswordAsync(localUserId, "Local-Password-1!");
        await factory.SeedUserAsync($"tenant-{subject}", email);
        using var browser = factory.CreateBrowser();

        var signIn = await this.SignInAsync(browser, subject, email);

        Assert.Equal("no_access", signIn.Error);
        Assert.Null(signIn.AccessToken);
        await using var scope = this.Database(out var database);
        Assert.False(await database.ExternalIdentities.AnyAsync(link => link.Subject == subject));
        Assert.False(await database.ProvisionedUsers.AnyAsync(user => user.Subject == subject));
    }

    /// <summary>
    ///     The platform provisioned the subject under the issuer of another provider. The identity is the pair of issuer
    ///     and subject, so the same subject at the configured provider reaches no account.
    /// </summary>
    [Fact]
    public async Task Callback_ForASubjectProvisionedUnderAnotherIssuer_Refuses()
    {
        this.Reset();
        var subject = NewSubject();
        var userId = await factory.SeedUserAsync($"other-{subject}", $"other-{subject}@customer.test");
        var tenant = await this.TenantWithClientsAsync(1);
        await factory.SeedTenantMembershipAsync(tenant.Id, userId, TenantRole.TenantUser);
        await this.SeedProvisioningRecordAsync(userId, "https://id.example.test/realms/other", subject);
        using var browser = factory.CreateBrowser();

        var signIn = await this.SignInAsync(browser, subject, null);

        Assert.Equal("no_access", signIn.Error);
        Assert.Null(signIn.AccessToken);
    }

    [Fact]
    public async Task Callback_ForAProvisionedSubjectWithAMembership_SignsInToItsAccount()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var tenant = await this.TenantWithClientsAsync(1);
        var subject = NewSubject();
        var userId = await ProvisionAsync(platform, subject, tenant.Id, tenant.Clients[0]);
        using var browser = factory.CreateBrowser();

        var signIn = await this.SignInAsync(browser, subject, $"{subject}@customer.test");

        Assert.Equal(ReturnUrl, signIn.Response.Headers.Location!.GetLeftPart(UriPartial.Path));
        Assert.Null(signIn.Error);
        Assert.Equal(userId.ToString(), SubjectOf(signIn.AccessToken));
        Assert.Equal("User", GlobalRoleOf(signIn.AccessToken));
        Assert.Contains(
            signIn.Response.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith("meisterpropr_refresh=", StringComparison.Ordinal)
                      && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/auth/refresh", null)).StatusCode);
        await using var scope = this.Database(out var database);
        Assert.NotNull((await database.ProvisionedUsers.AsNoTracking().SingleAsync(user => user.UserId == userId)).LastSignInAt);
        factory.AuditLog.Received(1).SignedInThroughProvisionedIdentityProvider(
            userId,
            Arg.Any<string>(),
            ProvisionedUserApiFactory.Authority,
            subject);
    }

    /// <summary>
    ///     A provisioned user reads the review list, the results and the execution traces of the clients that the platform
    ///     gave it, and creates personal access tokens in its own settings. The other client of its tenant, the clients of
    ///     other tenants, the administration of its tenant and the administration of the installation stay closed.
    /// </summary>
    [Fact]
    public async Task ASignedInProvisionedUser_ReadsTheReviewsAndTracesOfItsClientsAndNothingElse()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var tenant = await this.TenantWithClientsAsync(2);
        var otherTenant = await this.TenantWithClientsAsync(1);
        var subject = NewSubject();
        await ProvisionAsync(platform, subject, tenant.Id, tenant.Clients[0]);
        var ownReview = await factory.SeedReviewWithTraceAsync(tenant.Clients[0], "Summary of the own workspace");
        var otherWorkspaceReview = await factory.SeedReviewWithTraceAsync(tenant.Clients[1], "Summary of another workspace");
        var otherTenantReview = await factory.SeedReviewWithTraceAsync(otherTenant.Clients[0], "Summary of another organization");
        using var browser = factory.CreateBrowser();
        var signIn = await this.SignInAsync(browser, subject, $"{subject}@customer.test");
        using var person = factory.CreateClientWithAccessToken(signIn.AccessToken!);

        var reviews = await ReadJsonAsync(await person.GetAsync("/jobs"));

        Assert.Equal([ownReview], reviews.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.OK, (await person.GetAsync($"/jobs/{ownReview}/protocol")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await person.GetAsync($"/jobs/{ownReview}/result")).StatusCode);
        foreach (var review in new[] { otherWorkspaceReview, otherTenantReview })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await person.GetAsync($"/jobs/{review}/protocol")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await person.GetAsync($"/jobs/{review}/result")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await person.GetAsync($"/jobs?clientId={tenant.Clients[1]}")).StatusCode);
        foreach (var administration in new[]
                 {
                     $"/admin/tenants/{tenant.Id}/memberships",
                     $"/admin/tenants/{tenant.Id}/clients",
                     $"/admin/tenants/{tenant.Id}/sso-providers",
                     $"/admin/tenants/{tenant.Id}/provisioned-members",
                     "/admin/users",
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await person.GetAsync(administration)).StatusCode);
        }

        var me = await ReadJsonAsync(await person.GetAsync("/auth/me"));
        Assert.Equal("User", me.GetProperty("globalRole").GetString());
        Assert.Equal([tenant.Clients[0].ToString()], me.GetProperty("clientRoles").EnumerateObject().Select(role => role.Name));
        Assert.False(me.GetProperty("hasLocalPassword").GetBoolean());
        var password = await person.PostAsJsonAsync(
            "/users/me/password",
            new { currentPassword = "Any-Password-1!", newPassword = "New-Password-1!" });
        Assert.Equal(HttpStatusCode.Unauthorized, password.StatusCode);
        var token = await person.PostAsJsonAsync("/users/me/pats", new { label = "browser extension" });
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        using var extension = factory.CreateClientWithPersonalAccessToken((await ReadJsonAsync(token)).GetProperty("token").GetString()!);
        var reviewsOfTheExtension = await ReadJsonAsync(await extension.GetAsync("/jobs"));
        Assert.Equal([ownReview], reviewsOfTheExtension.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
    }

    /// <summary>
    ///     The platform removes the last membership when the person leaves the organization. The session in the browser,
    ///     the personal access token that the browser extension holds, and the next sign-in all lose their access.
    /// </summary>
    [Fact]
    public async Task AfterThePlatformRemovedTheLastMembership_TheSessionThePersonalAccessTokenAndTheNextSignInAreRefused()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var tenant = await this.TenantWithClientsAsync(1);
        var subject = NewSubject();
        await ProvisionAsync(platform, subject, tenant.Id, tenant.Clients[0]);
        var review = await factory.SeedReviewWithTraceAsync(tenant.Clients[0], "Summary");
        using var browser = factory.CreateBrowser();
        var signIn = await this.SignInAsync(browser, subject, null);
        using var person = factory.CreateClientWithAccessToken(signIn.AccessToken!);
        var token = (await ReadJsonAsync(await person.PostAsJsonAsync("/users/me/pats", new { label = "browser extension" })))
            .GetProperty("token").GetString()!;
        using var extension = factory.CreateClientWithPersonalAccessToken(token);
        Assert.Equal(HttpStatusCode.OK, (await extension.GetAsync($"/jobs/{review}/protocol")).StatusCode);

        var removal = await platform.DeleteAsync($"/admin/tenants/{tenant.Id}/provisioned-members/{Uri.EscapeDataString(subject)}");

        Assert.Equal(HttpStatusCode.NoContent, removal.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await extension.GetAsync($"/jobs/{review}/protocol")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await person.GetAsync($"/jobs/{review}/protocol")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/auth/refresh", null)).StatusCode);
        var again = await this.SignInAsync(browser, subject, null);
        Assert.Equal("no_access", again.Error);
        Assert.Null(again.AccessToken);
    }

    /// <summary>
    ///     A platform administrator enabled the account of a provisioned user that holds no membership. The sign-in
    ///     still needs a membership, which only the platform sets.
    /// </summary>
    [Fact]
    public async Task Callback_ForAProvisionedUserWithoutMembership_Refuses()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var subject = NewSubject();
        var userId = await ProvisionAsync(platform, subject, null);
        using var browser = factory.CreateBrowser();
        var inactive = await this.SignInAsync(browser, subject, null);
        Assert.Equal(HttpStatusCode.NoContent, (await platform.PatchAsJsonAsync($"/admin/users/{userId}", new { isActive = true })).StatusCode);

        var enabled = await this.SignInAsync(browser, subject, null);

        Assert.Equal("no_access", inactive.Error);
        Assert.Equal("no_access", enabled.Error);
        Assert.Null(enabled.AccessToken);
        factory.AuditLog.Received(1).ProvisionedIdentityProviderSignInRefused(
            userId,
            Arg.Any<string>(),
            ProvisionedUserApiFactory.Authority,
            subject,
            "account_inactive");
        factory.AuditLog.Received(1).ProvisionedIdentityProviderSignInRefused(
            userId,
            Arg.Any<string>(),
            ProvisionedUserApiFactory.Authority,
            subject,
            "no_membership");
    }

    /// <summary>
    ///     The provisioned sign-in never reaches an account with platform administrator rights, also when a provisioning
    ///     record names it.
    /// </summary>
    [Fact]
    public async Task Callback_ForAnAccountWithPlatformAdministratorRights_Refuses()
    {
        this.Reset();
        var subject = NewSubject();
        var administratorId = await factory.SeedUserAsync($"operator-{subject}", $"operator-{subject}@meister-dev.test", AppUserRole.Admin);
        var tenant = await this.TenantWithClientsAsync(1);
        await factory.SeedTenantMembershipAsync(tenant.Id, administratorId, TenantRole.TenantUser);
        await this.SeedProvisioningRecordAsync(administratorId, ProvisionedUserApiFactory.Authority, subject);
        using var browser = factory.CreateBrowser();

        var signIn = await this.SignInAsync(browser, subject, null);

        Assert.Equal("no_access", signIn.Error);
        Assert.Null(signIn.AccessToken);
        factory.AuditLog.Received(1).ProvisionedIdentityProviderSignInRefused(
            administratorId,
            Arg.Any<string>(),
            ProvisionedUserApiFactory.Authority,
            subject,
            "platform_administrator");
    }

    [Fact]
    public async Task Challenge_SendsTheBrowserToTheProviderWithStateNonceAndPkceAndKeepsTheStateInAnHttpOnlyCookie()
    {
        this.Reset();
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync(ChallengePath);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal(ProvisionedUserApiFactory.AuthorizationEndpoint, location.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal(ProvisionedUserApiFactory.ClientId, query["client_id"].ToString());
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Equal(CallbackUrl, query["redirect_uri"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.Equal(43, query["state"].ToString().Length);
        Assert.Equal(43, query["nonce"].ToString().Length);
        Assert.Equal(43, query["code_challenge"].ToString().Length);
        var cookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith($"{FlowCookieName}=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Callback_SendsTheCodeVerifierOfTheChallengeToTheTokenEndpoint()
    {
        this.Reset();
        using var browser = factory.CreateBrowser();

        var signIn = await this.SignInAsync(browser, NewSubject(), null);

        Assert.NotNull(signIn.CodeVerifierSent);
        Assert.Equal(signIn.CodeChallenge, AuthorizationCodeFlow.ComputeCodeChallenge(signIn.CodeVerifierSent));
    }

    [Fact]
    public async Task Callback_WithAnIdTokenIssuedForAnotherNonce_IsRefused()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var tenant = await this.TenantWithClientsAsync(1);
        var subject = NewSubject();
        await ProvisionAsync(platform, subject, tenant.Id, tenant.Clients[0]);
        using var browser = factory.CreateBrowser();

        var signIn = await this.SignInAsync(browser, subject, null, "nonce-of-another-sign-in");

        Assert.Equal("invalid_nonce", signIn.Error);
        Assert.Null(signIn.AccessToken);
    }

    /// <summary>
    ///     A callback in a browser that did not start the sign-in carries a valid state and code, as an address that
    ///     someone else sent to the person does. ProPR refuses it before it redeems the code.
    /// </summary>
    [Fact]
    public async Task Callback_InABrowserWithoutTheCookieOfTheChallenge_IsRefusedWithoutAskingTheProvider()
    {
        this.Reset();
        using var browser = factory.CreateBrowser();
        var challenge = await browser.GetAsync(ChallengePath);
        var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"].ToString();
        var providerAsked = this.QueueAnswerThatRecordsTheRequest();
        using var otherBrowser = factory.CreateBrowser();

        var response = await otherBrowser.GetAsync($"/auth/provisioned-user/callback?code=code&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_state", (await ReadJsonAsync(response)).GetProperty("error").GetString());
        Assert.False(providerAsked());
    }

    [Fact]
    public async Task Callback_WithTheStateOfAnotherSignIn_IsRefusedWithoutAskingTheProvider()
    {
        this.Reset();
        using var browser = factory.CreateBrowser();
        await browser.GetAsync(ChallengePath);
        using var otherBrowser = factory.CreateBrowser();
        var otherChallenge = await otherBrowser.GetAsync(ChallengePath);
        var otherState = QueryHelpers.ParseQuery(otherChallenge.Headers.Location!.Query)["state"].ToString();
        var providerAsked = this.QueueAnswerThatRecordsTheRequest();

        var response = await browser.GetAsync($"/auth/provisioned-user/callback?code=code&state={Uri.EscapeDataString(otherState)}");

        Assert.Equal("invalid_state", TenantSignIns.Fragment(response, "error"));
        Assert.False(providerAsked());
    }

    /// <summary>
    ///     The callback removes the cookie of the sign-in, but a copy of the cookie and of the address of the callback
    ///     reaches the callback again. ProPR accepts each state once.
    /// </summary>
    [Fact]
    public async Task Callback_RepeatedWithTheSameStateAndCookie_IsRefusedWithoutAskingTheProvider()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var tenant = await this.TenantWithClientsAsync(1);
        var subject = NewSubject();
        await ProvisionAsync(platform, subject, tenant.Id, tenant.Clients[0]);
        using var browser = factory.CreateBrowser();
        var challenge = await browser.GetAsync(ChallengePath);
        var flowCookie = TenantSignIns.CookieOf(challenge, FlowCookieName);
        var query = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
        var callbackPath = $"/auth/provisioned-user/callback?code=code&state={Uri.EscapeDataString(query["state"].ToString())}";
        factory.QueueExternalAuthResponse(_ => TenantSignIns.Json(new { id_token = IdToken(subject, null, query["nonce"].ToString()) }));
        var first = await browser.GetAsync(callbackPath);
        Assert.NotNull(TenantSignIns.Fragment(first, "accessToken"));
        var providerAsked = this.QueueAnswerThatRecordsTheRequest(subject, query["nonce"].ToString());
        using var replayingClient = factory.CreateBrowser();
        using var replay = new HttpRequestMessage(HttpMethod.Get, callbackPath);
        replay.Headers.Add("Cookie", flowCookie);

        var response = await replayingClient.SendAsync(replay);

        Assert.Equal("invalid_state", TenantSignIns.Fragment(response, "error"));
        Assert.Null(TenantSignIns.Fragment(response, "accessToken"));
        Assert.False(providerAsked());
    }

    /// <summary>
    ///     The access token travels in the fragment of the return address, so only the origins that serve the user
    ///     interface of the installation qualify, as for the other sign-ins.
    /// </summary>
    [Theory]
    [InlineData("https://elsewhere.example.test/login/callback")]
    [InlineData("https://publisher.gallerycdn.vsassets.io/extension/page.html")]
    [InlineData("http://localhost:5173/login/callback")]
    public async Task Challenge_WithAReturnUrlOnAnOriginThatDoesNotServeTheUserInterface_ReturnsBadRequest(string returnUrl)
    {
        this.Reset();
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync($"/auth/provisioned-user/challenge?returnUrl={Uri.EscapeDataString(returnUrl)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(
            response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            cookie => cookie.StartsWith($"{FlowCookieName}=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutTheSsoLicense_TheProviderIsNotOfferedAndItsEndpointsAreRefused()
    {
        this.Reset();
        factory.SetSsoCapabilityAvailability(false, "A commercial license is required to use single sign-on.");
        using var browser = factory.CreateBrowser();

        var options = await ReadJsonAsync(await browser.GetAsync("/auth/options"));
        var challenge = await browser.GetAsync(ChallengePath);
        var callback = await browser.GetAsync("/auth/provisioned-user/callback?code=code&state=state");

        Assert.Equal(JsonValueKind.Null, options.GetProperty("provisionedUserSignIn").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, challenge.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, callback.StatusCode);
        Assert.Equal("premium_feature_unavailable", (await ReadJsonAsync(challenge)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task AuthOptions_WithTheSsoLicense_OfferTheProviderUnderItsDisplayName()
    {
        this.Reset();

        var options = await ReadJsonAsync(await factory.CreateClient().GetAsync("/auth/options"));

        Assert.Equal(
            ProvisionedUserApiFactory.DisplayName,
            options.GetProperty("provisionedUserSignIn").GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, options.GetProperty("platformAdminSignIn").ValueKind);
    }

    private static async Task<Guid> ProvisionAsync(HttpClient platform, string subject, Guid? tenantId, params Guid[] clientIds)
    {
        var user = await platform.PutAsJsonAsync(
            $"/admin/provisioned-users/{Uri.EscapeDataString(subject)}",
            new { email = $"{subject}@customer.test", displayName = "Customer" });
        Assert.Equal(HttpStatusCode.Created, user.StatusCode);
        if (tenantId is { } tenant)
        {
            var membership = await platform.PutAsJsonAsync(
                $"/admin/tenants/{tenant}/provisioned-members/{Uri.EscapeDataString(subject)}",
                new { clientIds });
            Assert.Equal(HttpStatusCode.OK, membership.StatusCode);
        }

        return (await ReadJsonAsync(user)).GetProperty("userId").GetGuid();
    }

    private static string IdToken(string subject, string? email, string nonce)
    {
        return TenantSignIns.IdToken(ProvisionedUserApiFactory.Authority, ProvisionedUserApiFactory.ClientId, subject, email, true, nonce);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static string? SubjectOf(string? accessToken)
    {
        return accessToken is null ? null : new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Subject;
    }

    private static string? GlobalRoleOf(string? accessToken)
    {
        return accessToken is null
            ? null
            : new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims.Single(claim => claim.Type == "global_role").Value;
    }

    private static string NewSubject()
    {
        return $"customer-{Guid.NewGuid():N}";
    }

    private void Reset()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();
        factory.AuditLog.ClearReceivedCalls();
    }

    /// <summary>
    ///     Runs a sign-in in <paramref name="browser" />: the challenge, the token request the provider answers, and the
    ///     callback.
    /// </summary>
    private async Task<SignInResult> SignInAsync(HttpClient browser, string subject, string? email, string? tokenNonce = null)
    {
        var challenge = await browser.GetAsync(ChallengePath);
        Assert.Equal(HttpStatusCode.Found, challenge.StatusCode);
        var query = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
        string? codeVerifier = null;
        factory.QueueExternalAuthResponse(request =>
        {
            Assert.Equal(ProvisionedUserApiFactory.TokenEndpoint, request.RequestUri?.ToString());
            var form = TenantSignIns.FormOf(request);
            Assert.Equal("authorization_code", form["grant_type"].ToString());
            Assert.Equal(ProvisionedUserApiFactory.ClientId, form["client_id"].ToString());
            Assert.Equal(ProvisionedUserApiFactory.ClientSecret, form["client_secret"].ToString());
            Assert.Equal(CallbackUrl, form["redirect_uri"].ToString());
            codeVerifier = form["code_verifier"].ToString();
            return TenantSignIns.Json(new { id_token = IdToken(subject, email, tokenNonce ?? query["nonce"].ToString()) });
        });

        var callback = await browser.GetAsync($"/auth/provisioned-user/callback?code=code&state={Uri.EscapeDataString(query["state"].ToString())}");

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        return new SignInResult(
            callback,
            TenantSignIns.Fragment(callback, "accessToken"),
            TenantSignIns.Fragment(callback, "error"),
            TenantSignIns.Fragment(callback, "message"),
            codeVerifier,
            query["code_challenge"].ToString());
    }

    /// <summary>Queues a valid token answer and returns whether the provider was asked for it.</summary>
    private Func<bool> QueueAnswerThatRecordsTheRequest(string? subject = null, string? nonce = null)
    {
        var asked = false;
        factory.QueueExternalAuthResponse(_ =>
        {
            asked = true;
            return TenantSignIns.Json(new { id_token = IdToken(subject ?? NewSubject(), null, nonce ?? "nonce") });
        });
        return () => asked;
    }

    private async Task<HttpClient> PlatformAdministratorAsync()
    {
        var username = $"platform-{Guid.NewGuid():N}";
        var userId = await factory.SeedUserAsync(username, $"{username}@meister-dev.test", AppUserRole.Admin);
        return factory.CreateClientWithAccessToken(factory.GenerateToken(userId, AppUserRole.Admin));
    }

    private async Task<TestTenant> TenantWithClientsAsync(int clientCount)
    {
        var tenantId = await factory.SeedTenantAsync($"customer-{Guid.NewGuid():N}", "Customer organization");
        var clients = new List<Guid>();
        for (var index = 0; index < clientCount; index++)
        {
            clients.Add(await factory.SeedClientAsync(tenantId, $"Workspace {index}"));
        }

        return new TestTenant(tenantId, clients);
    }

    private async Task SeedProvisioningRecordAsync(Guid userId, string issuer, string subject)
    {
        await using var scope = this.Database(out var database);
        var now = DateTimeOffset.UtcNow;
        database.ProvisionedUsers.Add(
            new ProvisionedUserRecord
            {
                UserId = userId,
                Issuer = issuer,
                Subject = subject,
                Email = $"{subject}@customer.test",
                CreatedAt = now,
                UpdatedAt = now,
            });
        await database.SaveChangesAsync();
    }

    private async Task<int> CountAccountsAsync()
    {
        await using var scope = this.Database(out var database);
        return await database.AppUsers.CountAsync();
    }

    private AsyncServiceScope Database(out MeisterProPRDbContext database)
    {
        var scope = factory.Services.CreateAsyncScope();
        database = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        return scope;
    }

    private sealed record TestTenant(Guid Id, IReadOnlyList<Guid> Clients);

    private sealed record SignInResult(
        HttpResponseMessage Response,
        string? AccessToken,
        string? Error,
        string? Message,
        string? CodeVerifierSent,
        string CodeChallenge);
}
