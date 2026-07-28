# Observability

The endpoints to probe and scrape, what each health check means, and where traces and logs go.

## Endpoints

| Endpoint | Use it for |
|---|---|
| `GET /livez` | Liveness probes. Reports only that the process is up; runs no dependency checks. |
| `GET /healthz` | Readiness probes. Returns a JSON body naming every check and its status. |
| `GET /metrics` | Prometheus scraping, anonymous. Keep it off your public edge - see [what to block at your edge](../reference/security.md#what-to-block-at-your-edge). |

ProCursor serves the same three paths on its own port.

Behind a reverse proxy that prefixes the API, these paths move with it, and the frontend answers some of
them itself - a trap worth knowing about before you point a probe at the wrong one. See
[health endpoints behind the proxy](../reference/api.md#health-endpoints-behind-the-proxy) in the API
reference.

Every other endpoint, and what a stock deployment serves for API documentation, is in
[the API reference](../reference/api.md#more).

## What the health checks mean

The API's `/healthz` reports these checks:

| Check | Present when | Degraded means | Unhealthy means |
|---|---|---|---|
| `worker` | always | The SCM provider registry is unavailable, or a provider's adapters are not registered | The review worker is not running |
| `database` | a connection string is configured | - | The database is unreachable |
| `procursor-remote` | ProCursor runs as a remote service | ProCursor answered but reported itself unhealthy | ProCursor is unreachable or timed out, or rejected the shared key |

A degraded check still returns `200`; only unhealthy returns `503`. A ProCursor reporting itself unhealthy
therefore leaves the API readiness-green - reviews keep running, they just lose the code-knowledge tools.

The `worker` check covers the background workers inside that API instance. ProCursor's own `/healthz`
covers its indexing and token-usage rollup workers, and the API surfaces that result as the
`procursor-remote` entry.

## Traces, metrics and logs

Set `OTLP_ENDPOINT` to export traces to your own collector and `LOKI_URL` to ship logs to your own Grafana
Loki. Outside Development, logs are also written to stdout as JSON, so a cluster log collector needs
neither variable. Defaults for both are in [the environment variable reference](configuration.md#observability).

Metrics do not follow the OTLP endpoint - they are exposed for scraping only. Point your Prometheus at
`/metrics` on the API, and on ProCursor if you want its numbers too.

## Trace volume

Metrics are pre-aggregated, so their cost does not grow with traffic. Traces do: one span per request,
billed per record by most backends. The knobs that shape trace volume are listed under
[observability](configuration.md#observability); this is when to reach for them.

Most of the spans on an otherwise idle deployment come from work nobody is waiting for. The crawl and
mention workers re-read the provider APIs on every tick whether or not anything changed, and the health
checks probe on their own schedule. That is why outbound tracing defaults to `foreground`: those requests
are excluded, while the requests made while serving somebody stay traced. The excluded ones remain
visible through the `http.client.request.duration` metric and its `http.response.status_code` dimension,
so failure rates stay observable in aggregate even though the individual spans are gone. Set
`TELEMETRY_HTTP_CLIENT_TRACES=all` to get them back while diagnosing a polling problem.

If the volume is still too high after that, sample. Reach for `TELEMETRY_TRACE_SAMPLE_RATIO` last: it
drops whole traces at random, so a request you care about is as likely to be missing as any other.

## What to look at when a review misbehaves

Server logs are the wrong place to start. Every review records its own protocol - each pass, model call,
tool call and filter decision, and the publication gate's verdict on every finding. Open the review in
the management UI, or read it over the API. See
[review diagnostics](../reference/api.md#review-diagnostics) and
[why a finding did not get posted](../concepts/reviews.md#why-a-finding-did-not-get-posted).

If the protocol does not explain it either, work from the symptom index in
[troubleshooting](troubleshooting.md).
