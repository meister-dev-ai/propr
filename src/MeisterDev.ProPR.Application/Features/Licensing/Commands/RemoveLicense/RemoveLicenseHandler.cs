// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Licensing;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Features.Licensing.Commands.RemoveLicense;

/// <summary>
///     Removes the license the installation has on file and records that it was removed.
///     <para>
///         Removing when none is on file is not an error and records nothing, so a repeated request leaves the
///         installation and its history where the first one left them.
///     </para>
/// </summary>
public sealed partial class RemoveLicenseHandler(
    LicenseVerifier verifier,
    IActivatedLicenseStore licenseStore,
    ILicenseActivationEventStore activationEventStore,
    ILicenseStateProvider licenseStateProvider,
    TimeProvider timeProvider,
    ILogger<RemoveLicenseHandler> logger)
{
    /// <summary>Removes the license on file, if there is one.</summary>
    /// <param name="command">Who is removing it.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    /// <returns>A task that completes when no license is on file.</returns>
    public async Task HandleAsync(RemoveLicenseCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var mutation = await licenseStore.RemoveAndGetAsync(cancellationToken).ConfigureAwait(false);

        if (mutation.Previous is not { } stored)
        {
            return;
        }

        // Invalidated as soon as the license is off file, and before the outgoing document is identified or the
        // history is written. The provider caches a state derived from the stored document alone, so neither
        // step has a part in what it answers, and a failure in either must not leave this process serving the
        // license that was just removed.
        licenseStateProvider.Invalidate();

        var claims = this.IdentifyOutgoingLicense(stored, timeProvider.GetUtcNow());

        // The request token can cancel work through the store mutation. Once RemoveAndGetAsync returns, the
        // stored license is gone, so request cancellation must not stop reconciliation of that committed state.
        await this.RecordAsync(
                new LicenseActivationEvent
                {
                    Action = LicenseActivationAction.Removed,

                    // The instant the store observed, not this replica's clock. It is what orders the history
                    // against the mutations other replicas make.
                    OccurredAt = mutation.MutatedAt,
                    ActorUserId = command.ActorUserId,
                    LicenseId = claims?.LicenseId,
                    Licensee = claims?.Licensee,
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     The claims of the document being removed, or null when this build establishes none.
    ///     <para>
    ///         The document is verified rather than only read, so the recorded identity is one the build
    ///         accepted. A document that cannot be read back or no longer verifies is removed all the same and
    ///         recorded without an identity, because what it claims has not been established.
    ///     </para>
    ///     <para>
    ///         A term that has ended does not prevent this: the removal of an expired license is the ordinary
    ///         case when one is renewed, and the term does not affect whether the origin was established.
    ///     </para>
    ///     <para>
    ///         This runs after the removal has been committed, so it does not fail the request. Activation
    ///         verifies before it stores anything, which lets it refuse a document this build does not accept.
    ///         A removal cannot be ordered that way, because the document to identify is the one the store
    ///         returns, so a verification that fails here costs only the recorded identity.
    ///     </para>
    /// </summary>
    private LicenseClaims? IdentifyOutgoingLicense(StoredLicense stored, DateTimeOffset now)
    {
        if (!stored.IsReadable)
        {
            return null;
        }

        try
        {
            var verification = verifier.Verify(stored.CompactLicense, now);

            return verification.IsVerified ? verification.License.Claims : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A stored document can reach the verifier in a shape that makes a cryptographic primitive raise
            // instead of refusing. A raise is treated the same as a refusal: neither establishes where the
            // document came from, and the removal is already committed, so the history records it without an
            // identity rather than the request reporting a failure the operator cannot act on.
            LogOutgoingLicenseNotIdentified(logger, exception);

            return null;
        }
    }

    /// <summary>
    ///     Writes the history record, reporting a failure instead of raising it.
    ///     <para>
    ///         The license is already off file when this runs. Failing the request would tell the operator the
    ///         removal did not happen while the installation no longer holds a license, so the failure is logged
    ///         and the request reports the state the installation is actually in. The log entry carries what the
    ///         missing record would have said.
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
            LogRemovalNotRecorded(logger, activationEvent.LicenseId, exception);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message =
            "The installation's license was removed but the change was not recorded in the license history. License id: {LicenseId}.")]
    private static partial void LogRemovalNotRecorded(ILogger logger, string? licenseId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "The license removed from this installation could not be verified, so the history records the removal without a license id. The license is off file.")]
    private static partial void LogOutgoingLicenseNotIdentified(ILogger logger, Exception exception);
}
