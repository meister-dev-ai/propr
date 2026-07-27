# Environment variable reference

Everything ProPR and ProCursor read at startup comes from the process environment. There is no
configuration file to edit; everything else is configured in the management UI.

This page is the only place in these docs that states a default or an accepted range. Other pages name a
variable and link here.

## How a value can fail to take effect

Three different things happen depending on how a value is wrong, and only one of them is loud:

| What you did | What happens |
|---|---|
| Set it somewhere the container never sees | Silently ignored. See [the example stack](#does-the-example-stack-forward-it) below. |
| Set it to something unparsable - a word where a number belongs, or a blank value | Silently ignored; the default is used. |
| Set it to a parsable value outside the accepted range | **Startup fails** for most settings, with a message naming the setting and its bounds. |

Three groups behave differently, and it is worth knowing which:

- The session limits are stricter. `MEISTER_SESSION_IDLE_MINUTES` and `MEISTER_SESSION_ABSOLUTE_HOURS`
  fail startup on anything that is not a positive integer, rather than falling back to their default.
- The intervals marked "clamped" are looser. A value under the minimum is silently raised to it.
- **The `AI_*` review-tuning settings are not range-checked at all.** An out-of-range value is used as
  given. The ranges listed for them are the values the review loop is built for, not a guard, and a
  nonsensical one degrades reviews silently rather than refusing to start.

Other symptoms, and the page that fixes each: [troubleshooting](troubleshooting.md).

## Does the example stack forward it?

Read this before you put anything in `.env` and expect it to take effect.

Compose only passes a variable to a container if the service's `environment:` block names it. The bundled
`example/docker-compose/docker-compose.yml` names a fixed set. **Anything else you put in `.env` is
silently ignored.**

One syntax note that applies to every value on this page: in an env file, do not put spaces around the
equals sign. `KEY=value`, never `KEY = value` - the spaces become part of the name and the value.

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

What "required" means differs per variable, and the failure modes are not alike:

- The bootstrap admin values are read only when no active admin user exists. When one does, they are
  ignored. When none does and they are absent, startup fails.
- ProPR registers its database-backed features only when `DB_CONNECTION_STRING` is set.
- The ProCursor service refuses to start without its own connection string, its shared key, and the API
  base URL. On the API side, an absent shared key or service base URL simply turns ProCursor off - see
  [running without ProCursor](deploy.md#running-without-procursor).
- `MEISTER_JWT_SECRET` is different in kind: it is read when the first token is signed, not at startup.
  A missing or too-short secret therefore lets the stack come up healthy and fails the first sign-in
  instead.
- On the bundled compose stack, an absent `PROCURSOR_SHARED_KEY` does not turn ProCursor off - that
  stack always defines the service and gates the API on it, so ProCursor crash-loops and the API never
  starts. See [running without ProCursor](deploy.md#running-without-procursor).

So of the four the example stack asks for up front, three stop the stack from starting and the JWT
secret only stops sign-in.

Both secrets should be long random strings. What rotating the JWT secret costs:
[sign-in and sessions](../reference/security.md#sign-in-and-sessions).

## Public URL and browser origins

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `MEISTER_PUBLIC_BASE_URL` | The externally reachable API base URL, used for webhook listener URLs, SSO redirects and the allowed browser origin | falls back to the request host | absolute URL, including the proxy's `/api` prefix if it has one | yes |
| `CORS_ORIGINS` | Extra browser origins allowed to call the API | none | comma-separated origins | yes |

See [deployment topology](deploy.md#deployment-topology) for why both matter behind your own ingress.

## Encryption key ring

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `MEISTER_DATA_PROTECTION_KEYS_PATH` | Directory holding the key ring that encrypts stored provider and AI credentials | unset - keys live on the container's own filesystem and are lost when it is replaced | writable directory path, created if absent | pinned |

The example stack pins this to a fixed in-container path rather than reading it from `.env`, and mounts a
named volume there shared by both services. Putting it somewhere durable of your own means editing that
line and mounting your volume in its place.

Why this is not optional, why both services take the same path, and what to back up with it:
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

What these controls are for, and why the per-IP limit is deliberately looser than the account lockout:
[sign-in and sessions](../reference/security.md#sign-in-and-sessions).

## Review workers

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `WORKER_MAX_CONCURRENT_REVIEW_JOBS` | How many review jobs one API instance runs at once | `4` | 1–64 | no |
| `WORKER_POLL_INTERVAL_MILLISECONDS` | How often the worker looks for pending jobs | `2000` | 10–60000 | no |
| `WORKER_STUCK_JOB_TIMEOUT_MINUTES` | How long a job may sit in processing before it is failed | `30` | 5–1440 | no |

`WORKER_MAX_CONCURRENT_REVIEW_JOBS` needs a commercial license for parallel review execution to have any
effect - see [editions](../reference/editions.md) - and it is one of the two multipliers described under
[review workers](deploy.md#review-workers).

## Review workspace

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `REVIEW_WORKSPACE_ROOT_PATH` | Where repository mirrors and per-review workspaces are stored | a directory under the service account's local application data | writable directory path | no |
| `REVIEW_WORKSPACE_MAX_CACHE_SIZE_MEGABYTES` | Upper bound on the whole workspace root | `4096` | 128–1048576 | no |
| `REVIEW_WORKSPACE_RETENTION_MINUTES` | How long a released workspace is kept before cleanup may remove it | `180` | 1–10080 | no |
| `REVIEW_WORKSPACE_MAX_CONCURRENT_PREPARATIONS` | How many workspaces may be prepared at once | `4` | 1–128 | no |

Why this wants a mounted volume: [review workspace](deploy.md#review-workspace).

## Background intervals

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `PR_CRAWL_INTERVAL_SECONDS` | How often the crawler polls for open pull requests | `60` | clamped to at least 10 | no |
| `MENTION_CRAWL_INTERVAL_SECONDS` | How often ProPR scans for @-mentions to answer | `60` | clamped to at least 10 | no |
| `REVIEW_ARCHIVE_PURGE_INTERVAL_SECONDS` | How often the retention purge sweeps archived pull-request content | `3600` | clamped to at least 60 | no |

Crawling and @-mention scanning need a commercial license ([editions](../reference/editions.md)); the
purge sweep does not. What the purge deletes and what it never touches:
[what ProPR stores](../reference/security.md#what-propr-stores).

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

These bound the work one review may do. They are the settings that most directly move token spend that
the management UI does not expose - see [control cost](../guides/control-cost.md).

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

Which files land in which complexity tier is decided per review - see
[reviews](../concepts/reviews.md).

## Finding thresholds

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `AI_CONFIDENCE_THRESHOLD` | Confidence at which the reviewer stops investigating a concern | `70` | 0–100 | no |
| `AI_CONFIDENCE_FLOOR_ERROR` | Minimum confidence to post at error severity; below it the finding is downgraded to warning | `80` | 0–100 | yes |
| `AI_CONFIDENCE_FLOOR_WARNING` | Minimum confidence to post at warning severity; below it the finding is downgraded to suggestion | `60` | 0–100 | yes |
| `AI_QUALITY_FILTER_THRESHOLD` | Total comment count across all files below which the cross-file quality pass is skipped | `20` | 1–500 | yes |

These sit under the publication gate, not in place of it - see
[why a finding did not get posted](../concepts/reviews.md#why-a-finding-did-not-get-posted).

## Thread memory

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `AI_MEMORY_TOP_N` | Past resolutions retrieved per file review | `3` | 1–20 | yes |
| `AI_MEMORY_MIN_SIMILARITY` | Minimum similarity for a past resolution to be considered | `0.80` | 0.0–1.0 | yes |
| `AI_MEMORY_EMBEDDING_DIMENSIONS` | Embedding width; must match the model bound to the embedding purpose | `1536` | 64–4096 | yes |

The embedding model itself is configured per client, not here - see
[purposes](../ai/purposes.md).

## Code-structure tools

The kill switches exist so a deployment can fall back to simpler behaviour; the budgets bound how much
work one lookup may do before it returns what it has, marked truncated.

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
| `AI_ENABLE_RETAINED_TOOL_EVIDENCE` | Experimental. Keeps fetched file content across context compaction instead of dropping it, changing both token profile and review behaviour | `false` | `true`, `false` | no |

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

All three together supply one Azure service principal to the backend process. That one credential serves
two unrelated purposes: Azure DevOps operations for a client that has no connection of its own - see
[global Azure fallback](../platforms/azure-devops.md#global-azure-fallback) - and Azure-hosted AI
endpoints configured with Azure Identity instead of a key.

Set fewer than three and none of them are used. When they are all absent, ProPR uses the ambient Azure
credential instead: a managed identity, an Azure CLI login, or whatever else the host offers.

## Observability

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `OTLP_ENDPOINT` | OTLP collector to export traces to | none - traces are not exported | absolute URL | no |
| `LOKI_URL` | Grafana Loki instance to ship logs to | none - logs go to stdout only | absolute URL | pinned |
| `ASPNETCORE_ENVIRONMENT` | The runtime environment name | `Production` when unset | `Production`, `Development`, or your own name | pinned |

`Development` is not a production setting: it serves the API documentation UI - see
[the API reference](../reference/api.md#more) - and relaxes the outbound AI egress checks - see
[outbound request protection](../reference/security.md#outbound-request-protection). Run `Production`
anywhere real. Where traces and logs end up:
[observability](observability.md#traces-metrics-and-logs).

## An optional Azure OpenAI instruction evaluator

| Variable | What it does | Default | Accepted | Example stack |
|---|---|---|---|---|
| `AI_EVALUATOR_ENDPOINT` | Azure OpenAI endpoint for the repository-instruction relevance evaluator | none | Azure OpenAI endpoint URL | no |
| `AI_EVALUATOR_DEPLOYMENT` | Deployment name to call on it | none | - | no |
| `AI_API_KEY` | Key for that endpoint; omit to authenticate with the host's ambient Azure credential instead | none | - | no |

This evaluator judges which of a repository's instruction files apply to a diff. It is registered only
when the endpoint and the deployment are both set, and it is Azure OpenAI only - it does not go through
the per-client AI connections, so it is the one model call in the product that a client's provider
configuration does not control. Leave all three unset unless you specifically want it.
