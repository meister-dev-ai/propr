// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>Status of a pull request as reported by the source control provider.</summary>
public enum PrStatus
{
    /// <summary>Pull request is open and active.</summary>
    Active,

    /// <summary>Pull request was merged / completed.</summary>
    Completed,

    /// <summary>Pull request was abandoned / closed without merging.</summary>
    Abandoned,
}
