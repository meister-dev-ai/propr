// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>What the database said when a caller tried to claim a thread pass for a pull request.</summary>
/// <param name="WasClaimed">Whether this caller's pass was persisted.</param>
/// <param name="BlockingJob">
///     The pass that already holds the pull request: one still in flight, or one that already ran for the
///     same trigger state. Null when nothing blocked and the claim succeeded.
/// </param>
public sealed record TryClaimThreadPassResult(bool WasClaimed, ThreadPassJob? BlockingJob);

/// <summary>One thread the pass has already acted on, the comment count and the revision it acted at.</summary>
/// <remarks>
///     The revision belongs in the identity because an unanswered finding's comment count never changes: keyed
///     on the thread and the count alone, a finding would be judged once in the life of the pull request and
///     every later push would find its key already recorded.
/// </remarks>
/// <param name="ThreadId">Provider-native thread identifier.</param>
/// <param name="ObservedReplyCount">The non-reviewer comment count observed when the pass acted.</param>
/// <param name="RevisionKey">The stored revision key the acting pass was running at.</param>
public readonly record struct ThreadPassHandledThreadKey(string ThreadId, int ObservedReplyCount, string RevisionKey);

/// <summary>What a sweep of abandoned thread passes did.</summary>
/// <remarks>
///     An abandoned pass with attempts left is worth another go; one that died on its last attempt is not, and
///     leaving it pending would make it an undispatched row that holds the pull request's in-flight claim
///     forever. The two outcomes are counted apart so an operator can see which happened.
/// </remarks>
/// <param name="ReturnedToPending">How many passes were offered another attempt.</param>
/// <param name="Exhausted">How many passes had spent every attempt and were failed terminally instead.</param>
public readonly record struct StalledThreadPassSweep(int ReturnedToPending, int Exhausted);
