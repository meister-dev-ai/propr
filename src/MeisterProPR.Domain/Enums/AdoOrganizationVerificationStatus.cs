// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Verification state for one client-scoped Azure DevOps organization scope.
/// </summary>
public enum AdoOrganizationVerificationStatus
{
    /// <summary>
    ///     The verification status is unknown.
    /// </summary>
    Unknown = 0,

    /// <summary>
    ///     The organization has been verified.
    /// </summary>
    Verified = 1,

    /// <summary>
    ///     The organization verification failed due to unauthorized access.
    /// </summary>
    Unauthorized = 2,

    /// <summary>
    ///     The organization is unreachable.
    /// </summary>
    Unreachable = 3,

    /// <summary>
    ///     The organization verification is stale.
    /// </summary>
    Stale = 4,
}
