// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Per-run context an evidence-gathering verifier needs to independently substantiate a claim:
///     the review-context tools used to read the anchor code, the source branch to resolve them
///     against, and a chat client + model for the bounded judging call. When <see cref="Resolver" /> and
///     <see cref="ClientId" /> are supplied the verifier prefers an independent model resolved for
///     <c>AiPurpose.ReviewVerification</c> over the reviewer's own (<see cref="ChatClient" />) model. Any
///     field may be <see langword="null" /> when the hosting path cannot supply it; an evidence-backed
///     verifier must then degrade to the conservative deterministic outcome.
///     <see cref="EvidenceVerificationEnabled" /> carries the per-client opt-in: when <see langword="false" />
///     the composite verifier behaves exactly like the deterministic verifier and never calls a model.
///     <see cref="Intent" /> carries the pull request description, linked work items and file diff that the judge
///     weighs against a finding to decide whether the behaviour is intended and whether it contradicts a stated
///     requirement. It is <see langword="null" /> when the hosting path supplies none. <see cref="ProtocolId" /> names the
///     review protocol in which the verifier records its judge calls; without it no call is recorded.
/// </summary>
public sealed record ReviewVerificationContext(
    IReviewContextTools? Tools,
    string SourceBranch,
    IChatClient? ChatClient,
    string? ModelId,
    Guid ClientId = default,
    IAiRuntimeResolver? Resolver = null,
    bool EvidenceVerificationEnabled = false,
    ReviewVerificationIntent? Intent = null,
    Guid? ProtocolId = null);
