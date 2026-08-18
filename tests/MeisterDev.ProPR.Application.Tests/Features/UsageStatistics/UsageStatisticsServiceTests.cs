// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Services;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Support;
using MeisterDev.ProPR.Application.Support;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.UsageStatistics;

public sealed class UsageStatisticsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ACommunityInstallation_ReportsAnUnlockedControl()
    {
        var settings = await CreateService(UsageStatisticsTestDoubles.EnabledState(Now), InstallationEdition.Community)
            .Service.GetSettingsAsync();

        Assert.False(settings.ManagedByLicense);
        Assert.True(settings.Enabled);
        Assert.Equal(UsageStatisticsEdition.Community, settings.Edition);
    }

    // The control stays visible under a license rather than being hidden, so administrators can still see what
    // the installation sends.
    [Fact]
    public async Task ACommercialInstallation_ReportsALockedControlThatIsStillOn()
    {
        var state = UsageStatisticsTestDoubles.EnabledState(Now) with { CommunityOptIn = false };

        var settings = await CreateService(state, InstallationEdition.Commercial).Service.GetSettingsAsync();

        Assert.True(settings.ManagedByLicense);
        Assert.True(settings.Enabled);
        Assert.False(settings.CommunityOptIn);
    }

    [Fact]
    public async Task ACommercialInstallation_RefusesToChangeTheToggle()
    {
        var context = CreateService(UsageStatisticsTestDoubles.EnabledState(Now), InstallationEdition.Commercial);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Service.SetCommunityOptInAsync(false, null));

        Assert.Equal(UsageStatisticsService.ManagedByLicenseMessage, exception.Message);
        await context.Store.DidNotReceiveWithAnyArgs().SetCommunityOptInAsync(default, null, default);
    }

    [Fact]
    public async Task ACommunityInstallation_StoresTheToggleAndTheActor()
    {
        var actor = Guid.NewGuid();
        var context = CreateService(UsageStatisticsTestDoubles.EnabledState(Now), InstallationEdition.Community);
        context.Store.SetCommunityOptInAsync(false, actor, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(UsageStatisticsTestDoubles.EnabledState(Now) with { CommunityOptIn = false }));

        var settings = await context.Service.SetCommunityOptInAsync(false, actor);

        Assert.False(settings.CommunityOptIn);
        Assert.False(settings.Enabled);
    }

    // A commercial installation shows no notice, so an administrator signing in is what opens the gate.
    [Fact]
    public async Task AnAdministratorSigningIntoACommercialInstallation_OpensTheGate()
    {
        var context = CreateService(
            UsageStatisticsTestDoubles.EnabledState(Now) with { ConsentGateSatisfiedAt = null },
            InstallationEdition.Commercial);

        await context.Service.RecordAdministratorSignInAsync();

        await context.Store.Received(1).RecordConsentGateSatisfiedAsync(Arg.Any<CancellationToken>());
    }

    // A community installation is gated on the notice rendering instead, which requires the explanation to
    // have been displayed rather than only that someone signed in.
    [Fact]
    public async Task AnAdministratorSigningIntoACommunityInstallation_LeavesTheGateShut()
    {
        var context = CreateService(
            UsageStatisticsTestDoubles.EnabledState(Now) with { ConsentGateSatisfiedAt = null },
            InstallationEdition.Community);

        await context.Service.RecordAdministratorSignInAsync();

        await context.Store.DidNotReceiveWithAnyArgs().RecordConsentGateSatisfiedAsync(default);
    }

    [Fact]
    public async Task AnAlreadyOpenGate_IsNotWrittenAgainOnEverySignIn()
    {
        var context = CreateService(UsageStatisticsTestDoubles.EnabledState(Now), InstallationEdition.Commercial);

        await context.Service.RecordAdministratorSignInAsync();

        await context.Store.DidNotReceiveWithAnyArgs().RecordConsentGateSatisfiedAsync(default);
    }

    [Fact]
    public async Task ADismissedNotice_StopsBeingRequiredWithoutChangingWhatIsSent()
    {
        var dismissed = UsageStatisticsTestDoubles.EnabledState(Now) with { NoticeDismissedAt = Now };
        var context = CreateService(UsageStatisticsTestDoubles.EnabledState(Now), InstallationEdition.Community);
        context.Store.RecordNoticeDismissedAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(dismissed));

        var settings = await context.Service.DismissNoticeAsync();

        Assert.False(settings.NoticeRequired);
        Assert.True(settings.Enabled);
    }

    // A commercial installation never shows the notice; the license agreement covers it.
    [Fact]
    public async Task ACommercialInstallation_NeverRequiresTheNotice()
    {
        var settings = await CreateService(UsageStatisticsTestDoubles.EnabledState(Now), InstallationEdition.Commercial)
            .Service.GetSettingsAsync();

        Assert.False(settings.NoticeRequired);
    }

    // The preview is the payload itself rather than a description of it, so an operator reads what would be
    // sent.
    [Fact]
    public async Task ThePreview_IsTheExactPayloadAndItsDestination()
    {
        var context = CreateService(UsageStatisticsTestDoubles.EnabledState(Now), InstallationEdition.Community);

        var preview = await context.Service.BuildPreviewAsync();

        Assert.Equal(UsageStatisticsContract.PingEndpoint, preview.Endpoint);
        Assert.Equal("application/json", preview.ContentType);

        using var parsed = JsonDocument.Parse(preview.Payload);
        Assert.Equal(
            UsageStatisticsContract.SchemaVersion,
            parsed.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("community", parsed.RootElement.GetProperty("edition").GetString());
    }

    // Previewing is a local operation. An installation that has never sent, and one that is switched off, can
    // both show an administrator the payload.
    [Fact]
    public async Task ThePreview_WorksInAnInstallationThatSendsNothing()
    {
        var off = UsageStatisticsTestDoubles.EnabledState(Now) with
        {
            CommunityOptIn = false,
            ConsentGateSatisfiedAt = null,
        };

        var preview = await CreateService(off, InstallationEdition.Community).Service.BuildPreviewAsync();

        Assert.False(string.IsNullOrWhiteSpace(preview.Payload));
    }

    [Fact]
    public async Task ANewerReleaseReportedByTheReceiver_RaisesTheUpdateFlag()
    {
        var state = UsageStatisticsTestDoubles.EnabledState(Now) with
        {
            LatestVersion = "1.0.0.alpha.0050",
            UpdateInformationReceivedAt = Now,
        };

        var settings = await CreateService(state, InstallationEdition.Community, "1.0.0.alpha.0049")
            .Service.GetSettingsAsync();

        Assert.True(settings.Update.UpdateAvailable);
        Assert.Equal("1.0.0.alpha.0050", settings.Update.LatestVersion);
    }

    [Fact]
    public async Task TheCurrentRelease_RaisesNoUpdateFlag()
    {
        var state = UsageStatisticsTestDoubles.EnabledState(Now) with { LatestVersion = "1.0.0.alpha.0049" };

        var settings = await CreateService(state, InstallationEdition.Community, "1.0.0.alpha.0049")
            .Service.GetSettingsAsync();

        Assert.False(settings.Update.UpdateAvailable);
    }

    // A local build has no release number to compare against, so it must not report an available update.
    // Every developer machine would otherwise show the update badge.
    [Fact]
    public async Task AnUnstampedDevelopmentBuild_IsNotReportedAsOutOfDate()
    {
        var state = UsageStatisticsTestDoubles.EnabledState(Now) with { LatestVersion = "1.0.0.alpha.0050" };

        var settings = await CreateService(
                state,
                InstallationEdition.Community,
                AssemblyProductVersionProvider.UnstampedVersion)
            .Service.GetSettingsAsync();

        Assert.False(settings.Update.UpdateAvailable);
    }

    // Nothing received means nothing rendered. An installation with usage statistics off shows no badge and no
    // error.
    [Fact]
    public async Task AnInstallationThatHasNeverPinged_ReportsNoUpdateInformation()
    {
        var settings = await CreateService(UsageStatisticsTestDoubles.EnabledState(Now), InstallationEdition.Community)
            .Service.GetSettingsAsync();

        Assert.Null(settings.Update.LatestVersion);
        Assert.Empty(settings.Update.Advisories);
        Assert.Null(settings.Update.ReceivedAt);
        Assert.False(settings.Update.UpdateAvailable);
    }

    private static (UsageStatisticsService Service, IUsageStatisticsStateStore Store) CreateService(
        UsageStatisticsState state,
        InstallationEdition edition,
        string version = "1.0.0.alpha.0049")
    {
        var store = Substitute.For<IUsageStatisticsStateStore>();
        store.GetAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(state));
        store.RecordConsentGateSatisfiedAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(state));
        store.RecordNoticeDismissedAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(state));

        var timeProvider = new FakeTimeProvider(Now);
        var productVersion = UsageStatisticsTestDoubles.ProductVersion(version);
        var editionResolver = UsageStatisticsTestDoubles.EditionResolver(edition);

        var builder = new UsageStatisticsSnapshotBuilder(
            UsageStatisticsTestDoubles.CountSource(new UsageStatisticsCounts(1, 0, 0, null, null)),
            productVersion,
            timeProvider);

        return (new UsageStatisticsService(store, builder, editionResolver, productVersion), store);
    }
}
