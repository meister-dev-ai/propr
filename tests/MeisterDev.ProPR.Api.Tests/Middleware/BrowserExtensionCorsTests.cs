// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net.Http.Headers;

namespace MeisterDev.ProPR.Api.Tests.Middleware;

/// <summary>
///     The browser extension has to reach the API from Firefox, which enforces cross-origin rules on an
///     extension's own requests where Chromium exempts them. What it must not gain in the process is the
///     session cookie.
/// </summary>
/// <remarks>
///     Asserted over real responses rather than over the policy object, because what decides the outcome
///     is the set of headers a browser receives. The absence of
///     <c>Access-Control-Allow-Credentials</c> is the security property here, and an absence is exactly
///     what disappears in a refactor with nothing failing.
/// </remarks>
public sealed class BrowserExtensionCorsTests(ControllerSmokeTests.SmokeFactory factory)
    : IClassFixture<ControllerSmokeTests.SmokeFactory>
{
    private const string ExtensionOrigin = "moz-extension://a680db91-fbdc-443c-8ea7-e064537b3a0d";

    [Theory]
    [InlineData(ExtensionOrigin)]
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop")]
    public async Task Preflight_FromAnExtension_IsAllowed(string origin)
    {
        // Firefox sends this before the real request, and blocks it when the response carries no
        // allow-origin header — which is exactly what happened before the extension policy existed.
        var response = await this.PreflightAsync(origin);

        Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Preflight_FromAnExtension_PermitsTheHeaderItAuthenticatesWith()
    {
        var response = await this.PreflightAsync(ExtensionOrigin);

        var allowed = response.Headers.GetValues("Access-Control-Allow-Headers").Single();
        Assert.Contains("x-user-pat", allowed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preflight_FromAnExtension_RefusesCredentials()
    {
        // The refresh token is a cookie, and an extension origin can only be matched by scheme because
        // Firefox randomises it per installation. Allowing credentials for a scheme match would let any
        // installed extension ride that cookie and read the response.
        var response = await this.PreflightAsync(ExtensionOrigin);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Preflight_FromTheAdminUi_StillReceivesCredentials()
    {
        // Everything that is not an extension must be untouched: the admin UI depends on them.
        var response = await this.PreflightAsync("https://dev.azure.com");

        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
    }

    [Fact]
    public async Task Preflight_FromAHostThatMerelyMentionsTheScheme_IsNotTreatedAsAnExtension()
    {
        // A host is not a scheme. This origin is not allowed at all, so it gets no allow-origin header.
        var response = await this.PreflightAsync("https://moz-extension.example");

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private async Task<HttpResponseMessage> PreflightAsync(string origin)
    {
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/auth/me");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "x-user-pat");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return await httpClient.SendAsync(request);
    }
}
