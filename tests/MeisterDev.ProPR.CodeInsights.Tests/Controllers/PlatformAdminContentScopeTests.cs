// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.PlatformAdminElevation;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace MeisterDev.ProPR.CodeInsights.Tests.Controllers;

/// <summary>
///     A platform administrator reads code-insight counts of every client, and findings, file hotspots and missed
///     findings only of the clients whose content they may read: clients of the System tenant and clients an active
///     elevation covers.
/// </summary>
public sealed class PlatformAdminContentScopeTests
{
    private static readonly PlatformAdminElevationDto Elevation = new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "operator",
        CodeInsightAudienceHarness.MyTenant,
        "My tenant",
        CodeInsightAudienceHarness.MineB,
        "Client B",
        "TICKET-9",
        DateTimeOffset.UtcNow.AddMinutes(-5),
        DateTimeOffset.UtcNow.AddMinutes(55),
        null,
        null,
        PlatformAdminElevationState.Active,
        0);

    [Fact]
    public async Task FindingsCoverOnlyTheClientsWhoseContentTheAdministratorMayRead()
    {
        var harness = new CodeInsightAudienceHarness(isAdmin: true, elevations: Elevations());

        var result = await harness.CodeQuality.GetFindings();

        Assert.IsType<OkObjectResult>(result);
        CodeInsightAudienceHarness.AssertExactly(
            Assert.Single(harness.RequestedBrowseScopes),
            CodeInsightAudienceHarness.MineA,
            CodeInsightAudienceHarness.MineB);
        Assert.True(PlatformAdminContent.HasPendingReads(harness.HttpContext));
    }

    [Fact]
    public async Task FindingsOfANamedClientWithoutAnElevationAreRefused()
    {
        var harness = new CodeInsightAudienceHarness(isAdmin: true, elevations: Elevations());

        var result = await harness.CodeQuality.GetFindings(clientId: CodeInsightAudienceHarness.SomebodyElses);

        var refusal = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, refusal.StatusCode);
        Assert.Equal(PlatformAdminContent.ElevationRequiredCode, Assert.IsType<ElevationRequiredResponse>(refusal.Value).Code);
        Assert.Empty(harness.RequestedBrowseScopes);
    }

    [Fact]
    public async Task TheConcentrationByFileIsNarrowedAndTheConcentrationByRepositoryIsNot()
    {
        var harness = new CodeInsightAudienceHarness(isAdmin: true, elevations: Elevations());

        var byFile = await harness.CodeQuality.GetConcentration(grain: "file");
        var byRepository = await harness.CodeQuality.GetConcentration(grain: "repository");

        Assert.IsType<OkObjectResult>(byFile);
        Assert.IsType<OkObjectResult>(byRepository);
        CodeInsightAudienceHarness.AssertExactly(
            harness.RequestedRollupScopes[0],
            CodeInsightAudienceHarness.MineA,
            CodeInsightAudienceHarness.MineB);
        CodeInsightAudienceHarness.AssertExactly(
            harness.RequestedRollupScopes[1],
            CodeInsightAudienceHarness.MineA,
            CodeInsightAudienceHarness.MineB,
            CodeInsightAudienceHarness.SomebodyElses);
    }

    [Fact]
    public async Task MissedFindingsCoverOnlyTheClientsWhoseContentTheAdministratorMayRead()
    {
        var harness = new CodeInsightAudienceHarness(isAdmin: true, elevations: Elevations());

        var result = await harness.ReviewerPerformance.GetMisses();

        Assert.IsType<OkObjectResult>(result);
        CodeInsightAudienceHarness.AssertExactly(
            Assert.Single(harness.RequestedBrowseScopes),
            CodeInsightAudienceHarness.MineA,
            CodeInsightAudienceHarness.MineB);
    }

    [Fact]
    public async Task ATenantUserReadsFindingsOfTheirClientsWithoutTheElevationRule()
    {
        var elevations = Elevations();
        var harness = new CodeInsightAudienceHarness(elevations: elevations);

        await harness.CodeQuality.GetFindings();

        CodeInsightAudienceHarness.AssertExactly(
            Assert.Single(harness.RequestedBrowseScopes),
            CodeInsightAudienceHarness.MineA,
            CodeInsightAudienceHarness.MineB);
        await elevations.DidNotReceiveWithAnyArgs().ResolveReadableAsync(default, default);
        Assert.False(PlatformAdminContent.HasPendingReads(harness.HttpContext));
    }

    private static IPlatformAdminElevationService Elevations()
    {
        var elevations = Substitute.For<IPlatformAdminElevationService>();
        elevations.ResolveReadableAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new ContentAccessGrant(CodeInsightAudienceHarness.MineA, "Client A", Guid.Empty, ContentAccessKind.Installation),
                new ContentAccessGrant(
                    CodeInsightAudienceHarness.MineB,
                    "Client B",
                    CodeInsightAudienceHarness.MyTenant,
                    ContentAccessKind.Elevation,
                    Elevation),
            ]);
        elevations.ResolveAsync(
                Arg.Any<Guid>(),
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(CodeInsightAudienceHarness.SomebodyElses)),
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new ContentAccessGrant(
                    CodeInsightAudienceHarness.SomebodyElses,
                    "Somebody Else",
                    CodeInsightAudienceHarness.OtherTenant,
                    ContentAccessKind.Refused),
            ]);
        return elevations;
    }
}
