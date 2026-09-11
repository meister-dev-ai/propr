// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Execution;

/// <summary>
///     Reducing a provider scope path or a stored connection host to the host they name. That reduced value
///     decides whether a connection belongs to the pull request being observed.
/// </summary>
public sealed class ScmConnectionHostMatchTests
{
    [Theory]
    [InlineData("https://dev.azure.com/org", "https://dev.azure.com")]
    [InlineData("https://dev.azure.com/org/", "https://dev.azure.com")]
    [InlineData("https://github.company.com:8443/x", "https://github.company.com:8443")]
    [InlineData("https://github.company.com:443/x", "https://github.company.com")]
    [InlineData("http://github.company.com:80/x", "http://github.company.com")]
    [InlineData("http://github.company.com:8080/x", "http://github.company.com:8080")]
    [InlineData("https://DEV.Azure.com/org", "https://dev.azure.com")]
    [InlineData("HTTPS://dev.azure.com/org", "https://dev.azure.com")]
    [InlineData("https://dev.azure.com:443/org", "https://dev.azure.com")]
    [InlineData("https://[2001:db8::1]/repo", "https://[2001:db8::1]")]
    [InlineData("https://[2001:db8::1]:8443/repo", "https://[2001:db8::1]:8443")]
    public void AScopePathIsReducedToItsSchemeHostAndPort(string scopePath, string expected)
    {
        Assert.Equal(expected, ScmConnectionHostMatch.ToAuthority(scopePath));
    }

    [Theory]
    [InlineData("https://token@dev.azure.com/org", "https://dev.azure.com")]
    [InlineData("https://user:pw@github.company.com:8443/x", "https://github.company.com:8443")]
    public void CredentialsCarriedInTheScopePathAreDropped(string scopePath, string expected)
    {
        // The reduced value is compared against stored connections and named in a log line, so userinfo would
        // otherwise put the secret into both.
        var authority = ScmConnectionHostMatch.ToAuthority(scopePath);

        Assert.Equal(expected, authority);
        Assert.DoesNotContain("@", authority!, StringComparison.Ordinal);
        Assert.DoesNotContain("pw", authority!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("file:///tmp/repo")]
    [InlineData("urn:isbn:12345")]
    [InlineData("https://")]
    [InlineData("https://host:notaport/x")]
    [InlineData("http://:8080/x")]
    [InlineData("my-org")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AValueThatNamesNoHostHasNoAuthority(string? value)
    {
        // Absolute parsing alone does not establish a host: the first two parse and name none.
        Assert.Null(ScmConnectionHostMatch.ToAuthority(value));
    }

    [Theory]
    [InlineData("https://dev.azure.com/org", "https://dev.azure.com", true)]
    [InlineData("https://token@dev.azure.com/org", "https://dev.azure.com", true)]
    [InlineData("https://dev.azure.com/org", "https://github.com", false)]
    [InlineData("https://github.company.com:8443/x", "https://github.company.com", false)]
    [InlineData("https://DEV.Azure.com/org", "https://dev.azure.com", true)]
    [InlineData("HTTPS://dev.azure.com/org", "https://dev.azure.com", true)]
    [InlineData("https://dev.azure.com:443/org", "https://dev.azure.com", true)]
    [InlineData("https://svc:token@dev.azure.com/org", "https://dev.azure.com", true)]
    [InlineData("https://github.company.com:8443/x", "https://github.company.com:8443", true)]
    [InlineData("https://example.com./org", "https://example.com", true)]
    [InlineData("https://example.com/org", "https://example.com.", true)]
    public void AConnectionMatchesTheHostItNames(string connectionHostBaseUrl, string hostAuthority, bool expected)
    {
        Assert.Equal(expected, ScmConnectionHostMatch.MatchesAuthority(connectionHostBaseUrl, hostAuthority));
    }


    [Theory]
    [InlineData("dev.azure.com", "https://dev.azure.com")]
    [InlineData("dev.azure.com", "dev.azure.com")]
    [InlineData("dev.azure.com:8443", "https://dev.azure.com:8443")]
    public void AConnectionStoringNoSchemeMatchesNothing(string connectionHostBaseUrl, string hostAuthority)
    {
        // Both sides are reduced the same way, and a value with no scheme reduces to nothing. A connection is
        // matched by stating a URL, and every connection this has resolved stores one.
        Assert.False(ScmConnectionHostMatch.MatchesAuthority(connectionHostBaseUrl, hostAuthority));
    }

    [Theory]
    [InlineData(null, "https://dev.azure.com")]
    [InlineData("", "https://dev.azure.com")]
    [InlineData("   ", "https://dev.azure.com")]
    [InlineData("urn:isbn:12345", "")]
    [InlineData("file:///tmp/x", "file:///tmp/x")]
    [InlineData("urn:isbn:12345", "urn:isbn:12345")]
    [InlineData("https://example.com:invalid", "https://example.com:invalid")]
    [InlineData("https://dev.azure.com", "")]
    [InlineData("https://dev.azure.com", "   ")]
    [InlineData("https://dev.azure.com", "file:///tmp/x")]
    [InlineData("https://dev.azure.com", "urn:isbn:12345")]
    [InlineData("https://dev.azure.com", null)]
    [InlineData(null, null)]
    [InlineData("https://", "https://")]
    [InlineData("svc:token@dev.azure.com", "svc:token@dev.azure.com")]
    public void ABlankOrHostlessSideMatchesNothing(string? connectionHostBaseUrl, string? hostAuthority)
    {
        Assert.False(ScmConnectionHostMatch.MatchesAuthority(connectionHostBaseUrl, hostAuthority));
    }
}
