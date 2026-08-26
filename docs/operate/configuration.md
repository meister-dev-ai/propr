# Environment variable reference

ProPR and ProCursor read their startup settings from the process environment. There is no configuration
file to edit; everything else is configured in the management UI.

Defaults and accepted ranges are stated here. Other pages name a variable and link to this one.

## How a value can fail to take effect

A wrong value behaves in one of three ways:

| What you did | What happens |
|---|---|
| Set it somewhere the container never sees | Silently ignored. See [the example stack](#does-the-example-stack-forward-it) below. |
| Set it to something unparsable - a word where a number belongs, or a blank value | Silently ignored; the default is used. |
| Set it to a parsable value outside the accepted range | **Startup fails** for most settings, with a message naming the setting and its bounds. |

Three groups depart from that:

- `MEISTER_SESSION_IDLE_MINUTES` and `MEISTER_SESSION_ABSOLUTE_HOURS` fail startup on anything that is
  not a positive integer. They do not fall back to their default.
- A value under the minimum of an interval marked "clamped" is silently raised to that minimum.
- **The `AI_*` review-tuning settings are not range-checked at all.** An out-of-range value is used as
  given. The ranges listed for them are the values the review loop is built for, not a guard. A
  nonsensical value degrades reviews silently and startup still succeeds.

Other symptoms, and the page that fixes each: [troubleshooting](troubleshooting.md).

## Does the example stack forward it?

Compose passes a variable to a container only when the service's `environment:` block names it. The
bundled `example/docker-compose/docker-compose.yml` names a fixed set. **Anything else you put in `.env`
is silently ignored.**

In an env file, do not put spaces around the equals sign. Write `KEY=value`, not `KEY = value` - the
spaces become part of the name and the value.

The last column of every table below says which:

| Value | Meaning |
|---|---|
| yes | The compose file forwards it from `.env` |
| pinned | The compose file sets a fixed value; `.env` cannot change it |
| no | Not forwarded. Setting it in `.env` does nothing. |

To use one that is not forwarded, add it to the `meisterpropr` service's `environment:` block yourself:

```yaml
    environment:
      - AI_ALLOW_PRIVATE_EGRESS=${AI_ALLOW_PRIVATE_EGRESS:-}
```

## Required values

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `MEISTER_JWT_SECRET` | Signing secret for session access tokens | none | at least 32 characters | yes |
| `MEISTER_BOOTSTRAP_ADMIN_USER` | Username of the admin account seeded on first start | none | - | yes |
| `MEISTER_BOOTSTRAP_ADMIN_PASSWORD` | Password for that account | none | - | yes |
| `DB_CONNECTION_STRING` | PostgreSQL connection string for the ProPR database | none | - | yes |
| `PROCURSOR_SHARED_KEY` | Shared secret the API and ProCursor authenticate to each other with | none | - | yes |
| `PROCURSOR_DB_CONNECTION_STRING` | PostgreSQL connection string for the ProCursor database | none | - | yes |
| `PROCURSOR_PROPR_BASE_URL` | Internal base URL ProCursor calls the API on | none | absolute URL | yes |

What happens when one of them is absent:

- The bootstrap admin values are read only when no active admin user exists. When one does, they are
  ignored. When none does and they are absent, startup fails.
- ProPR registers its database-backed features only when `DB_CONNECTION_STRING` is set.
- The ProCursor service refuses to start without its own connection string, its shared key, and the API
  base URL. On the API side, an absent shared key or service base URL turns ProCursor off - see
  [running without ProCursor](deploy.md#running-without-procursor).
- `MEISTER_JWT_SECRET` is read when the first token is signed, not at startup. A missing or too-short
  secret lets the stack come up healthy and fails the first sign-in.
- On the bundled compose stack, an absent `PROCURSOR_SHARED_KEY` does not turn ProCursor off. That stack
  always defines the service and gates the API on it, so ProCursor crash-loops and the API never starts.
  See [running without ProCursor](deploy.md#running-without-procursor).

Use a long random string for `MEISTER_JWT_SECRET` and `PROCURSOR_SHARED_KEY`. What rotating the JWT
secret costs: [sign-in and sessions](../reference/security.md#sign-in-and-sessions).

## Public URL and browser origins

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `MEISTER_PUBLIC_BASE_URL` | The externally reachable API base URL, used for webhook listener URLs, SSO redirects and the allowed browser origin | falls back to the request host | absolute URL, including the proxy's `/api` prefix if it has one | yes |
| `CORS_ORIGINS` | Extra browser origins allowed to call the API | none | comma-separated origins | yes |

See [deployment topology](deploy.md#deployment-topology) for why both matter behind your own ingress.

## Encryption key ring

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `MEISTER_DATA_PROTECTION_KEYS_PATH` | Directory holding the key ring that encrypts stored provider and AI credentials and the activated license | unset - keys live on the container's own filesystem and are lost when it is replaced | writable directory path, created if absent | pinned |

The example stack pins this to a fixed in-container path and mounts a named volume there, shared by both
services. To put the key ring on durable storage of your own, edit that line and mount your volume in
its place.

Both services must be given the same path. What the key ring protects and what to back up with it:
[the encryption key ring](../reference/security.md#the-encryption-key-ring).

## Sessions and sign-in protection

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `MEISTER_SESSION_IDLE_MINUTES` | Idle timeout, renewed by activity | `480` | any positive integer | no |
| `MEISTER_SESSION_ABSOLUTE_HOURS` | Absolute session lifetime, fixed at sign-in | `72` | any positive integer | no |
| `MEISTER_AUTH_LOCKOUT_MAX_ATTEMPTS` | Consecutive failed passwords that trigger a lockout | `5` | 1–100 | no |
| `MEISTER_AUTH_LOCKOUT_BASE_MINUTES` | Lockout duration at the first threshold | `15` | 1–1440 | no |
| `MEISTER_AUTH_LOCKOUT_MAX_MINUTES` | Cap on the exponentially backed-off lockout duration | `60` | 1–10080 | no |
| `MEISTER_AUTH_RATELIMIT_ENABLED` | Whether the per-IP limiter on the auth endpoints runs | `true` | `true`, `false` | no |
| `MEISTER_AUTH_RATELIMIT_PERMITS` | Auth requests permitted per window per client IP | `20` | 1–10000 | no |
| `MEISTER_AUTH_RATELIMIT_WINDOW_SECONDS` | Length of that window | `60` | 1–3600 | no |

What these controls are for: [sign-in and sessions](../reference/security.md#sign-in-and-sessions).

## Review workers

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `WORKER_MAX_CONCURRENT_REVIEW_JOBS` | How many review jobs one API instance runs at once | `4` | 1–64 | no |
| `WORKER_POLL_INTERVAL_MILLISECONDS` | How often the worker looks for pending jobs | `2000` | 10–60000 | no |
| `WORKER_STUCK_JOB_TIMEOUT_MINUTES` | Retired. Accepted and ignored; the worker warns at startup when it is set | - | - | no |

`WORKER_MAX_CONCURRENT_REVIEW_JOBS` takes effect only with a commercial license that allows parallel
review execution. Without one, ProPR runs one review at a time. Where the license allows fewer concurrent
reviews than this setting, the license limit applies - see [editions](../reference/editions.md). How this
setting and `AI_MAX_FILE_REVIEW_CONCURRENCY` combine into peak load:
[review workers](deploy.md#review-workers).

## The runner host

Read by the runner container, not by the control plane. The runner exposes a health endpoint and
nothing else.

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `RUNNER_CONTROL_PLANE_URL` | Base URL of the control plane it leases from | none | absolute URL | no |
| `RUNNER_CREDENTIAL` | The credential issued at enrollment | none | - | no |
| `RUNNER_REGISTRATION_TOKEN` | Operator-issued token, spending one of its uses to enroll | none | - | no |
| `RUNNER_DISPLAY_NAME` | Operator-facing name in the registry | the machine name | - | no |
| `RUNNER_TAGS` | Tags this runner declares | none | comma-separated | no |
| `RUNNER_CAPACITY` | How many jobs it runs at once | `2` | 1-64 | no |
| `RUNNER_POLL_INTERVAL_SECONDS` | Wait between asks when the last found no work | `5` | 1-3600 | no |
| `RUNNER_MAX_BACKOFF_SECONDS` | Backoff ceiling while the control plane is unreachable | `60` | 5-3600 | no |
| `RUNNER_WORK_ROOT` | Where leased jobs are worked | a temp directory | writable path | no |
| `RUNNER_LOG_LEVEL` | Minimum log level | `Information` | a Serilog level name | no |

The control plane reads these two, not the runner. They govern the registry the fleet is listed from:

| Variable | Meaning | Default | Range | Secret |
|---|---|---|---|---|
| `RUNNER_PRUNE_UNSEEN_DAYS` | How long a runner may be silent before its row is removed. `0` keeps every row until an operator deletes it | `30` | 0-3650 | no |
| `RUNNER_PRUNE_INTERVAL_SECONDS` | How often the prune sweep runs | `3600` | 60+ | no |

A runner holds its registration token and its credential in memory only, so a host that restarts enrolls
again as a new runner and leaves its earlier row in the registry. The sweep deletes rows that have not
been heard from within `RUNNER_PRUNE_UNSEEN_DAYS`. It skips a runner still holding a lease and removes it
on a later sweep.

An enrolled runner counts towards the licensed number of runners as long as its credential is valid, whether
or not the host is still running. A runner left behind by a restart therefore holds its place until the
credential expires or the sweep deletes it. Deleting it on the Runners page frees the place immediately.

The runner reads `OTLP_ENDPOINT` like every other service. With none set it installs no exporter.

`RUNNER_CONTROL_PLANE_URL` must be `https`, except for loopback addresses. The credential is sent on
every call.

**Set `RUNNER_REGISTRATION_TOKEN`, not `RUNNER_CREDENTIAL`.** Issue the token in the admin UI and give
it to the host. The runner exchanges it for a credential on its first cycle and renews that credential
before it expires. A host given neither reports that on `/healthz` and keeps running.

**A registration token is single-use by default, and every enrollment spends one use.** A restart is an
enrollment, so a host you start by hand needs a fresh token each time it restarts, unless you issue the
token for more than one use.

**Issue a token for as many hosts as the deployment will start.** A scaling group's replicas come up
without an operator present to issue each of them a token. Give the group one token whose enrollment
count covers the replicas it may run, and keep it in the platform's secret store. The Runners page shows
each token's remaining uses, and a token can be revoked at any point. To rotate one, issue the
replacement before revoking the old token.

**Both bounds are optional.** Leave the lifetime empty for a token that does not expire, and the enrollment
count empty for one with no limit. Set both to cover the scaling window you expect, and leave them empty only
where you have decided a standing credential is acceptable.

A token is a credential. Whoever holds it can enroll as many hosts as its remaining uses allow, for as
long as its lifetime runs. Keep an unbounded token in a secret store with a named owner, and rotate it on
the cadence you would rotate any other standing credential.

**To remove an enrolled host, revoke the runner, not its token.** A token's use is spent at enrollment,
so revoking the token afterwards stops only the uses that remain. Revoking the runner makes every call it
makes fail authentication. A lease it holds expires by itself, up to one lease duration later.

**What touches the runner's disk.** The repository content under review sits in plaintext under
`RUNNER_WORK_ROOT` while a job runs, and is purged when the job ends and again at startup. Everything
else a review produces - trace, results, spend - is held in memory, batched to the control plane, and
never written to disk. Purging cannot cover a host imaged or destroyed mid-job, so where disk remanence
matters, put `RUNNER_WORK_ROOT` on an encrypted or ephemeral volume.

`RUNNER_CREDENTIAL` is for a host that already has one, such as a redeploy that must keep its registry
identity. Leave it unset otherwise.

The runner requests a lease only when it has a free slot. An unreachable control plane, a full slot
pool, an unsupported contract version and a drain are each reported on `/healthz` and retried with
backoff. None of them exits the process. On shutdown the runner releases the leases it holds.

**The runner also reads the `AI_*` review options**, because it runs the review pipeline. Set them to
the same values as the control plane. A runner with different values reviews differently, and nothing
reports the difference. It reads no other `AI_*` value: connections, keys and model bindings stay on the
control plane.

## Runner fleet and queue stalls

A runner counts as active when it is enrolled, not revoked, speaks a contract version this control
plane can serve, and was heard from inside `RUNNER_ACTIVE_HEARTBEAT_WINDOW_SECONDS`.

The control plane does not execute a job itself when an active runner is eligible for that job's
client. Jobs for clients no active runner can serve continue to run in the control plane. There is no
setting that re-enables in-process execution for a job a runner could take.

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `RUNNER_ACTIVE_HEARTBEAT_WINDOW_SECONDS` | How recently a runner must have been heard from to count as capacity | `120` | 15-3600 | no |
| `RUNNER_FLEET_EMPTY_SETTLE_SECONDS` | How long the fleet must be continuously empty before in-process execution resumes | `300` | 0-3600 | no |
| `RUNNER_QUEUE_STALL_GRACE_SECONDS` | How long work may sit pending with no runner taking it before the queue is called stalled | `600` | 30-86400 | no |
| `RUNNER_ADVERTISED_URL` | Base URL this control-plane replica advertises to runners for job-scoped calls | unset | absolute https URL (loopback may be http) | no |

`RUNNER_ACTIVE_HEARTBEAT_WINDOW_SECONDS` must not exceed `REVIEW_LEASE_DURATION_SECONDS`, and startup
fails when it does. A runner counted as available for longer than its leases survive leaves work with
nobody to run it.

`RUNNER_FLEET_EMPTY_SETTLE_SECONDS` delays only the return to in-process execution. A runner becoming
active takes effect at once.

A stalled queue reports one of `NoActiveRunner`, `NoFreeSlot` or `NoRunnerMatchesRequiredTags`.

**Running more than one control-plane replica with runners requires `RUNNER_ADVERTISED_URL` on every
replica.** The replica that grants a lease serves that job: its workspace mirror is on local disk, and
the job's budget scope, tools and workspace registration live in its process. A runner configured with
only a load-balanced URL reaches whichever replica the balancer picks, and that replica refuses the job's
calls as though the lease were lost. Set each replica's own reachable address here. The lease carries it
to the runner, which uses it for everything job-scoped and keeps the load-balanced URL for enrollment and
for asking for work. Unset, the lease carries no address and runners use `RUNNER_CONTROL_PLANE_URL` for
everything, which works for a single replica.

## Review job leases

A review job is claimed under a lease. The claim is a single conditional database write, so exactly one
host wins a given job however many are polling. The holder keeps the lease alive by renewing it on a
timer that runs independently of review progress. A job whose lease stops being renewed counts as
abandoned, however long it had been processing.

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `REVIEW_LEASE_DURATION_SECONDS` | How long a claim holds a job before its lease must be renewed | `120` | 30–3600 | no |
| `REVIEW_LEASE_HEARTBEAT_INTERVAL_SECONDS` | How often the holder renews its lease | `20` | 5–1200 | no |
| `REVIEW_LEASE_HEARTBEAT_JITTER_FRACTION` | Random fraction of the interval each renewal is brought forward by | `0.2` | 0–0.5 | no |
| `REVIEW_LEASE_MAX_HEARTBEAT_FAILURES` | Consecutive renewal failures tolerated before the holder stops working on the job | `3` | 1–20 | no |
| `REVIEW_LEASE_CLAIM_CANDIDATE_LIMIT` | How many pending jobs one poll cycle considers | `50` | 1–500 | no |

`REVIEW_LEASE_DURATION_SECONDS` must be at least three times `REVIEW_LEASE_HEARTBEAT_INTERVAL_SECONDS`,
and startup fails when it is not. A lease that survives only one or two renewals is lost to a single slow
database call, and the job is handed to another host while the first is still reviewing it.

Raise the duration where reviews run on hosts with slow or intermittent database access. A longer lease
also means a dead host's jobs wait longer before another host can take them over.

### Reclaim

A job whose lease expires is taken back and offered again, not failed. Reclaim replaced the retired
`WORKER_STUCK_JOB_TIMEOUT_MINUTES`.

Reclaims are budgeted, which bounds what a job that keeps being reclaimed can spend. Completing further
files clears the consecutive count, so only a job that keeps cycling without progress exhausts its
budget. A graceful release during a deploy or scale-in does not count against either budget.

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `REVIEW_LEASE_MAX_CONSECUTIVE_RECLAIMS` | Reclaims allowed without completing further files | `3` | 1–50 | no |
| `REVIEW_LEASE_MAX_TOTAL_RECLAIMS` | Reclaims allowed in total, whatever the progress | `12` | 1–500 | no |
| `REVIEW_LEASE_RECLAIM_BACKOFF_SECONDS` | How long a reclaimed job is left alone before it may be reclaimed again | `60` | 0–3600 | no |
| `REVIEW_LEASE_MAX_RECLAIMS_PER_SWEEP` | How many jobs one sweep takes back | `20` | 1–500 | no |
| `REVIEW_LEASE_RECLAIM_SWEEP_INTERVAL_SECONDS` | Seconds between reclaim sweeps | `30` | 5–3600 | no |
| `REVIEW_LEASE_PUBLICATION_TIMEOUT_MINUTES` | How long publication may run before the job counts as stuck | `30` | 1–720 | no |
| `REVIEW_LEASE_MAX_REVIEW_DURATION_MINUTES` | How long one execution of a review may run before it is failed | `180` | 5–1440 | no |

A job that exhausts its reclaim budget is failed with a reason naming the lease loss, so it reads
differently from a review that failed for its own reasons.

A review that is publishing its comments is not reclaimable, however long its lease has been gone,
because reclaiming it mid-publication posts the same comments twice. Publication has its own longer
timeout. A publication that outlives it fails the job instead of retrying it, because some comments may
already be posted.

The reclaim rules above deal with a holder that stopped renewing. A holder that keeps renewing is never
taken off its job, however long it takes, so `REVIEW_LEASE_MAX_REVIEW_DURATION_MINUTES` caps one
execution: past it the next renewal is refused and the job is failed with that reason. The cap counts
from when the attempt started processing, so a reclaimed job gets the full allowance again. The reclaim
budgets above bound how often that can repeat.

The check happens when a renewal is attempted, so an execution can overrun the cap by up to one
`REVIEW_LEASE_HEARTBEAT_INTERVAL_SECONDS` before it stops. A review that finishes inside that window
publishes normally. The refused renewal cancels the token the review runs under, so the review is
interrupted wherever it had got to, which can be part-way through posting comments. The job ends as
failed, and comments it had already posted stay on the pull request.
`REVIEW_LEASE_PUBLICATION_TIMEOUT_MINUTES` does not prevent this; it governs how long a job may sit in
publication before a sweep treats it as stuck. Set the cap well above how long a review of your largest
pull requests takes.

## Review workspace

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `REVIEW_WORKSPACE_ROOT_PATH` | Where repository mirrors and per-review workspaces are stored | a directory under the service account's local application data | writable directory path | no |
| `REVIEW_WORKSPACE_MAX_CACHE_SIZE_MEGABYTES` | Size the mirror eviction sweep works towards. Mirrors held by a running review are skipped, so the total can exceed it while those reviews run; per-review checkouts are not counted against it | `4096` | 128–1048576 | no |
| `REVIEW_WORKSPACE_RETENTION_MINUTES` | How long an unreferenced workspace directory waits before the sweep removes it. This covers what a failed preparation, an interrupted review, an interrupted release, a failed delete, or a restart leaves behind; a review that ends normally deletes its own checkout at once | `180` | 1–10080 | no |
| `REVIEW_WORKSPACE_MAX_CONCURRENT_PREPARATIONS` | How many workspaces may be prepared at once | `4` | 1–128 | no |
| `REVIEW_WORKSPACE_FETCH_DEPTH_POLICY` | How much of a repository a mirror fetch brings down | `full` | `full`, `blobless`, `shallow` | no |
| `REVIEW_WORKSPACE_FETCH_DEPTH` | Commits fetched under the `shallow` policy. Ignored by the others, which also do not check the range | `200` | 1–100000 | no |

One preparation fetches into a mirror and writes a checkout of the repository, so
`REVIEW_WORKSPACE_MAX_CONCURRENT_PREPARATIONS` bounds how much of the workspace disk is written at any
moment. Lower it on a small disk: running out of space during a checkout fails the review.

The fetch depth policy trades local disk against fetching over the network:

- `full` fetches commits, trees and file contents. Nothing is fetched again during a review.
- `blobless` fetches commits and trees and leaves file contents on the server, downloading them when
  something reads them. It needs a server that offers filtered fetches; Azure DevOps and GitHub both do.
  It saves the file contents of the revisions nothing reads, which in a repository with long history is
  most of them. A review still downloads the head revision it checks out, and the base-revision files it
  compares the target side against, as it reads them. The saving therefore grows with the history behind
  a repository, not with its size at one commit, and the first review after the policy changes pays for
  what it reads.
- `shallow` fetches `REVIEW_WORKSPACE_FETCH_DEPTH` commits. The depth has to exceed the divergence of the
  pull requests being reviewed: a merge base outside the fetched history cannot be resolved and the review
  fails to prepare. Prefer `blobless` for that reason.

A policy change applies to every existing mirror on its next fetch. A mirror fetched under `shallow` has
its boundary removed by the next fetch under `full` or `blobless`, and one fetched under `blobless` stops
filtering under `full` or `shallow`.

Leaving `blobless` costs one large fetch. The widening fetch asks for the contents the filter omitted, so
the first preparation after the change transfers and writes the file contents of every fetched revision,
and reviews after it read locally. Expect that fetch to take as long as a fresh clone of the repository,
and the mirror to grow to its unfiltered size.

Why this wants a mounted volume: [review workspace](deploy.md#review-workspace).

## Background intervals

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `PR_CRAWL_INTERVAL_SECONDS` | How often the crawler polls for open pull requests | `60` | clamped to at least 10 | no |
| `MENTION_CRAWL_INTERVAL_SECONDS` | How often the mention scan runs. Each mention configuration may also ask to be visited less often than this | `60` | clamped to at least 10 | no |
| `THREAD_PASS_SCAN_INTERVAL_SECONDS` | How often queued thread passes are picked up and run | `30` | clamped to at least 5 | no |
| `REVIEW_ARCHIVE_PURGE_INTERVAL_SECONDS` | How often the retention purge sweeps archived pull-request content | `3600` | clamped to at least 60 | no |
| `LICENSE_STAGE_INTERVAL_SECONDS` | How often the licensing sweep runs: it reads the license stage and logs transitions, re-observes the system profile, and evaluates the metered author allowance. `0` or a negative value disables all three | `900` | values at or below `0` disable; positive values are clamped to at least 60 | no |
| `WEBHOOK_DELIVERY_IDLE_POLL_SECONDS` | How often an idle installation asks for a queued webhook delivery | `2` | 1–300 | no |
| `WEBHOOK_DELIVERY_MAX_CONCURRENCY` | Deliveries one replica turns into reviews at once | `4` | 1–32 | no |
| `WEBHOOK_DELIVERY_CLAIM_SECONDS` | How long one replica's claim on a delivery is good for | `300` | 30–3600 | no |
| `WEBHOOK_DELIVERY_MAX_ATTEMPTS` | Attempts a delivery gets before it is kept as failed and no longer retried | `5` | 1–20 | no |
| `WEBHOOK_DELIVERY_RETRY_BACKOFF_SECONDS` | Wait before a failed delivery is eligible again | `30` | 1–3600 | no |

A webhook delivery is answered as soon as it is verified and stored. A worker turns it into a review
afterwards, so no provider's delivery timeout decides whether a review happens. A backlog is drained
without waiting; `WEBHOOK_DELIVERY_IDLE_POLL_SECONDS` governs only how often an empty queue is asked.
Raise `WEBHOOK_DELIVERY_CLAIM_SECONDS` above the slowest intake a large pull request can take on your
providers, because a delivery held longer than that is assumed abandoned and given to another replica.

Raise `WEBHOOK_DELIVERY_MAX_CONCURRENCY` when reviews are waiting while runners sit idle. Turning a
delivery into a review takes seconds, so a replica working one at a time creates roughly one job every
few seconds however much execution capacity is available. Measured against a three-runner fleet with six
slots, serial intake held the fleet to four slots at peak. The provider's rate limit is the ceiling,
because each delivery reads a pull request from the provider that sent it.

Crawling and @-mention scanning need a commercial license ([editions](../reference/editions.md)); the
purge sweep does not. What the purge deletes:
[what ProPR stores](../reference/security.md#what-propr-stores).

`LICENSE_STAGE_INTERVAL_SECONDS` sets how often the licensing sweep runs. Each run logs license stage
changes, re-reads the system profile, and checks the monthly author count. Switching it off changes nothing
about what the license allows, because every capability check reads the license directly. It stops the
profile and author checks from running.

## The link to ProCursor

Read by the API to reach ProCursor, and by ProCursor to reach the API.

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `PROCURSOR_REMOTE_MODE` | `proprManagedRemote` to use the service, `disabled` to run without it | inferred: remote when a service base URL and shared key are both set, otherwise disabled | `proprManagedRemote`, `disabled` | yes |
| `PROCURSOR_SERVICE_BASE_URL` | Internal base URL the API calls ProCursor on | none | absolute URL | yes |
| `PROCURSOR_HEALTH_ENDPOINT` | Path the API probes for ProCursor's health | `/healthz` | path | no |
| `PROCURSOR_REQUEST_TIMEOUT_SECONDS` | Timeout budget for calls between the two services | `30` | 1–600 | no |
| `PROCURSOR_RUNTIME_CONFIG_TTL_SECONDS` | How long ProCursor caches the runtime configuration it fetches from the API | `300` | 1 or more | no |

## ProCursor indexing and queries

Read by the ProCursor service.

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `PROCURSOR_MAX_INDEX_CONCURRENCY` | Concurrent indexing jobs | `2` | 1–32 | no |
| `PROCURSOR_MAX_QUERY_RESULTS` | Results returned per knowledge or symbol query | `5` | 1–20 | no |
| `PROCURSOR_MAX_SOURCES_PER_QUERY` | Sources scanned for one query | `20` | 1–50 | no |
| `PROCURSOR_CHUNK_TARGET_LINES` | Target chunk size, in lines, for indexed text | `120` | 10–1000 | no |
| `PROCURSOR_MINI_INDEX_TTL_MINUTES` | Lifetime of a review-time mini-index overlay | `30` | 1–1440 | no |
| `PROCURSOR_REFRESH_POLL_SECONDS` | How often the indexing worker looks for work | `30` | 1–3600 | no |
| `PROCURSOR_TEMP_WORKSPACE_RETENTION_MINUTES` | How long a stale temporary indexing workspace is kept | `120` | 1–1440 | no |
| `PROCURSOR_EMBEDDING_DIMENSIONS` | Expected embedding vector width; must match the embedding model in use | `1536` | 1–4096 | no |
| `PROCURSOR_TOKEN_USAGE_ROLLUP_POLL_SECONDS` | How often ProCursor aggregates its token usage | `900` | 10–86400 | no |
| `PROCURSOR_TOKEN_USAGE_EVENT_RETENTION_DAYS` | Retention for raw ProCursor token usage events | `365` | 1–3650 | no |
| `PROCURSOR_TOKEN_USAGE_ROLLUP_RETENTION_DAYS` | Retention for aggregated ProCursor token rollups | `730` | 1–3650 | no |

## Review loop budgets

These bound the work one review may do, and the management UI does not expose them. Of everything on this
page they move token spend the most - see [control cost](../guides/control-cost.md).

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `AI_MAX_ITERATIONS_LOW` | Investigation iterations allowed on a low-complexity file | `5` | 1–100 | yes |
| `AI_MAX_ITERATIONS_MEDIUM` | Same, medium complexity | `10` | 1–100 | yes |
| `AI_MAX_ITERATIONS_HIGH` | Same, high complexity | `20` | 1–100 | yes |
| `AI_MAX_REVIEW_ITERATIONS` | Iteration ceiling for review work that is not per-file, where no complexity tier applies | `20` | 1–100 | no |
| `AI_MAX_FILE_REVIEW_CONCURRENCY` | How many files one review works on in parallel | `3` | 1–10 | no |
| `AI_MAX_FILE_REVIEW_RETRIES` | Retries for a review job with failed file passes | `3` | 1–10 | no |
| `AI_MAX_RATE_LIMIT_RETRIES` | Transparent retries after a provider rate-limit response | `3` | 1–10 | no |
| `AI_MAX_BACKOFF_SECONDS` | Maximum backoff between those retries | `30` | 5–120 | no |
| `AI_FILE_BATCH_LINES` | Lines returned per file-content read the reviewer makes | `100` | 10–1000 | no |
| `AI_MAX_FILE_SIZE_BYTES` | Largest file the reviewer may read; above it the read returns an error instead of content | `1048576` | 1024 or more | no |

`AI_MAX_FILE_REVIEW_CONCURRENCY` needs a commercial license for parallel review execution to have any
effect - see [editions](../reference/editions.md). Without it a review works on one file at a time, the
same rule the worker applies to whole jobs. Sizing it alongside
`WORKER_MAX_CONCURRENT_REVIEW_JOBS`: [review workers](deploy.md#review-workers).

When a provider rate-limits a call, ProPR waits for the delay that provider asked for. The delay is read
from the `Retry-After` header, or from the error message when the provider states it there instead.
`AI_MAX_BACKOFF_SECONDS` caps that wait and `AI_MAX_RATE_LIMIT_RETRIES` sets how many further attempts the
call gets. While one connection is rate-limited, other calls bound for the same connection wait too, so a
review working on several files at once stops sending requests the provider has already refused.

Which files land in which complexity tier is decided per review - see
[reviews](../concepts/reviews.md).

## Finding thresholds

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `AI_CONFIDENCE_THRESHOLD` | Confidence at which the reviewer stops investigating a concern | `70` | 0–100 | no |
| `AI_CONFIDENCE_FLOOR_ERROR` | Minimum confidence to post at error severity; below it the finding is downgraded to warning | `80` | 0–100 | yes |
| `AI_CONFIDENCE_FLOOR_WARNING` | Minimum confidence to post at warning severity; below it the finding is downgraded to suggestion | `60` | 0–100 | yes |
| `AI_QUALITY_FILTER_THRESHOLD` | Total comment count across all files below which the cross-file quality pass is skipped | `20` | 1–500 | yes |

A finding that clears these thresholds still goes through the publication gate - see
[why a finding did not get posted](../concepts/reviews.md#why-a-finding-did-not-get-posted).

## Thread memory

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `AI_MEMORY_TOP_N` | Past resolutions retrieved per file review | `3` | 1–20 | yes |
| `AI_MEMORY_MIN_SIMILARITY` | Minimum similarity for a past resolution to be considered | `0.80` | 0.0–1.0 | yes |
| `AI_MEMORY_EMBEDDING_DIMENSIONS` | Embedding width; must match the model bound to the embedding purpose | `1536` | 64–4096 | yes |
| `AI_POSTED_FINDING_MIN_SIMILARITY` | Minimum similarity for a finding to count as a duplicate of one already posted on the pull request | `0.85` | 0.0–1.0 | yes |

The embedding model itself is configured per client, not here - see
[purposes](../ai/purposes.md).

## Code-structure tools

The `AI_ENABLE_*` switches turn these tools off and fall back to simpler behaviour. The budgets bound how
much work one lookup may do before it returns what it has, marked truncated.

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `AI_ENABLE_STRUCTURAL_BOUNDARY_RESOLUTION` | Use the structural analyzer to pick context boundaries instead of a line heuristic | `true` | `true`, `false` | no |
| `AI_STRUCTURAL_PARSE_TIMEOUT_MS` | Per-file budget for that analysis before it falls back to the heuristic | `200` | 10–5000 | no |
| `AI_MAX_STRUCTURAL_PARSE_BYTES` | Largest file it will parse | `524288` | 1024–5242880 | no |
| `AI_ENABLE_STRUCTURAL_REFERENCE_TOOLS` | Register the cross-file reference and definition lookups and the caller-evidence feed | `true` | `true`, `false` | no |
| `AI_MAX_REFERENCE_CANDIDATE_FILES` | Files scanned per reference lookup | `200` | 1–2000 | no |
| `AI_MAX_REFERENCE_RESULTS` | Confirmed sites returned per lookup | `50` | 1–1000 | no |
| `AI_MAX_REFERENCE_RESULT_CHARS` | Character budget for one lookup result | `8000` | 256–64000 | no |
| `AI_REFERENCE_RESOLUTION_TIMEOUT_MS` | Wall-clock budget for one lookup | `4000` | 50–30000 | no |
| `AI_ENABLE_RETAINED_TOOL_EVIDENCE` | Keeps fetched file content across context compaction instead of dropping it, so the reviewer does not re-read content it already holds. Set to `false` to drop it | `true` | `true`, `false` | no |

## Linked work items and issues

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `AI_ENABLE_LINKED_ITEM_TOOLS` | Whether the on-demand linked-item lookups are available; `false` withholds them even from clients that include linked items | `true` | `true`, `false` | no |
| `AI_MAX_LINKED_ITEMS_IN_CONTEXT` | Linked items injected into the review context before the rest are dropped | `5` | 1–50 | no |
| `AI_MAX_LINKED_ITEM_DESCRIPTION_CHARS` | Length each linked item's description is truncated to | `2000` | 128–20000 | no |
| `AI_MAX_LINKED_ITEM_TOOL_CALLS` | On-demand linked-item lookups allowed per review | `6` | 0–100 | no |
| `AI_MAX_LINKED_ITEM_TOOL_RESULT_CHARS` | Character budget for one such result | `8000` | 256–64000 | no |
| `AI_LINKED_ITEM_TOOL_TIMEOUT_MS` | Budget for one such lookup; on overshoot it returns empty | `5000` | 100–30000 | no |

Whether linked items are pulled in at all is a per-client setting - see
[what you can tune](../concepts/reviews.md#what-you-can-tune).

## Egress and stored content

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `AI_ALLOW_PRIVATE_EGRESS` | Permit outbound AI calls to private, loopback and link-local addresses | `false` | `true`, `false` | no |
| `AI_CAPTURE_REASONING_IN_PROTOCOL` | Record the model's reasoning into the review protocol; it can contain verbatim source excerpts | `true` | `true`, `false` | no |

What private egress does and does not permit:
[outbound request protection](../reference/security.md#outbound-request-protection). What the captured
reasoning contains, and when to turn it off:
[what ProPR stores](../reference/security.md#what-propr-stores).

## A process-wide Azure credential

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `AZURE_TENANT_ID` | Microsoft Entra tenant of the service principal | none | - | no |
| `AZURE_CLIENT_ID` | Application ID of the service principal | none | - | no |
| `AZURE_CLIENT_SECRET` | Its client secret | none | - | no |

All three together supply one Azure service principal to the backend process. That credential serves two
unrelated purposes: Azure DevOps operations for a client that has no connection of its own - see
[global Azure fallback](../platforms/azure-devops.md#global-azure-fallback) - and Azure-hosted AI
endpoints configured with Azure Identity instead of a key.

Set fewer than three and none of them are used. With all three absent, ProPR uses the ambient Azure
credential: a managed identity, an Azure CLI login, or whatever else the host offers.

## Observability

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `OTLP_ENDPOINT` | OTLP collector to export traces to | none - no trace pipeline is built at all, so spans are never assembled | absolute URL | no |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | The same collector, under the name the OpenTelemetry specification gives it. Read when `OTLP_ENDPOINT` is unset or empty. A managed OpenTelemetry agent injects this name | none | absolute URL | no |
| `TELEMETRY_HTTP_CLIENT_TRACES` | Which outbound requests become spans. `foreground` skips unattended work: crawl cycles, mention scans and health probes | `foreground` | `foreground`, `all`, `off` | no |
| `TELEMETRY_TRACE_SAMPLE_RATIO` | Head-sampling ratio applied to the traces that survive the filters | `1.0` - no sampler is installed, which leaves the standard `OTEL_TRACES_SAMPLER` and `OTEL_TRACES_SAMPLER_ARG` in charge | `0.0` to `1.0`, clamped into range | no |
| `TELEMETRY_TRACE_IGNORED_PATHS` | Request path prefixes that are never traced, inbound or outbound | `/healthz,/livez,/metrics` | comma-separated path prefixes, leading `/` optional | no |
| `LOKI_URL` | Grafana Loki instance to ship logs to | none - logs go to stdout only | absolute URL | pinned |
| `ASPNETCORE_ENVIRONMENT` | The runtime environment name | `Production` when unset | `Production`, `Development`, or your own name | pinned |

The three `TELEMETRY_` variables do something only while an OTLP endpoint is configured, under either
name. None of them affect `/metrics`, which keeps counting every request. An unrecognised value is not an
error: the trace mode falls back to `foreground` and an unparseable ratio to `1.0`, so a typo silently
gets you the default and the service still starts. Why you would change them:
[trace volume](observability.md#trace-volume).

`Development` serves the API documentation UI - see [the API reference](../reference/api.md#more) - and
relaxes the outbound AI egress checks - see
[outbound request protection](../reference/security.md#outbound-request-protection). Use `Production`
outside development. Where traces and logs end up:
[observability](observability.md#traces-metrics-and-logs).

## An optional Azure OpenAI instruction evaluator

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `AI_EVALUATOR_ENDPOINT` | Azure OpenAI endpoint for the repository-instruction relevance evaluator | none | Azure OpenAI endpoint URL | no |
| `AI_EVALUATOR_DEPLOYMENT` | Deployment name to call on it | none | - | no |
| `AI_API_KEY` | Key for that endpoint; omit to authenticate with the host's ambient Azure credential instead | none | - | no |

This evaluator judges which of a repository's instruction files apply to a diff. It is registered only
when the endpoint and the deployment are both set, and it calls Azure OpenAI directly. It does not go
through the per-client AI connections, so a client's provider configuration does not control it. Leave
all three unset unless you want it.
