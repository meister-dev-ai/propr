// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.UsageStatistics.Dtos;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Support;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Support;

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Services;

/// <summary>
///     The operations the administration UI performs on anonymous usage statistics.
///     <para>
///         This class has no transport dependency. Reading the settings, previewing the payload and dismissing
///         the notice are local operations, so the payload preview works in an installation that has never sent
///         anything.
///     </para>
/// </summary>
public sealed class UsageStatisticsService(
    IUsageStatisticsStateStore stateStore,
    UsageStatisticsSnapshotBuilder snapshotBuilder,
    UsageStatisticsEditionResolver editionResolver,
    IProductVersionProvider productVersionProvider,
    UsageStatisticsSender? sender = null)
{
    /// <summary>The message shown when a commercial license governs the control.</summary>
    public const string ManagedByLicenseMessage =
        "Anonymous usage statistics are managed by your commercial license.";

    /// <summary>Returns the current settings, including the last send outcome and any update information.</summary>
    public async Task<UsageStatisticsSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var state = await stateStore.GetAsync(cancellationToken);
        var edition = await editionResolver.ResolveAsync(cancellationToken);

        return this.ToDto(state, edition);
    }

    /// <summary>
    ///     Stores the community toggle.
    ///     <para>
    ///         Refused while a license is installed. The control stays visible in that state rather than being
    ///         hidden, so administrators can still see what the installation sends.
    ///     </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">A commercial license governs the setting.</exception>
    public async Task<UsageStatisticsSettingsDto> SetCommunityOptInAsync(
        bool optIn,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var edition = await editionResolver.ResolveAsync(cancellationToken);
        if (edition == UsageStatisticsEdition.Commercial)
        {
            throw new InvalidOperationException(ManagedByLicenseMessage);
        }

        var state = await stateStore.SetCommunityOptInAsync(optIn, actorUserId, cancellationToken);
        return this.ToDto(state, edition);
    }

    /// <summary>
    ///     Records that the consent notice was shown to an administrator, which opens the gate in community
    ///     installations.
    /// </summary>
    public async Task<UsageStatisticsSettingsDto> RecordNoticeShownAsync(CancellationToken cancellationToken = default)
    {
        var state = await stateStore.RecordConsentGateSatisfiedAsync(cancellationToken);
        var edition = await editionResolver.ResolveAsync(cancellationToken);

        return this.ToDto(state, edition);
    }

    /// <summary>Hides the notice for this installation. Dismissal changes nothing about what is sent.</summary>
    public async Task<UsageStatisticsSettingsDto> DismissNoticeAsync(CancellationToken cancellationToken = default)
    {
        var state = await stateStore.RecordNoticeDismissedAsync(cancellationToken);
        var edition = await editionResolver.ResolveAsync(cancellationToken);

        return this.ToDto(state, edition);
    }

    /// <summary>
    ///     Opens the gate on an administrator's sign-in, for installations that show no consent notice.
    ///     <para>
    ///         Community installations are gated on the notice rendering instead, which requires the
    ///         explanation to have been displayed rather than only that an administrator signed in.
    ///     </para>
    /// </summary>
    public async Task RecordAdministratorSignInAsync(CancellationToken cancellationToken = default)
    {
        var edition = await editionResolver.ResolveAsync(cancellationToken);
        if (edition != UsageStatisticsEdition.Commercial)
        {
            return;
        }

        var state = await stateStore.GetAsync(cancellationToken);
        if (state.IsConsentGateSatisfied)
        {
            return;
        }

        await stateStore.RecordConsentGateSatisfiedAsync(cancellationToken);
    }

    /// <summary>
    ///     Runs a send cycle now instead of waiting for the daily one.
    ///     <para>
    ///         This runs the same cycle as the background loop, under the same rules: an installation that is
    ///         off or has not shown the notice sends nothing, and one that already sent today returns not due
    ///         rather than sending a second snapshot. The documented limit of one snapshot a day still applies.
    ///     </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The installation cannot send at all.</exception>
    public async Task<UsageStatisticsSendResultDto> SendNowAsync(CancellationToken cancellationToken = default)
    {
        if (sender is null)
        {
            throw new InvalidOperationException("This installation has no usage statistics sender configured.");
        }

        var result = await sender.SendIfDueAsync(cancellationToken);

        return new UsageStatisticsSendResultDto(result.Decision, await this.GetSettingsAsync(cancellationToken));
    }

    /// <summary>Builds the payload the next ping would carry, without sending anything.</summary>
    public async Task<UsageStatisticsPreviewDto> BuildPreviewAsync(CancellationToken cancellationToken = default)
    {
        var state = await stateStore.GetAsync(cancellationToken);
        var edition = await editionResolver.ResolveAsync(cancellationToken);
        var snapshot = await snapshotBuilder.BuildAsync(state, edition, cancellationToken);

        return new UsageStatisticsPreviewDto(
            UsageStatisticsContract.PingEndpoint,
            "application/json",
            UsageStatisticsContract.Serialize(snapshot),
            UsageStatisticsContract.PayloadDocumentationUrl);
    }

    private UsageStatisticsSettingsDto ToDto(UsageStatisticsState state, UsageStatisticsEdition edition)
    {
        var managedByLicense = edition == UsageStatisticsEdition.Commercial;

        return new UsageStatisticsSettingsDto(
            edition,
            state.IsSendingEnabled(edition),
            state.CommunityOptIn,
            managedByLicense,
            state.IsConsentGateSatisfied,
            !managedByLicense && state.NoticeDismissedAt is null,
            state.LastAttemptAt,
            state.LastAttemptSucceeded,
            state.LastAttemptDetail,
            state.LastSuccessAt,
            UsageStatisticsContract.PingEndpoint,
            UsageStatisticsContract.PayloadDocumentationUrl,
            UsageStatisticsContract.PrivacyContact,
            this.ToUpdateStatus(state));
    }

    private UsageStatisticsUpdateStatusDto ToUpdateStatus(UsageStatisticsState state)
    {
        var currentVersion = productVersionProvider.Version;

        // An unstamped development build has no release number to compare against, so it never reports an
        // available update. Every local build would otherwise show the update badge.
        var comparable = !string.Equals(
            currentVersion,
            AssemblyProductVersionProvider.UnstampedVersion,
            StringComparison.Ordinal);

        var updateAvailable = comparable
                              && !string.IsNullOrWhiteSpace(state.LatestVersion)
                              && !string.Equals(state.LatestVersion, currentVersion, StringComparison.OrdinalIgnoreCase);

        return new UsageStatisticsUpdateStatusDto(
            currentVersion,
            state.LatestVersion,
            updateAvailable,
            state.Advisories,
            state.UpdateInformationReceivedAt);
    }
}
