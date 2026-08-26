// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MeisterDev.ProPR.Licensing.Tests;

/// <summary>
///     Covers the anchor this build carries and the two states a test can build without it.
/// </summary>
public sealed class LicenseTrustAnchorTests
{
    // The SHA-256 fingerprint of the licensing root, pinned so that embedding a different certificate cannot
    // pass without this constant changing with it.
    private const string PinnedFingerprint = "87B107B54BD535D2769B82914F26E2B380230BEC8E5B4BF1C0B1D6CC3C69C5FD";

    private const string RootCommonName = "Meister DEV Licensing Root CA";

    [Fact]
    public void TheAnchorThisBuildCarries_HasThePinnedFingerprint()
    {
        using var anchor = LicenseTrustAnchor.FromThisBuild();

        Assert.True(anchor.IsPresent);
        Assert.Equal(PinnedFingerprint, anchor.Certificate.GetCertHashString(HashAlgorithmName.SHA256), ignoreCase: true);
    }

    [Fact]
    public void TheAnchorThisBuildCarries_IsTheSelfSignedLicensingRoot()
    {
        using var anchor = LicenseTrustAnchor.FromThisBuild();

        Assert.True(anchor.IsPresent);
        Assert.Equal(RootCommonName, anchor.Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        Assert.Equal(anchor.Certificate.Subject, anchor.Certificate.Issuer);
    }

    // Every call loads its own certificate from the resource, so one holder disposing an anchor leaves the next
    // read unaffected.
    [Fact]
    public void TheAnchorThisBuildCarries_ReadsAgainAfterAnEarlierAnchorWasDisposed()
    {
        LicenseTrustAnchor.FromThisBuild().Dispose();

        using var anchor = LicenseTrustAnchor.FromThisBuild();

        Assert.True(anchor.IsPresent);
        Assert.Equal(PinnedFingerprint, anchor.Certificate.GetCertHashString(HashAlgorithmName.SHA256), ignoreCase: true);
    }

    [Fact]
    public void AnAbsentAnchor_ReportsNoCertificate()
    {
        using var anchor = LicenseTrustAnchor.None();

        Assert.False(anchor.IsPresent);
        Assert.Null(anchor.Certificate);
    }

    [Fact]
    public void AnAnchorBuiltFromACertificate_CarriesThatCertificate()
    {
        var root = TestLicenseChain.LoadRootCertificate();
        var expectedThumbprint = root.Thumbprint;

        using var anchor = LicenseTrustAnchor.Of(root);

        Assert.True(anchor.IsPresent);
        Assert.Equal(expectedThumbprint, anchor.Certificate.Thumbprint);
    }
}
