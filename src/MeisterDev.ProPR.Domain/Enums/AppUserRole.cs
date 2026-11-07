// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>Global role assigned directly to an application user.</summary>
public enum AppUserRole
{
    /// <summary>Access scoped to the assigned clients; the <c>UserClientRole</c> of each client decides what the user may do there.</summary>
    User = 0,

    /// <summary>Full administrative access across all clients and system settings.</summary>
    Admin = 1,
}
