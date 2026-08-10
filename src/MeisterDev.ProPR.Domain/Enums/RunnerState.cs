// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>Whether an enrolled runner may still be given work.</summary>
public enum RunnerState
{
    /// <summary>Enrolled and eligible for work, subject to its credential still being valid.</summary>
    Enrolled = 0,

    /// <summary>
    ///     Revoked by an operator. It cannot lease, and any lease it still holds is reclaimable, because
    ///     revocation is what an operator reaches for when they no longer trust the host.
    /// </summary>
    Revoked = 1,
}
