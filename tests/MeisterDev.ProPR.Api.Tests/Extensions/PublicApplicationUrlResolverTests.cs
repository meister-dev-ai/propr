// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Extensions;
using Microsoft.Extensions.Configuration;

namespace MeisterDev.ProPR.Api.Tests.Extensions;

/// <summary>
///     Covers how the configured public address is read, in particular the difference between the API base URL
///     that is configured and the browser origin derived from it.
/// </summary>
public sealed class PublicApplicationUrlResolverTests
{
    [Theory]
    // The documented deployment puts the API behind a proxy prefix while the interface is served from the root
    // of the same host, so the prefix has to come off or every generated link lands on an API path.
    [InlineData("https://propr.example.com/api", "https://propr.example.com")]
    [InlineData("https://propr.example.com/api/", "https://propr.example.com")]
    [InlineData("https://localhost:5443/api", "https://localhost:5443")]
    [InlineData("https://propr.example.com", "https://propr.example.com")]
    [InlineData("http://propr.example.com:8080/deep/prefix", "http://propr.example.com:8080")]
    public void GetConfiguredPublicUiOrigin_ReturnsTheAuthorityOfTheConfiguredBaseUrl(string configured, string expected)
    {
        var configuration = BuildConfiguration(configured);

        var origin = PublicApplicationUrlResolver.GetConfiguredPublicUiOrigin(configuration);

        Assert.Equal(expected, origin);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    // A path-only value parses as an absolute file:// URI on Linux. Its authority is empty, so accepting it
    // would put "file://" in the browser allow-list and in links sent to a pull request.
    [InlineData("/relative/only")]
    [InlineData("file:///srv/propr")]
    // Credentials in the configured URL must not survive into an origin. The value is put in front of a
    // browser and written into links ProPR posts on pull requests, where a secret cannot be taken back.
    [InlineData("https://svc:s3cret@propr.example.com/api")]
    [InlineData("https://svc@propr.example.com/api")]
    public void GetConfiguredPublicUiOrigin_WithoutAnAbsoluteHttpUrl_ReturnsNull(string? configured)
    {
        var configuration = BuildConfiguration(configured);

        Assert.Null(PublicApplicationUrlResolver.GetConfiguredPublicUiOrigin(configuration));
    }

    [Fact]
    public void GetConfiguredPublicBaseUrl_KeepsThePathTheUiOriginDrops()
    {
        var configuration = BuildConfiguration("https://propr.example.com/api");

        // The two answer different questions: callbacks and webhook listeners address the API behind its
        // prefix, and only a browser link needs the bare origin.
        Assert.Equal("https://propr.example.com/api", PublicApplicationUrlResolver.GetConfiguredPublicBaseUrl(configuration));
        Assert.Equal("https://propr.example.com", PublicApplicationUrlResolver.GetConfiguredPublicUiOrigin(configuration));
    }

    private static IConfiguration BuildConfiguration(string? publicBaseUrl)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MEISTER_PUBLIC_BASE_URL"] = publicBaseUrl })
            .Build();
    }
}
