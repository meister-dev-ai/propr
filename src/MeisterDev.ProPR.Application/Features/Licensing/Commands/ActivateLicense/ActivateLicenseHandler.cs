// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Licensing;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Features.Licensing.Commands.ActivateLicense;

/// <summary>
///     Activates a license document an operator supplied.
///     <para>
///         The document is verified before anything is stored, so a document this build does not accept leaves
///         the installation on the license it already had. Verification is offline, so it can run before the
///         write rather than after it.
///     </para>
///     <para>
///         Activation also refuses a license that would grant nothing once stored, which no other caller does.
///         Elsewhere the lifecycle stage is reported and the license is still one whose origin was established.
///         A document whose term has not begun, and one whose term and grace window have both ended, are refused
///         here: storing either would leave the installation configured with a license that grants nothing. A
///         document inside its term, or inside the grace window that follows it, is accepted, because it is in
///         force the moment it is stored. That is what lets an installation re-activate the file it already has
///         while a renewal is being arranged.
///     </para>
/// </summary>
public sealed partial class ActivateLicenseHandler(
    LicenseVerifier verifier,
    IActivatedLicenseStore licenseStore,
    ILicenseActivationEventStore activationEventStore,
    ILicenseStateProvider licenseStateProvider,
    ILicensingCapabilityService licensingCapabilityService,
    ILicensingClock licensingClock,
    ILogger<ActivateLicenseHandler> logger)
{
    /// <summary>Verifies the supplied document and, when it is accepted, makes it the installation's license.</summary>
    /// <param name="command">The document and who supplied it.</param>
    /// <param name="cancellationToken">Cancels the activation.</param>
    /// <returns>The resulting licensing summary, or the reason the document was refused.</returns>
    public async Task<ActivateLicenseResult> HandleAsync(
        ActivateLicenseCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The term is judged against the licensing clock, the same reading capability resolution uses. Judging
        // it against the host clock instead would let a document be accepted here and then placed past its
        // grace window by the next capability check, on an installation whose host clock had been set back.
        var termInstant = await licensingClock.GetUtcNowAsync(cancellationToken).ConfigureAwait(false);
        var verification = verifier.Verify(command.CompactLicense, termInstant);

        if (!verification.IsVerified)
        {
            return ActivateLicenseResult.Refused(verification.FailureReason.Value, verification.FailureDetail);
        }

        if (StageRefusal(LicenseState.StageFor(verification.License.Claims, termInstant)) is { } refusal)
        {
            return refusal;
        }

        // The store reads and replaces under the guarantee its contract states, so a concurrent activation
        // cannot turn two first activations into two history entries that both claim no document was on file.
        var mutation = await licenseStore.ReplaceAsync(command.CompactLicense, command.ActorUserId, cancellationToken)
            .ConfigureAwait(false);

        var action = mutation.Previous is null ? LicenseActivationAction.Activated : LicenseActivationAction.Replaced;

        // Invalidated as soon as the stored document changed, and before the history is written. The provider
        // caches a state derived from the document alone, so the history record has no part in what it answers,
        // and a failed record write must not leave this process serving the license the activation replaced.
        licenseStateProvider.Invalidate();

        // The request token can cancel work through the store mutation. Once ReplaceAsync returns, the stored
        // license has changed, so request cancellation must not stop reconciliation of that committed state.
        await this.RecordAsync(
                new LicenseActivationEvent
                {
                    Action = action,

                    // The instant the store observed, not this replica's clock. It is what orders the history
                    // against the mutations other replicas make.
                    OccurredAt = mutation.MutatedAt,
                    ActorUserId = command.ActorUserId,
                    LicenseId = verification.License.Claims.LicenseId,
                    Licensee = verification.License.Claims.Licensee,
                },
                CancellationToken.None)
            .ConfigureAwait(false);

        return ActivateLicenseResult.Activated(await this.ReadSummaryAsync().ConfigureAwait(false));
    }

    /// <summary>
    ///     Reads the licensing summary the activation is reported with, or reports a failure and returns
    ///     nothing.
    ///     <para>
    ///         The license has already been stored when this runs, and the state provider has been invalidated,
    ///         so this read reloads and verifies the stored document, advances the observed instant, and on a
    ///         first activation seeds the installation identity and observes the system profile. Raising from
    ///         any of that would report a failed activation while the installation runs on the new license, and
    ///         the operator's retry would record a second replacement.
    ///     </para>
    ///     <para>
    ///         Omitting the summary rather than guarding only the exception, because the result reports whether
    ///         the document was stored, and that answer no longer depends on the summary being readable. The
    ///         caller reads the summary again to report the state.
    ///     </para>
    /// </summary>
    private async Task<LicensingSummaryDto?> ReadSummaryAsync()
    {
        try
        {
            return await licensingCapabilityService.GetSummaryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSummaryNotRead(logger, exception);

            return null;
        }
    }

    /// <summary>
    ///     Refuses the two stages in which a stored document would grant nothing, and lets the three in which it
    ///     is in force through.
    /// </summary>
    private static ActivateLicenseResult? StageRefusal(LicenseStage stage)
    {
        return stage switch
        {
            LicenseStage.NotYetValid => ActivateLicenseResult.Refused(
                LicenseFailureReason.NotYetValid,
                "The license term has not started yet, so this license cannot be activated."),
            LicenseStage.Reverted => ActivateLicenseResult.Refused(
                LicenseFailureReason.Expired,
                "The license term and the grace window after it have both ended, so this license cannot be activated."),
            _ => null,
        };
    }

    /// <summary>
    ///     Writes the history record, reporting a failure instead of raising it.
    ///     <para>
    ///         The license has already been stored when this runs. Failing the request would tell the operator
    ///         the activation did not happen while the installation runs on the new license, so the failure is
    ///         logged and the request reports the state the installation is actually in. The log entry carries
    ///         what the missing record would have said.
    ///     </para>
    /// </summary>
    private async Task RecordAsync(LicenseActivationEvent activationEvent, CancellationToken cancellationToken)
    {
        try
        {
            await activationEventStore.RecordAsync(activationEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogActivationNotRecorded(logger, activationEvent.Action, activationEvent.LicenseId, exception);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message =
            "The installation's license was stored but the licensing summary could not be read back, so the activation is reported without it. The license is in force; read the licensing summary again to see the state it puts the installation in.")]
    private static partial void LogSummaryNotRead(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message =
            "The installation's license was {Action} but the change was not recorded in the license history. License id: {LicenseId}.")]
    private static partial void LogActivationNotRecorded(
        ILogger logger,
        LicenseActivationAction action,
        string? licenseId,
        Exception exception);
}
