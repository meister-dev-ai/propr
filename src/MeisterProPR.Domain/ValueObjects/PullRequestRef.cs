// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Lightweight pull-request reference containing only the branch names and status.
///     Returned by <c>IPullRequestFetcher.FetchRefAsync</c> so that the local git workspace
///     can be prepared before the full content fetch.
/// </summary>
public sealed record PullRequestRef(
    string SourceBranch,
    string TargetBranch,
    PrStatus Status);
