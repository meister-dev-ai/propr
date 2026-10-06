// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>Verification state; an absent flag does not establish uniqueness.</summary>
public enum CodeInsightDuplicateState
{
    Unknown = 0,
    CheckedNonduplicate = 1,
    Suspected = 2,
    ConfirmedDuplicate = 3,
}
