// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Verification state for one client-scoped Azure DevOps organization scope.
/// </summary>
public enum AdoOrganizationVerificationStatus
{
    Unknown = 0,
    Verified = 1,
    Unauthorized = 2,
    Unreachable = 3,
    Stale = 4,
}
