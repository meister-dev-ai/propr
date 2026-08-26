// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Licensing;

/// <summary>
///     What an admission reports, and what committing or disposing one does to the transaction it was decided
///     in.
/// </summary>
public sealed class StockQuotaAdmissionTests
{
    // A refusal is quoted back to an operator, so it has to carry the number the installation is held to, the
    // number it was measured at, and which side of the licensing rules produced the first one.
    [Fact]
    public async Task ARefusal_CarriesTheCeilingTheCountAndTheSource()
    {
        var limit = LicenseLimitResolution.Of(
            LicenseLimitKey.Runners,
            3,
            LicenseLimitSource.License,
            LicenseStage.Warning);

        await using var admission = StockQuotaAdmission.Refused(limit, 3);

        Assert.False(admission.IsAdmitted);
        Assert.Equal(LicenseLimitKey.Runners, admission.Limit.Key);
        Assert.Equal(LicenseLimitCeiling.Count, admission.Limit.Ceiling);
        Assert.Equal(3, admission.Limit.Count);
        Assert.Equal(LicenseLimitSource.License, admission.Limit.Source);
        Assert.Equal(3, admission.CurrentCount);
    }

    // Nothing is counted under a ceiling that bounds nothing, so the admission reports no count rather than a
    // zero a caller could read as an empty installation.
    [Fact]
    public async Task AnAdmissionUnderAnUnlimitedCeiling_CarriesNoCount()
    {
        var limit = LicenseLimitResolution.Unlimited(
            LicenseLimitKey.Clients,
            LicenseLimitSource.Community,
            LicenseStage.None);

        await using var admission = StockQuotaAdmission.Admitted(limit);

        Assert.True(admission.IsAdmitted);
        Assert.Null(admission.CurrentCount);
    }

    // A caller commits and disposes the same way whether a ceiling applied or not, so an admission that holds
    // no transaction has to accept both calls.
    [Fact]
    public async Task AnAdmissionWithoutATransaction_CommitsAndDisposesWithoutEffect()
    {
        var admission = StockQuotaAdmission.Admitted(
            LicenseLimitResolution.Unlimited(
                LicenseLimitKey.Clients,
                LicenseLimitSource.License,
                LicenseStage.Active));

        await admission.CommitAsync();
        await admission.DisposeAsync();
    }

    [Fact]
    public async Task CommittingAnAdmission_EndsTheTransactionItWasDecidedIn()
    {
        var scope = Substitute.For<IStockQuotaAdmissionScope>();
        var admission = StockQuotaAdmission.Admitted(
            LicenseLimitResolution.Of(LicenseLimitKey.Clients, 5, LicenseLimitSource.License, LicenseStage.Active),
            4,
            scope);

        await admission.CommitAsync();
        await admission.DisposeAsync();

        await scope.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await scope.Received(1).DisposeAsync();
    }
}
