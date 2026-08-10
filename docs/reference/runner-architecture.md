# Runner architecture

A runner is a host that executes reviews without being the control plane. This page covers what a runner
does, what stays on the control plane, when a review runs on one, and what a deployment looks like.

The wire-level contract is in [runner-contract.md](runner-contract.md). Where each pipeline collaborator
runs is in [runner-collaborator-classification.md](runner-collaborator-classification.md). The settings
are in [configuration.md](../operate/configuration.md), and scaling a fleet is in
[deploy.md](../operate/deploy.md).

## What a runner is for

A review spends CPU on repository work (clones, worktrees, searches, structural analysis) and time on
long-lived AI loops. Runners move that work onto hosts you control, so reviewing capacity is something
you add rather than a control-plane host you resize.

Three things stay on the control plane:

- **Credentials.** Provider tokens, AI connection secrets and the code-knowledge service. A runner
  authenticates with its own enrolled credential and names things, such as a logical model or a job,
  that it cannot open itself.
- **The database.** A runner has none. What it needs arrives in the job manifest or through proxied
  calls, and what it produces is written back by the same persistence the in-process path uses.
- **Publication.** The control plane posts the review to the provider. A runner submits findings and
  never talks to the pull request.

One of the design goals is to have **parity** with the reviews performed directly on the ProPR main
instance. The same job reviewed remotely and locally uses the same collaborators, the same prompts, the
same budget enforcement and the same persisted shape. Where a collaborator is absent on a runner, the
absence is recorded in the job's protocol.

## The two sides

```
   Provider (Forgejo / GitLab / Azure DevOps / GitHub)
        │ webhooks / crawls                    ▲ posted review
        ▼                                      │
┌─ Control plane (N replicas, shared PostgreSQL) ─────────────────────────────┐
│  Intake, review jobs, in-process execution                                  │
│  Lease dispatch: offer, claim, workspace, job manifest                      │
│  Runner surface (HTTP, runner credential):                                  │
│      lease / heartbeat / release · workspace fetch (git smart HTTP) ·       │
│      tools · memory · ai/chat relay · ingest · findings                     │
└──────────────────────────────────────────────────────────────────────────────┘
        ▲ leases, proxied calls, batches          │ manifest, pack files
        │                                         ▼
┌─ Runner host (1..M per operator) ───────────────────────────────────────────┐
│  Poll for a lease, fetch the workspace, run the review, submit findings     │
│  Local: git worktrees, repository search, structural analysis               │
│  Proxied: credentialed tools, thread memory, every model call               │
└──────────────────────────────────────────────────────────────────────────────┘
```

Three assemblies carry the feature. **Runner.Contracts** is the dependency-free wire vocabulary.
The **runner host** composes the review pipeline from the same building blocks the control plane uses,
which is how parity is achieved rather than by re-implementation. The **control plane** gains the lease
machinery and the runner-facing surface, and its in-process execution path is unchanged.

## Where a review runs

A job whose client has an active, eligible runner is left for that runner. There is no silent fallback
to the control plane, because isolation is what runner operators rely on.

There are two exceptions. A pass list that publishes a PR-wide entry runs in-process, because runners
compose no PR-wide generator yet, and each such review logs a warning. And the worker pages deeper
through the pending queue when a whole claim window was reserved for runners, so one tenant's runner
backlog cannot starve a runner-less tenant behind it.

## Deployment

**Minimal.** One control-plane process, one PostgreSQL, one runner process. The runner needs
`RUNNER_CONTROL_PLANE_URL` and a `RUNNER_REGISTRATION_TOKEN` issued by an administrator. It enrolls,
receives its own credential, and works. The per-runner settings are `RUNNER_DISPLAY_NAME`,
`RUNNER_CAPACITY` and `RUNNER_WORK_ROOT`.

**Multiple control-plane replicas.** The database is the only state shared between replicas. Claims,
leases and transitions are single conditional statements, so replicas never coordinate directly.

Per-lease state is not shared: the mirror on disk, the registered tools, the budget scope, the replay
cache and the submission ledger all live in the replica that dispatched the job. Each replica therefore
sets `RUNNER_ADVERTISED_URL`, and the manifest carries `servedBy` so the runner sends all job-scoped
traffic to that replica. If a replica dies mid-job, its lease expires, another replica reclaims the job
through the database, and the next dispatch rebuilds what it needs from durable rows.

**Trust zones.** The runner surface accepts the runner credential only; admin and user surfaces are
separate. A runner is scoped at enrollment to a tenant, an optional client list and tags, and nothing it
sends can widen that scope. Every job-scoped call is re-authorized against the job's current status,
lease owner and generation, so a superseded runner's calls fail closed. Revoking a runner, not its
token, is the kill switch; deletion is refused while a lease is held.

**Disk.** The control plane keeps a mirror and two worktrees per dispatched job under the review
workspace root, and deletes them when the job is released or published. A runner keeps one working copy
per job under `RUNNER_WORK_ROOT`, in plain text, for as long as the job runs. The host owns the
remanence window; see the note in [configuration.md](../operate/configuration.md).

## Failure and recovery

The heartbeat renews the lease and carries directives back to the runner: stop, superseded, and budget
cut all arrive as a refusal the runner can act on.

If a runner disappears, its lease expires and a sweep reclaims the job and requeues it. Each reclaim
spends one of the job's reclaim budgets, three consecutive and twelve total by default. Past that the
job fails with a reason an operator can read. The next attempt reads prior results back and pays only
for the files that remain.
