// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

/// <summary>
///     The protection of the tenant sign-in flow: PKCE for every provider kind, a nonce for the OpenID Connect kinds,
///     and a state that is bound to the browser that started the sign-in and accepted once.
/// </summary>
public sealed class TenantSignInFlowProtectionTests(TenantAdministrationApiFactory factory)
    : IClassFixture<TenantAdministrationApiFactory>
{
    private const string FlowCookieName = "meisterpropr_tenant_sign_in";

    [Theory]
    [InlineData("EntraId")]
    [InlineData("Google")]
    [InlineData("GitHub")]
    public async Task Challenge_SendsAPkceChallengeAndKeepsTheStateInAnHttpOnlyCookie(string providerKind)
    {
        this.Reset();
        var tenant = await this.CreateTenantAsync(providerKind);
        using var browser = TenantSignIns.CreateBrowser(factory);

        var response = await browser.GetAsync(TenantSignIns.ChallengePath(tenant.Slug, tenant.ProviderId));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.Equal(43, query["state"].ToString().Length);
        Assert.Equal(43, query["code_challenge"].ToString().Length);
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        if (providerKind == "GitHub")
        {
            Assert.False(query.ContainsKey("nonce"));
        }
        else
        {
            Assert.Equal(43, query["nonce"].ToString().Length);
        }

        var cookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith($"{FlowCookieName}=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("EntraId")]
    [InlineData("GitHub")]
    public async Task Callback_SendsTheCodeVerifierOfTheChallengeToTheTokenEndpoint(string providerKind)
    {
        this.Reset();
        var tenant = await this.CreateTenantAsync(providerKind);
        using var browser = TenantSignIns.CreateBrowser(factory);
        var challenge = await TenantSignIns.StartAsync(browser, tenant.Slug, tenant.ProviderId);
        string? codeVerifier = null;
        if (providerKind == "GitHub")
        {
            TenantSignIns.QueueGitHubAnswers(
                factory,
                Random.Shared.NextInt64(1_000_000, 9_000_000),
                NewEmail(),
                request => codeVerifier = TenantSignIns.FormOf(request)["code_verifier"]);
        }
        else
        {
            TenantSignIns.QueueOidcAnswer(
                factory,
                challenge,
                tenant.Authority,
                tenant.ClientId,
                NewSubject(),
                NewEmail(),
                inspectForm: form => codeVerifier = form["code_verifier"]);
        }

        var response = await TenantSignIns.CallbackAsync(browser, tenant.Slug, challenge.State);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(codeVerifier);
        Assert.Equal(challenge.CodeChallenge, AuthorizationCodeFlow.ComputeCodeChallenge(codeVerifier));
    }

    [Fact]
    public async Task Callback_WithAnIdTokenIssuedForAnotherNonce_IsRefused()
    {
        this.Reset();
        var tenant = await this.CreateTenantAsync("EntraId");
        var email = NewEmail();
        using var browser = TenantSignIns.CreateBrowser(factory);
        var challenge = await TenantSignIns.StartAsync(browser, tenant.Slug, tenant.ProviderId);
        TenantSignIns.QueueOidcAnswer(
            factory,
            challenge with { Nonce = "nonce-of-another-sign-in" },
            tenant.Authority,
            tenant.ClientId,
            NewSubject(),
            email);

        var response = await TenantSignIns.CallbackAsync(browser, tenant.Slug, challenge.State);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("external_identity_rejected", await ErrorOfAsync(response));
        Assert.False(await this.HasAccountWithEmailAsync(email));
    }

    /// <summary>
    ///     A callback in a browser that did not start the sign-in carries a valid state and code, as an address that
    ///     someone else sent to the person does. ProPR refuses it before it redeems the code.
    /// </summary>
    [Fact]
    public async Task Callback_InABrowserWithoutTheCookieOfTheChallenge_IsRefusedWithoutAskingTheProvider()
    {
        this.Reset();
        var tenant = await this.CreateTenantAsync("EntraId");
        using var browser = TenantSignIns.CreateBrowser(factory);
        var challenge = await TenantSignIns.StartAsync(browser, tenant.Slug, tenant.ProviderId);
        var providerAsked = this.QueueAnswerThatRecordsTheRequest(challenge, tenant);
        using var otherBrowser = TenantSignIns.CreateBrowser(factory);

        var response = await TenantSignIns.CallbackAsync(otherBrowser, tenant.Slug, challenge.State);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_state", await ErrorOfAsync(response));
        Assert.False(providerAsked());
    }

    [Fact]
    public async Task Callback_WithTheStateOfAnotherSignIn_IsRefusedWithoutAskingTheProvider()
    {
        this.Reset();
        var tenant = await this.CreateTenantAsync("EntraId");
        using var browser = TenantSignIns.CreateBrowser(factory);
        var challenge = await TenantSignIns.StartAsync(browser, tenant.Slug, tenant.ProviderId);
        using var otherBrowser = TenantSignIns.CreateBrowser(factory);
        var otherChallenge = await TenantSignIns.StartAsync(otherBrowser, tenant.Slug, tenant.ProviderId);
        var providerAsked = this.QueueAnswerThatRecordsTheRequest(challenge, tenant);

        var response = await TenantSignIns.CallbackAsync(browser, tenant.Slug, otherChallenge.State);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_state", await ErrorOfAsync(response));
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
        var tenant = await this.CreateTenantAsync("EntraId");
        using var browser = TenantSignIns.CreateBrowser(factory);
        var challengeResponse = await browser.GetAsync(TenantSignIns.ChallengePath(tenant.Slug, tenant.ProviderId));
        var flowCookie = TenantSignIns.CookieOf(challengeResponse, FlowCookieName);
        var query = QueryHelpers.ParseQuery(challengeResponse.Headers.Location!.Query);
        var challenge = new TenantChallenge(
            challengeResponse.Headers.Location,
            query["state"].ToString(),
            query["nonce"].ToString(),
            query["code_challenge"].ToString());
        TenantSignIns.QueueOidcAnswer(factory, challenge, tenant.Authority, tenant.ClientId, NewSubject(), NewEmail());
        var first = await TenantSignIns.CallbackAsync(browser, tenant.Slug, challenge.State);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var providerAsked = this.QueueAnswerThatRecordsTheRequest(challenge, tenant);
        using var replayingClient = factory.CreateClient();
        using var replay = new HttpRequestMessage(
            HttpMethod.Get,
            $"/auth/external/callback/{tenant.Slug}?code=code&state={Uri.EscapeDataString(challenge.State)}");
        replay.Headers.Add("Cookie", flowCookie);

        var response = await replayingClient.SendAsync(replay);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_state", await ErrorOfAsync(response));
        Assert.False(providerAsked());
    }

    private void Reset()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();
    }

    /// <summary>Queues a valid token answer and returns whether the provider was asked for it.</summary>
    private Func<bool> QueueAnswerThatRecordsTheRequest(TenantChallenge challenge, TestTenant tenant)
    {
        var asked = false;
        factory.QueueExternalAuthResponse(_ =>
        {
            asked = true;
            return TenantSignIns.Json(
                new
                {
                    id_token = TenantSignIns.IdToken(tenant.Authority, tenant.ClientId, NewSubject(), NewEmail(), true, challenge.Nonce),
                });
        });
        return () => asked;
    }

    private async Task<TestTenant> CreateTenantAsync(string providerKind)
    {
        var slug = $"flow-{Guid.NewGuid():N}";
        var tenantId = await factory.SeedTenantAsync(slug, "Flow tenant");
        var clientId = $"client-{Guid.NewGuid():N}";
        var (protocolKind, authority) = providerKind switch
        {
            "GitHub" => ("Oauth2", "https://github.com"),
            "Google" => ("Oidc", "https://accounts.google.com"),
            _ => ("Oidc", $"https://login.microsoftonline.com/{Guid.NewGuid()}/v2.0"),
        };
        var providerId = await factory.SeedSsoProviderAsync(tenantId, providerKind, providerKind, protocolKind, authority, clientId);
        return new TestTenant(slug, providerId, authority, clientId);
    }

    private async Task<bool> HasAccountWithEmailAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var normalizedEmail = email.ToUpperInvariant();
        return await database.AppUsers.AnyAsync(user => user.NormalizedEmail == normalizedEmail);
    }

    private static async Task<string?> ErrorOfAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    private static string NewSubject()
    {
        return $"subject-{Guid.NewGuid():N}";
    }

    private static string NewEmail()
    {
        return $"person.{Guid.NewGuid():N}@acme.test";
    }

    private sealed record TestTenant(string Slug, Guid ProviderId, string Authority, string ClientId);
}
