// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Licensing;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Application.Tests.Features.Licensing;

/// <summary>
///     Where a license term places the installation, and what that stage amounts to. The boundaries are asserted
///     a second either side of each one, because an off-by-one there either takes capabilities away early or
///     keeps granting them after the grace window has closed.
/// </summary>
public sealed class LicenseStateStageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NotBefore = Now.AddYears(-1);
    private static readonly DateTimeOffset ExpiresAt = Now.AddDays(60);

    private static readonly DateTimeOffset WarningStartsAt = ExpiresAt - LicenseState.WarningWindow;
    private static readonly DateTimeOffset GraceEndsAt = ExpiresAt + LicenseState.GraceWindow;

    private readonly LicenseTrustAnchor _anchor;
    private readonly LicenseTestChain _chain;
    private readonly LicenseVerifier _verifier;

    public LicenseStateStageTests()
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

    /// <summary>Which boundary an evaluation instant is being placed against.</summary>
    public enum Boundary
    {
        /// <summary>The first moment the license is in force.</summary>
        TermStart = 0,

        /// <summary>The first moment the installation is reported as approaching expiry.</summary>
        WarningStart = 1,

        /// <summary>The moment the term ends.</summary>
        Expiry = 2,

        /// <summary>The moment the grace window ends.</summary>
        GraceEnd = 3,
    }

    [Theory]
    [InlineData(Boundary.TermStart, -1, LicenseStage.NotYetValid)]
    [InlineData(Boundary.TermStart, 0, LicenseStage.Active)]
    [InlineData(Boundary.WarningStart, -1, LicenseStage.Active)]
    [InlineData(Boundary.WarningStart, 0, LicenseStage.Warning)]
    [InlineData(Boundary.Expiry, -1, LicenseStage.Warning)]
    [InlineData(Boundary.Expiry, 0, LicenseStage.Grace)]
    [InlineData(Boundary.GraceEnd, -1, LicenseStage.Grace)]
    [InlineData(Boundary.GraceEnd, 0, LicenseStage.Reverted)]
    public void TheStage_ChangesExactlyAtTheBoundary(Boundary boundary, int offsetSeconds, LicenseStage expectedStage)
    {
        var state = this.StateAt(InstantAt(boundary).AddSeconds(offsetSeconds));

        Assert.Equal(expectedStage, state.Stage);
    }

    // Warning and grace are still entitled states: the installation keeps every capability its license names
    // while it is being renewed. Only the two stages outside the term plus the grace window fall back.
    [Theory]
    [InlineData(LicenseStage.NotYetValid, InstallationEdition.Community)]
    [InlineData(LicenseStage.Active, InstallationEdition.Commercial)]
    [InlineData(LicenseStage.Warning, InstallationEdition.Commercial)]
    [InlineData(LicenseStage.Grace, InstallationEdition.Commercial)]
    [InlineData(LicenseStage.Reverted, InstallationEdition.Community)]
    public void TheEdition_FollowsTheStage(LicenseStage stage, InstallationEdition expectedEdition)
    {
        var state = this.StateAt(InstantIn(stage));

        Assert.Equal(stage, state.Stage);
        Assert.Equal(expectedEdition, state.Edition);
    }

    // A state built outside the factories would name no license and read as the community edition, so no
    // assembly can construct one: the constructor and every setter stay inside this one.
    [Fact]
    public void ALicenseState_IsConstructedOnlyThroughTheFactories()
    {
        Assert.Empty(typeof(LicenseState).GetConstructors());

        var settableFromOutside = typeof(LicenseState)
            .GetProperties()
            .Where(property => property.SetMethod is { IsPublic: true })
            .Select(property => property.Name);

        Assert.Empty(settableFromOutside);
    }

    // A resolution names a ceiling and carries the number that ceiling stands for. A caller able to construct
    // one directly could name a count and carry none, which every consumer of a count would then have to guard.
    [Fact]
    public void ALimitResolution_IsConstructedOnlyThroughTheFactories()
    {
        Assert.Empty(typeof(LicenseLimitResolution).GetConstructors());

        var settableFromOutside = typeof(LicenseLimitResolution)
            .GetProperties()
            .Where(property => property.SetMethod is { IsPublic: true })
            .Select(property => property.Name);

        Assert.Empty(settableFromOutside);
    }

    // A record supports with, which copies an instance and reassigns a member through its setter. A setter
    // reachable from outside the type therefore produces the state closing the constructor was meant to prevent:
    // a ceiling that names a count while carrying none, or a count under a ceiling that names none. Every setter
    // on the type is private for that reason.
    [Fact]
    public void ALimitResolution_HasNoSetterReachableOutsideTheType()
    {
        var settableOutsideTheType = typeof(LicenseLimitResolution)
            .GetProperties()
            .Where(property => property.SetMethod is { IsPrivate: false })
            .Select(property => property.Name);

        Assert.Empty(settableOutsideTheType);
    }

    // A term can sit close enough to either end of the representable range that shifting it by a window
    // leaves that range. The stage is decided by the distance between the two instants, so a term the reader
    // accepts is answered for rather than raised on.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(29)]
    public void ATermAtTheStartOfTheRepresentableRange_ResolvesAStageInsteadOfThrowing(int daysAfterTheStart)
    {
        var notBefore = DateTimeOffset.MinValue;
        var claims = LicenseTestChain.ClaimsFor(notBefore, notBefore.AddDays(daysAfterTheStart + 1));

        var stage = LicenseState.StageFor(claims, notBefore.AddDays(daysAfterTheStart));

        Assert.NotEqual(LicenseStage.None, stage);
    }

    [Fact]
    public void AStateWithNoLicenseOnFile_HasNoStageAndNoBoundaries()
    {
        var state = LicenseState.None();

        Assert.Equal(LicenseStage.None, state.Stage);
        Assert.Equal(InstallationEdition.Community, state.Edition);
        Assert.Null(state.NotBefore);
        Assert.Null(state.ExpiresAt);
        Assert.Null(state.WarningStartsAt);
        Assert.Null(state.GraceEndsAt);
        Assert.Null(state.DaysRemaining);
    }

    [Fact]
    public void AStoredDocumentThatDidNotVerify_HasNoStage()
    {
        var state = LicenseState.Invalid(LicenseFailureReason.UntrustedSigner, "detail", Now, null);

        Assert.Equal(LicenseStage.None, state.Stage);
        Assert.Equal(InstallationEdition.Community, state.Edition);
    }

    [Fact]
    public void AStoredValueThatCouldNotBeRead_HasNoStage()
    {
        var state = LicenseState.Unreadable(Now, null);

        Assert.Equal(LicenseStage.None, state.Stage);
        Assert.Equal(InstallationEdition.Community, state.Edition);
    }

    [Fact]
    public void TheBoundaryInstants_AreTheTermPlusTheTwoWindows()
    {
        var state = this.StateAt(Now);

        Assert.Equal(NotBefore, state.NotBefore);
        Assert.Equal(ExpiresAt, state.ExpiresAt);
        Assert.Equal(WarningStartsAt, state.WarningStartsAt);
        Assert.Equal(GraceEndsAt, state.GraceEndsAt);
    }

    // The count is what a panel puts in front of an operator, so it has to say how long the installation stays
    // entitled: to the end of the term while it runs, and to the end of the grace window once it has ended.
    [Theory]
    [InlineData(LicenseStage.Active, 60)]
    [InlineData(LicenseStage.Warning, 30)]
    [InlineData(LicenseStage.Grace, 14)]
    [InlineData(LicenseStage.Reverted, 0)]
    public void TheDaysRemaining_CountToTheEndOfTheEntitlement(LicenseStage stage, int expectedDays)
    {
        var state = this.StateAt(InstantIn(stage));

        Assert.Equal(expectedDays, state.DaysRemaining);
    }

    [Fact]
    public void TheDaysRemaining_CountAPartialDayAsAWholeOne()
    {
        var state = this.StateAt(ExpiresAt.AddHours(-6));

        Assert.Equal(1, state.DaysRemaining);
    }

    [Fact]
    public void TheDaysRemaining_AreNotReportedBeforeTheTermBegins()
    {
        var state = this.StateAt(NotBefore.AddDays(-1));

        Assert.Equal(LicenseStage.NotYetValid, state.Stage);
        Assert.Null(state.DaysRemaining);
    }

    // A term shorter than the warning window has no stretch where the expiry is far away, so it opens in the
    // warning stage. The installation is entitled from the first moment either way. The reported warning instant
    // is clamped to the start of the term, because exp minus the window falls before the license was in force.
    [Fact]
    public void ATermShorterThanTheWarningWindow_OpensInTheWarningStageAndReportsTheTermStartAsTheWarningInstant()
    {
        var state = this.StateAt(Now, notBefore: Now, expiresAt: Now.AddDays(7));

        Assert.Equal(LicenseStage.Warning, state.Stage);
        Assert.Equal(InstallationEdition.Commercial, state.Edition);
        Assert.Equal(Now, state.NotBefore);
        Assert.Equal(Now, state.WarningStartsAt);
    }

    [Fact]
    public void ATermLongerThanTheWarningWindow_ReportsTheWarningInstantInsideTheTerm()
    {
        var state = this.StateAt(Now);

        Assert.Equal(NotBefore, state.NotBefore);
        Assert.Equal(WarningStartsAt, state.WarningStartsAt);
        Assert.True(state.WarningStartsAt > state.NotBefore);
    }

    // The term status the verifier reports is the plain reading of the two claims. The stage sits above it and
    // does not change what it says, so a license in grace still reports its term as ended.
    [Fact]
    public void TheTermStatus_IsUnchangedByTheStage()
    {
        var inGrace = this.StateAt(ExpiresAt.AddDays(1));
        var inWarning = this.StateAt(ExpiresAt.AddDays(-1));

        Assert.Equal(LicenseStage.Grace, inGrace.Stage);
        Assert.Equal(LicenseTermStatus.Expired, inGrace.TermStatus);
        Assert.Equal(LicenseStage.Warning, inWarning.Stage);
        Assert.Equal(LicenseTermStatus.Active, inWarning.TermStatus);
    }

    private static DateTimeOffset InstantAt(Boundary boundary)
    {
        return boundary switch
        {
            Boundary.TermStart => NotBefore,
            Boundary.WarningStart => WarningStartsAt,
            Boundary.Expiry => ExpiresAt,
            _ => GraceEndsAt,
        };
    }

    /// <summary>An instant well inside the requested stage, at a whole number of days from a boundary.</summary>
    private static DateTimeOffset InstantIn(LicenseStage stage)
    {
        return stage switch
        {
            LicenseStage.NotYetValid => NotBefore.AddDays(-1),
            LicenseStage.Active => ExpiresAt.AddDays(-60),
            LicenseStage.Warning => ExpiresAt.AddDays(-30),
            LicenseStage.Grace => ExpiresAt,
            _ => GraceEndsAt.AddDays(1),
        };
    }

    /// <summary>
    ///     A license state over a document this test's chain signed and the product's own verifier accepted, so
    ///     the claims the stage is derived from are the ones a stored document carries.
    /// </summary>
    private LicenseState StateAt(
        DateTimeOffset evaluatedAt,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expiresAt = null)
    {
        var compactLicense = this._chain.Sign(LicenseTestChain.ClaimsFor(notBefore ?? NotBefore, expiresAt ?? ExpiresAt));
        var verification = this._verifier.Verify(compactLicense, evaluatedAt);
        Assert.True(verification.IsVerified, verification.FailureDetail);

        return LicenseState.Verified(verification.License, evaluatedAt, Now.AddYears(-1), null);
    }
}
