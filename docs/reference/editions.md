# Editions and licensed features

Which features need a commercial license, how an installation's edition is set, and what happens when
a feature is not licensed.

This page describes runtime behaviour. The authoritative legal terms are in
[LICENSE](../../LICENSE), [LICENSING.md](../../LICENSING.md) and [COMMERCIAL.md](../../COMMERCIAL.md);
which source files are commercial-only is generated into
[the source license map](source-license-map.md).

## Two editions

| Edition | What it is |
|---|---|
| **Community** | The default. A fresh installation starts here. |
| **Commercial** | Unlocks the licensed capabilities below. Required even when self-hosted. |

The edition is installation-wide, not per tenant or per client. It persists across restarts and
redeploys, and is not configured through an environment variable - see
[Setting the edition](#setting-the-edition).

## What requires a commercial license

| Capability | Key | Without it |
|---|---|---|
| Single sign-on | `sso-authentication` | Tenant users sign in with local accounts only |
| Parallel review execution | `parallel-review-execution` | One review at a time, working on one file at a time, however many replicas run and whatever the concurrency settings say; a submission made while another review is queued or running is refused with HTTP 409 and a license message |
| Distributed review execution | `distributed-execution` | No runner can enroll and none can lease; reviews run in the control plane as before |
| Multiple SCM providers | `multiple-scm-providers` | One SCM provider connection per client |
| Crawl configurations | `crawl-configs` | No scheduled crawling and crawl-setup discovery is refused; reviews come from webhooks or the API |
| Mention answering | `mention-answering` | No @-mention scanning, so a question asked of the reviewer in a pull request comment goes unanswered |
| Budgeting | `budgeting` | No USD spend caps or budget enforcement; the tenant Budget and Spend views are refused with the same license message |

Mention answering used to be gated on `crawl-configs`. On upgrade, an installation that had overridden
`crawl-configs` keeps that setting for `mention-answering` as well, so what it was entitled to does not
change. An installation that had never overridden it keeps the default for both.

Queue depth follows from the same capability. With it, one API instance drains the queue several
reviews at a time, as many as `WORKER_MAX_CONCURRENT_REVIEW_JOBS` allows; see
[the environment variable reference](../operate/configuration.md#review-workers). Without it, queued jobs
drain strictly one at a time.

Multi-tenancy is commercial as well, gated by the edition itself rather than by a key of its own. A
Community installation has exactly one tenant, the built-in System tenant: no further tenants can be
created, and the System tenant cannot be edited. Anything configured per tenant - identity providers,
login policy, the [AI provider and endpoint-host compliance restrictions](../ai/compliance.md), and the
tenant logical-model catalog with the tenant-owned AI connections behind it - is therefore out of reach
in Community.

Everything else - reviewing itself, all four SCM provider families, every AI provider, per-client
logical models, thread memory, ProCursor, and the full review diagnostics - is available in Community
edition.

The model catalog is available with one limit worth knowing before you evaluate: browsing it and attaching
its models to a client's connection work, but overriding a price and hand-defining a model are
tenant-scoped screens and so out of reach in Community. What that costs you, and what to do instead, is
under [a model is missing its context window or price](../ai/models-and-catalog.md#a-model-is-missing-its-context-window-or-price).

**AI provider breadth is not licensed.** Using Anthropic, Bedrock, Vertex, or any OpenAI-compatible
endpoint needs no commercial license. Bring whatever model you want in either edition.

## How a disabled capability behaves

A capability that is off is refused clearly, not hidden and not silently degraded. The API returns a
message naming what is unavailable and that a license is required; the UI shows the feature as
disabled rather than removing it, so you can see what a license would give you.

Dropping back to Community does not delete anything. Your crawl configurations, budget caps, and
additional provider connections stay in the database exactly as you left them.

They stop being exercised, though. Scheduled crawling stops running with `crawl-configs`, @-mention
scanning stops with `mention-answering`, and budget caps stop being enforced with `budgeting`, until each
capability is available again. Nothing warns you per configuration row - the feature simply goes quiet.

If you are not sure a license is what refused you, [troubleshooting](../operate/troubleshooting.md) routes
the symptom to the page that fixes it.

## Setting the edition

A platform administrator sets the edition under **Administration → Licensing** in the management UI.
The capability list on that page is read-only.

Individual capabilities can be overridden one at a time - that is how a license covering part of the
product is reflected - but only through the API, by sending a `capabilityOverrides` array to
`PATCH /api/admin/licensing`. There is no UI control for it. That request also requires `edition`, so
send the edition you are keeping, and each override entry carries a capability `key` from the table
above plus an `overrideState` of `default`, `enabled` or `disabled`.

To read the current state from the API, call `GET /api/auth/me`: it returns the installation edition
and the state of every capability, and is the same source the UI uses. Platform administrators can
also call `GET /api/admin/licensing`.
