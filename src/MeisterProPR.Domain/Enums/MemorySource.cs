// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Discriminator that indicates how a <c>ThreadMemoryRecord</c> was created.
/// </summary>
public enum MemorySource
{
    /// <summary>Created by the crawl state machine when a PR review thread transitions to resolved.</summary>
    ThreadResolved = 0,

    /// <summary>Created by an admin explicitly dismissing a review finding.</summary>
    AdminDismissed = 1,
}
