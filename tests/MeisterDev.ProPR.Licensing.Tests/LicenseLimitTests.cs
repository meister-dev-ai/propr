// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Licensing.Tests;

public sealed class LicenseLimitTests
{
    // The three states drive different decisions, so none of them may collapse into another: an unstated
    // limit constrains nothing, an unlimited one was stated and set to no ceiling, and zero allows nothing.
    [Fact]
    public void Absent_Unlimited_AndACountAreThreeDistinctStates()
    {
        var absent = LicenseLimit.Absent;
        var unlimited = LicenseLimit.Unlimited;
        var zero = LicenseLimit.Of(0);

        Assert.True(absent.IsAbsent);
        Assert.True(unlimited.IsUnlimited);
        Assert.True(zero.HasCount);
        Assert.NotEqual(absent, unlimited);
        Assert.NotEqual(absent, zero);
        Assert.NotEqual(unlimited, zero);
        Assert.Equal(0, zero.Count);
    }

    [Fact]
    public void AnUnstatedLimit_IsTheDefaultValueOfTheType()
    {
        Assert.Equal(LicenseLimit.Absent, default(LicenseLimit));
        Assert.True(LicenseLimits.None.IsEmpty);
        Assert.True(LicenseLimits.None.AuthorsPerMonth.IsAbsent);
    }

    [Fact]
    public void ReadingACountThatIsNotThere_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => LicenseLimit.Absent.Count);
        Assert.Throws<InvalidOperationException>(() => LicenseLimit.Unlimited.Count);
        Assert.False(LicenseLimit.Unlimited.TryGetCount(out _));
    }

    [Fact]
    public void ANegativeCount_CannotBeBuilt()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LicenseLimit.Of(-1));
    }

    [Fact]
    public void EachStateSurvivesSigningAndReading()
    {
        var claims = TestLicenseChain.GoldenClaims() with
        {
            Limits = new LicenseLimits
            {
                AuthorsPerMonth = LicenseLimit.Of(250),
                Clients = LicenseLimit.Unlimited,
                Runners = LicenseLimit.Absent,
                ConcurrentReviews = LicenseLimit.Of(0),
            },
        };

        using var publicKey = TestLicenseChain.LoadSignerPublicKey();
        var result = LicenseReader.Read(TestLicenseChain.Sign(claims), publicKey);

        Assert.True(result.IsSuccess, result.FailureDetail);
        using var license = result.License;
        Assert.Equal(LicenseLimit.Of(250), license.Claims.Limits.AuthorsPerMonth);
        Assert.Equal(LicenseLimit.Unlimited, license.Claims.Limits.Clients);
        Assert.Equal(LicenseLimit.Absent, license.Claims.Limits.Runners);
        Assert.Equal(LicenseLimit.Of(0), license.Claims.Limits.ConcurrentReviews);
    }

    [Fact]
    public void AnOmittedLimitsObject_LeavesEveryLimitAbsent()
    {
        var compact = RawLicense.Sign(RawLicense.HeaderJson(), RawLicense.PayloadJson(RawLicense.AcceptedClaims()));

        using var publicKey = TestLicenseChain.LoadSignerPublicKey();
        var result = LicenseReader.Read(compact, publicKey);

        Assert.True(result.IsSuccess, result.FailureDetail);
        using var license = result.License;
        Assert.True(license.Claims.Limits.IsEmpty);
    }

    [Fact]
    public void AnUnknownLimitMember_IsIgnored()
    {
        var compact = RawLicense.SignWithClaim("limits", "{\"clients\":3,\"seatsPerTeam\":9}");

        using var publicKey = TestLicenseChain.LoadSignerPublicKey();
        var result = LicenseReader.Read(compact, publicKey);

        Assert.True(result.IsSuccess, result.FailureDetail);
        using var license = result.License;
        Assert.Equal(LicenseLimit.Of(3), license.Claims.Limits.Clients);
        Assert.True(license.Claims.Limits.Runners.IsAbsent);
    }

    [Theory]
    [InlineData("{\"authorsPerMonth\":-1}")]
    [InlineData("{\"authorsPerMonth\":2.5}")]
    [InlineData("{\"clients\":\"unbounded\"}")]
    [InlineData("{\"clients\":\"Unlimited\"}")]
    [InlineData("{\"runners\":true}")]
    [InlineData("{\"runners\":null}")]
    [InlineData("{\"concurrentReviews\":[]}")]
    [InlineData("\"unlimited\"")]
    [InlineData("null")]
    public void ALimitOfTheWrongShape_IsMalformed(string limitsJson)
    {
        using var publicKey = TestLicenseChain.LoadSignerPublicKey();

        var result = LicenseReader.Read(RawLicense.SignWithClaim("limits", limitsJson), publicKey);

        Assert.False(result.IsSuccess);
        Assert.Equal(LicenseFailureReason.Malformed, result.FailureReason);
    }
}
