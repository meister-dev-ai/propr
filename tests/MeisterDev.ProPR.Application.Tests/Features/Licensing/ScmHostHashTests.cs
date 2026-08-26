// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Support;

namespace MeisterDev.ProPR.Application.Tests.Features.Licensing;

/// <summary>
///     Turning configured SCM connection URLs into the salted hashes the profile carries. The hashes have to
///     match across the replicas of one installation and across the installations of one license. Different
///     license identifiers produce different hash namespaces.
/// </summary>
public sealed class ScmHostHashTests
{
    private const string LicenseId = "f0a1a0d6-1f2b-4f7a-9c3f-2b4d6e8a1c05";

    [Theory]
    [InlineData("https://dev.azure.com/northwind", "https://dev.azure.com")]
    [InlineData("HTTPS://DEV.AZURE.COM/Northwind", "https://dev.azure.com")]
    [InlineData("https://dev.azure.com/", "https://dev.azure.com")]
    [InlineData("https://git.example.com:443/group", "https://git.example.com")]
    [InlineData("http://git.example.com:80", "http://git.example.com")]
    [InlineData("https://git.example.com:8443/group", "https://git.example.com:8443")]
    [InlineData("https://[2001:db8::1]/group", "https://[2001:db8::1]")]
    [InlineData("https://[2001:db8::1]:8443/group", "https://[2001:db8::1]:8443")]
    [InlineData("  https://git.example.com/group  ", "https://git.example.com")]
    public void Normalizing_KeepsTheSchemeHostAndNonDefaultPortAndNothingElse(string hostBaseUrl, string expected)
    {
        Assert.Equal(expected, ScmHostHash.Normalize(hostBaseUrl));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dev.azure.com/northwind")]
    public void AValueWithNoHostToRead_IsLeftOut(string? hostBaseUrl)
    {
        Assert.Null(ScmHostHash.Normalize(hostBaseUrl));
    }

    // Two clients configured against one host are one host, whatever organization path each connection carries.
    [Fact]
    public void ConnectionsToOneHostUnderDifferentPaths_ProduceOneHash()
    {
        var hashes = ScmHostHash.ComputeSet(
            ["https://dev.azure.com/northwind", "https://dev.azure.com/contoso"],
            LicenseId);

        Assert.Single(hashes);
    }

    // Replicas of one installation, and separate installations of one license, have to produce values that can
    // be compared with each other.
    [Fact]
    public void TheSameHostsUnderTheSameLicense_HashIdentically()
    {
        var first = ScmHostHash.ComputeSet(["https://dev.azure.com/northwind"], LicenseId);
        var second = ScmHostHash.ComputeSet(["https://dev.azure.com/northwind"], LicenseId);

        Assert.Equal(first, second);
    }

    // The license identifier scopes comparison. It is not a secret and provides no protection against candidate
    // host testing by a party that also has an individual component hash.
    [Fact]
    public void TheSameHostsUnderADifferentLicense_HashDifferently()
    {
        var first = ScmHostHash.ComputeSet(["https://dev.azure.com/northwind"], LicenseId);
        var second = ScmHostHash.ComputeSet(["https://dev.azure.com/northwind"], "9d2c1e30-8a1f-4d20-b9a3-0c7f5a6e2d11");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TheHashes_CarryNothingOfThePlainHost()
    {
        var hash = Assert.Single(ScmHostHash.ComputeSet(["https://dev.azure.com/northwind"], LicenseId));

        Assert.DoesNotContain("azure", hash, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
    }

    [Fact]
    public void TheSet_IsSortedAndFreeOfDuplicates()
    {
        var hashes = ScmHostHash.ComputeSet(
            ["https://z.example.com", "https://a.example.com", "https://z.example.com/other"],
            LicenseId);

        Assert.Equal(2, hashes.Count);
        Assert.Equal(hashes.OrderBy(hash => hash, StringComparer.Ordinal), hashes);
    }

    [Fact]
    public void NoConfiguredConnection_ProducesAnEmptySetRatherThanAnAbsentOne()
    {
        Assert.Empty(ScmHostHash.ComputeSet([], LicenseId));
    }
}
