# Deploying ProPR

What a real deployment looks like: what routes where, which images to run, and how to size and persist
the parts that actually do the reviewing.

For what the components are and how they fit together, see
[what you run](../concepts/how-it-works.md#what-you-run). Every variable named on this page has its
default and accepted range in [the environment variable reference](configuration.md), and nowhere else.

## Deployment topology

If you bring your own ingress instead of the bundled proxy, this is what has to route where.

| Path | Goes to | Notes |
|---|---|---|
| `/` | Frontend | A static SPA; unknown paths fall back to `index.html` |
| `/api/` | API, prefix stripped | The `/api` prefix exists only at the proxy layer |
| `/webhooks/` | API, prefix **not** stripped | Webhook ingress is not under `/api` |

The routing is not optional: the published frontend image calls the API at `/api` on its own origin, so
the frontend and the API have to be served from one hostname.

The API serves plain HTTP on port 8080 and ProCursor on port 8081, both as a non-root user. The frontend
image also serves HTTP on 8080, as a non-root user. TLS terminates at your proxy. Nothing routes to
ProCursor's port from outside - see [ProCursor as a separate service](#procursor-as-a-separate-service).

Set `MEISTER_PUBLIC_BASE_URL` to the externally reachable API base URL, including the `/api` prefix if
your proxy uses one. It is used to generate the webhook listener URLs shown in the UI, to build SSO
redirect URLs, and to allow the browser origin it belongs to. Without it, callback URLs fall back to the
request host.

`X-Forwarded-For` and `X-Forwarded-Proto` are honoured from a single hop, and only when the connecting
proxy is on loopback or in a private range (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`). There is no
setting that widens that list. If your ingress sits outside it, both headers are ignored and ProPR sees
the direct connection instead: every request appears to come from the proxy's own address, so the per-IP
rate limit on the sign-in endpoints buckets all sign-in attempts together (see
[sign-in and sessions](../reference/security.md#sign-in-and-sessions)), and the request scheme is
whatever the proxy used to reach the API rather than the scheme the browser used. Setting
`MEISTER_PUBLIC_BASE_URL` keeps generated URLs correct regardless, since it is used ahead of the
request's own scheme and host.

Additional browser origins can be allowed with `CORS_ORIGINS` (comma-separated).

Do not route `/metrics`, or `/api/metrics`, from a public edge - see
[what to block at your edge](../reference/security.md#what-to-block-at-your-edge).

Symptoms that trace back to routing or the public base URL are indexed in
[troubleshooting](troubleshooting.md).

## Running published images

`docker compose up --build` builds the three ProPR images - API, ProCursor and frontend - from the
checkout. To run published release images instead, replace the `build:` block of each of those three
services in `example/docker-compose/docker-compose.yml` with an `image:` line:

| Service | Image |
|---|---|
| `meisterpropr` | `ghcr.io/meister-dev-ai/propr:<tag>` |
| `procursor` | `ghcr.io/meister-dev-ai/propr/procursor:<tag>` |
| `frontend` | `ghcr.io/meister-dev-ai/propr/frontend:<tag>` |

Then start the stack with `docker compose up -d`, without `--build`. Keep the three tags aligned - see
[upgrades and backups](upgrades-and-backups.md#upgrading). Everything else in the file, including
PostgreSQL and the bundled proxy, already runs published images.

A fourth image, `ghcr.io/meister-dev-ai/propr/runner:<tag>`, is published from the same release and is
not part of the compose stack. It runs on hosts of your own; see [scaling runners](#scaling-runners).
Give it the same tag as the control plane, because a runner has to stay inside that control plane's
contract compatibility window.

## Review workers

There is no separate worker to deploy - the background work runs inside the API process, listed under
[what you run](../concepts/how-it-works.md#what-you-run). Sizing review throughput therefore means
sizing the API host.

Two settings multiply into peak load. `WORKER_MAX_CONCURRENT_REVIEW_JOBS` bounds how many reviews one API
instance runs at once, and `AI_MAX_FILE_REVIEW_CONCURRENCY` bounds how many files one review works on in
parallel. Raise either and the host needs the memory, CPU and provider rate limit to match.

Of those two, only `WORKER_MAX_CONCURRENT_REVIEW_JOBS` is licensed: without parallel review execution it
has no effect at all - see [editions](../reference/editions.md).

More API instances is not the lever, and is not safe: those in-process workers are not coordinated
between processes, so a second instance means the crawler, the mention scan and the retention purge all
run twice. Run one API instance and raise its concurrency instead.

## Scaling runners

Runners are the one part of ProPR you can add more of. The in-process workers above are not coordinated
between API instances, so a second API is unsafe; a second runner is the supported way to review more at
once. See [the runner architecture](../reference/runner-architecture.md) for what a runner is.

Two shapes work, and they differ in how a host gets its enrollment.

**Hosts you start yourself.** Issue a token per host and start it. Straightforward, and what a small
installation should do.

**A group the platform scales for you**, such as Azure Container Apps, a Kubernetes Deployment or an ECS
service. Replicas appear without an operator present, so they cannot each be handed their own token. Issue one
token whose enrollment count covers the replicas the group may run, put it in the platform's secret
store, and give every replica the same environment. Each spends one use as it starts.

### Scaling on the queue, not on traffic

A runner polls for work; nothing calls it. An HTTP scale rule therefore measures nothing, and the default
Container Apps rule will hold the group at its minimum however deep the queue is. Scale on the queue
itself with a KEDA PostgreSQL scaler pointed at the control plane's database:

```yaml
scale:
  minReplicas: 1
  maxReplicas: 10
  rules:
    - name: pending-reviews
      custom:
        type: postgresql
        metadata:
          query: "SELECT count(*) FROM review_jobs WHERE status = 'Pending'"
          targetQueryValue: "2"
        auth:
          - secretRef: propr-db-connection
            triggerParameter: connection
```

`targetQueryValue` is jobs per replica: at two, a queue of ten asks for five replicas. Size it against
`RUNNER_CAPACITY`, which is how many each replica runs at once. A capacity of two and a target of two
means a replica is asked for only once the existing ones are full.

**On `minReplicas: 0`.** Scale-to-zero is cheapest and costs the first review of an idle period a cold
start, including the runner's tree-sitter probe. For a tool somebody is waiting on, one warm replica is
usually worth more than the saving.

### What a rolling update depends on

Three behaviours make a redeploy safe.

**Draining.** The platform signals the replica and waits. The runner releases the leases it holds on
shutdown, so a job in flight returns to Pending and another replica picks it up. Give the group a
termination grace period long enough for that release. It takes a handful of requests, not the length of
a review.

**Version skew.** During the rollout, replicas on both the old and new contract version poll at once. The
compatibility window covers this: a runner outside it is refused in a way that reads as a lost lease
rather than a crash. Skipping enough versions in one jump to leave that window is what breaks it, so
update runners with the control plane rather than long after it.

**Identity.** A replaced replica does not keep its registry row. The credential is held in memory only,
so the new one enrolls afresh. Rows from replicas that are gone are removed by the prune sweep; see
`RUNNER_PRUNE_UNSEEN_DAYS` in [the configuration reference](configuration.md).

## Review workspace

ProPR clones the repositories it reviews to local disk on the API host and reuses the mirror across
reviews. Plan for a writable directory large enough for the repositories you review, and bound it with
`REVIEW_WORKSPACE_MAX_CACHE_SIZE_MEGABYTES`.

The directory is a cache - losing it costs re-cloning, not data. But if you leave it on the container's
writable layer, as the example stack does, every restart re-clones everything. Mount a volume for it in
any deployment you keep, and point `REVIEW_WORKSPACE_ROOT_PATH` at it.

## ProCursor as a separate service

ProCursor runs as a separate internal service and is never exposed publicly: the API is the only public
control plane, and the two authenticate to each other with `PROCURSOR_SHARED_KEY`. It keeps its own
operational data - indexes, snapshots, token usage - in its own database, and reports health on its own
endpoint, which the API surfaces as the `procursor-remote` check - see
[what the health checks mean](observability.md#what-the-health-checks-mean).

Point both services at the same encryption key ring - see
[the encryption key ring](../reference/security.md#the-encryption-key-ring).

For what ProCursor does and what indexing costs, see [ProCursor](../concepts/how-it-works.md#procursor).

## Running without ProCursor

To deploy ProPR without the code-knowledge service, set `PROCURSOR_REMOTE_MODE=disabled` and leave
`PROCURSOR_SERVICE_BASE_URL` and `PROCURSOR_SHARED_KEY` unset. ProPR then omits the ProCursor review
tools instead of reporting a broken dependency, and the `procursor-remote` check drops out of `/healthz`.
Reviews still run - for what they lose, see [ProCursor](../concepts/how-it-works.md#procursor).

**This applies to a deployment you assemble yourself, not to the bundled compose stack.** That stack
always defines the ProCursor service and will not start without it: the API waits for ProCursor to
report healthy, and ProCursor itself will not start without the shared key it
[requires](configuration.md#required-values). Leaving the key unset there gets you a crash-looping
ProCursor and an API that never comes up. To evaluate without ProCursor on the example
stack, delete the `procursor` service and the `procursor` entry under the API service's `depends_on`
before setting the mode to `disabled`.
