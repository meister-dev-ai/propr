// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Licensing;

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     Where the installation's stored license stands, and the edition that follows from it.
///     <para>
///         Every case carries what an operator needs to act on it: a verified license carries its claims, its
///         term status and the lifecycle stage it is in, one that did not verify carries the refusal reason and
///         its diagnostic, and both carry when the license was activated. A state that carries no license still
///         reports a stage and an edition, so a caller never has to decide what an absent license means.
///     </para>
///     <para>
///         The stage and the days remaining are computed when the state is built, against the instant the term
///         was judged at, and stay fixed for the life of the instance. A caller that holds a state across a
///         stage boundary therefore reads the stage that held when the state was loaded.
///     </para>
/// </summary>
public sealed record LicenseState
{
    /// <summary>
    ///     How long before the term ends the installation is reported as approaching expiry. Everything the
    ///     license grants stays available inside this window.
    /// </summary>
    public static readonly TimeSpan WarningWindow = TimeSpan.FromDays(30);

    /// <summary>
    ///     How long after the term ends the license keeps granting what it names. The window exists so an
    ///     expiry does not stop reviews that are already running while a renewal is being arranged.
    /// </summary>
    public static readonly TimeSpan GraceWindow = LicenseDocument.GraceWindow;

    /// <summary>
    ///     Constructed through the factories on this type, which is why the constructor is internal. Without
    ///     it the record would carry the implicit public parameterless constructor, and any assembly could
    ///     build a state that names no license and reads as the community edition.
    ///     <para>
    ///         The members cannot carry <see langword="required" /> instead: a required member of a public type
    ///         may not have a setter less visible than the type, so it cannot be combined with the internal
    ///         setters that keep the members out of reach of a caller outside this assembly.
    ///     </para>
    /// </summary>
    internal LicenseState()
    {
    }

    /// <summary>What the stored license amounts to.</summary>
    public LicenseStateKind Kind { get; internal init; }

    /// <summary>Where the installation stands in the license's lifecycle, as of the instant the state was built.</summary>
    public LicenseStage Stage { get; internal init; } = LicenseStage.None;

    /// <summary>The claims the document carries, on a verified license.</summary>
    public LicenseClaims? Claims { get; internal init; }

    /// <summary>Where a verified license stands against its own term.</summary>
    public LicenseTermStatus? TermStatus { get; internal init; }

    /// <summary>Why a stored document did not verify.</summary>
    public LicenseFailureReason? FailureReason { get; internal init; }

    /// <summary>What stopped verification, in terms an operator can act on. Safe to log.</summary>
    public string? FailureDetail { get; internal init; }

    /// <summary>When the stored license was activated, when one is on file.</summary>
    public DateTimeOffset? ActivatedAt { get; internal init; }

    /// <summary>Who activated it, when the activation was made by a signed-in user.</summary>
    public Guid? ActivatedByUserId { get; internal init; }

    /// <summary>
    ///     Whole days of entitlement left, as of the instant the state was built: until the term ends while it
    ///     is running, until the grace window ends once the term has, and zero once both have. A partial day
    ///     counts as a whole one, so a license with hours left reports one rather than none. Null when the state
    ///     carries no term, and null before the term begins, when no entitlement has started.
    /// </summary>
    public int? DaysRemaining { get; internal init; }

    /// <summary>When the license term begins, on a verified license.</summary>
    public DateTimeOffset? NotBefore => this.Claims?.NotBefore;

    /// <summary>When the license term ends, on a verified license.</summary>
    public DateTimeOffset? ExpiresAt => this.Claims?.ExpiresAt;

    /// <summary>
    ///     When the installation starts being reported as approaching expiry, on a verified license.
    ///     <para>
    ///         Never earlier than the start of the term. A term shorter than the warning window would otherwise
    ///         report a warning instant before the license was in force at all, while the stage it actually
    ///         opens in is the warning one.
    ///     </para>
    /// </summary>
    public DateTimeOffset? WarningStartsAt =>
        this.Claims is { } claims
            ? claims.ExpiresAt - claims.NotBefore <= WarningWindow
                ? claims.NotBefore
                : claims.ExpiresAt - WarningWindow
            : null;

    /// <summary>When the grace window after the term ends, on a verified license.</summary>
    public DateTimeOffset? GraceEndsAt =>
        this.Claims is { } claims ? claims.ExpiresAt + GraceWindow : null;

    /// <summary>The edition this state amounts to.</summary>
    public InstallationEdition Edition => EditionFor(this.Stage);

    /// <summary>The state of an installation with no license on file.</summary>
    /// <returns>The state.</returns>
    public static LicenseState None() => new() { Kind = LicenseStateKind.None };

    /// <summary>The state of a stored value that could not be read back out of its protected form.</summary>
    /// <param name="activatedAt">When the license was activated.</param>
    /// <param name="activatedByUserId">Who activated it.</param>
    /// <returns>The state.</returns>
    public static LicenseState Unreadable(DateTimeOffset activatedAt, Guid? activatedByUserId) => new()
    {
        Kind = LicenseStateKind.Unreadable,
        ActivatedAt = activatedAt,
        ActivatedByUserId = activatedByUserId,
    };

    /// <summary>The state of a stored document that did not verify.</summary>
    /// <param name="failureReason">Why no license was established.</param>
    /// <param name="failureDetail">What stopped verification.</param>
    /// <param name="activatedAt">When the license was activated.</param>
    /// <param name="activatedByUserId">Who activated it.</param>
    /// <returns>The state.</returns>
    public static LicenseState Invalid(
        LicenseFailureReason failureReason,
        string failureDetail,
        DateTimeOffset activatedAt,
        Guid? activatedByUserId) => new()
    {
        Kind = LicenseStateKind.Invalid,
        FailureReason = failureReason,
        FailureDetail = failureDetail,
        ActivatedAt = activatedAt,
        ActivatedByUserId = activatedByUserId,
    };

    /// <summary>The state of a stored document that comes from a signer this build accepts.</summary>
    /// <param name="license">The verified license.</param>
    /// <param name="evaluatedAt">
    ///     The instant the term was judged at, which is what the stage and the days remaining are computed
    ///     against. It is a parameter so the caller decides which clock is authoritative.
    /// </param>
    /// <param name="activatedAt">When the license was activated.</param>
    /// <param name="activatedByUserId">Who activated it.</param>
    /// <returns>The state.</returns>
    public static LicenseState Verified(
        VerifiedLicense license,
        DateTimeOffset evaluatedAt,
        DateTimeOffset activatedAt,
        Guid? activatedByUserId)
    {
        ArgumentNullException.ThrowIfNull(license);

        var stage = StageFor(license.Claims, evaluatedAt);

        return new LicenseState
        {
            Kind = LicenseStateKind.Verified,
            Stage = stage,
            Claims = license.Claims,
            TermStatus = license.TermStatus,
            DaysRemaining = DaysRemainingFor(stage, license.Claims, evaluatedAt),
            ActivatedAt = activatedAt,
            ActivatedByUserId = activatedByUserId,
        };
    }

    /// <summary>
    ///     The single place a license state becomes an edition.
    ///     <para>
    ///         The commercial edition holds while the term is running and through the grace window that follows
    ///         it, so an expiry does not take capabilities away the moment the term ends. A license whose grace
    ///         window has also ended, one whose term has not begun, one that did not verify, one that could not
    ///         be read, and an installation with no license all read as the community edition.
    ///     </para>
    ///     <para>
    ///         Every rule about what an installation is entitled to is added here rather than at a call site, so
    ///         every caller keeps the same answer.
    ///     </para>
    /// </summary>
    private static InstallationEdition EditionFor(LicenseStage stage)
    {
        return stage is LicenseStage.Active or LicenseStage.Warning or LicenseStage.Grace
            ? InstallationEdition.Commercial
            : InstallationEdition.Community;
    }

    /// <summary>
    ///     Places an instant in the license's lifecycle. Each window includes its lower bound and excludes its
    ///     upper one, so an instant lies in exactly one stage.
    ///     <para>
    ///         A term shorter than the warning window never reaches <see cref="LicenseStage.Active" />: it opens
    ///         in <see cref="LicenseStage.Warning" />, because its end is already inside the window when it
    ///         begins. The installation is entitled from the first moment of the term in either case.
    ///     </para>
    ///     <para>
    ///         Public because activation decides whether to store a document from the same rule that resolution
    ///         reads it by. Deriving the answer twice would let a document be refused for a stage the next
    ///         capability check does not agree with.
    ///     </para>
    /// </summary>
    /// <param name="claims">The claims whose term is being placed.</param>
    /// <param name="evaluatedAt">The instant to place against the term.</param>
    /// <returns>The stage the instant falls in.</returns>
    public static LicenseStage StageFor(LicenseClaims claims, DateTimeOffset evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(claims);

        if (evaluatedAt < claims.NotBefore)
        {
            return LicenseStage.NotYetValid;
        }

        // The windows are compared as durations between the two instants rather than by shifting one of them.
        // A term can sit close enough to either end of the representable range that shifting it leaves that
        // range, and this method answers for whatever claims a verified document carries.
        if (claims.ExpiresAt - evaluatedAt > WarningWindow)
        {
            return LicenseStage.Active;
        }

        if (evaluatedAt < claims.ExpiresAt)
        {
            return LicenseStage.Warning;
        }

        return evaluatedAt - claims.ExpiresAt < GraceWindow
            ? LicenseStage.Grace
            : LicenseStage.Reverted;
    }

    private static int? DaysRemainingFor(LicenseStage stage, LicenseClaims claims, DateTimeOffset evaluatedAt)
    {
        return stage switch
        {
            LicenseStage.Active or LicenseStage.Warning => WholeDaysUntil(claims.ExpiresAt, evaluatedAt),
            LicenseStage.Grace => WholeDaysUntil(claims.ExpiresAt + GraceWindow, evaluatedAt),
            LicenseStage.Reverted => 0,
            _ => null,
        };
    }

    private static int WholeDaysUntil(DateTimeOffset target, DateTimeOffset from) =>
        (int)Math.Ceiling((target - from).TotalDays);
}
