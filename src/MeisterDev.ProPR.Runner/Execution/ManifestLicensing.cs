// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Runner.Contracts;

namespace MeisterDev.ProPR.Runner.Execution;

/// <summary>
///     The licensing answers a review may ask on a host with no license store: exactly the ones the
///     manifest carries, resolved at the same dispatch that resolved everything else.
///     <para>
///         Only the capability the pipeline actually reads is carried. Any other key is answered with a
///         throw rather than a guess, because a guessed "no" silently disables a licensed feature and a
///         guessed "yes" un-licenses one — the same defect class, in both directions, that quiet nulls
///         produced across this composition before every divergence was made to speak.
///     </para>
/// </summary>
internal sealed class ManifestLicensing(RunnerJobManifest manifest) : ILicensingCapabilityService
{
    /// <inheritdoc />
    public ValueTask<bool> IsEnabledAsync(string capabilityKey, CancellationToken cancellationToken = default)
    {
        if (string.Equals(capabilityKey, PremiumCapabilityKey.ParallelReviewExecution, StringComparison.Ordinal))
        {
            // Null means an older control plane that did not resolve the capability — the review behaves
            // exactly as it did before the field existed, which was unclamped.
            return ValueTask.FromResult(manifest.ParallelReviewExecutionLicensed ?? true);
        }

        throw new NotSupportedException(
            $"The job manifest does not carry the '{capabilityKey}' capability. Resolve it at dispatch and "
            + "carry it before reading it on an executor.");
    }

    /// <inheritdoc />
    public Task<LicensingSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Licensing is summarized by the control plane, which owns the license.");
    }

    /// <inheritdoc />
    public Task<AuthOptionsDto> GetAuthOptionsAsync(CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Sign-in options are the control plane's; a runner has no sign-in surface.");
    }

    /// <inheritdoc />
    public Task<CapabilitySnapshot> GetCapabilityAsync(string capabilityKey, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Capability snapshots are served by the control plane, which owns the license.");
    }

    /// <inheritdoc />
    public Task<LicensingSummaryDto> UpdateAsync(
        InstallationEdition edition,
        IReadOnlyCollection<CapabilityOverrideMutation> capabilityOverrides,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Licensing is updated through the control plane's admin surface.");
    }
}
