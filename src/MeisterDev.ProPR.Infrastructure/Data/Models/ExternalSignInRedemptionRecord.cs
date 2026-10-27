// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     A one-time value of an external sign-in that ProPR accepted: the state of a sign-in at its callback, or a pending
///     link when it was used. ProPR refuses a value that is recorded here, and removes the record when the value has
///     expired.
/// </summary>
public sealed class ExternalSignInRedemptionRecord
{
    /// <summary>The SHA-256 hash of the one-time value, in base64url without padding.</summary>
    public string RedemptionKey { get; set; } = string.Empty;

    /// <summary>When the value expires. After this time ProPR refuses the value for its age.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
