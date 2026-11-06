// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Options;
using Microsoft.Extensions.Configuration;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.IdentityAndAccess.ExtensionAuth;

public sealed class ExtensionOptionsTests
{
    [Fact]
    public void MissingConfigurationDisablesSignIn()
    {
        Assert.False(ExtensionAuthorizationOptions.FromConfiguration(new ConfigurationBuilder().Build()).Enabled);
    }

    [Fact]
    public void AnInsecureWebsiteDisablesExtensionSignInWithAnExplicitReason()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["MEISTER_EXTENSION_AUTH_ISSUER"] = "https://propr.test/api",
                ["MEISTER_EXTENSION_AUTH_REDIRECT_URIS"] = "[\"https://abcdefghijklmnopabcdefghijklmnop.chromiumapp.org/propr\"]",
                ["MEISTER_PUBLIC_BASE_URL"] = "http://propr.test",
            }).Build();
        var options = ExtensionAuthorizationOptions.FromConfiguration(configuration);
        Assert.False(options.Enabled);
        Assert.Equal("insecure_ui_origin", options.UnavailableReason);
    }

    [Theory]
    [InlineData("[")]
    [InlineData("{}")]
    [InlineData("123")]
    [InlineData("[123]")]
    [InlineData("null")]
    public void MalformedCallbackConfigurationUsesAnActionableRedactedDiagnostic(string raw)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["MEISTER_EXTENSION_AUTH_ISSUER"] = "https://propr.test/api",
                ["MEISTER_EXTENSION_AUTH_REDIRECT_URIS"] = raw,
            }).Build();
        var failure = Assert.Throws<InvalidOperationException>(() => ExtensionAuthorizationOptions.FromConfiguration(configuration));
        Assert.Equal("MEISTER_EXTENSION_AUTH_REDIRECT_URIS must be a JSON array of callback strings.", failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Theory]
    [InlineData("http://propr.test/api", "https://abcdefghijklmnopabcdefghijklmnop.chromiumapp.org/propr")]
    [InlineData("https://propr.test/api", "https://*.chromiumapp.org/propr")]
    [InlineData("https://propr.test/api", "https://unrelated.test/propr")]
    [InlineData("https://propr.test/api", "https://abcdefghijklmnopabcdefghijklmnop.chromiumapp.org/propr?other=1")]
    [InlineData("https://user:password@propr.test/api", "https://abcdefghijklmnopabcdefghijklmnop.chromiumapp.org/propr")]
    public void InvalidIssuerAndNonSdkOrWildcardCallbacksAreRejected(string issuer, string callback)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["MEISTER_EXTENSION_AUTH_ISSUER"] = issuer,
                ["MEISTER_EXTENSION_AUTH_REDIRECT_URIS"] = JsonSerializer.Serialize(new[] { callback }),
            }).Build();
        Assert.Throws<InvalidOperationException>(() => ExtensionAuthorizationOptions.FromConfiguration(configuration));
    }
}
