// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.ProPR.Infrastructure.Auth;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

/// <summary>
///     The page a tenant sign-in returns to and the message it shows: the access token travels in the fragment of the
///     return address, so only the origins of the user interface qualify, at the challenge and again at the callback.
///     The description that the provider sends with an error stays in the log.
/// </summary>
public sealed class TenantSignInReturnAddressTests(TenantAdministrationApiFactory factory)
    : IClassFixture<TenantAdministrationApiFactory>
{
    private const string FlowCookieName = "meisterpropr_tenant_sign_in";

    /// <summary>
    ///     The origins that the CORS policy allows for SCM browser extensions, on which anyone who publishes an extension
    ///     can serve pages, and the local frontend origins outside Development do not serve the user interface.
    /// </summary>
    [Theory]
    [InlineData("https://elsewhere.example.test/tenants/acme/login/callback")]
    [InlineData("https://publisher.gallerycdn.vsassets.io/extension/page.html")]
    [InlineData("https://dev.azure.com/organization/login/callback")]
    [InlineData("https://organization.visualstudio.com/login/callback")]
    [InlineData("http://localhost:5173/tenants/acme/login/callback")]
    public async Task Challenge_WithAReturnUrlOnAnOriginThatDoesNotServeTheUserInterface_ReturnsBadRequest(string returnUrl)
    {
        this.Reset();
        var tenant = await this.CreateTenantAsync();
        using var browser = TenantSignIns.CreateBrowser(factory);

        var response = await browser.GetAsync(TenantSignIns.ChallengePath(tenant.Slug, tenant.ProviderId, returnUrl));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("return_url_not_allowed", await ErrorOfAsync(response));
        Assert.DoesNotContain(
            response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            cookie => cookie.StartsWith($"{FlowCookieName}=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SignIn_WithAReturnUrlOnTheOriginOfTheUserInterface_ReturnsTheSessionInTheFragment()
    {
        this.Reset();
        var tenant = await this.CreateTenantAsync();
        using var browser = TenantSignIns.CreateBrowser(factory);
        var challenge = await TenantSignIns.StartAsync(browser, tenant.Slug, tenant.ProviderId, TenantSignIns.ReturnUrl(tenant.Slug));
        TenantSignIns.QueueOidcAnswer(factory, challenge, tenant.Authority, tenant.ClientId, NewSubject(), NewEmail());

        var response = await TenantSignIns.CallbackAsync(browser, tenant.Slug, challenge.State);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(TenantSignIns.ReturnUrl(tenant.Slug), response.Headers.Location!.GetLeftPart(UriPartial.Path));
        Assert.False(string.IsNullOrWhiteSpace(TenantSignIns.Fragment(response, "accessToken")));
    }

    /// <summary>
    ///     The flow of this sign-in names a return page on an origin that no longer serves the user interface, as after a
    ///     change of <c>CORS_ORIGINS</c> while the person was at the provider. The callback hands no session to that page.
    /// </summary>
    [Fact]
    public async Task Callback_WhenTheReturnPageNoLongerServesTheUserInterface_HandsNoSessionToIt()
    {
        this.Reset();
        var tenant = await this.CreateTenantAsync();
        const string state = "state-of-a-flow-with-a-foreign-return-page";
        const string nonce = "nonce-of-a-flow-with-a-foreign-return-page";
        var flow = JsonSerializer.Serialize(
            new
            {
                State = state,
                Nonce = nonce,
                CodeVerifier = AuthorizationCodeFlow.CreateRandomValue(),
                TenantSlug = tenant.Slug,
                ProviderId = tenant.ProviderId,
                CallbackUrl = $"http://localhost/auth/external/callback/{tenant.Slug}",
                FrontendReturnUrl = "https://elsewhere.example.test/tenants/acme/login/callback",
                Mode = 0,
                IssuedAtUtc = DateTimeOffset.UtcNow,
            });
        using var browser = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        TenantSignIns.QueueOidcAnswer(
            factory,
            new TenantChallenge(new Uri("https://login.example.test"), state, nonce, null),
            tenant.Authority,
            tenant.ClientId,
            NewSubject(),
            NewEmail());
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/auth/external/callback/{tenant.Slug}?code=code&state={state}");
        request.Headers.Add("Cookie", $"{FlowCookieName}={Uri.EscapeDataString("protected::" + flow)}");

        var response = await browser.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("return_url_not_allowed", await ErrorOfAsync(response));
        Assert.DoesNotContain(
            response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            cookie => cookie.StartsWith("meisterpropr_refresh=", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The administrator of the tenant configures the provider, so the description it sends with an error can say
    ///     anything. The person reads a fixed message for the error code.
    /// </summary>
    [Fact]
    public async Task Callback_WhenTheProviderReportsAnError_ShowsAFixedMessageInsteadOfTheDescriptionOfTheProvider()
    {
        this.Reset();
        var tenant = await this.CreateTenantAsync();
        using var browser = TenantSignIns.CreateBrowser(factory);
        var challenge = await TenantSignIns.StartAsync(browser, tenant.Slug, tenant.ProviderId, TenantSignIns.ReturnUrl(tenant.Slug));

        var response = await browser.GetAsync(
            $"/auth/external/callback/{tenant.Slug}?error=access_denied&error_description={Uri.EscapeDataString("Call +1 555 0100 to unlock your account")}&state={Uri.EscapeDataString(challenge.State)}");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("provider_error", TenantSignIns.Fragment(response, "error"));
        Assert.Equal("The sign-in was cancelled or refused at the identity provider.", TenantSignIns.Fragment(response, "message"));
        Assert.DoesNotContain("unlock", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private void Reset()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();
    }

    private async Task<TestTenant> CreateTenantAsync()
    {
        var slug = $"return-{Guid.NewGuid():N}";
        var tenantId = await factory.SeedTenantAsync(slug, "Return tenant");
        var authority = $"https://login.microsoftonline.com/{Guid.NewGuid()}/v2.0";
        var clientId = $"client-{Guid.NewGuid():N}";
        var providerId = await factory.SeedSsoProviderAsync(tenantId, "Return Entra", issuerOrAuthorityUrl: authority, clientId: clientId);
        return new TestTenant(slug, providerId, authority, clientId);
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
