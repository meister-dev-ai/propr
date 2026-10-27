// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

/// <summary>
///     Runs tenant sign-ins against the API as a browser does: the challenge, the answers of the identity provider
///     that the test queues, and the callback.
/// </summary>
internal static class TenantSignIns
{
    /// <summary>The page of the user interface that a tenant sign-in returns to.</summary>
    public static string ReturnUrl(string tenantSlug)
    {
        return $"http://localhost/tenants/{tenantSlug}/login/callback";
    }

    /// <summary>A client that keeps cookies and does not follow redirects, as a browser on the sign-in pages.</summary>
    public static HttpClient CreateBrowser(TenantAdministrationApiFactory factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    }

    /// <summary>Starts a sign-in and returns the parameters of the authorization request that ProPR sends the browser to.</summary>
    public static async Task<TenantChallenge> StartAsync(
        HttpClient browser,
        string tenantSlug,
        Guid providerId,
        string? returnUrl = null,
        string? mode = null)
    {
        var response = await browser.GetAsync(ChallengePath(tenantSlug, providerId, returnUrl, mode));
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location;
        Assert.NotNull(location);
        var query = QueryHelpers.ParseQuery(location.Query);
        return new TenantChallenge(
            location,
            Single(query, "state"),
            Optional(query, "nonce"),
            Optional(query, "code_challenge"));
    }

    /// <summary>The address of the challenge endpoint.</summary>
    public static string ChallengePath(string tenantSlug, Guid providerId, string? returnUrl = null, string? mode = null)
    {
        var parameters = new List<string>();
        if (returnUrl is not null)
        {
            parameters.Add($"returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        if (mode is not null)
        {
            parameters.Add($"mode={Uri.EscapeDataString(mode)}");
        }

        var path = $"/auth/external/challenge/{tenantSlug}/{providerId}";
        return parameters.Count == 0 ? path : $"{path}?{string.Join("&", parameters)}";
    }

    /// <summary>Calls the callback as the browser does when the provider sends it back with a code.</summary>
    public static Task<HttpResponseMessage> CallbackAsync(HttpClient browser, string tenantSlug, string state, string code = "code")
    {
        return browser.GetAsync($"/auth/external/callback/{tenantSlug}?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}");
    }

    /// <summary>
    ///     Queues the answer of the token endpoint of an OpenID Connect provider: an ID token for the subject, with the
    ///     nonce of the challenge. The form of the token request is handed to <paramref name="inspectForm" />.
    /// </summary>
    public static void QueueOidcAnswer(
        TenantAdministrationApiFactory factory,
        TenantChallenge challenge,
        string issuer,
        string clientId,
        string subject,
        string? email,
        bool? emailVerified = true,
        Action<Dictionary<string, StringValues>>? inspectForm = null)
    {
        factory.QueueExternalAuthResponse(request =>
        {
            inspectForm?.Invoke(QueryHelpers.ParseQuery(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult()));
            return Json(new { id_token = IdToken(issuer, clientId, subject, email, emailVerified, challenge.Nonce) });
        });
    }

    /// <summary>
    ///     Queues the three answers of a GitHub host: the token endpoint, the user and the email addresses of the user.
    ///     The form of the token request is handed to <paramref name="inspectForm" />.
    /// </summary>
    public static void QueueGitHubAnswers(
        TenantAdministrationApiFactory factory,
        long userId,
        string email,
        Action<HttpRequestMessage>? inspectTokenRequest = null)
    {
        factory.QueueExternalAuthResponse(request =>
        {
            inspectTokenRequest?.Invoke(request);
            return Json(new { access_token = "github-access-token", token_type = "bearer", scope = "read:user,user:email" });
        });
        factory.QueueExternalAuthResponse(_ => Json(new { id = userId, login = $"user-{userId}", name = "GitHub User" }));
        factory.QueueExternalAuthResponse(_ => Json(new[] { new GitHubEmail(email, true, true) }));
    }

    /// <summary>Returns an ID token signed with the key the test host trusts.</summary>
    public static string IdToken(
        string issuer,
        string audience,
        string subject,
        string? email,
        bool? emailVerified,
        string? nonce)
    {
        var claims = new List<Claim> { new("sub", subject) };
        if (email is not null)
        {
            claims.Add(new Claim("email", email));
        }

        if (emailVerified is not null)
        {
            claims.Add(new Claim("email_verified", emailVerified.Value ? "true" : "false"));
        }

        if (nonce is not null)
        {
            claims.Add(new Claim("nonce", nonce));
        }

        var token = new JwtSecurityToken(
            issuer,
            audience,
            claims,
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: TenantAdministrationApiFactory.OidcSigningCredentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Returns a value of the fragment of the address a response redirects to, or null.</summary>
    public static string? Fragment(HttpResponseMessage response, string name)
    {
        var location = response.Headers.Location;
        if (location is null)
        {
            return null;
        }

        var fragment = QueryHelpers.ParseQuery("?" + location.Fragment.TrimStart('#'));
        return fragment.TryGetValue(name, out var values) ? values.ToString() : null;
    }

    /// <summary>Returns the form fields of a request to a token endpoint.</summary>
    public static Dictionary<string, StringValues> FormOf(HttpRequestMessage request)
    {
        return QueryHelpers.ParseQuery(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult());
    }

    /// <summary>Returns the cookie that a response set, as the <c>name=value</c> pair a browser sends back.</summary>
    public static string CookieOf(HttpResponseMessage response, string name)
    {
        var header = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith($"{name}=", StringComparison.Ordinal));
        return header.Split(';', 2)[0];
    }

    /// <summary>Returns the subject of an access token that ProPR issued.</summary>
    public static string? SubjectOf(string? accessToken)
    {
        return accessToken is null ? null : new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Subject;
    }

    public static HttpResponseMessage Json<T>(T payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
    }

    private static string Single(Dictionary<string, StringValues> query, string name)
    {
        Assert.True(query.TryGetValue(name, out var values), $"Missing parameter '{name}'.");
        return Assert.Single(values)!;
    }

    private static string? Optional(Dictionary<string, StringValues> query, string name)
    {
        return query.TryGetValue(name, out var values) ? values.ToString() : null;
    }

    private sealed record GitHubEmail(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("verified")]
        bool Verified,
        [property: JsonPropertyName("primary")]
        bool Primary);
}

/// <summary>The parameters of the authorization request that a challenge sends the browser to.</summary>
internal sealed record TenantChallenge(Uri Location, string State, string? Nonce, string? CodeChallenge);
