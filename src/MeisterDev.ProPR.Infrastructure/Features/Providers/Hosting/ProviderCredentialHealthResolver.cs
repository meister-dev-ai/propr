// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>One statement about a connection's credential, and when it was made.</summary>
/// <param name="Health">The state, from the host's closed set.</param>
/// <param name="Cause">What was observed, shown to an operator beside the state.</param>
/// <param name="ObservedAt">When it was observed, or null when nothing recorded a time.</param>
public sealed record ProviderCredentialHealthState(
    AiCredentialHealth Health,
    string? Cause = null,
    DateTimeOffset? ObservedAt = null)
{
    /// <summary>Nothing has been observed about the credential.</summary>
    public static ProviderCredentialHealthState Unknown { get; } = new(AiCredentialHealth.Unreported);

    /// <summary>Whether this says anything at all.</summary>
    public bool IsStated => this.Health != AiCredentialHealth.Unreported;
}

/// <summary>
///     Decides what a connection's credential health is, from the three things that can say something about it.
/// </summary>
/// <remarks>
///     <para>
///         The three are a verification the host ran, a report the provider family made, and what the retry stage
///         concluded from a failed call. They disagree, and the order between them follows what each one knows: a
///         verification is a live check the host performed, a family's report is something the family observed on
///         a real call, and a classification is inferred from a failure.
///     </para>
///     <para>
///         Between the verification and the family's report, the more recent one holds. A verification run now
///         replaces what a family said earlier, and that makes re-verifying a connection the way to clear a
///         reported problem; a family that sees a revoked grant after a verification succeeded is reporting
///         something the verification could not have seen. Between the family's report and the classification the
///         order is fixed, because the classification is a guess made from a failed call and the report is not.
///     </para>
/// </remarks>
public static class ProviderCredentialHealthResolver
{
    /// <summary>What a credential with no stated expiry is reported as, and why.</summary>
    private const string MissingExpiryCause =
        "The stored credential does not state when it expires, so it cannot be renewed. Re-authorize the "
        + "connection to replace it.";

    /// <summary>Resolves the connection's credential health.</summary>
    /// <param name="verification">What the host's own verification found, or null when none has run.</param>
    /// <param name="familyReport">What the provider family reported, or null when it has reported nothing.</param>
    /// <param name="runtimeClassification">
    ///     What the retry stage concluded from a failed call, or null when nothing has failed.
    /// </param>
    /// <param name="credentialMissingExpiry">
    ///     Whether a credential is stored that does not say when it expires, for a family whose credentials do.
    ///     Such a credential reads as never usable wherever it is read, so every call would try to renew it and a
    ///     whole review would serialise through one row lock. It is reported as needing re-authorization instead,
    ///     which names a cause an operator can act on.
    /// </param>
    public static ProviderCredentialHealthState Resolve(
        ProviderCredentialHealthState? verification,
        ProviderCredentialHealthState? familyReport,
        ProviderCredentialHealthState? runtimeClassification,
        bool credentialMissingExpiry = false)
    {
        if (credentialMissingExpiry)
        {
            return new ProviderCredentialHealthState(
                AiCredentialHealth.NeedsReauthorization,
                MissingExpiryCause,
                MoreRecentTimestamp(familyReport?.ObservedAt, verification?.ObservedAt));
        }

        var verified = Stated(verification);

        var reported = Stated(familyReport);

        if (verified is not null && reported is not null)
        {
            return MoreRecent(verified, reported);
        }

        return verified ?? reported ?? Stated(runtimeClassification) ?? ProviderCredentialHealthState.Unknown;
    }

    /// <summary>
    ///     What a stored verification snapshot says about the credential, or null when none has run.
    /// </summary>
    /// <remarks>
    ///     Reads the columns rather than a loaded entity, so a caller that projected the row and one that loaded
    ///     it reach the same statement.
    /// </remarks>
    /// <param name="status">The stored verification status, as the column holds it.</param>
    /// <param name="summary">What the verification reported, shown to an operator beside the state.</param>
    /// <param name="checkedAt">When the verification ran.</param>
    public static ProviderCredentialHealthState? FromVerification(
        string? status,
        string? summary,
        DateTimeOffset? checkedAt)
    {
        if (!Enum.TryParse<AiVerificationStatus>(status, out var parsed))
        {
            return null;
        }

        return parsed switch
        {
            AiVerificationStatus.Verified => new ProviderCredentialHealthState(
                AiCredentialHealth.Healthy,
                summary,
                checkedAt),

            // A failed verification says the credential did not work, not why. Needing re-authorization is the
            // remedy an operator can act on, and a family that knows better reports over it.
            AiVerificationStatus.Failed => new ProviderCredentialHealthState(
                AiCredentialHealth.NeedsReauthorization,
                summary,
                checkedAt),
            _ => null,
        };
    }

    /// <summary>
    ///     What the provider family last reported about the credential, or null when it has reported nothing.
    /// </summary>
    /// <param name="health">The stored health, as the column holds it.</param>
    /// <param name="cause">What the family observed.</param>
    /// <param name="reportedAt">When it reported.</param>
    public static ProviderCredentialHealthState? FromReport(
        string? health,
        string? cause,
        DateTimeOffset? reportedAt)
    {
        // A stored value that resolves to no member reads as needing re-authorization rather than as healthy, so
        // a connection is never shown as working on a value nothing wrote. A fresh verification recovers it.
        if (string.IsNullOrWhiteSpace(health))
        {
            return null;
        }

        // Enum.TryParse accepts a numeric string and hands back whatever member carries that number, defined or
        // not, so the parse alone would let '99' through as a health state nothing can key on. The write path
        // refuses an undefined member, which leaves a value written straight into the column as the way one
        // arrives; it reads as needing re-authorization like any other name this build does not know.
        return Enum.TryParse<AiCredentialHealth>(health, out var parsed) && Enum.IsDefined(parsed)
            ? new ProviderCredentialHealthState(parsed, cause, reportedAt)
            : new ProviderCredentialHealthState(
                AiCredentialHealth.NeedsReauthorization,
                $"The stored credential health '{health}' is not a state this build knows.",
                reportedAt);
    }

    /// <summary>
    ///     The more recent of the two statements, with the verification holding where neither is more recent.
    /// </summary>
    /// <remarks>
    ///     The three cases where a time is missing are handled before two times are compared, because every
    ///     comparison involving a missing one answers false and so would return the verification whichever way
    ///     round the missing time sat. A statement that recorded a time is the more recent of the two when the
    ///     other recorded none: nothing dates the other, and the one that is dated is the one that was observed.
    ///     With neither dated there is nothing to order them by, and the verification holds because it is the
    ///     check the host ran itself.
    /// </remarks>
    /// <param name="verified">What the host's own verification found.</param>
    /// <param name="reported">What the provider family reported.</param>
    private static ProviderCredentialHealthState MoreRecent(
        ProviderCredentialHealthState verified,
        ProviderCredentialHealthState reported)
    {
        if (reported.ObservedAt is not { } reportedAt)
        {
            return verified;
        }

        return verified.ObservedAt is not { } verifiedAt || reportedAt > verifiedAt ? reported : verified;
    }

    private static ProviderCredentialHealthState? Stated(ProviderCredentialHealthState? state)
    {
        return state?.IsStated == true ? state : null;
    }

    // The newer of the two, not whichever was written first in the expression. Preferring the family report
    // stamped a state with a time older than the verification it was resolved against.
    private static DateTimeOffset? MoreRecentTimestamp(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (first is null)
        {
            return second;
        }

        return second is null || first >= second ? first : second;
    }
}
