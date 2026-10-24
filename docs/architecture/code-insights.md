# Code Insights architecture

Code Insights collects review evidence and provides reviewer-performance measurements and saved reports.
Collection requires the commercial Code Insights capability and an enabled client collection policy.

For chart controls and score interpretation, see [Reviewer performance](../concepts/reviewer-performance.md).

## Collection

Reviewing and Crawling supply observations through provider-neutral contracts. Code Insights owns analytics
storage and calculations; PostgreSQL stores the evidence. Collection failures do not interrupt reviews,
comment publication or thread memory.

The durable job result is stored before awaited completion hooks and analytics intake. A heartbeat
that observes successful completion of its generation stops renewal without canceling that work.
Host shutdown, caller cancellation and stale lease generations retain their cancellation behavior.

```mermaid
flowchart LR
    Observations[Review and crawl observations] --> Evidence[Stored evidence]
    Evidence --> Measurements[Performance measurements]
    Measurements -->|Save snapshot| Reports[Saved reports]
```

## Evidence and identity

| Evidence | Retained information |
|---|---|
| Reviewer findings | ProPR records the producing model, finding type and kind, impact, likelihood, correctness confidence, derived risk, publication, outcome and duplicate evidence. |
| Human concerns | ProPR records the discussion, substantive and acted-on judgments, scope, classification and collection coverage. |
| Model opportunities | Versioned checkpoints retain source, revision, assigned file/range, actual model/pass, candidate completeness, terminal disposition and first durable server acceptance. |

Evidence is associated with its client, provider host or organization, repository and pull request.
Repeated observations of one publication count once; suppressed repeats receive no additional publication
credit. Connection edits do not change a captured source identity.

Finding types are assigned during review from the core vocabulary and the client's captured custom tags.
Synthesis and analytics ingestion preserve supported classifications. Background classification fills missing
types without replacing review-produced assignments. Invalid or missing type metadata does not discard findings.

Missing source identity, classification or judgment remains visible as unknown.

Finding assessments retain impact, likelihood, correctness confidence, derived risk and supporting evidence.
Evidence text is protected; scalar values support filtering. The server derives risk from its rubric and records
confidence provenance independently. Combined findings retain supported risk and the weakest material confidence.
Raw findings remain independent of publication formatting, and missing assessments remain unassessed.

Verification-withheld candidates retain their original comment, model and pass provenance, scope,
grounding and withholding reason separately from publishable comments. File checkpoints store this
evidence in nullable JSONB and restore it during job recovery. Resetting a file retry clears its old
evidence. Synthesis excludes carried-forward file evidence from the newly produced population.
Runner checkpoints, prior results and final annotations carry the same optional evidence.

Completed job results retain the full withheld collection in the existing result JSON independently
of collection authorization or success. An Infrastructure persistence serializer includes this internal
field; default HTTP serialization omits it. Older results without the field read as an empty collection.

Final review collection gives these candidates negative ordinals in a deterministic order, separate
from the nonnegative publication-candidate ordinals. Changes in the published population cannot reuse
their identities. They are `PolicyWithheld` observations without provider thread or comment receipts and contribute
to withheld coverage counts. They receive no publication or settled precision credit. Capability and
client collection-policy checks apply to this evidence through the existing collection path.

Thread observations update current outcomes, including reopened threads. Unfiltered Combined retains its
global reviewer-finding overlap exclusions. A separate consumer retains encrypted independent human
concerns before those exclusions, including substantive human confirmation in reviewer-owned threads.
Native original anchors propagate through live collection, historical collection and optional archives.
Its retention and enumeration acknowledgments are independent of legacy misses and outcomes; an optional
consumer failure cannot suppress successful legacy coverage.

Current thread observations receive neutral resolution intent from provider policies. Providerless
stored discussion text uses an explicit compatibility decoder for the historical
service-account identifier, summary prefix and generated-marker grammar. Those exclusions remain
global because the retained text does not establish a provider family or current runtime capability.
Disposition, harvesting and history import obtain normalized intent for saved discussion statuses through
the credential-free compatibility codec. Their account and publication exclusions retain the separate
providerless stored-evidence decoder.

Discussion consumers share one provider observation. PostgreSQL records independent consumer progress
so failed consumers can retry without repeating completed work. Collection and archive policies still
apply, and immutable measurements require acknowledged final evidence.

Active review admission defers resolution-memory generation to durable discussion refreshes. Memory
acknowledgments are checkpointed after successful processing, and interrupted replacements invalidate
their previous acknowledgment before writing new memory. Final close observations retain complete
consumer processing before evidence is sealed.

## Measurements

Performance measurements apply scoring premises to stored counts. Counts refresh as evidence is collected,
and background catch-up recovers missed updates. Filtering and range calculations reuse those counts.
Side-by-side views use a consistent set of evidence. Each view shows its latest evidence update time and
pending-update status separately from the response capture time.

Quality and performance measurements support combined assessment filters and keep unassessed findings separate.
Reprocessing preserves known evidence and rebuilds affected counts. Unreadable optional evidence remains
unassessed without interrupting projection of other findings.

Risk-specific recall and F1 require compatible independent human assessments. When those assessments are
unavailable, coverage records the limitation while publication precision uses the selected findings.
Persistence measurements use the first assessment in the requested window to define the cohort.

The catch-up worker projects findings and seals eligible pull requests before refreshing retained
provider evidence, including closed pull requests and reviews with no findings. Durable claims and
leases prevent stale completion. Retryable failures use a configured cooldown; missing usable connections,
source coordinates or provider identity, unsupported historical collection and missing observer dependencies
require repair and explicit requeue. Both database selection paths remove repair-required sources before
applying claim limits. Cancellation and lease expiry prevent completion, and the worker waits for active
operation cleanup. Attempt metadata does not extend retention or change scores.
[Configuration](../operate/configuration.md#code-insights) lists the
batch and time limits.

Candidate selection and managed gap qualification use the same whitespace rules for missing evidence
and native statuses. Nonempty source namespaces retain exact comparisons.

Historical sources use captured job coordinates and exact client, repository and pull-request ownership.
Missing finding or exposure namespaces are recovered from their own retained jobs. Human concerns and
coverage without a namespace require one unambiguous captured source. Existing namespaces remain fixed;
current connection edits do not establish historical identity. Conflicting human judgments remain
separate and unavailable. Corrected coverage requires a fresh complete provider read, and thread
eligibility retains its latest source watermark and exclusion state.

Source observation timestamps mark the read start; completion timestamps are stored separately.
This orders concurrent observations so a delayed response cannot replace newer retained evidence.
Unexpected historical failures log the unavailable stage, exception type and numeric diagnostic code
without exception messages, stack traces or payloads.
Each sweep logs attempted, observed, unavailable and disabled totals. Failed sources log their bounded
reason, stage and recovery disposition, including failures to acknowledge retained evidence.

Tenant administrators can requeue an authorized aggregate for a subsequent bounded sweep. Requeue clears
the previous attempt token, preserves evidence and retention timestamps, and rejects disabled collection
or an active lease. A cleared attempt token, a retained eligibility time and no failure reason identify an
explicit queued request. Captured recovery metadata distinguishes queued, active and completed attempts.
The live exclusions dialog refreshes progress within its inspected measurement. A queued request performs no provider collection. Missing usable local configuration
requires repair; a connection lookup exception remains retryable. Generic provider errors do not establish
remote repository deletion.

Each publication, human concern and source requires acknowledged retention before a claim completes.
Partial enumeration or missing identity keeps collection unavailable. Infrastructure selects ordinary
close reads and strict historical reads. Forgejo supports ordinary close collection; historical thread
correlation is unavailable. PR-wide comment receipts require one unique observed thread. Thread-local
comment receipts require the captured thread identity.

Consistent historical outcomes and human concerns preserve successful judgments, classifier provenance,
dimensions and judged discussion. Changed evidence uses bounded classification. Completed retained
populations consume no further provider or model calls. Live projections refresh; initial dispositions,
sealed measurements and saved reports retain their stored values.

The range calculation retains the identity of each source pull request before coalescing counts or applying finding
dimensions. Complete enumeration, successful human judgments and compatible attribution determine the
covered population. Unsupported sources are excluded from all TP, FP and FN inputs for recall and F1.
An unsuccessful refresh does not invalidate sufficient retained evidence. Successfully judged open
threads remain provisional and informational.

The default advisory minimums are 80% covered source pull requests and one covered pull request. Both are
configurable and captured with each response. Measurements below either advisory retain their supported
scores and display the coverage warning. Recall and F1 require at least one covered source and a nonempty
metric denominator. Zero misses is valid. Model recall and F1 qualify each effective selected model set
against original native concerns and complete applicable opportunities. Selected-model Combined uses any
compatible selected detector and requires known non-detection from every applicable selected opportunity.
Canonical concerns and source PR coverage are distinct unions, never sums of per-model populations.
Type or kind restrictions exclude sources with incompatible settled-miss classification before filtering
can remove those misses.

Calculation version `reviewer-performance-v2` returns coherent covered scenarios in `score` and independent
overall publication precision in `overallPrecision`. Each view captures a deduplicated exclusion catalog;
point references identify its excluded sources, reasons and recovery metadata. Optional browser links use
validated, unambiguous captured coordinates within the same database snapshot. Metric reads perform no
provider or model calls. The user interface distinguishes inactive periods from unsupported scores and
shows coverage for the inspected point or window end.

Capture schema `model-opportunity-v1` is independent of calculation version. Shared generation records
valid empty output and raw candidates before verification and publication. Per-file assignments close
atomically; partial preparation cannot establish non-assignment. A complete per-file checkpoint cannot
establish non-detection when the global file inventory is incomplete. Each actual configured runtime retains
its own connection/model IDs even when cohort labels coincide. Local checkpoints and the runner use the
existing result stores. Server acceptance validates dispatched identity, revision, attempt and current
lease generation. Optional proof failures preserve ordinary results. Bounded proof fragments assemble
only after complete checksum validation; the complete durable assembly establishes acceptance. Retry
batches stay immutable until acknowledged so new ordinary outcomes cannot be lost to replay.

The catch-up worker reconciles positively retained generation proof, including zero-result reviews,
through existing exposure and projection paths. Historical configuration, protocol silence and absent
findings cannot reconstruct missing certificates. PR-wide generation retains positive candidates but
currently lacks a complete assigned line-level scope for negative proof. Cross-revision reuse without
complete relevant-context proof remains informational. Metric reads invoke no collection or matching.

Independent concern matching uses bounded explicit candidate identities, allowlisted structured decisions,
classifier versions and exact source/candidate fingerprints. A negative requires a complete evaluated set.
Native concern identity includes both thread and root-comment identity within the source. Editing a
concern invalidates its equivalence links and cached inverse links; bounded graph overflow excludes the
affected source. Equivalence in one candidate chunk does not stop later detection matching.
Successful A survives unrelated B failures. Provider enumeration and concern classification have separate
completeness. A newer failed enumeration revokes model completeness without changing retained native
cutoffs or legacy coverage. Earlier failed applicable passes remain unknown beside a completed pass;
only positively later executions are ignored for the earlier concern. Retained retries use the original encrypted source and observation cutoff under the existing
discussion lease, exact provider namespace/connection and captured cooldown. They do not rerun reviews or
advance human enumeration time. Retry attempts are capped automatically; an authorized explicit retry can
reset failed attempts after repair. Missing original proof is nonretryable.
Permanent classifier input or equivalence limits offer no unchanged retry. Catch-up excludes unindexable
paths before its job limit and checks whole-source replay eligibility before consuming retry slots.

Model queries bound raw execution and concern metadata to 32 MiB each, 4,096 jobs and 100,000 checkpoints
and concern/decision rows. Deterministic preflight permits at most 8,192 concern/model evaluations and
1,000,000 candidate or pass references per snapshot; exceeding this budget rejects the query before
series or matrix traversal. Immutable candidate signatures are shared across concern evaluations.

The [API reference](../reference/api.md#reviewer-performance) defines query limits and request fields.

## Saved reports and deletion

A saved report captures its scope, counts, scoring premises, results, coverage policy, exclusion catalog and
evidence cutoff, including risk selections and observed rubric versions. Opening the report uses that captured response without recalculation. Its evidence update
time and pending-update status
describe the capture. Older reports retain their stored scoring version and identify absent coverage
metadata. Recovery actions are available only for current evidence. Every report operation requires access
to all included clients.

| Event | Effect on saved reports |
|---|---|
| Evidence or scoring changes | Saved reports retain their captured results. |
| Collection is disabled | Collection stops; authorized saved reports remain available until expiry or deletion. |
| Source evidence expires | Saved reports follow their own retention period. |
| A report expires or is deleted | ProPR removes the complete report. |
| A client or its collection data is deleted | ProPR removes every report containing that client's data, including reports spanning several clients. |

[Configuration](../operate/configuration.md#code-insights) defines collection, background processing and
retention settings. [Security](../reference/security.md) describes tenant access.
