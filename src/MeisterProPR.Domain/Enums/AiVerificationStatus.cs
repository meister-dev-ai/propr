// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Latest verification state for one AI connection profile.
/// </summary>
public enum AiVerificationStatus
{
    /// <summary>No verification has been executed yet.</summary>
    NeverVerified = 0,

    /// <summary>The most recent verification succeeded.</summary>
    Verified = 1,

    /// <summary>The most recent verification failed.</summary>
    Failed = 2,
}
