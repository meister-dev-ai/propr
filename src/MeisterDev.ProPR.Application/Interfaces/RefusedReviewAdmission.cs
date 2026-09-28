// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     The pull request head review admission last refused, and the bounds it was refused under.
/// </summary>
/// <param name="StoredRevisionKey">
///     The stored revision key of the refused head, compared with the key of the revision a trigger is about
///     to submit.
/// </param>
/// <param name="PolicyFingerprint">
///     The client's admission bounds as they stood at the refusal, or null on a job refused before the bounds
///     were recorded.
/// </param>
public sealed record RefusedReviewAdmission(string StoredRevisionKey, string? PolicyFingerprint);
