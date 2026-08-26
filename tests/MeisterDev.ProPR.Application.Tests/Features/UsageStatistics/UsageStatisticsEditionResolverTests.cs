// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Services;
using MeisterDev.ProPR.Licensing;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Application.Tests.Features.UsageStatistics;

/// <summary>
///     What the daily snapshot reports as the installation's edition, one case per license lifecycle stage. The
///     resolver reads the edition the licensing state derives rather than deciding for itself, so the stages that
///     keep an installation entitled have to arrive as commercial without the resolver knowing about them.
/// </summary>
public sealed class UsageStatisticsEditionResolverTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly LicenseTrustAnchor _anchor;
    private readonly LicenseTestChain _chain;
    private readonly LicenseVerifier _verifier;

    public UsageStatisticsEditionResolverTests()
    {
        this._chain = LicenseTestChain.Create();
        this._anchor = this._chain.CreateAnchor();
        this._verifier = new LicenseVerifier(this._anchor);
    }

    public void Dispose()
    {
        this._anchor.Dispose();
        this._chain.Dispose();
    }

    [Theory]
    [InlineData(LicenseStage.NotYetValid, UsageStatisticsEdition.Community)]
    [InlineData(LicenseStage.Active, UsageStatisticsEdition.Commercial)]
    [InlineData(LicenseStage.Warning, UsageStatisticsEdition.Commercial)]
    [InlineData(LicenseStage.Grace, UsageStatisticsEdition.Commercial)]
    [InlineData(LicenseStage.Reverted, UsageStatisticsEdition.Community)]
    public async Task TheReportedEdition_FollowsTheLicenseStage(LicenseStage stage, UsageStatisticsEdition expected)
    {
        var state = this.StateIn(stage);
        var sut = UsageStatisticsTestDoubles.EditionResolver(state.Edition);

        Assert.Equal(stage, state.Stage);
        Assert.Equal(expected, await sut.ResolveAsync());
    }

    [Fact]
    public async Task AnInstallationWithNoLicenseOnFile_ReportsCommunity()
    {
        var sut = UsageStatisticsTestDoubles.EditionResolver(LicenseState.None().Edition);

        Assert.Equal(UsageStatisticsEdition.Community, await sut.ResolveAsync());
    }

    // A deployment without a database registers no licensing module, so there is no license to read and nothing
    // to report but the community edition.
    [Fact]
    public async Task WithoutTheLicensingModule_ReportsCommunity()
    {
        Assert.Equal(UsageStatisticsEdition.Community, await new UsageStatisticsEditionResolver().ResolveAsync());
    }

    private LicenseState StateIn(LicenseStage stage)
    {
        var (notBefore, expiresAt, evaluatedAt) = stage switch
        {
            LicenseStage.NotYetValid => (Now.AddDays(30), Now.AddYears(1), Now),
            LicenseStage.Active => (Now.AddDays(-30), Now.AddYears(1), Now),
            LicenseStage.Warning => (Now.AddDays(-30), Now.AddDays(10), Now),
            LicenseStage.Grace => (Now.AddDays(-30), Now.AddDays(-1), Now),
            _ => (Now.AddDays(-30), Now.AddDays(-15), Now),
        };

        var verification = this._verifier.Verify(
            this._chain.Sign(LicenseTestChain.ClaimsFor(notBefore, expiresAt)),
            evaluatedAt);
        Assert.True(verification.IsVerified, verification.FailureDetail);

        return LicenseState.Verified(verification.License, evaluatedAt, Now.AddDays(-30), null);
    }
}
