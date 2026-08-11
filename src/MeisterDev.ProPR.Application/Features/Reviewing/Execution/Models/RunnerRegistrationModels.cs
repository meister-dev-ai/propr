// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     What a host sends to enroll.
///     <para>
///         There is no client-scope field, and that absence is the design. A runner that could name the
///         clients it serves would make tenant isolation a matter of configuration discipline; the scope
///         comes from the operator-issued token instead, so nothing a host sends can widen it.
///     </para>
/// </summary>
/// <param name="RegistrationToken">The operator-issued token.</param>
/// <param name="DisplayName">Operator-facing name for the host.</param>
/// <param name="Tags">Routing tags the host declares. They narrow within its stamped scope, never widen it.</param>
/// <param name="ContractVersion">The contract version the host speaks.</param>
public sealed record RunnerRegistrationRequest(
    string RegistrationToken,
    string DisplayName,
    IReadOnlyList<string> Tags,
    int ContractVersion);

/// <summary>The outcome of enrolling or renewing.</summary>
/// <param name="RunnerId">The enrolled runner, when it succeeded.</param>
/// <param name="Credential">
///     The issued secret, returned exactly once. Nothing stores it, so an operator who loses it renews
///     rather than reads it back.
/// </param>
/// <param name="ExpiresAt">When the credential must be renewed by.</param>
/// <param name="Refusal">Why enrollment was refused, when it was.</param>
public sealed record RunnerRegistrationResult(
    Guid? RunnerId,
    string? Credential,
    DateTimeOffset? ExpiresAt,
    string? Refusal)
{
    /// <summary>Whether a credential was issued.</summary>
    public bool Succeeded => this.RunnerId is not null;

    /// <summary>A successful enrollment or renewal.</summary>
    public static RunnerRegistrationResult Enrolled(Guid runnerId, string credential, DateTimeOffset expiresAt)
    {
        return new RunnerRegistrationResult(runnerId, credential, expiresAt, null);
    }

    /// <summary>A refusal, deliberately not saying which of the several ways it failed.</summary>
    public static RunnerRegistrationResult Refused(string reason)
    {
        return new RunnerRegistrationResult(null, null, null, reason);
    }
}

/// <summary>
///     A freshly minted registration token. The secret appears here and nowhere else, ever: only its
///     hashes are stored, so an operator who loses it issues another rather than reading this one back.
/// </summary>
/// <param name="TokenId">Identity of the token, for the audit and for revoking it.</param>
/// <param name="Token">The secret, returned exactly once.</param>
/// <param name="ExpiresAt">When it stops being usable.</param>
public sealed record RunnerRegistrationTokenIssue(Guid TokenId, string Token, DateTimeOffset? ExpiresAt)
{
    /// <summary>
    ///     Redacted. A positional record prints every property, so the generated version would put the
    ///     plaintext token into any log line, exception message, or diagnostic dump that formats this,
    ///     which is the exposure the one-time return is meant to avoid.
    /// </summary>
    public override string ToString()
    {
        return $"RunnerRegistrationTokenIssue {{ TokenId = {this.TokenId}, Token = <redacted>, ExpiresAt = {this.ExpiresAt:O} }}";
    }
}
