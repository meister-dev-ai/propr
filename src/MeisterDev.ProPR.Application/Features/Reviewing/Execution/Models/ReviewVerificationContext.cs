// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Per-run context an evidence-gathering verifier needs to independently substantiate a claim:
///     the review-context tools used to read the anchor code, the source branch to resolve them
///     against, and a chat client + model for the bounded judging call. When <see cref="Resolver" /> and
///     <see cref="ClientId" /> are supplied the verifier uses the model resolved for <c>AiPurpose.ReviewLowEffort</c>,
///     and uses the reviewer's own (<see cref="ChatClient" />) model only when that resolution fails. Any
///     field may be <see langword="null" /> when the hosting path cannot supply it; an evidence-backed
///     verifier must then degrade to the conservative deterministic outcome.
///     <see cref="Intent" /> carries the pull request description, linked work items and file diff that the judge
///     weighs against a finding to decide whether the behaviour is intended and whether it contradicts a stated
///     requirement. It is <see langword="null" /> when the hosting path supplies none. <see cref="ProtocolId" /> names the
///     review protocol in which the verifier records its judge calls; without it no call is recorded.
///     <see cref="PassReviewedWithRepositoryTools" /> states whether the review pass that produced the findings could
///     read the repository through tools. The judge publishes a finding it cannot decide for lack of code only when
///     that pass could read the repository; the default withholds it. A work item that states
///     <see cref="VerificationWorkItem.ProducedWithRepositoryTools" /> overrides this value for its own claim.
///     <see cref="ReviewerReads" /> lists the file ranges
///     that pass read; the judge receives bounded excerpts of those that contain a symbol the claim names.
/// </summary>
public sealed record ReviewVerificationContext(
    IReviewContextTools? Tools,
    string SourceBranch,
    IChatClient? ChatClient,
    string? ModelId,
    Guid ClientId = default,
    IAiRuntimeResolver? Resolver = null,
    ReviewVerificationIntent? Intent = null,
    Guid? ProtocolId = null,
    bool PassReviewedWithRepositoryTools = false,
    IReadOnlyList<ReviewerFileRead>? ReviewerReads = null);
