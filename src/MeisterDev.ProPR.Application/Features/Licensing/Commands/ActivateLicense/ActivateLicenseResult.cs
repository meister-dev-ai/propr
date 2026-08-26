// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Diagnostics.CodeAnalysis;
using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Licensing;

namespace MeisterDev.ProPR.Application.Features.Licensing.Commands.ActivateLicense;

/// <summary>
///     The outcome of an activation: the licensing state the installation now runs under, or a typed reason the
///     document was refused together with a message an operator can act on.
///     <para>
///         A refusal means nothing was stored, so the license the installation had on file before the attempt is
///         still the license it holds.
///     </para>
/// </summary>
public sealed class ActivateLicenseResult
{
    private ActivateLicenseResult(
        bool isActivated,
        LicensingSummaryDto? summary,
        LicenseFailureReason? refusalReason,
        string? refusalDetail)
    {
        this.IsActivated = isActivated;
        this.Summary = summary;
        this.RefusalReason = refusalReason;
        this.RefusalDetail = refusalDetail;
    }

    /// <summary>
    ///     The licensing summary after a successful activation, or <see langword="null" /> when the summary
    ///     could not be read. The license is stored either way, so an absent summary reports a state the
    ///     caller has to read again rather than an activation that did not happen.
    /// </summary>
    public LicensingSummaryDto? Summary { get; }

    /// <summary>
    ///     Why the document was refused, when it was. Term status appears here as well: activation refuses a
    ///     license whose term has ended or has not started, where other callers accept it and report the term.
    /// </summary>
    public LicenseFailureReason? RefusalReason { get; }

    /// <summary>What an operator has to change about the document. Safe to log.</summary>
    public string? RefusalDetail { get; }

    /// <summary>
    ///     Whether the document was accepted and stored. Carried in its own right rather than derived from the
    ///     summary, because the summary is read after the store has committed and a failure there does not
    ///     unstore the license.
    /// </summary>
    [MemberNotNullWhen(false, nameof(RefusalReason))]
    [MemberNotNullWhen(false, nameof(RefusalDetail))]
    public bool IsActivated { get; }

    /// <summary>Builds the outcome of an activation that was stored.</summary>
    /// <param name="summary">
    ///     The licensing summary the installation now reports, or <see langword="null" /> when it could not be
    ///     read.
    /// </param>
    /// <returns>The result.</returns>
    public static ActivateLicenseResult Activated(LicensingSummaryDto? summary)
    {
        return new ActivateLicenseResult(true, summary, null, null);
    }

    /// <summary>Builds the outcome of an activation that stored nothing.</summary>
    /// <param name="reason">Why the document was refused.</param>
    /// <param name="detail">What an operator has to change about it.</param>
    /// <returns>The result.</returns>
    public static ActivateLicenseResult Refused(LicenseFailureReason reason, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        return new ActivateLicenseResult(false, null, reason, detail);
    }
}
