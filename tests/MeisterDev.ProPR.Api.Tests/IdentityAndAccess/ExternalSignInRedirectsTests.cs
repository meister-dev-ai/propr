// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Features.IdentityAndAccess;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

/// <summary>The origins a sign-in returns its access token to.</summary>
public sealed class ExternalSignInRedirectsTests
{
    /// <summary>
    ///     Without <c>MEISTER_PUBLIC_BASE_URL</c>, the origin of the request qualifies. A request whose host names the
    ///     default port explicitly has the same origin as a return URL without it.
    /// </summary>
    [Theory]
    [InlineData("https", "example.test:443", "https://example.test/login/callback")]
    [InlineData("http", "example.test:80", "http://example.test/login/callback")]
    [InlineData("https", "example.test", "https://example.test:443/login/callback")]
    public void TryResolveUserInterfaceReturnUrl_AcceptsTheOriginOfTheRequestWithOrWithoutItsDefaultPort(
        string scheme,
        string host,
        string returnUrl)
    {
        var request = Request(scheme, host);

        var resolved = ExternalSignInRedirects.TryResolveUserInterfaceReturnUrl(
            returnUrl,
            request,
            new ConfigurationBuilder().Build(),
            isDevelopment: false);

        Assert.Equal(new Uri(returnUrl).AbsoluteUri, resolved?.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://example.test:8443/login/callback")]
    [InlineData("http://example.test/login/callback")]
    [InlineData("https://other.example.test/login/callback")]
    public void TryResolveUserInterfaceReturnUrl_RefusesAnotherPortSchemeOrHost(string returnUrl)
    {
        var resolved = ExternalSignInRedirects.TryResolveUserInterfaceReturnUrl(
            returnUrl,
            Request("https", "example.test:443"),
            new ConfigurationBuilder().Build(),
            isDevelopment: false);

        Assert.Null(resolved);
    }

    /// <summary>
    ///     With <c>MEISTER_PUBLIC_BASE_URL</c>, the origin of the configured address qualifies, also when the request
    ///     reached the API at another origin, for example behind a proxy. The path of the configured address and an
    ///     explicit default port do not change its origin.
    /// </summary>
    [Theory]
    [InlineData("https://propr.example.test/api", "https://propr.example.test/login/callback")]
    [InlineData("https://propr.example.test:443/api", "https://propr.example.test/login/callback")]
    [InlineData("https://propr.example.test:8443", "https://propr.example.test:8443/login/callback")]
    public void TryResolveUserInterfaceReturnUrl_AcceptsTheOriginOfTheConfiguredPublicBaseUrl(
        string publicBaseUrl,
        string returnUrl)
    {
        var resolved = ExternalSignInRedirects.TryResolveUserInterfaceReturnUrl(
            returnUrl,
            Request("http", "propr-api.internal:8080"),
            PublicBaseUrl(publicBaseUrl),
            isDevelopment: false);

        Assert.Equal(new Uri(returnUrl).AbsoluteUri, resolved?.AbsoluteUri);
    }

    /// <summary>
    ///     With <c>MEISTER_PUBLIC_BASE_URL</c>, the origin the request reached the API at no longer qualifies, and
    ///     neither does another port, scheme or host than the one of the configured address.
    /// </summary>
    [Theory]
    [InlineData("http://propr-api.internal:8080/login/callback")]
    [InlineData("https://propr.example.test:8443/login/callback")]
    [InlineData("http://propr.example.test/login/callback")]
    [InlineData("https://other.example.test/login/callback")]
    public void TryResolveUserInterfaceReturnUrl_WithAConfiguredPublicBaseUrl_RefusesEveryOtherOrigin(string returnUrl)
    {
        var resolved = ExternalSignInRedirects.TryResolveUserInterfaceReturnUrl(
            returnUrl,
            Request("http", "propr-api.internal:8080"),
            PublicBaseUrl("https://propr.example.test/api"),
            isDevelopment: false);

        Assert.Null(resolved);
    }

    /// <summary>
    ///     A configured address with credentials yields no origin, so its host does not qualify, and the origin the
    ///     request reached the API at does not qualify in its place.
    /// </summary>
    [Theory]
    [InlineData("https://propr.example.test/login/callback")]
    [InlineData("http://propr-api.internal:8080/login/callback")]
    public void TryResolveUserInterfaceReturnUrl_WithAConfiguredPublicBaseUrlThatCarriesCredentials_RefusesItsHostAndTheOriginOfTheRequest(string returnUrl)
    {
        var resolved = ExternalSignInRedirects.TryResolveUserInterfaceReturnUrl(
            returnUrl,
            Request("http", "propr-api.internal:8080"),
            PublicBaseUrl("https://user:secret@propr.example.test/api"),
            isDevelopment: false);

        Assert.Null(resolved);
    }

    private static IConfiguration PublicBaseUrl(string publicBaseUrl) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MEISTER_PUBLIC_BASE_URL"] = publicBaseUrl })
            .Build();

    private static HttpRequest Request(string scheme, string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        return context.Request;
    }
}
