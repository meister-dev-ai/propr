# Runner execution contract

This page is the source of truth for both sides of distributed review execution: the control plane that
offers work, and the runner that performs it. A control plane and the runners it serves are deployed at
different times, so the two will disagree eventually.

## Versioning

The contract carries a single integer version covering all operations. A runner reports the version it
speaks when it asks for a lease, and the control plane validates it before offering any work.

- **Current version**: 3
- **Compatibility window**: one prior version

A runner inside the window is served. One outside it is refused with a diagnostic naming both the version
it reported and the range the control plane accepts, and saying which side is the older one.

Evolution within the window is **additive, and readers are tolerant**: an older peer ignores fields a
newer one sent, so a control plane can add to the manifest without refusing every runner that has not been
upgraded yet.

A field whose meaning or structure moved is a shape change, and tolerance does not cover it. A version that
changes shapes raises the **manifest floor** (`OldestManifestCompatible`, currently 3). The floor clamps the
whole served window: a runner below it is refused by the offer, the heartbeat and the execution surface
alike, with one diagnostic naming the shape change. While the floor equals the current version, as it does
at 3, no older runner is served at all. The one-prior window resumes at the next additive version.

Version 3 removed the comment-screening and evidence-verification switches from the review behaviour,
because the runner judges every finding. A runner of version 2 would still run the removed stages, so a
version 3 control plane refuses it. Upgrade the runner image together with the control plane.

The heartbeat carries the runner's version too, so a control-plane deploy mid-review shows up as a refused
renewal naming the skew. A runner old enough not to send it is gated at its next lease.

## Operations

| Operation | What it is for |
|---|---|
| `runner.register` | Enroll with an operator-issued registration token and receive a runner credential |
| `runner.credential.renew` | Renew that credential, keeping the same identity and stamped scope |
| `runner.lease` | Ask for a job; answered with a manifest, or with nothing when none matches |
| `runner.heartbeat` | Renew the lease and receive the control plane's directive in return |
| `runner.lease.release` | Hand a lease back, saying why: a drain costs the job nothing, a failure spends one of its reclaim attempts |
| `runner.workspace.fetch` | Fetch repository content from the control plane's mirror, authorized per lease |
| `runner.tools.call` | Call a review-context tool that needs a credential the runner does not hold |
| `runner.memory.reconsider` | Reconsider one file's draft against thread memory |
| `runner.ai.chat` | Relay a chat completion, where usage is priced against the resolved model and the hard cap is enforced. The request carries the call's portable options: tool declarations (name, description, parameter schema; the implementations stay on the runner and the model's calls travel back), temperature, output ceiling, and the reasoning knobs in neutral terms |
| `runner.ingest` | Ship a batch of trace events, per-file results, and spend |
| `runner.prior-results` | Read back what an earlier attempt at this job already reviewed |
| `runner.findings.submit` | Submit findings for the control plane to deduplicate and publish |

`runner.register`, `runner.credential.renew` and `runner.lease` run before the caller holds a job. Every
other operation carries the caller's job identity and lease generation and is authorized against them. A caller presenting a superseded generation is refused even for its own job.

### Successful completion heartbeat

A heartbeat for the generation that completed successfully returns `accepted: false`,
`expiresAt: null` and `stopReason: "Completed"`. The runner stops heartbeats and allows its in-flight
findings submission to finish. The durable result is stored before awaited completion hooks and
Code Insights intake. Further execution calls still require a current lease. Stale generations and
other refused renewals retain their cancellation behavior; host shutdown and caller cancellation
still cancel the submission.

Deploy the control plane and runner with support for this reason together. A runner without this
handling cancels an in-flight submission when it receives `Completed`. The response shape and
contract version are unchanged.

## Runner dispatch eligibility

A runner requests a lease only when it has a free slot, and reports how many it has. The control plane
keeps no view of runner capacity.

A runner may be offered a job when all of the following hold.

1. **Tenant.** Never optional. A runner is offered nothing outside the tenant it enrolled into.
2. **Stamped client scope.** The clients the server wrote onto the registration. Empty means every client
   in the tenant. Nothing a runner sends can widen it.
3. **Tags.** The runner declares every tag in the client's `required_runner_tags`, a comma-separated
   list on the client. Tags narrow within the stamped scope and never widen it.

Candidates are ordered fairly across clients: every client's oldest pending job is offered before any
client's second-oldest.

Winning a candidate is the same conditional claim the in-process worker uses, so two runners offered the
same candidate resolve it in the database. A job that cannot be prepared, or whose manifest cannot be
resolved, is returned to the queue and the next candidate is tried.

A pending job whose client requires a tag no active runner declares is reported as **unroutable**.

## Model opportunity evidence

The server-dispatched manifest freezes optional actual model connection/configuration IDs, pass ordinal,
scope, lens and shadow assignment, file inventory, source connection, revision, attempt and lease generation. Finding-producing shared execution checkpoints
carry optional `modelEvidence`; job-level checkpoints use `modelManifests`. Old peers without these fields
remain valid but cannot establish model recall/F1 evidence. Invalid optional analytics metadata becomes
unknown without rejecting ordinary file results. It does not change publication or carry-forward policy.

Capture schema `model-opportunity-v1` distinguishes positively completed valid empty generation from
skips, failures, cancellation and invalid output. Candidate completeness is retained before verification.
The control plane validates nested manifest proofs and split file proofs against the same dispatched
source, actual changed-file paths, model, runtime, configuration and pass scope. PR-wide proof identifies a
finding-producing synthesis or investigation stage. Missing assignment metadata remains unknown.
The control plane validates every certificate against the dispatched bindings and held generation at the
durable write. Each pass retains its first complete server acceptance; claimed completion timestamps or a
first transport fragment cannot backdate it. A later B checkpoint cannot move A's original receipt.
New attempts and lease generations receive current dispatch bindings and a new inventory receipt.
Compatible certificates from the same source and revision retain their original pass identities and
receipts; they do not certify the new dispatch. A same-file resumed checkpoint may contain both prior and
current passes. Prior content must match server-retained proof for the same source, revision, input and
ranges; the server restores its retained receipts. Runner-claimed historical receipts do not establish that proof. Historical lookup is
bounded to 128 requested files and 32 MiB. Optional source-connection lookup failure leaves proof
unknown and permits ordinary execution.

The spool splits delivery within configured byte/item ceilings and freezes an unacknowledged sequence
until its acknowledgement arrives. Optional proof can use bounded checksum-qualified fragments: at most
3 MiB complete proof, 256 fragments and 16 KiB base64 data per fragment. Duplicates and reordering are
idempotent; incomplete, conflicting or invalid assemblies cannot establish scoring evidence. Ordinary
results remain deliverable when analytics proof exceeds its bound. Reclaimed same-attempt leases cannot
retain proof under an earlier generation.
Analytics-only outcomes, including rejected malformed fragments, do not reset or replace ordinary file
failure checkpoints.

## Withheld finding evidence

Per-file ingest outcomes, prior-result responses and final submission annotations carry optional
`withheldFindings` arrays. Each entry contains the original review comment and its withholding reason.
Entries require a comment and a nonblank `reasonCode` of at most 128 characters. Null entries and
malformed evidence receive HTTP 400 before ingestion or publication.
These entries remain separate from comments submitted for SCM publication. The control plane stores
checkpoint evidence for recovery and passes final evidence to authorized Code Insights collection.

An omitted or null array means no withheld evidence was supplied. Older payloads remain valid, and batch and
submission retries retain their existing identifiers. An older peer ignores these additive fields;
end-to-end retention requires both the runner and control plane to support them.

Each model binding has an optional `supportsLowReasoningEffort` flag. The control plane sets it only
when the exact model advertises low effort and the selected transport supports it. Missing or false
leaves automatic judge effort at provider defaults. The output budgets and bounded recovery still apply.

Chat options carry optional `toolMode: "none"` when tool declarations must remain present but no further
lookups are allowed. An omitted mode preserves provider defaults. Prior-result responses use the
`{ unavailable, value }` envelope; `value` contains the per-file checkpoints when available.

## The job manifest

The manifest is everything non-secret a review needs, resolved once when the job is dispatched. The runner
holds it for the duration of its lease and never persists it.

Resolving it once lets a host without database access run the review, and it stops a configuration change
made mid-review from altering a review already in progress.

| Field | What it carries |
|---|---|
| `contractVersion` | The version the manifest was written against |
| `jobId`, `clientId` | Which review, for which client |
| `leaseGeneration` | The generation the manifest was issued under, presented on every proxied call |
| `target` | Provider, repository, review number, title, description, branches, head and base commit, the frozen changed-path scope, and the conversation already on the review |
| `workspace` | Where to fetch repository content from, at which commits, and the transfer ceiling |
| `defaultModel` | The model every stage that does not name a pass runs on |
| `passes` | The ordered pass list, each with its own model binding |
| `prompts` | Output language, aggressiveness, and prompt overrides |
| `exclusions` | Paths the client excludes from review |
| `repositoryInstructions` | Repository instructions, already fetched |
| `budgetHeadroomUsd` | Remaining spend before the hard cap, when one is configured |
| `traceContext` | W3C trace context, so one review is followable across both processes |
| `behaviour` | The per-client decisions that change what the review does, not which model runs it, and whether the review may record the model's reasoning |
| `linkedItems` | The work items linked to the review, discovered and bounded at dispatch |
| `servedBy` | The granting replica's advertised base URL, when the operator sets one; every job-scoped call goes there |
| `parallelReviewExecutionLicensed` | Whether files may fan out in parallel, resolved at dispatch |
| `timeLimit` | The maximum review duration of the tenant and the seconds left of it when the manifest was issued, when the tenant sets one |

### Secret handling

The manifest has no field for a credential, a connection string, or a key. A test walks the whole schema
graph and fails if one is added.

A model binding names a **logical model**, never a connection. The relay resolves that name to a stored
connection on the control-plane side, so the provider key stays off the runner. The rest of a binding
(remote model id, provider family, tokenizer, and the two token limits) is what the runner needs to count a
prompt and budget its context before it makes the call.

A client whose review purpose resolves to a connection, not to a named model, cannot be dispatched to a
runner. The refusal names what to configure.

A pass list containing a publishing `pr_wide` entry runs in-process because runners do not compose a
PR-wide generator. Lease dispatch skips those jobs before claiming them, and manifest preparation also
checks eligibility. The in-process worker logs a warning for each such job. Shadow `pr_wide` entries
remain eligible for runner dispatch because they do not publish findings.

`behaviour` contains client settings unavailable to a runner without database access: multi-pass union,
linked-item context, temperature, pipeline profile, and reasoning capture. The field is optional. An
omitted field uses the runner defaults, including disabled multi-pass union.

`behaviour.captureReasoning` contains the tenant policy combined with the installation setting at dispatch.
It overrides the runner's `AI_CAPTURE_REASONING_IN_PROTOCOL` environment setting when present. A manifest
without this value uses the runner's environment setting.

The control plane reads the tenant's reasoning policy again when serving an AI relay response and when
persisting a spooled batch. A policy change during a lease applies to each subsequent operation. Persisted
trace events follow the policy active when the control plane accepted them.

The AI relay replaces the runner's reasoning-summary option with the effective tenant policy. It applies
that policy to fresh responses and cached responses. When capture is disabled, it removes reasoning content,
provider metadata from messages and responses, and raw provider representations that may contain reasoning.
A response whose reasoning exists only in metadata remains usable after that metadata is removed.
Reasoning token counts remain available for budget accounting.

The relay cache retains the original provider response in the memory of the replica that made the call.
If the tenant enables capture before an authorized retry, the relay can return reasoning from that cached
response. The cache is not persisted or shared between replicas. Entries expire when findings are submitted,
the lease is released, the abandoned-state sweep observes a terminal job, or the process stops. A retry
requires authorization against the original lease generation.

The ingest writer removes reasoning from spooled `protocol.ai_call` events when capture is disabled.
If a sample cannot be parsed into its components, the writer replaces the whole sample with a short note.
For example, an object where a serialized string is required cannot be redacted by component. Usage counts
remain unchanged.

When capture is disabled and no reasoning effort is configured, the relay removes provider request options
that the review pipeline did not create. Evaluating such options would execute caller code against a relay
client with different provider semantics, so the relay cannot verify their reasoning settings. It logs a
warning with the job and logical model and removes the request from a copy of the options. The caller's
original options remain unchanged.

`budgetHeadroomUsd` lets the runner stop before another call would exceed its captured estimate. The AI relay
enforces the current hard cap for every completion.

`timeLimit` contains `maxReviewDurationSeconds` and `remainingSeconds`. The runner counts `remainingSeconds` on its
own clock from the moment it receives the manifest, so a clock that differs from the control plane's does not move
the deadline. Once the time is up, the runner starts no further files and submits the findings of the files it has
reviewed. The annotations of the final submission carry `timeLimitReached`, `timeLimitSeconds`,
`timeLimitSkippedFilePaths` and `timeLimitSkippedPrWidePasses`. The control plane writes the summary note from them
and records the completion reason of the job. A runner of an earlier release ignores `timeLimit` and reviews every
file.

### Lease-scoped state

The granting replica registers a budget scope before returning the manifest. It removes the scope when
findings are published, the runner releases the lease, or the lease is reclaimed. The relay charges
completions to that scope and refuses calls without a registered scope. Uncapped clients still receive a
scope for usage accounting.

The replica also owns the repository mirror used for workspace fetches. On installations with multiple
replicas, each replica sets `RUNNER_ADVERTISED_URL`. The manifest's `servedBy` field contains that address,
and the runner sends execution, workspace, heartbeat, and release calls there. Enrollment, credential renewal,
and lease requests use the configured control-plane URL.

A manifest without `servedBy` uses the configured URL, as in a single-replica deployment. The runner releases
the lease and reports a refusal when the advertised URL fails the HTTPS requirement. Loopback URLs are
exempt. This protects the runner credential sent on job-scoped calls.

## Error shapes

A refusal carries a stable machine-readable code and an operator-readable message.

| Code | Meaning |
|---|---|
| `unsupported_contract_version` | The runner speaks a version this control plane cannot serve |
| `lease_not_held` | The caller does not hold the lease it presented, or holds a superseded generation |
| `registration_revoked` | The runner's registration has been revoked |
| `budget_cap_reached` | The job's hard cap is reached; no further completions are served |
| `payload_too_large` | The request exceeded a payload or batch ceiling |
| `slot_limit_reached` | The installation is not licensed to run reviews on runners |

The lease call answers `429` for one reason: the installation is not licensed to run reviews on runners. The
name `slot_limit_reached` comes from a per-runner slot ceiling that no longer exists, and it is kept because
the code is published on the wire.

An installation that has reached its concurrent-review limit is not refused. ProPR answers it with no work,
the same as an empty queue, so the runner keeps polling and takes the next review when a running one
finishes.

## Contract assembly

`src/MeisterDev.ProPR.Runner.Contracts` holds the version rule, the manifest schema, the operation names,
and the error shapes. It references nothing else in the solution, so the runner host cannot reach the domain
model, the database context, or the provider adapters.
