// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Diagnostics.CodeAnalysis;

namespace MeisterDev.ProPR.Licensing;

/// <summary>
///     The outcome of reading a license document: the parsed document, or a typed reason it was refused
///     together with a diagnostic an operator can act on.
///     <para>
///         Refusal is data rather than an exception because a rejected license is an expected state on a
///         running installation, and the caller has to report the reason rather than only log a stack trace.
///     </para>
/// </summary>
public sealed class LicenseReadResult
{
    private LicenseReadResult(ParsedLicense? license, LicenseFailureReason? failureReason, string? failureDetail)
    {
        this.License = license;
        this.FailureReason = failureReason;
        this.FailureDetail = failureDetail;
    }

    /// <summary>Whether the document was accepted.</summary>
    [MemberNotNullWhen(true, nameof(License))]
    [MemberNotNullWhen(false, nameof(FailureReason))]
    [MemberNotNullWhen(false, nameof(FailureDetail))]
    public bool IsSuccess => this.License is not null;

    /// <summary>The parsed document, when the document was accepted. The caller disposes it.</summary>
    public ParsedLicense? License { get; }

    /// <summary>Why the document was refused, when it was refused.</summary>
    public LicenseFailureReason? FailureReason { get; }

    /// <summary>
    ///     What was wrong with the document, in terms an operator can act on. It repeats no text from the
    ///     document and stays bounded in length: at most one number read from the document appears in it, such
    ///     as the schema version the payload declares. It is therefore safe to log.
    /// </summary>
    public string? FailureDetail { get; }

    /// <summary>Builds an accepted result.</summary>
    /// <param name="license">The parsed document.</param>
    /// <returns>The result.</returns>
    public static LicenseReadResult Success(ParsedLicense license)
    {
        ArgumentNullException.ThrowIfNull(license);

        return new LicenseReadResult(license, null, null);
    }

    /// <summary>Builds a refused result.</summary>
    /// <param name="reason">Why the document was refused.</param>
    /// <param name="detail">What was wrong with the document.</param>
    /// <returns>The result.</returns>
    public static LicenseReadResult Failure(LicenseFailureReason reason, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        return new LicenseReadResult(null, reason, detail);
    }
}
