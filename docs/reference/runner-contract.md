# Runner execution contract

This page is the source of truth for both sides of distributed review execution: the control plane that
offers work, and the runner that performs it. A control plane and the runners it serves are deployed at
different times, so the two will disagree eventually.

## Versioning

The contract carries a single integer version covering all operations. A runner reports the version it
speaks when it asks for a lease, and the control plane validates it before offering any work.

- **Current version**: 2
- **Compatibility window**: one prior version

A runner inside the window is served. One outside it is refused with a diagnostic naming both the version
it reported and the range the control plane accepts, and saying which side is the older one.

Evolution within the window is **additive, and readers are tolerant**: an older peer ignores fields a
newer one sent, so a control plane can add to the manifest without refusing every runner that has not been
upgraded yet.

A field whose meaning or structure moved is a shape change, and tolerance does not cover it. A version that
changes shapes raises the **manifest floor** (`OldestManifestCompatible`, currently 2). The floor clamps the
whole served window: a runner below it is refused by the offer, the heartbeat and the execution surface
alike, with one diagnostic naming the shape change. While the floor equals the current version, as it does
at 2, no older runner is served at all. The one-prior window resumes at the next additive version.

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

## Which runner gets which job

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
| `behaviour` | The per-client decisions that change what the review does, not which model runs it |
| `linkedItems` | The work items linked to the review, discovered and bounded at dispatch |
| `servedBy` | The granting replica's advertised base URL, when the operator sets one; every job-scoped call goes there |
| `parallelReviewExecutionLicensed` | Whether files may fan out in parallel, resolved at dispatch |

### No field can carry a secret

The manifest has no field for a credential, a connection string, or a key. A test walks the whole schema
graph and fails if one is added.

A model binding names a **logical model**, never a connection. The relay resolves that name to a stored
connection on the control-plane side, so the provider key stays off the runner. The rest of a binding
(remote model id, provider family, tokenizer, and the two token limits) is what the runner needs to count a
prompt and budget its context before it makes the call.

A client whose review purpose resolves to a connection, not to a named model, cannot be dispatched to a
runner. The refusal names what to configure.

The same applies to a pass list with a publishing pr_wide-scope entry. The executor composes no PR-wide
generator yet, so the offer skips such a job before claiming anything, and the manifest refusal backstops
the race. The in-process worker runs it, the one named exception to leaving a runner-eligible job for a
runner, and logs a warning per job so an operator relying on runner isolation sees each review that stayed
local. A shadow pr_wide entry still dispatches. It publishes nothing, so skipping it remotely changes
telemetry, not the review.

`behaviour` carries the settings a runner cannot read for itself, because they live on the client record
and a runner has no database: whether the pass list is unioned, whether the semantic screener and
evidence-backed verification run, whether linked items are offered, the review temperature, and the
pipeline profile. Omitting the field sends every one of them to its default, and the pass list above does
nothing unless the union is on. The field is optional so a manifest from an older control plane still
deserializes; a runner reading one without it behaves as it did before the field existed.

`budgetHeadroomUsd` is an optimisation, not an enforcement point. It lets a runner wind down instead of
being refused mid-pass, and it is stale the moment it is written. The AI relay is where the cap is enforced.

### Holding a job open

The replica that grants a lease registers a **budget scope** for the job before the manifest leaves, and
drops it when the job publishes, when the runner hands the lease back, or when the lease is reclaimed.

The relay charges every completion against that scope and refuses when it cannot find one. The scope both
records what a cap is enforced against and marks the job as this replica's to serve. A runner fetches its
workspace from the replica that granted its lease for the same reason: the mirror is local disk, not shared
storage.

On a multi-replica installation the runner finds that replica through the manifest's `servedBy` field. Each
replica advertises its own reachable address (`RUNNER_ADVERTISED_URL`) and the lease carries it. The runner
directs everything job-scoped there: the execution surface, the workspace fetch, the heartbeat, and the
release. The configured control-plane URL keeps serving what is not job-scoped: enrollment, credential
renewal, and asking for work. A manifest without the field means the configured URL serves the job, which is
the single-replica case. The runner hands the lease back when the advertised address is not https (loopback
exempt), for the same reason it refuses such a configured URL: the credential rides on every call.

**A scope is registered for every leased job, including one belonging to a client that configures no
caps.** That client gets a scope with nothing to trip: metered, never stopped, exactly as the in-process
path behaves.

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

## Where this lives

`src/MeisterDev.ProPR.Runner.Contracts` holds the version rule, the manifest schema, the operation names,
and the error shapes. It references nothing else in the solution, so the runner host cannot reach the domain
model, the database context, or the provider adapters.
