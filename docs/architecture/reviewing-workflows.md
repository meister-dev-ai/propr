# Reviewing Workflows

This page covers the runtime path from provider-neutral review submission to posted provider-native
comments, plus ProRV-focused guidance, filtering, verification, synthesis, final-gate, dedup, and
token-control mechanics that keep the review loop safe and efficient.

## Review Strategies

The Reviewing module currently allows new review jobs to select only `FileByFile`, the baseline
per-file review path.

Historical persisted jobs may still report `AgenticFileByFile` or `PrWideAgentic` in diagnostics,
but those strategies are disabled for new selection and for runtime execution.

## Review Submission And Execution

```mermaid
sequenceDiagram
    actor Caller
    participant MW as AuthMiddleware
    participant RC as ReviewJobsController
    participant JR as JobRepository
    participant WK as ReviewJobWorker
    participant PF as ICodeReviewQueryService
    participant AI as ToolAwareAiReviewCore
    participant CP as ICodeReviewPublicationService

    Caller->>MW: POST /clients/{clientId}/reviewing/jobs
    MW->>MW: resolve UserId, IsAdmin, ClientRoles
    MW->>RC: forward request
    RC->>RC: require ClientAdministrator for {clientId}
    RC->>RC: verify X-Ado-Token for Azure DevOps compatibility requests
    RC->>JR: FindActiveJob()/AddAsync()
    JR-->>RC: jobId (new or existing)
    RC-->>Caller: 202 Accepted { jobId }

    loop every 2 seconds
        WK->>JR: DequeueNextPendingAsync()
        JR-->>WK: ReviewJob
        WK->>PF: FetchPullRequestAsync()
        PF-->>WK: PR diff + file contents
        WK->>AI: ReviewAsync(pr, files)
        AI-->>WK: ReviewResult (summary + comments)
        WK->>CP: PostCommentsAsync(result)
        CP-->>WK: done
        WK->>JR: MarkCompletedAsync(jobId, result)
    end
```

Review submission depends on the caller identity resolved in [security-and-access.md](security-and-access.md)
plus optional `X-Ado-Token` validation for Azure DevOps identity checks. The intake controller
accepts provider-neutral repository, review, and revision identities for Azure DevOps, GitHub,
GitLab, and Forgejo-family reviews. The worker claims the job, resolves the provider-specific query
and publication adapters through the shared provider registry, runs the AI review, and posts
comment threads back to the originating provider.

Production execution enters through `ReviewOrchestrationService`, and offline evaluation enters through `ReviewWorkflowRunner`. Both flows resolve strategy and profile routing through `IReviewStrategyDispatcher`, which now permits only `FileByFile` execution.

## Offline Prompt Experiment Harness

The offline harness can replay one fixture as a batch of baseline and named prompt variants without
changing production review behavior. This path stays behind `ReviewWorkflowRunner` and
`PromptExperimentBatchRunner`; production orchestration does not accept or persist prompt experiment
configuration.

1. `PromptExperimentBatchRunner` validates one `PromptExperimentBatch`, resolves the offline chat
   runtime once for the batch, clones the target `ReviewJob` per run, and attaches a
   `PromptExperimentContext` only for that offline execution.
2. Shared prompt construction remains centralized in `ReviewPrompts`. Offline variants target a
   neutral prompt-stage catalog keyed by stage ids such as `per_file_user`, `synthesis_system`, and
   `pr_verification_user`.
3. Each stage variant declares one prompt role (`system` or `user`) plus one composition mode:
   `replace`, `prepend`, or `append`. Unconfigured stages keep their default prompt construction.
4. Prompt-stage evidence is recorded explicitly at the prompt-driven execution sites rather than
   inferred only from protocol labels. The recorder stores stage key, variant name, composition
   mode, default-vs-variant usage, and the actual system and user prompt text used.
5. Offline artifact assembly projects that evidence into `EvaluationArtifact` stage records so one
   artifact can show both stage execution and the exact prompt content that produced it.
6. When explicit prompt-stage evidence is absent, offline projection keeps a fallback inference path
   for older protocol data so previously captured artifacts remain readable.

This boundary lets reviewers compare baseline and variant runs by stage and prompt text while
preserving the default production prompt path when no offline experiment context is present.

Provider operational readiness is evaluated separately from onboarding verification. A provider
connection can be verified and ready for onboarding while lacking workflow-complete proof, such as
enabled scope or full host-variant support evidence. Reviewer-trigger identity is surfaced as an
optional narrowing filter, not as an authentication requirement. The admin provider-operations view
and the health surface consume these readiness states rather than relying on
`verificationStatus == verified` alone.

## Per-File Comment Relevance Filter

The Reviewing module includes a code-selected comment relevance filter stage between deterministic
hard guards and the downstream thread-memory, persistence, synthesis, and publication flow.
Startup selects `hybrid-v1` through `Program.GetSelectedCommentRelevanceFilterId()`.

1. `IAiReviewCore` produces the initial per-file `ReviewResult`, and deterministic hard guards
   normalize low-confidence or malformed comments before any filter runs.
2. When a named filter implementation is selected in code, `FileByFileReviewOrchestrator` builds a
   `CommentRelevanceFilterRequest` and runs either `pass-through-v1`, `heuristic-v1`, or
   `hybrid-v1` for that file. The configured path selects `hybrid-v1`.
3. Every implementation emits the same `RecordedFilterOutput` shape with implementation identity,
   counts, reason buckets, decision-source totals, discarded-comment details, degraded markers, and
   optional `aiTokenUsage`.
4. `hybrid-v1` reuses deterministic screening first, then asks `AiCommentRelevanceAmbiguityEvaluator`
   to adjudicate only ambiguous survivors on the client default review runtime carried in
   `ReviewSystemContext.DefaultReviewChatClient` and `DefaultReviewModelId`. Evaluator outages,
   parse failures, incomplete decision sets, and other failures fail open by keeping the affected
   comments and recording machine-readable `degradedComponents` plus `fallbackChecks`.
5. Only kept comments move into thread memory reconsideration, `ReviewFileResult` persistence,
   synthesis, and provider publication.

The filter uses the review protocol transport instead of introducing a separate storage path.
`comment_relevance_filter_output`, `comment_relevance_filter_degraded`,
`comment_relevance_evaluator_degraded`, `comment_relevance_filter_selection_fallback`, and
`ai_call_comment_relevance_evaluator` are stored on `ReviewJobProtocol` so the Job Protocol UI can
explain why a comment was discarded or retained.

## Session-Aware File Review Loop

The default `FileByFile` workflow now runs the shared multi-turn review loop with internal
session-aware continuation. This remains an internal execution concern; no public HTTP contract or
admin API shape changes are required for the slice.

1. `ToolAwareAiReviewCore` selects one session mode per file review pass from runtime capability and
   workflow context:
   `provider_managed_session` when the active chat runtime supports provider-managed continuation,
   `local_managed_session` for the per-file default path when provider-managed continuation is not
   available, and `stateless_replay` for paths that cannot safely retain session state.
2. In provider-managed mode, later turns send only the newest turn input while the runtime carries
   forward the earlier transcript through `ConversationId`, response-chain identifiers, and any
   provider continuation token retained in `ReviewLoopState`.
3. In local-managed mode, the loop keeps durable system framing plus the latest live turn while
   compacting earlier bulky tool payloads into working-memory summaries. Later turns therefore reuse
   bounded summaries instead of replaying every earlier large file read or search result in full.
4. When provider-managed continuation fails after an earlier successful turn, the loop downgrades to
   local-managed continuation, preserves the durable prompts and latest turn transcript, and
   continues the review instead of failing the entire file pass solely because the preferred
   continuation mode became unavailable.
5. `FileReviewer` owns the per-file protocol lifecycle around that shared loop. It carries the
   session and loop metrics into the file-scoped `ReviewSystemContext`, completes the protocol with
   final token and iteration totals, and preserves the existing retry boundary for failed file
   attempts.

The protocol surface remains event-driven. Each reviewed file pass can emit:

- `review_agent_session_binding` with the file-owned local session identifier, binding method,
  binding outcome, prompt mode, and remote conversation identifier when managed remote conversation
  binding succeeds
- `review_agent_session_turn` with turn number, selected session mode, context strategy,
  prompt mode, replay-summary metadata, compacted-payload summary, remote-vs-local continuation
  markers, and any provider session or response-chain identifiers
- `review_agent_session_fallback` with the downgraded-from mode, downgraded-to mode, fallback
  reason, turn number, and preserved-state description

Operators should interpret those events together with the normal AI call and tool call events:

- `sessionMode` identifies whether the pass used stateless replay, local managed continuation, or
  provider-managed continuation.
- `promptMode` identifies whether the turn established the remote conversation binding, continued it
  with only the current prompt, or fell back to explicit replay.
- `contextStrategy` distinguishes full-context submission from delta-context continuation on each AI
  turn.
- `replayedPayloadSummary` and `compactedPayloadSummary` provide the evidence that later tiny or
  empty follow-up results did not force the earlier bulky payloads to be retransmitted in full.
- `remoteConversationId` and `providerResponseId` are present only when the runtime exposes them.

For Agent Framework-backed managed remote conversations, the first successful managed turn creates a
session and binds it to the remote conversation. Later turns continue through that same session using
only the current prompt and do not re-pass the remote conversation identifier on each turn.

## Diagnostics Trace Search

The diagnostics surface keeps trace investigation inside one opened review's execution traces tab.

1. `JobsController` exposes the existing job protocol endpoints and applies the same client-role
   boundary used by the rest of the diagnostics endpoints.
2. `IReviewDiagnosticsReader.GetJobProtocolAsync(...)` and `GetJobProtocolPassAsync(...)` read existing
   `ReviewJob`, `ReviewJobProtocol`, and `ProtocolEvent` records without duplicating protocol payloads
   into a second store.
3. `ProtocolEvent.EventCategory` is stored as additive metadata on the existing trace rows and derived
   best-effort for legacy rows when the persisted category is absent.
4. The frontend execution traces tab loads all passes for the current review, derives suggestion-backed
   filters from that review-local data, and filters rows in place without navigating to a separate
   diagnostics route.

This keeps Job Protocol as the authoritative investigation experience while making stored execution
traces searchable across all traces in one review.

## ProRV Focused Guidance

The Reviewing module can run a ProRV prefilter stage before each file review. ProRV is a bounded
review-knowledge library that ranks the most relevant review checks for one changed file from the
file path, diff, and optional language hints. The ranked items become focused guidance for the
downstream file review rather than standalone findings.

1. `ProRVFocusedReviewGuidanceResolver.TryResolveAsync(...)` runs before the main file prompt for
   `FileByFile`.
2. The stage is skipped when ProRV is not configured, the file is binary, the file was deleted, or
   the diff is empty.
3. Runtime selection prefers a dedicated `proRvPrefilter` AI purpose binding. If that binding is not
   available, the stage falls back to the file review runtime instead of failing the whole review.
4. `IProRVPrefilter` resolves a supported language from file extension or technology hints, loads
   the embedded ProRV knowledge catalog for that language, and asks the configured chat runtime to
   rank the most relevant checks.
5. The result is bounded to a small set of ranked guidance items and recorded as protocol events.
   The review continues when ProRV returns no items or an unusable response.
6. `FileReviewer` and `AgenticFileReviewer` inject the ranked guidance into the file-scoped prompts
   and emit `prorv_focused_guidance_applied` when guidance influenced the review context.
7. ProRV narrows reviewer attention early, but verification, synthesis, deduplication, and the final
   finding gate stay authoritative for publication decisions.

## Synthesis And Final Finding Gate

```mermaid
flowchart TD
    CTX["Build review context"] --> PRORV["Optional ProRV prefilter"]
    PRORV --> FILE["Parallel per-file review"]
    FILE --> HARD["Hard guards"]
    HARD --> REL["Comment relevance filter"]
    REL --> MEM["Thread memory reconsideration"]
    MEM --> LVER["Local verification"]
    LVER --> STORE["Persist verified ReviewFileResult"]
    STORE --> SYN["Synthesis pass"]
    SYN --> DEDUP["Deduplicate per-file findings and optional quality filter"]
    DEDUP --> PVER["PR-level verification"]
    PVER --> GATE["DeterministicReviewFindingGate"]
    GATE --> RECON["Summary reconciliation"]
    RECON --> PUB["Publish comments"]
    RECON --> SUM["Finalize summary-only findings"]
```

1. `ReviewOrchestrationService.BuildReviewContextAsync(...)` creates `ReviewSystemContext` with
   repository instructions, exclusion rules, `IReviewContextTools`, and the client default review
   runtime (`DefaultReviewChatClient` and `DefaultReviewModelId`).
2. Before the main file prompt, ProRV can rank a small set of focused review guidance items from the
   changed diff by using the dedicated `proRvPrefilter` AI purpose when configured, or the review
   runtime as a fallback.
3. `ToolAwareAiReviewCore` reviews each file against diff-only prompts and can call
    `get_changed_files`, `get_file_tree`, `get_file_content`, the legacy scoped regex tools
    (`search_source_repo`, `search_source_changed_files`, `search_target_repo`,
    `search_target_changed_files`), the structured discovery tools (`search_code`,
    `search_paths`, `get_repository_overview`, `get_file_neighborhood`),
    `ask_procursor_knowledge`, and `get_procursor_symbol_info` while investigating a finding.
    Code and path search return structured result objects that distinguish success, no-match,
    partial, invalid-request, and bounded-tool blocked outcomes, and they reuse shared branch/scope
    resolution for exact identifier, exact phrase, regex, related-code, path-name, filter,
    deterministic ordering, truncation, and limitation reporting across live providers and offline fixtures. The per-file prompt carries a bounded changed-file manifest; full PR file lists remain available through `get_changed_files` when needed.
4. `FileByFileReviewOrchestrator` applies the confidence floor, speculative-language removal,
   `INFO` stripping, vague-suggestion removal, the selected comment relevance filter, and optional
   thread-memory reconsideration before extracting structured claims and running local verification.
   Deterministic contradiction checks use curated invariant facts, while evidence-needing local
   generic claims are withheld conservatively until a bounded verifier can support them.
5. Only publishable verified local findings are persisted through `ReviewFileResult.MarkCompleted(...)`.
   Contradicted claims are dropped, local claims that remain `SummaryOnly` are withheld from the
   stored comment set, and the per-file summary is rewritten from the surviving verified findings so
   synthesis input does not repeat unsupported local assertions.
6. `SynthesizeResultsAsync(...)` excludes carried-forward rows, resolves the high-effort review
runtime, and asks the model for both a PR narrative summary and structured synthesized
cross-cutting findings.
7. Synthesized cross-cutting findings carry `EvidenceReference` metadata from the synthesis payload:
   `supportingFindingIds`, `supportingFiles`, `evidenceResolutionState`, and `evidenceSource`.
   These fields are retrieval hints only; fetched files or support metadata do not prove a claim.
8. Deduplicated per-file comments and synthesized findings are merged into `CandidateReviewFinding`
instances. PR-level findings then route through targeted verification work items, evidence
retrieval through `IReviewContextTools`, and bounded AI micro-verification for unresolved claims.
Evidence collection records source attempts and ProCursor empty, unavailable, or successful result
states in the evidence bundle.
9. Eligible unresolved deeper-follow-up findings may enter one repeated-judgment pass. That pass
     reuses the same bounded evidence set and claim identity from the prior PR-level verification path;
     it does not widen search, re-run Stage B planning, or fetch broader repository context.
10. `DeterministicReviewFindingGate` consumes invariant facts plus any attached `VerificationOutcome`
     and decides `Publish`, `SummaryOnly`, or `Drop` deterministically.
11. Summary reconciliation rewrites or preserves the synthesis summary so the final summary matches
     surviving `Publish` and `SummaryOnly` outcomes rather than repeating dropped claims.
12. The protocol records machine-readable ProRV, verification, final-gate, and token-optimization events, including
      `prorv_prefilter_started`, `prorv_prefilter_skipped`, `prorv_prefilter_completed`,

## Protocol Timing Observability

The job protocol diagnostics surface preserves tool timing on the existing pass and event records rather
than sending timing to a separate telemetry store.

1. Each timing-enabled tool call records `startedAt`, `completedAt`, `durationMs`, optional
   `waitDurationMs`, optional `activeDurationMs`, `timingAvailability`, `toolOutcome`, and ordered
   `phaseTimings` directly on the protocol event.
1. Repository-search-related tools can record nested phases such as request preparation,
   provider/API work, SCM file tree or content fetches, repository search, retry backoff, result
   shaping, summarization, and payload bounding.
1. `timingAvailability` is explicit. `captured` means the displayed timing was measured,
   `not_captured` means the historical or legacy execution path never emitted that timing,
   `unavailable` means the path could not measure that dimension reliably, and `incomplete` means
   execution started but no trustworthy terminal timing was observed.
1. Operators should interpret `waitDurationMs` and `activeDurationMs` as optional refinements of
   `durationMs`, not independent totals. When those split values are absent, the UI should keep the
   whole-tool duration visible without implying zero wait or zero active time.
1. Phase lists are sparse by design. Missing phases mean the execution path did not capture them, not
   that the phase consumed zero time.
1. Large or sensitive tool arguments and results remain bounded summaries. Timing fidelity survives
   bounded payload replay so operators can compare slow calls without requiring raw large payload
   storage.
1. The traces experience keeps timing attributable to the pass and file context that already exists in
   `ReviewJobProtocol`. The sidebar can show each visible pass's slowest captured tool call, and the
   active pass can rank the slowest visible tool calls across loaded passes without leaving the job
   protocol page.
     `prorv_prefilter_failed`, `prorv_focused_guidance_applied`,
     `verification_claims_extracted`, `verification_local_decision`,
    `verification_evidence_collected`, `verification_pr_decision`, `verification_degraded`,
    `repeated_judgment_decision`, `summary_reconciliation`, `review_finding_gate_summary`, and
    `review_finding_gate_decision`, plus per-call cache status, cached input tokens, prefix eligibility,
    bounded tool-evidence metadata, and finalization attempt reason/outcome on the existing AI/tool events.

Repository discovery stays bounded by the same review-tool policy surface as the existing file tools.
When a search, overview, or neighborhood tool is not allowed or the tool budget is exhausted,
`BoundedReviewContextTools` returns a structured blocked result instead of throwing.
Changed-files-only target searches skip paths missing on the target branch and report those paths
as limitations rather than failing the whole request. Repository overview and file neighborhood
results are branch-specific, structured, and cacheable within one review-context instance; missing
neighborhood files produce explicit not-found limitations.

For the file-based strategies, the common per-file post-processing shape is explicit through Reviewing-owned pipeline profiles and a shared pipeline runner. The baseline and experimental profiles differ by ordered stage composition rather than by introducing a new top-level orchestrator.

## Final Finding Gate Rules

- `cross_cutting` findings publish only when an explicit bounded `VerificationOutcome` recommends
   `Publish`; raw resolved multi-file evidence is treated as a hint and is demoted to `SummaryOnly`
   when no verified claim support exists.
- Findings with explicit contradiction-backed local verification outcomes are dropped by the gate.
- Evidence-needing local generic findings that do not gain bounded support are withheld before
  persistence, so they do not re-enter the publish path through synthesis or final gating.
- Repeated-judgment findings publish only when the repeated pass recorded agreement on the same
  evidence set. Disagreement without explicit support is dropped.
- Findings with degraded or unresolved verification outcomes are treated conservatively by
   preferring `SummaryOnly` over `Publish`.
- Broad weak categories (`architecture`, `documentation`, `test`, `ui`, `configuration`, and
   `robustness`) are demoted to `SummaryOnly`.
- `non_actionable` findings and `consider ...` style comments are dropped.
- All remaining findings default to `Publish`.
- Curated invariant facts come from `DomainReviewInvariantFactProvider` and
  `PersistenceReviewInvariantFactProvider`.
- The gate is the last policy authority. Evidence collection and AI micro-verification execute
   upstream and surface their outcome through `VerificationOutcome` rather than bypassing the
   deterministic final decision path.

## Incremental Dedup And Publication

1. `ReviewOrchestrationService.BuildReviewContextAsync(...)` carries forward completed per-file
   results from the previous reviewed iteration for files with no new diff.
2. `FileByFileReviewOrchestrator.SynthesizeResultsAsync(...)` excludes `IsCarriedForward` file
   results from synthesis summaries, cross-file deduplication, and quality-filter input, while
   preserving `CarriedForwardFilePaths` and a carried-forward skip count on the final `ReviewResult`.
3. `ReviewOrchestrationService.PublishReviewResultAsync(...)` opens a dedicated
     `ReviewJobProtocol` pass labeled `posting`, reads the client-level
     `ScmCommentPostingEnabled` policy, conditionally calls the provider-specific
     `ICodeReviewPublicationService`, persists the final `ReviewResult`, and records aggregate
     duplicate-suppression diagnostics even when outbound SCM publication is skipped.
4. Publication identity is derived from authenticated provider connection metadata fetched with the
    PR, not from the configured reviewer-trigger identity. Optional reviewer assignment uses
     the configured trigger identity when present.
5. Azure DevOps publication receives provider-local publication context from orchestration so it can
   use the fetched thread list and the reviewed diff comparison baseline. Inline ADO anchors publish
   only when the finding has a positive line number and the diff exposes a matching
   `ChangeTrackingId` for the reviewed compare iteration. When that mapping is not trustworthy,
   publication falls back to a file-level thread when the file path is known, or to a PR-level
   thread when no trustworthy file anchor remains.
6. On incremental Azure DevOps reviews, the same publication context carries the effective
   publication identity used for bot-authorship checks. Summary suppression uses that identity to
   recognize a prior bot-authored summary even when the live connection cannot rehydrate the same
   author GUID, while fresh actionable findings in the reviewed run continue to publish normally.
7. GitHub, GitLab, and Forgejo publication normalize inline file paths by trimming whitespace and
   removing any leading slash before sending provider-native review payloads. Comments without a
   positive line number remain overview or summary content instead of claiming a false inline anchor.
8. The publication service evaluates each candidate finding against bot-authored PR threads using
   normalized file-path and anchor matching, resolved-thread reuse, exact normalized-text matching,
   pull-request-scoped thread memory similarity, and a deterministic text-similarity fallback when
   historical signals are degraded.
9. The posting protocol emits `dedup_summary` on every posting pass and `dedup_degraded_mode` when
   duplicate protection falls back to reduced checks.
10. AI-owned thread auto-resolution stays mode-aware behind the shared orchestration seam. In
    `CommentResolutionBehavior.WithReply`, a resolved thread must have a non-empty AI explanation,
    that reply is posted first, and only then is the provider-native thread status moved to
    `fixed`. In `Silent`, the same resolved outcome skips the reply and only updates status, while
    human-owned or already-resolved threads remain untouched.
11. Azure DevOps review-body rendering separates display formatting from duplicate normalization.
    Summaries, inline comments, and AI-authored thread replies preserve readable quotes and command
    syntax while neutralizing HTML-like markup by breaking tag parsing instead of blanket-encoding
    every quote character.

This keeps incremental reviews additive: carried-forward findings remain visible in stored review
history, but only genuinely fresh findings are allowed to create new provider-native threads, and
clients with SCM publication disabled retain internal review history plus posting-pass
diagnostics without creating new provider-native comments.

## Verification Responsibilities

| Concern | Runtime handling |
|---------|------------------|
| Candidate generation | Parallel per-file review plus one synthesis pass |
| Local truth checks | Structured claim extraction plus deterministic contradiction verification before persistence |
| Cross-file evidence gathering | Review tools and ProCursor are reused through targeted verification work items |
| Publication decision | `DeterministicReviewFindingGate` decides `Publish`, `SummaryOnly`, or `Drop` |
| Summary behavior | Post-gate summary reconciliation keeps final text aligned with surviving findings |

The runtime uses targeted verification that attaches evidence collection to specific claims and
retrieved context instead of running one PR-wide verification session.

## Job State Machine

```mermaid
stateDiagram-v2
    [*] --> Pending : POST /reviews (job created)
    Pending --> Processing : Worker dequeues job
    Processing --> Completed : AI review posted to provider
    Processing --> Failed : Exception / provider query or publication error
    Failed --> Pending : Retry (if retry count < max)
    Processing --> Failed : Stuck - no heartbeat > StuckJobTimeoutMinutes
    Completed --> [*]
    Failed --> [*] : Max retries exceeded

    note right of Processing
        ICodeReviewQueryService
        FileByFileReviewOrchestrator
        ICodeReviewPublicationService
    end note

    note left of Failed
        ReviewJobWorker cleanup
        runs on startup + every 10 min
    end note
```

The cleanup path runs at startup and on a periodic sweep so interrupted jobs can be recovered
without manual intervention.

## Token Optimization Pipeline

Several techniques work together to keep AI token consumption bounded per review.

### 1 - File Exclusion

```mermaid
flowchart TD
    START([Changed files in PR]) --> RULES

    RULES{"File matches<br/>exclusion pattern?"}
    RULES -- "yes" --> EXCL["MarkExcluded(pattern)<br/>Protocol: status=Excluded, 0 tokens"]
    RULES -- "no" --> REVIEW["Dispatch to IAiReviewCore"]

    EXCL --> SYNTH
    REVIEW --> SYNTH

    SYNTH([Synthesis - non-excluded files only])
```

Exclusion patterns are read from `.meister-propr/exclude` on the target branch. If the file is
absent, the built-in defaults apply (`**/Migrations/*.Designer.cs` and
`**/Migrations/*ModelSnapshot.cs`). An empty file disables all exclusions.

### 2 - Diff-Only Review Messages

The per-file review input contains only the unified diff for that file. Full file content is
omitted, and the AI is instructed to call the existing `get_file_content` tool if it needs more
context. This is the biggest token-saving measure for large files.

### 3 - Cache-Aware Stable Prefixes

`ToolAwareAiReviewCore` structures each file review as:

- Step 1: global system prompt (S1) + per-file context prompt (S2) + user message
- Step 2+: per-file context prompt (S2) only + accumulated conversation

S1 and the tool schema are kept stable ahead of volatile diff and tool-result content so providers
that support automatic prefix caching can reuse the prefix across parallel file slots for the same
pull request. Runtime capabilities record whether prompt caching and cache routing are expected, and
protocol events read `CachedInputTokenCount` when the provider exposes it. Operators compare raw input,
cached input, and effective input (`raw - cached`) per AI call; unobservable providers are reported as
unobservable rather than as cache misses.

### 4 - Bounded Tool Replay And Evidence Visibility

Large tool results are bounded before they are replayed into later loop turns. The replayed message
keeps a compact summary and tells the model when it can refresh the precise evidence through the same
tool. The protocol records the source tool, original payload token estimate, bounded replay token
estimate, action, and refreshability only when a tool result was actually bounded or summarized.

### 5 - Tier-Sized Output And Finalization Visibility

File complexity controls review output budget selection so low-risk files do not receive the same
worst-case output allowance as high-complexity files. Remaining forced-final and schema-repair calls
are recorded with attempt kind, reason, and outcome so full-context finalization costs are explicit in
the job protocol and in offline evaluation artifacts.

### 6 - Full Protocol Text Capture

Protocol diagnostics persist the full captured AI prompt, AI response, and tool call text for each
event. Recorder sanitization strips embedded null bytes so PostgreSQL can safely store the
content, but the persisted samples are no longer truncated before they reach the diagnostics API or
admin UI.
