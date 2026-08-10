# Where each review collaborator runs

Review orchestration reaches roughly twenty-five collaborators at the top level, and each of the
sub-orchestrators it drives carries its own tree. Before any of that can run outside the control plane,
every one of them has to be placed in exactly one of four groups. This page is that placement, and it is
the thing to update when a collaborator is added: an unplaced dependency is how a review ends up working
locally and failing remotely for reasons that take a day to find.

The four groups:

- **Manifest-resolved**: a configuration input, read once at dispatch and carried in the job manifest.
- **Workspace-bound**: needs a local working copy, which the executor has, because the control plane
  replicates the repository to it over the git protocol.
- **Proxy-backed**: needs a credential or a service the executor must not reach, so it is called through
  the control plane behind the unchanged port interface.
- **Control-plane-only**: the executor never calls it at all; it is a step the control plane performs
  before or after execution.

## Manifest-resolved

| Collaborator | Note |
|---|---|
| `IClientRegistry` | Pass list, baseline effort, output language, custom system message, per-client flags |
| `IRepositoryExclusionFetcher` | Patterns resolved at dispatch |
| `IRepositoryInstructionFetcher` | Instructions fetched at dispatch |
| `IRepositoryInstructionEvaluator` | Relevance decided at dispatch; it reads the changed set |
| `IPromptOverrideService` | Overrides resolved at dispatch |
| `AiReviewOptions` | Host configuration, present on both sides independently |
| `IReviewPipelineProfileProvider` | A static catalogue with no database behind it |
| `IBudgetCapsProvider`, `IReviewSpendAccumulator` | Only as the headroom figure; enforcement stays central |

## Workspace-bound (runs on the executor)

| Collaborator | Note |
|---|---|
| `IStructuralCodeAnalyzer` | Tree-sitter and Roslyn parsing over local files |
| `IReviewRepositoryWorkspaceManager` | Materialises base and head worktrees from the replicated mirror |
| Repository search and cross-file reference resolution | Reads the local working copy |
| File content reads | Reads the local working copy |

This group is why the runner image is not small: it carries git, the structural-analysis natives, and
tokenizers. That is a stated cost, not an accident.

## Proxy-backed

| Collaborator | What is proxied |
|---|---|
| `IReviewContextTools` (six of eighteen operations) | See the split below |
| `IProCursorGateway` | Code-knowledge lookups, kept behind the existing gateway boundary |
| `IThreadMemoryService` | Per-file memory retrieval and reconsideration |
| `IAiReviewCore` / `IChatClient` / `IAiChatClientFactory` | Chat completions, through the relay |
| `IAiRuntimeResolver`, `ILogicalModelResolver`, `IAiConnectionRepository` | Not proxied as such: the manifest names a logical model and the relay resolves it centrally |
| `IProtocolRecorder` | Trace events, buffered locally and shipped in batches |
| `IJobRepository` (per-file result writes only) | Through the batched ingest operation |

## Control-plane-only

| Collaborator | Why |
|---|---|
| Publication services (`IScmProviderRegistry` publication half) | The control plane publishes |
| `IPostedCommentOriginStore`, `IPostedFindingIndex` | Publication bookkeeping |
| `IReviewArchiveIngestionService`, `ICodeInsightFindingIngestionService` | Post-publication ingestion |
| `IReviewJobExecutionStore`, `IReviewJobLeaseStore` | Job state and leases |
| `IReviewPrScanWatermarkStore` | Intake bookkeeping |
| `IReviewJobCancellationRegistry` | Process-local by definition; the heartbeat is the remote channel |

## How the review-context tools split

`IReviewContextTools` is eighteen operations, and they do not all belong on the same side. Twelve read the
working copy, which the executor has, so proxying them would turn the bulk of a review into per-call
network traffic for no gain. Six need a credential or a service the executor must not reach.

**Proxied (six):**

| Operation | Why |
|---|---|
| `GetChangedFilesAsync` | Source-control metadata |
| `AskProCursorKnowledgeAsync` | Code knowledge, kept behind the existing gateway |
| `GetProCursorSymbolInfoAsync` | Code knowledge, kept behind the existing gateway |
| `GetLinkedItemDetailsAsync` | Work-item metadata from the provider |
| `GetLinkedItemDiscussionAsync` | Work-item metadata from the provider |
| `ResolveLinkedItemAsync` | Work-item metadata from the provider |

**Executor-side against the replicated workspace (twelve):** `GetFileTreeAsync`, `GetFileContentAsync`,
`SearchSourceRepoAsync`, `SearchSourceChangedFilesAsync`, `SearchTargetRepoAsync`,
`SearchTargetChangedFilesAsync`, `SearchCodeAsync`, `SearchPathsAsync`, `GetRepositoryOverviewAsync`,
`GetFileNeighborhoodAsync`, `FindReferencesAsync`, `GetDefinitionAsync`.

This is the ratio the whole design rests on. Cross-file reference resolution and repository search are the
chattiest things a review does, and they are all in the second group.

## Every proxied call is authorized against the lease

An executor is semi-trusted: it runs the analysis, but the control plane does not own its lifecycle, and a
compromised or merely stale one must not act on a job it no longer holds. Every proxied operation presents
the job, the lease generation it believes it holds, and its own identity, and all three are checked against
the job's current state on every call rather than trusted from a token the caller carries. A lease can be
reclaimed at any moment, and a caller that was legitimate a second ago is precisely the caller this stops.

Refusals are distinguished rather than merged: a superseded generation and a caller that never held the
lease are different problems, and an operator reading the audit needs to see which happened.

## Two placements the market requirement got wrong

Writing the classification down surfaced two collaborators the requirement placed in a group they cannot
be in. Both are now decided.

### Thread memory is proxied

`FileReviewer` calls `IThreadMemoryService.RetrieveAndReconsiderAsync` once per file. It reads the thread
memory store and computes embeddings, so it cannot run on a credential-free executor, and it is not one of
the three things the requirement lists as proxied (source-control metadata, code-knowledge lookups, AI
completions).

**Decided: it becomes a fourth proxied lookup.** The alternative was resolving per-file memory into the
manifest up front, which changes what memory is for: the point of it is to reconsider against what is
there at the time, not against a snapshot taken before the review started.

### Deduplication runs on the executor

`ReviewSynthesisExecutor` takes `IFindingDeduplicator` directly. The requirement places synthesis on the
executor and deduplication in the control plane, and both cannot hold while one calls the other.

**Decided: synthesis-time deduplication runs on the executor**, where synthesis already is. This works
without any proxying of its own: `SemanticFindingDeduplicator` is anchor-overlap arithmetic plus an AI
merge judge, and the judge reaches its model through the relay like every other completion.

This does not weaken the invariant that submitted findings enter the existing intake exactly as in-process
results do. There are two deduplication layers, and this decision moves only the first. The
publication-time layer, along with thread memory, posted-comment origins, and the posted-finding index,
stays in the control plane, so what a runner submits is deduplicated against what has actually been posted
by the same code path that handles an in-process review.

## What this means for the offload thesis

The thesis survives, and the tool split is the evidence. The earlier concern was that a credential-free
executor is impossible because every tools factory needs a local git workspace; control-plane-served
workspace replication answers that, and twelve of the eighteen tool operations then run executor-side
against that workspace, including every chatty one. What stays proxied is source-control metadata, code
knowledge, AI completions, thread memory, and result ingestion, which is a bounded surface rather than the
whole review.
