// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Trigger behavior for tracked-branch refresh scheduling.
/// </summary>
public enum ProCursorRefreshTriggerMode
{
    Manual = 0,
    BranchUpdate = 1,
}
