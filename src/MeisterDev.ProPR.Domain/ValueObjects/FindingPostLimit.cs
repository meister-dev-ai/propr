// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>
///     The bounds of a client's limit on the findings that ProPR posts as comments on one pull request. A finding above
///     the limit stays in the ProPR review, and the review summary on the pull request states how many were held back.
///     A client without a limit posts every finding that the other publication settings allow.
/// </summary>
public static class FindingPostLimit
{
    /// <summary>The limit of a client that has not changed the setting.</summary>
    public const int Default = 20;

    /// <summary>The smallest limit a client can set. A stored value below it sets no limit.</summary>
    public const int Minimum = 1;
}
