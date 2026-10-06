// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>Recorded publication evidence for a generated finding.</summary>
public enum CodeInsightPublicationState
{
    Unknown = 0,
    Published = 1,
    SuppressedRepeat = 2,
    PolicyWithheld = 3,
    Shadow = 4,
    PostingDisabled = 5,
    Failed = 6,
}
