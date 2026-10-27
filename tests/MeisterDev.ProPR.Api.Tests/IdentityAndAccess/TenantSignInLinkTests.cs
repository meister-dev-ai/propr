// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

/// <summary>
///     How a tenant sign-in finds the account of an external identity: through a link of the identity, never through
///     the email address that the identity provider states. When an account with that address exists, the sign-in
///     keeps the identity as a pending link, which the person confirms from an account or turns into a new account.
/// </summary>
public sealed class TenantSignInLinkTests(TenantAdministrationApiFactory factory)
    : IClassFixture<TenantAdministrationApiFactory>
{
    private const string GoogleIssuer = "https://accounts.google.com";

    /// <summary>
    ///     The administrator of tenant B configures a provider whose server states the email address of an account
    ///     that a sign-in of tenant A created. The sign-in through that provider does not reach the account and gives
    ///     it no membership in tenant B.
    /// </summary>
    [Theory]
    [InlineData("GitHub")]
    [InlineData("EntraId")]
    public async Task SignIn_ThroughAProviderOfAnotherTenantThatStatesTheEmailOfAnAccount_DoesNotReachTheAccount(string attackerKind)
    {
        this.Reset();
        var victim = await this.CreateAccountThroughEntraAsync();
        var attackerTenantSlug = $"globex-{Guid.NewGuid():N}";
        var attackerTenantId = await factory.SeedTenantAsync(attackerTenantSlug, "Globex");
        using var browser = TenantSignIns.CreateBrowser(factory);
        HttpResponseMessage response;
        if (attackerKind == "GitHub")
        {
            var providerId = await factory.SeedSsoProviderAsync(
                attackerTenantId,
                "Globex GitHub",
                "GitHub",
                "Oauth2",
                "https://ghe.attacker.test",
                "attacker-client");
            var challenge = await TenantSignIns.StartAsync(browser, attackerTenantSlug, providerId);
            TenantSignIns.QueueGitHubAnswers(factory, 4242, victim.Email);
            response = await TenantSignIns.CallbackAsync(browser, attackerTenantSlug, challenge.State);
        }
        else
        {
            // The attacker's server states the address without email_verified, which an Entra provider accepts as
            // verified.
            const string attackerAuthority = "https://login.attacker.test/globex/v2.0";
            var providerId = await factory.SeedSsoProviderAsync(
                attackerTenantId,
                "Globex Entra",
                issuerOrAuthorityUrl: attackerAuthority,
                clientId: "attacker-client");
            var challenge = await TenantSignIns.StartAsync(browser, attackerTenantSlug, providerId);
            TenantSignIns.QueueOidcAnswer(factory, challenge, attackerAuthority, "attacker-client", "attacker-subject", victim.Email, null);
            response = await TenantSignIns.CallbackAsync(browser, attackerTenantSlug, challenge.State);
        }

        Assert.NotEqual(victim.UserId.ToString(), await SessionSubjectAsync(response));
        Assert.False(await this.IsMemberAsync(attackerTenantId, victim.UserId));
        Assert.False(await this.HasLinkAsync(attackerTenantId, victim.UserId));
    }

    [Fact]
    public async Task SignIn_WhenAnAccountHasTheEmailOfTheIdentity_KeepsAPendingLinkAndSignsNobodyIn()
    {
        this.Reset();
        var existing = await this.CreateAccountThroughEntraAsync();
        var other = await this.CreateEntraTenantAsync();
        using var browser = TenantSignIns.CreateBrowser(factory);

        var response = await this.SignInThroughEntraAsync(browser, other, "other-subject", existing.Email, TenantSignIns.ReturnUrl(other.Slug));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("pending", TenantSignIns.Fragment(response, "link"));
        Assert.Null(TenantSignIns.Fragment(response, "accessToken"));
        var cookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("meisterpropr_tenant_sign_in_link=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("meisterpropr_refresh=", StringComparison.Ordinal));
        var pendingLink = await this.GetJsonAsync(browser, "/auth/external/pending-link");
        Assert.Equal(other.Slug, pendingLink.GetProperty("tenantSlug").GetString());
        Assert.Equal("Other Entra", pendingLink.GetProperty("providerDisplayName").GetString());
        Assert.Equal(existing.Email, pendingLink.GetProperty("email").GetString());
        Assert.True(pendingLink.GetProperty("canCreateAccount").GetBoolean());
        Assert.False(await this.IsMemberAsync(other.TenantId, existing.UserId));
    }

    /// <summary>
    ///     A person who is a member of several tenants signs in to the existing account and confirms the link. The
    ///     identity is linked to that account, which receives a membership, and the next sign-in through the provider
    ///     reaches the account.
    /// </summary>
    [Fact]
    public async Task PendingLink_ConfirmedFromTheExistingAccount_LinksTheIdentityToTheAccount()
    {
        this.Reset();
        var existing = await this.CreateAccountThroughEntraAsync();
        var other = await this.CreateEntraTenantAsync();
        using var browser = TenantSignIns.CreateBrowser(factory);
        await this.SignInThroughEntraAsync(browser, other, "guest-subject", existing.Email);

        var confirmation = await this.SendAsync(browser, HttpMethod.Post, "/auth/external/pending-link/confirm", existing.UserId);

        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        using (var body = JsonDocument.Parse(await confirmation.Content.ReadAsStringAsync()))
        {
            Assert.Equal(other.Slug, body.RootElement.GetProperty("tenantSlug").GetString());
        }

        Assert.True(await this.IsMemberAsync(other.TenantId, existing.UserId));
        Assert.True(await this.HasLinkAsync(other.TenantId, existing.UserId));
        using var laterBrowser = TenantSignIns.CreateBrowser(factory);
        var later = await this.SignInThroughEntraAsync(laterBrowser, other, "guest-subject", existing.Email);
        Assert.Equal(existing.UserId.ToString(), await SessionSubjectAsync(later));
    }

    /// <summary>The account that is signed in when the person confirms receives the link, whatever its address is.</summary>
    [Fact]
    public async Task PendingLink_ConfirmedFromAnotherAccount_LinksTheIdentityToTheAccountThatIsSignedIn()
    {
        this.Reset();
        var existing = await this.CreateAccountThroughEntraAsync();
        var signedIn = await this.CreateAccountThroughEntraAsync();
        var other = await this.CreateEntraTenantAsync();
        using var browser = TenantSignIns.CreateBrowser(factory);
        await this.SignInThroughEntraAsync(browser, other, "guest-subject", existing.Email);

        var confirmation = await this.SendAsync(browser, HttpMethod.Post, "/auth/external/pending-link/confirm", signedIn.UserId);

        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        Assert.True(await this.HasLinkAsync(other.TenantId, signedIn.UserId));
        Assert.False(await this.HasLinkAsync(other.TenantId, existing.UserId));
        Assert.False(await this.IsMemberAsync(other.TenantId, existing.UserId));
    }

    [Fact]
    public async Task PendingLink_ContinuedWithANewAccount_CreatesASeparateAccountAndLeavesTheExistingAccountAlone()
    {
        this.Reset();
        var existing = await this.CreateAccountThroughEntraAsync();
        var other = await this.CreateEntraTenantAsync();
        using var browser = TenantSignIns.CreateBrowser(factory);
        await this.SignInThroughEntraAsync(browser, other, "new-subject", existing.Email);

        var response = await this.SendAsync(browser, HttpMethod.Post, "/auth/external/pending-link/new-account");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string? accessToken;
        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            accessToken = body.RootElement.GetProperty("accessToken").GetString();
            Assert.False(body.RootElement.TryGetProperty("refreshToken", out _));
        }

        var newAccountId = Guid.Parse(TenantSignIns.SubjectOf(accessToken)!);
        Assert.NotEqual(existing.UserId, newAccountId);
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith("meisterpropr_refresh=", StringComparison.Ordinal)
                      && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.True(await this.IsMemberAsync(other.TenantId, newAccountId));
        Assert.True(await this.HasLinkAsync(other.TenantId, newAccountId));
        Assert.False(await this.IsMemberAsync(other.TenantId, existing.UserId));
    }

    /// <summary>The pending link is single-use: a browser that sends its cookie again after the confirmation is refused.</summary>
    [Fact]
    public async Task PendingLink_SentAgainAfterItsConfirmation_IsRefused()
    {
        this.Reset();
        var existing = await this.CreateAccountThroughEntraAsync();
        var signedIn = await this.CreateAccountThroughEntraAsync();
        var other = await this.CreateEntraTenantAsync();
        using var browser = TenantSignIns.CreateBrowser(factory);
        var callback = await this.SignInThroughEntraAsync(browser, other, "replayed-subject", existing.Email);
        var pendingLinkCookie = TenantSignIns.CookieOf(callback, "meisterpropr_tenant_sign_in_link");
        Assert.Equal(
            HttpStatusCode.OK,
            (await this.SendAsync(browser, HttpMethod.Post, "/auth/external/pending-link/confirm", existing.UserId)).StatusCode);
        using var replayingClient = factory.CreateClient();

        var confirmedAgain = await this.SendAsync(
            replayingClient,
            HttpMethod.Post,
            "/auth/external/pending-link/confirm",
            signedIn.UserId,
            cookie: pendingLinkCookie);
        var newAccount = await this.SendAsync(
            replayingClient,
            HttpMethod.Post,
            "/auth/external/pending-link/new-account",
            cookie: pendingLinkCookie);

        Assert.Equal(HttpStatusCode.NotFound, confirmedAgain.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, newAccount.StatusCode);
        Assert.False(await this.HasLinkAsync(other.TenantId, signedIn.UserId));
    }

    /// <summary>
    ///     The pending link waits in the browser that completed the sign-in. Another browser cannot read, confirm or use
    ///     it, while the browser with the cookie can.
    /// </summary>
    [Fact]
    public async Task PendingLink_InABrowserWithoutItsCookie_IsNotFound()
    {
        this.Reset();
        var existing = await this.CreateAccountThroughEntraAsync();
        var other = await this.CreateEntraTenantAsync();
        using var browser = TenantSignIns.CreateBrowser(factory);
        await this.SignInThroughEntraAsync(browser, other, "cookie-subject", existing.Email);
        using var otherBrowser = TenantSignIns.CreateBrowser(factory);

        var read = await this.SendAsync(otherBrowser, HttpMethod.Get, "/auth/external/pending-link");
        var confirmed = await this.SendAsync(otherBrowser, HttpMethod.Post, "/auth/external/pending-link/confirm", existing.UserId);
        var newAccount = await this.SendAsync(otherBrowser, HttpMethod.Post, "/auth/external/pending-link/new-account");
        var confirmedWithTheCookie = await this.SendAsync(browser, HttpMethod.Post, "/auth/external/pending-link/confirm", existing.UserId);

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, confirmed.StatusCode);
        Assert.Equal("pending_link_not_found", await ErrorOfAsync(confirmed));
        Assert.Equal(HttpStatusCode.NotFound, newAccount.StatusCode);
        Assert.Equal(HttpStatusCode.OK, confirmedWithTheCookie.StatusCode);
    }

    [Fact]
    public async Task PendingLink_ConfirmedWithoutASignedInAccount_IsRefused()
    {
        this.Reset();
        var existing = await this.CreateAccountThroughEntraAsync();
        var other = await this.CreateEntraTenantAsync();
        using var browser = TenantSignIns.CreateBrowser(factory);
        await this.SignInThroughEntraAsync(browser, other, "anonymous-subject", existing.Email);

        var confirmation = await this.SendAsync(browser, HttpMethod.Post, "/auth/external/pending-link/confirm");

        Assert.Equal(HttpStatusCode.Unauthorized, confirmation.StatusCode);
        Assert.False(await this.HasLinkAsync(other.TenantId, existing.UserId));
    }

    /// <summary>
    ///     The provider of a tenant must not reach the account of a platform administrator, so ProPR links no sign-in of
    ///     a tenant to such an account.
    /// </summary>
    [Fact]
    public async Task PendingLink_ConfirmedFromTheAccountOfAPlatformAdministrator_IsRefused()
    {
        this.Reset();
        var existing = await this.CreateAccountThroughEntraAsync();
        var administratorId = await factory.SeedUserAsync($"admin-{Guid.NewGuid():N}", $"admin.{Guid.NewGuid():N}@acme.test", AppUserRole.Admin);
        var other = await this.CreateEntraTenantAsync();
        using var browser = TenantSignIns.CreateBrowser(factory);
        await this.SignInThroughEntraAsync(browser, other, "admin-subject", existing.Email);

        var confirmation = await this.SendAsync(
            browser,
            HttpMethod.Post,
            "/auth/external/pending-link/confirm",
            administratorId,
            AppUserRole.Admin);

        Assert.Equal(HttpStatusCode.Forbidden, confirmation.StatusCode);
        Assert.Equal("platform_administrator_not_linkable", await ErrorOfAsync(confirmation));
        Assert.False(await this.HasLinkAsync(other.TenantId, administratorId));
    }

    /// <summary>
    ///     A provider that admits no new members creates no account, and links an identity only to an account that a
    ///     tenant administrator made a member.
    /// </summary>
    [Fact]
    public async Task PendingLink_OfAProviderThatAdmitsNoNewMembers_OffersNoNewAccountAndLinksOnlyAMember()
    {
        this.Reset();
        var existing = await this.CreateAccountThroughEntraAsync();
        var other = await this.CreateEntraTenantAsync(autoCreateUsers: false);
        using var browser = TenantSignIns.CreateBrowser(factory);
        var callback = await this.SignInThroughEntraAsync(browser, other, "invited-subject", existing.Email);
        Assert.Equal("link_confirmation_required", await ErrorOfAsync(callback));

        var pendingLink = await this.GetJsonAsync(browser, "/auth/external/pending-link");
        var newAccount = await this.SendAsync(browser, HttpMethod.Post, "/auth/external/pending-link/new-account");
        var beforeMembership = await this.SendAsync(browser, HttpMethod.Post, "/auth/external/pending-link/confirm", existing.UserId);
        await factory.SeedTenantMembershipAsync(other.TenantId, existing.UserId, TenantRole.TenantUser);
        var afterMembership = await this.SendAsync(browser, HttpMethod.Post, "/auth/external/pending-link/confirm", existing.UserId);

        Assert.False(pendingLink.GetProperty("canCreateAccount").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, newAccount.StatusCode);
        Assert.Equal("auto_create_disabled", await ErrorOfAsync(newAccount));
        Assert.Equal(HttpStatusCode.Forbidden, beforeMembership.StatusCode);
        Assert.Equal("auto_create_disabled", await ErrorOfAsync(beforeMembership));
        Assert.Equal(HttpStatusCode.OK, afterMembership.StatusCode);
        Assert.True(await this.HasLinkAsync(other.TenantId, existing.UserId));
    }

    /// <summary>
    ///     A tenant administrator restricts the allowed email domains of the provider while the sign-in waits. The policy
    ///     as it is when the person uses the pending link decides, so neither a link nor a new account follows.
    /// </summary>
    [Fact]
    public async Task PendingLink_AfterTheProviderNoLongerAllowsItsEmailDomain_IsRefused()
    {
        this.Reset();
        var existing = await this.CreateAccountThroughEntraAsync();
        var other = await this.CreateEntraTenantAsync();
        using var browser = TenantSignIns.CreateBrowser(factory);
        await this.SignInThroughEntraAsync(browser, other, "restricted-subject", existing.Email);
        await this.AllowOnlyEmailDomainAsync(other, "globex.test");

        var confirmation = await this.SendAsync(browser, HttpMethod.Post, "/auth/external/pending-link/confirm", existing.UserId);
        var newAccount = await this.SendAsync(browser, HttpMethod.Post, "/auth/external/pending-link/new-account");

        Assert.Equal(HttpStatusCode.Forbidden, confirmation.StatusCode);
        Assert.Equal("disallowed_domain", await ErrorOfAsync(confirmation));
        Assert.Equal(HttpStatusCode.Forbidden, newAccount.StatusCode);
        Assert.Equal("disallowed_domain", await ErrorOfAsync(newAccount));
        Assert.False(await this.HasLinkAsync(other.TenantId, existing.UserId));
        Assert.False(await this.IsMemberAsync(other.TenantId, existing.UserId));
        Assert.Equal(1, await this.CountAccountsWithEmailAsync(existing.Email));
    }

    /// <summary>
    ///     Google states the same subject for a person to every client, and its keys signed both ID tokens. A sign-in
    ///     through the Google provider of another tenant therefore reaches the account that the subject signed in to.
    /// </summary>
    [Fact]
    public async Task SignIn_ThroughAGoogleProviderOfAnotherTenant_ReachesTheAccountOfTheSameGoogleSubject()
    {
        this.Reset();
        var subject = $"google-{Guid.NewGuid():N}";
        var email = $"person.{Guid.NewGuid():N}@acme.test";
        var first = await this.CreateGoogleTenantAsync();
        var second = await this.CreateGoogleTenantAsync();
        using var firstBrowser = TenantSignIns.CreateBrowser(factory);
        var firstAccount = await SessionSubjectAsync(await this.SignInThroughGoogleAsync(firstBrowser, first, subject, email));
        Assert.NotNull(firstAccount);
        using var secondBrowser = TenantSignIns.CreateBrowser(factory);

        var secondAccount = await SessionSubjectAsync(await this.SignInThroughGoogleAsync(secondBrowser, second, subject, email));

        Assert.Equal(firstAccount, secondAccount);
        Assert.True(await this.IsMemberAsync(second.TenantId, Guid.Parse(firstAccount)));
        Assert.True(await this.HasLinkAsync(second.TenantId, Guid.Parse(firstAccount)));
    }

    /// <summary>
    ///     A link that no sign-in proved since ProPR checks the issuer of a provider does not let another provider with
    ///     the same issuer reach its account.
    /// </summary>
    [Fact]
    public async Task SignIn_ThroughAnotherProviderWithTheSameIssuer_DoesNotFollowALinkThatNoSignInProved()
    {
        this.Reset();
        var subject = $"google-{Guid.NewGuid():N}";
        var email = $"person.{Guid.NewGuid():N}@acme.test";
        var first = await this.CreateGoogleTenantAsync();
        var accountId = await factory.SeedUserAsync($"seeded-{Guid.NewGuid():N}", email);
        await factory.SeedTenantMembershipAsync(first.TenantId, accountId, TenantRole.TenantUser);
        await factory.SeedExternalIdentityAsync(first.TenantId, accountId, first.ProviderId, GoogleIssuer, subject, email, issuerVerified: false);
        var second = await this.CreateGoogleTenantAsync();
        using var browser = TenantSignIns.CreateBrowser(factory);

        var response = await this.SignInThroughGoogleAsync(browser, second, subject, email);

        Assert.Equal("link_confirmation_required", await ErrorOfAsync(response));
        Assert.False(await this.IsMemberAsync(second.TenantId, accountId));
    }

    /// <summary>
    ///     The issuer of a GitHub provider is the host that answers for the user. A sign-in through the GitHub provider
    ///     of another tenant reaches the account of the same user ID only when both providers reach the same host.
    /// </summary>
    [Theory]
    [InlineData("https://github.com", true)]
    [InlineData("https://ghe.globex.test", false)]
    public async Task SignIn_ThroughAGitHubProviderOfAnotherTenant_ReachesTheAccountOfTheSameUserOnlyOnTheSameHost(
        string secondAuthority,
        bool reachesTheAccount)
    {
        this.Reset();
        var gitHubUserId = Random.Shared.NextInt64(1_000_000, 9_000_000);
        var first = await this.CreateGitHubTenantAsync("https://github.com");
        using var firstBrowser = TenantSignIns.CreateBrowser(factory);
        var firstAccount = await SessionSubjectAsync(
            await this.SignInThroughGitHubAsync(firstBrowser, first, gitHubUserId, $"person.{Guid.NewGuid():N}@acme.test"));
        Assert.NotNull(firstAccount);
        var second = await this.CreateGitHubTenantAsync(secondAuthority);
        using var secondBrowser = TenantSignIns.CreateBrowser(factory);

        var secondAccount = await SessionSubjectAsync(
            await this.SignInThroughGitHubAsync(secondBrowser, second, gitHubUserId, $"other.{Guid.NewGuid():N}@acme.test"));

        Assert.NotNull(secondAccount);
        Assert.Equal(reachesTheAccount, secondAccount == firstAccount);
    }

    /// <summary>
    ///     A server at the authority of an OpenID Connect provider that names another issuer in its discovery document
    ///     could sign ID tokens in the name of that issuer. The sign-in refuses such a token, so it cannot reach the
    ///     account that a sign-in through the real issuer linked.
    /// </summary>
    [Fact]
    public async Task SignIn_ThroughAProviderWhoseDiscoveryDocumentNamesAnotherIssuer_IsRefused()
    {
        this.Reset();
        var victim = await this.CreateAccountThroughEntraAsync();
        var attackerTenantSlug = $"globex-{Guid.NewGuid():N}";
        var attackerTenantId = await factory.SeedTenantAsync(attackerTenantSlug, "Globex");
        var attackerAuthority = $"https://login.attacker.test/{Guid.NewGuid():N}/v2.0";
        factory.SetDiscoveryIssuer(attackerAuthority, victim.Issuer);
        var providerId = await factory.SeedSsoProviderAsync(
            attackerTenantId,
            "Globex Entra",
            issuerOrAuthorityUrl: attackerAuthority,
            clientId: "attacker-client");
        using var browser = TenantSignIns.CreateBrowser(factory);
        var challenge = await TenantSignIns.StartAsync(browser, attackerTenantSlug, providerId);
        TenantSignIns.QueueOidcAnswer(factory, challenge, victim.Issuer, "attacker-client", victim.Subject, $"other.{Guid.NewGuid():N}@acme.test");

        var response = await TenantSignIns.CallbackAsync(browser, attackerTenantSlug, challenge.State);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("external_identity_rejected", await ErrorOfAsync(response));
        Assert.False(await this.IsMemberAsync(attackerTenantId, victim.UserId));
        Assert.False(await this.HasLinkAsync(attackerTenantId, victim.UserId));
    }

    /// <summary>
    ///     A sign-in started from the settings of an account signs nobody in and creates no account. It keeps the
    ///     identity as a pending link, and the account that confirms it receives the link.
    /// </summary>
    [Fact]
    public async Task SignIn_StartedToLinkAnAccount_SignsNobodyInAndLinksTheIdentityToTheAccountThatConfirms()
    {
        this.Reset();
        var signedIn = await this.CreateAccountThroughEntraAsync();
        var other = await this.CreateEntraTenantAsync();
        var email = $"guest.{Guid.NewGuid():N}@acme.test";
        using var browser = TenantSignIns.CreateBrowser(factory);
        var challenge = await TenantSignIns.StartAsync(browser, other.Slug, other.ProviderId, TenantSignIns.ReturnUrl(other.Slug), "link");
        TenantSignIns.QueueOidcAnswer(factory, challenge, other.Authority, other.ClientId, "linked-subject", email);

        var callback = await TenantSignIns.CallbackAsync(browser, other.Slug, challenge.State);

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Equal("pending", TenantSignIns.Fragment(callback, "link"));
        Assert.Null(TenantSignIns.Fragment(callback, "accessToken"));
        Assert.False(await this.HasAccountWithEmailAsync(email));
        Assert.False((await this.GetJsonAsync(browser, "/auth/external/pending-link")).GetProperty("canCreateAccount").GetBoolean());

        var confirmation = await this.SendAsync(browser, HttpMethod.Post, "/auth/external/pending-link/confirm", signedIn.UserId);

        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        Assert.True(await this.HasLinkAsync(other.TenantId, signedIn.UserId));
        Assert.True(await this.IsMemberAsync(other.TenantId, signedIn.UserId));
    }

    [Fact]
    public async Task Challenge_ToLinkAnAccountWithoutAReturnUrl_ReturnsBadRequest()
    {
        this.Reset();
        var other = await this.CreateEntraTenantAsync();
        using var browser = TenantSignIns.CreateBrowser(factory);

        var response = await browser.GetAsync(TenantSignIns.ChallengePath(other.Slug, other.ProviderId, mode: "link"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SignInLinks_ListTheLinksOfTheAccountThatIsSignedIn()
    {
        this.Reset();
        var account = await this.CreateAccountThroughEntraAsync();
        using var browser = TenantSignIns.CreateBrowser(factory);

        var listed = await this.SendAsync(browser, HttpMethod.Get, "/auth/external/links", account.UserId);
        var anonymous = await this.SendAsync(browser, HttpMethod.Get, "/auth/external/links");

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var body = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        var link = Assert.Single(body.RootElement.EnumerateArray());
        Assert.Equal(account.TenantSlug, link.GetProperty("tenantSlug").GetString());
        Assert.Equal("Acme Entra", link.GetProperty("providerDisplayName").GetString());
        Assert.Equal("Microsoft", link.GetProperty("providerLabel").GetString());
        Assert.Equal(account.Email, link.GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    private void Reset()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();
    }

    /// <summary>Signs a new person in through the Entra provider of a new tenant, which creates the account.</summary>
    private async Task<SignedInAccount> CreateAccountThroughEntraAsync()
    {
        var tenant = await this.CreateEntraTenantAsync("Acme", "Acme Entra");
        var email = $"person.{Guid.NewGuid():N}@acme.test";
        var subject = $"entra-{Guid.NewGuid():N}";
        using var browser = TenantSignIns.CreateBrowser(factory);

        var response = await this.SignInThroughEntraAsync(browser, tenant, subject, email);

        var userId = await SessionSubjectAsync(response);
        Assert.NotNull(userId);
        return new SignedInAccount(Guid.Parse(userId), email, tenant.Slug, tenant.Authority, subject);
    }

    private async Task<TestTenant> CreateEntraTenantAsync(
        string tenantName = "Other",
        string providerName = "Other Entra",
        bool autoCreateUsers = true)
    {
        var slug = $"{tenantName.ToLowerInvariant()}-{Guid.NewGuid():N}";
        var tenantId = await factory.SeedTenantAsync(slug, tenantName);
        var authority = $"https://login.microsoftonline.com/{Guid.NewGuid()}/v2.0";
        var clientId = $"client-{Guid.NewGuid():N}";
        var providerId = await factory.SeedSsoProviderAsync(
            tenantId,
            providerName,
            issuerOrAuthorityUrl: authority,
            clientId: clientId,
            autoCreateUsers: autoCreateUsers);
        return new TestTenant(slug, tenantId, providerId, authority, clientId);
    }

    private async Task<TestTenant> CreateGoogleTenantAsync()
    {
        var slug = $"google-{Guid.NewGuid():N}";
        var tenantId = await factory.SeedTenantAsync(slug, "Google tenant");
        var clientId = $"google-client-{Guid.NewGuid():N}";
        var providerId = await factory.SeedSsoProviderAsync(tenantId, "Google", "Google", "Oidc", GoogleIssuer, clientId);
        return new TestTenant(slug, tenantId, providerId, GoogleIssuer, clientId);
    }

    private async Task<TestTenant> CreateGitHubTenantAsync(string authority)
    {
        var slug = $"github-{Guid.NewGuid():N}";
        var tenantId = await factory.SeedTenantAsync(slug, "GitHub tenant");
        var clientId = $"github-client-{Guid.NewGuid():N}";
        var providerId = await factory.SeedSsoProviderAsync(tenantId, "GitHub", "GitHub", "Oauth2", authority, clientId);
        return new TestTenant(slug, tenantId, providerId, authority, clientId);
    }

    private async Task<HttpResponseMessage> SignInThroughEntraAsync(
        HttpClient browser,
        TestTenant tenant,
        string subject,
        string email,
        string? returnUrl = null)
    {
        var challenge = await TenantSignIns.StartAsync(browser, tenant.Slug, tenant.ProviderId, returnUrl);
        TenantSignIns.QueueOidcAnswer(factory, challenge, tenant.Authority, tenant.ClientId, subject, email);
        return await TenantSignIns.CallbackAsync(browser, tenant.Slug, challenge.State);
    }

    private async Task<HttpResponseMessage> SignInThroughGoogleAsync(HttpClient browser, TestTenant tenant, string subject, string email)
    {
        var challenge = await TenantSignIns.StartAsync(browser, tenant.Slug, tenant.ProviderId);
        TenantSignIns.QueueOidcAnswer(factory, challenge, GoogleIssuer, tenant.ClientId, subject, email);
        return await TenantSignIns.CallbackAsync(browser, tenant.Slug, challenge.State);
    }

    private async Task<HttpResponseMessage> SignInThroughGitHubAsync(HttpClient browser, TestTenant tenant, long userId, string email)
    {
        var challenge = await TenantSignIns.StartAsync(browser, tenant.Slug, tenant.ProviderId);
        TenantSignIns.QueueGitHubAnswers(factory, userId, email);
        return await TenantSignIns.CallbackAsync(browser, tenant.Slug, challenge.State);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        Guid? userId = null,
        AppUserRole role = AppUserRole.User,
        string? cookie = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (userId is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateToken(userId.Value, role));
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return await client.SendAsync(request);
    }

    private async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        var response = await this.SendAsync(client, HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.Clone();
    }

    private async Task<bool> IsMemberAsync(Guid tenantId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        return await database.TenantMemberships.AnyAsync(membership => membership.TenantId == tenantId && membership.UserId == userId);
    }

    private async Task<bool> HasLinkAsync(Guid tenantId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        return await database.ExternalIdentities.AnyAsync(link => link.TenantId == tenantId && link.UserId == userId);
    }

    /// <summary>Changes the allowed email domains of the Entra provider of <paramref name="tenant" /> to one domain.</summary>
    private async Task AllowOnlyEmailDomainAsync(TestTenant tenant, string domain)
    {
        using var scope = factory.Services.CreateScope();
        var providers = scope.ServiceProvider.GetRequiredService<ITenantSsoProviderService>();
        var updated = await providers.UpdateAsync(
            tenant.TenantId,
            tenant.ProviderId,
            "Other Entra",
            "EntraId",
            "Oidc",
            tenant.Authority,
            tenant.ClientId,
            null,
            ["openid", "profile", "email"],
            [domain],
            true,
            true);
        Assert.NotNull(updated);
    }

    private async Task<int> CountAccountsWithEmailAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var normalizedEmail = email.ToUpperInvariant();
        return await database.AppUsers.CountAsync(user => user.NormalizedEmail == normalizedEmail);
    }

    private async Task<bool> HasAccountWithEmailAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var normalizedEmail = email.ToUpperInvariant();
        return await database.AppUsers.AnyAsync(user => user.NormalizedEmail == normalizedEmail);
    }

    /// <summary>The account a callback signed in, from the JSON session or the fragment of the redirect; null when none.</summary>
    private static async Task<string?> SessionSubjectAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.OK)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return TenantSignIns.SubjectOf(body.RootElement.GetProperty("accessToken").GetString());
        }

        return TenantSignIns.SubjectOf(TenantSignIns.Fragment(response, "accessToken"));
    }

    /// <summary>The error code of a refused request, from its JSON body or from the fragment of its redirect.</summary>
    private static async Task<string?> ErrorOfAsync(HttpResponseMessage response)
    {
        if (response.Headers.Location is not null)
        {
            return TenantSignIns.Fragment(response, "error");
        }

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    private sealed record SignedInAccount(Guid UserId, string Email, string TenantSlug, string Issuer, string Subject);

    private sealed record TestTenant(string Slug, Guid TenantId, Guid ProviderId, string Authority, string ClientId);
}
