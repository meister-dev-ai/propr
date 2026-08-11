// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Runner.Contracts;

/// <summary>
///     Everything non-secret a review needs, resolved once when the job is dispatched.
///     <para>
///         Configuration is otherwise read from the database throughout a review, which an executor without
///         database access cannot do, and which also means a configuration change part-way through can
///         alter a review already in progress. Resolving it once fixes both: the executor holds this
///         for the duration of its lease and never persists it.
///     </para>
///     <para>
///         Secrets are structurally absent rather than merely left unset. There is no field here that can
///         carry a credential, a connection string, or a key, and a test asserts it, because "we remember not
///         to populate it" is not enforceable.
///     </para>
/// </summary>
/// <param name="ContractVersion">The contract version this manifest was written against.</param>
/// <param name="JobId">The review job this manifest describes.</param>
/// <param name="ClientId">The client the job belongs to.</param>
/// <param name="LeaseGeneration">The lease generation the manifest was issued under, presented on every proxied call.</param>
/// <param name="Target">Which review, on which repository, at which revision.</param>
/// <param name="Workspace">Where the executor gets the repository content from.</param>
/// <param name="DefaultModel">
///     The model an unlisted pass runs on: the baseline per-file review, synthesis, screening, and every
///     other stage that does not name a pass of its own.
/// </param>
/// <param name="Passes">The ordered pass list, already resolved from client configuration.</param>
/// <param name="Prompts">Prompt configuration overrides that apply to this review.</param>
/// <param name="Exclusions">Paths the client excludes from review.</param>
/// <param name="RepositoryInstructions">
///     Repository instructions that apply to this review, already fetched and already filtered to the ones
///     relevant to the changed paths. Filtering happens here rather than on the executor because deciding
///     relevance reads the repository, which is a credentialed operation.
/// </param>
/// <param name="BudgetHeadroomUsd">
///     Remaining spend before the job's hard cap, when one is configured. An optimisation only: it lets the
///     executor wind down gracefully rather than being refused mid-pass, and the relay stays the point where
///     the cap is actually enforced, because this number is stale the moment it is written.
/// </param>
/// <param name="TraceContext">W3C trace context, so one review is followable across the two processes.</param>
/// <param name="LinkedItems">
///     The work items linked to the review, discovered and bounded at dispatch the way the in-process path
///     discovers them at review start. Carried because discovery is a credentialed provider call; the
///     executor reads these into the prompt and asks follow-up questions through the proxied tools.
/// </param>
/// <param name="ServedBy">
///     The base URL of the control-plane replica that granted this lease, when the operator advertises
///     one. The workspace mirror is that replica's local disk and the budget, tool, and workspace
///     registries are that replica's process, so every call this job makes has to reach the replica that
///     holds them. A load balancer in front of the fleet routes to whichever replica is next, which is
///     the wrong replica. Unset on a single-replica installation, where the one configured URL is
///     already the right one.
/// </param>
/// <param name="ParallelReviewExecutionLicensed">
///     Whether reviewing several files in parallel is licensed, resolved at dispatch because the license
///     lives in the control plane's database. Without it the pipeline works one file at a time however
///     high the configured concurrency is. This is the same clamp the in-process planner applies. Null from an
///     older control plane reads as licensed, which is how the review behaved before the field existed.
/// </param>
public sealed record RunnerJobManifest(
    int ContractVersion,
    Guid JobId,
    Guid ClientId,
    int LeaseGeneration,
    RunnerReviewTarget Target,
    RunnerWorkspaceReference Workspace,
    RunnerModelBinding DefaultModel,
    IReadOnlyList<RunnerReviewPass> Passes,
    RunnerPromptConfiguration Prompts,
    IReadOnlyList<string> Exclusions,
    IReadOnlyList<RunnerRepositoryInstruction> RepositoryInstructions,
    decimal? BudgetHeadroomUsd,
    RunnerTraceContext TraceContext,
    RunnerReviewBehaviour? Behaviour = null,
    IReadOnlyList<RunnerLinkedItem>? LinkedItems = null,
    string? ServedBy = null,
    bool? ParallelReviewExecutionLicensed = null);

/// <summary>
///     One work item linked to the review, reduced to the provider-neutral summary the prompt is built
///     from. The shape mirrors the domain's linked-item summary so the reviewer reads the same thing on
///     both sides.
/// </summary>
/// <param name="ProviderKey">The provider's identifier for the item, used to ask follow-up questions.</param>
/// <param name="ItemType">What kind of item it is, in the provider's vocabulary.</param>
/// <param name="Title">The item's title.</param>
/// <param name="Description">The item's description, already bounded at dispatch.</param>
/// <param name="Url">Where the item lives, when the provider names it.</param>
/// <param name="RelatedLinks">Links from this item to others, resolvable through the proxied tools.</param>
public sealed record RunnerLinkedItem(
    string ProviderKey,
    string ItemType,
    string Title,
    string? Description,
    string? Url,
    IReadOnlyList<RunnerLinkedItemRef> RelatedLinks);

/// <summary>One link from a linked item to a related item.</summary>
/// <param name="Kind">The relationship, in the provider's vocabulary.</param>
/// <param name="TargetKey">The provider's identifier for the related item.</param>
/// <param name="Url">Where the related item lives, when known.</param>
/// <param name="Title">The related item's title, when known.</param>
public sealed record RunnerLinkedItemRef(
    string Kind,
    string TargetKey,
    string? Url = null,
    string? Title = null);

/// <summary>
///     The per-client decisions that change what a review does rather than which model runs it.
///     <para>
///         Carried because the executor cannot read them: they live on the client record, which a runner
///         has no database to reach. Absent, every one of them falls to its default and the review becomes
///         a different review, with multi-pass union off, screening off, verification off, temperature
///         unset and profile Balanced, and nothing in the result stating this.
///     </para>
///     <para>
///         Optional on the contract so a manifest from an older control plane still deserializes. A runner
///         reading one without this reverts to the behaviour it had before the field existed.
///     </para>
/// </summary>
/// <param name="EnableMultiPassUnion">
///     Whether the pass list is actually unioned. Off, the carefully-bound passes in this manifest are
///     decorative: the baseline result is returned as-is and the semantic deduplicator never runs.
/// </param>
/// <param name="EnableLanguageRobustScreening">Whether the semantic screener runs over candidates.</param>
/// <param name="EnableEvidenceBackedVerification">Whether findings are verified against collected evidence.</param>
/// <param name="IncludeLinkedItemsInContext">Whether linked work items and issues are offered to the review.</param>
/// <param name="Temperature">The review temperature, when the job pins one.</param>
/// <param name="ReviewPipelineProfileId">The pipeline profile the review runs under, when one is configured.</param>
public sealed record RunnerReviewBehaviour(
    bool EnableMultiPassUnion,
    bool EnableLanguageRobustScreening,
    bool EnableEvidenceBackedVerification,
    bool IncludeLinkedItemsInContext,
    float? Temperature,
    string? ReviewPipelineProfileId);

/// <summary>
///     One repository instruction, carried whole. Flattening these into a single blob would lose the
///     structure the prompt is built from: the pipeline uses the description and the when-to-use guidance
///     to decide how to present each one.
/// </summary>
/// <param name="FileName">The instruction file it came from.</param>
/// <param name="Description">What the instruction is about.</param>
/// <param name="WhenToUse">When the instruction applies.</param>
/// <param name="Body">The instruction text.</param>
public sealed record RunnerRepositoryInstruction(
    string FileName,
    string Description,
    string WhenToUse,
    string Body);

/// <summary>
///     Which review the executor is being asked to perform.
///     <para>
///         The title, description, and branch names are here because the reviewer reads them: a change is
///         judged against what its author said it does. An executor that could not see them would review
///         the diff in isolation and reach different conclusions than the in-process path on the same
///         commit.
///     </para>
/// </summary>
/// <param name="Provider">The source-control provider family, as a stable token.</param>
/// <param name="OrganizationUrl">The provider host the review lives on.</param>
/// <param name="ProjectId">The project or namespace the repository belongs to.</param>
/// <param name="RepositoryId">The provider's repository identifier.</param>
/// <param name="RepositoryName">The repository's display name.</param>
/// <param name="ExternalReviewId">The provider's identifier for the pull request or merge request.</param>
/// <param name="Number">The human-facing review number.</param>
/// <param name="IterationId">The revision's iteration number within the review.</param>
/// <param name="Title">The review's title.</param>
/// <param name="Description">The review's description, when the author wrote one.</param>
/// <param name="SourceBranch">The branch the change is on.</param>
/// <param name="TargetBranch">The branch the change is proposed into.</param>
/// <param name="HeadSha">The head commit of the revision under review.</param>
/// <param name="BaseSha">The base commit the revision is compared against.</param>
/// <param name="ChangedPaths">The frozen changed-path scope of this revision.</param>
/// <param name="ExistingThreads">
///     The conversation already on the review. The reviewer reads it to avoid raising again what a reviewer
///     has already answered, so an executor without it would post duplicates of findings the author has
///     addressed.
/// </param>
public sealed record RunnerReviewTarget(
    string Provider,
    string OrganizationUrl,
    string ProjectId,
    string RepositoryId,
    string RepositoryName,
    string ExternalReviewId,
    int Number,
    int IterationId,
    string Title,
    string? Description,
    string SourceBranch,
    string TargetBranch,
    string HeadSha,
    string BaseSha,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<RunnerReviewThread> ExistingThreads);

/// <summary>
///     One conversation thread on the review, reduced to what a reviewer reads: where it sits, whether it
///     is still open, and what was said. Author identity beyond a display name and the provider's internal
///     ids are left out, because nothing on the executor's side uses them.
/// </summary>
/// <param name="FilePath">The file the thread is anchored to, or null for a review-level thread.</param>
/// <param name="LineNumber">The line the thread is anchored to, when it has one.</param>
/// <param name="Status">The thread's status as the provider reports it.</param>
/// <param name="Comments">The comments, oldest first.</param>
public sealed record RunnerReviewThread(
    string? FilePath,
    int? LineNumber,
    string? Status,
    IReadOnlyList<RunnerReviewThreadComment> Comments);

/// <summary>One comment in a thread.</summary>
/// <param name="AuthorName">The author's display name.</param>
/// <param name="Content">What they wrote.</param>
public sealed record RunnerReviewThreadComment(string? AuthorName, string Content);

/// <summary>
///     Where the executor fetches repository content. The control plane serves its own mirror over the git
///     wire protocol, authorized per lease, so the executor never holds a source-control credential and
///     never contacts the source-control system.
/// </summary>
/// <param name="FetchPath">Path on the control plane the executor fetches from, relative to its base URL.</param>
/// <param name="HeadSha">The commit the head worktree is materialized at.</param>
/// <param name="BaseSha">The commit the base worktree is materialized at.</param>
/// <param name="MaxTransferBytes">
///     Ceiling on the transfer. Exceeding it fails the job with an operator-readable reason rather than
///     moving unbounded data to a host that may be paying for the egress.
/// </param>
public sealed record RunnerWorkspaceReference(
    string FetchPath,
    string HeadSha,
    string BaseSha,
    long MaxTransferBytes);

/// <summary>
///     One entry of the resolved pass list.
/// </summary>
/// <param name="Ordinal">Position in the list; a pass's ordinal is how its trace is identified.</param>
/// <param name="Model">The model this pass runs on.</param>
/// <param name="Lens">The specialist lens this pass applies, or null for an ordinary pass.</param>
/// <param name="Scope">Whether the pass runs per file or once over the whole change set.</param>
/// <param name="Shadow">
///     Whether the pass runs for comparison only. A shadow pass records its full trace and never publishes,
///     which is the reason for running one, so an executor that ignored this flag would post findings from a
///     pass the client is still evaluating.
/// </param>
public sealed record RunnerReviewPass(
    int Ordinal,
    RunnerModelBinding Model,
    string? Lens,
    string? Scope,
    bool Shadow);

/// <summary>
///     A model the executor may call, named rather than connected.
///     <para>
///         The relay resolves <paramref name="LogicalModelName" /> to a connection and a credential on the
///         control-plane side, which is what keeps the key off the executor. Everything else here is
///         non-secret description the pipeline needs before it makes a call: which tokenizer counts the
///         prompt, what fits in the context window, and which behaviours the model supports.
///     </para>
/// </summary>
/// <param name="LogicalModelName">The named model role the relay resolves.</param>
/// <param name="RemoteModelId">The provider's model identifier, recorded on the trace.</param>
/// <param name="ProviderKind">The provider family, which decides prompt-shape details.</param>
/// <param name="ReasoningEffort">The reasoning effort this binding asks for.</param>
/// <param name="TokenizerName">The tokenizer that counts this model's prompts, when one is configured.</param>
/// <param name="MaxInputTokens">The largest prompt the model accepts, when known.</param>
/// <param name="MaxContextTokens">The context window the budgeter degrades against, when known.</param>
/// <param name="SupportsPromptCaching">Whether the model can serve a cached prefix.</param>
/// <param name="SupportsToolUse">Whether the reviewer may offer this model tools.</param>
/// <param name="SupportsStructuredOutput">Whether findings can be requested as a structured response.</param>
public sealed record RunnerModelBinding(
    string LogicalModelName,
    string RemoteModelId,
    string ProviderKind,
    string ReasoningEffort,
    string? TokenizerName,
    int? MaxInputTokens,
    int? MaxContextTokens,
    bool SupportsPromptCaching,
    bool SupportsToolUse,
    bool SupportsStructuredOutput);

/// <summary>Prompt configuration for this review, resolved from client configuration at dispatch.</summary>
/// <param name="Language">The output language contract for findings, when the client sets one.</param>
/// <param name="Aggressiveness">The configured review aggressiveness.</param>
/// <param name="Overrides">Prompt overrides by prompt key.</param>
public sealed record RunnerPromptConfiguration(
    string? Language,
    string? Aggressiveness,
    IReadOnlyDictionary<string, string> Overrides);

/// <summary>
///     W3C trace context carried from the lease into the executor's spans and back, so one review can be
///     followed across the boundary instead of appearing as two unrelated traces.
/// </summary>
/// <param name="TraceParent">The <c>traceparent</c> header value.</param>
/// <param name="TraceState">The <c>tracestate</c> header value, when present.</param>
public sealed record RunnerTraceContext(string TraceParent, string? TraceState);
