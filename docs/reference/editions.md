# Editions and licensed features

This page covers which features need a commercial license, how an installation gets one, and what happens
when a feature is not licensed.

The legal terms are in [LICENSE](../../LICENSE), [LICENSING.md](../../LICENSING.md) and
[COMMERCIAL.md](../../COMMERCIAL.md). Which installations one license may be activated on is in
[the commercial license policy](../../COMMERCIAL-LICENSE-POLICY.md). The request and response shapes behind
the endpoints named here are in [the API reference](api.md).

## Two editions

| Edition | What it is |
|---|---|
| **Community** | A fresh installation starts here |
| **Commercial** | Unlocks the licensed capabilities below. Required even when self-hosted |

The edition applies to the whole installation, not to a tenant or a client, and follows from the license the
installation has activated.

## What requires a commercial license

Each capability is unlocked by the key the license names, so a license may grant part of this table and not
the rest.

| Capability | Key | Without it |
|---|---|---|
| Single sign-on | `sso-authentication` | Tenant users sign in with local accounts only |
| Parallel review execution | `parallel-review-execution` | One review at a time, one file at a time, however many replicas run |
| Distributed review execution | `distributed-execution` | No runner can enroll or lease; reviews run in the control plane |
| Multiple SCM providers | `multiple-scm-providers` | One SCM provider connection per client |
| Crawl configurations | `crawl-configs` | No scheduled crawling; reviews come from webhooks or the API |
| Mention answering | `mention-answering` | A question asked of the reviewer in a pull request comment goes unanswered |
| Budgeting | `budgeting` | Spend caps cannot be set or changed, and spend against them is not shown; caps already configured go on being enforced |
| Code Insights | `code-insights` | No quality analytics are collected or shown |
| Multi-tenancy | `multi-tenancy` | One tenant only, the built-in System tenant, and nothing configured per tenant is reachable |

Everything else is available in Community edition: reviewing itself, all four SCM provider families,
per-client logical models, thread memory, ProCursor, and the full review diagnostics. Every AI provider works
in both editions, including Anthropic, Bedrock, Vertex and any OpenAI-compatible endpoint.

Without `parallel-review-execution` the queue drains one review at a time whatever
[`WORKER_MAX_CONCURRENT_REVIEW_JOBS`](../operate/configuration.md#review-workers) says. Without
`multi-tenancy` the model catalog can be browsed and attached to a client, but overriding a price or
hand-defining a model is a tenant-scoped screen; see
[a model is missing its context window or price](../ai/models-and-catalog.md#a-model-is-missing-its-context-window-or-price).

## How a disabled capability behaves

A capability that is off is refused with a message naming what is unavailable and what would make it
available. The UI shows the feature as disabled instead of hiding it.

Dropping back to Community deletes nothing. Crawl configurations, budget caps and additional provider
connections stay in the database as you left them, and stop being exercised until the capability is available
again. Budget caps are the exception: they go on being enforced in every edition, at every stage.

If you are not sure whether the license caused a refusal, see
[troubleshooting](../operate/troubleshooting.md).

## Activating a license

A license is a signed file issued to your organization. An installation holds one at a time, and activating
another replaces it immediately, at any stage.

A platform administrator uploads it on the **Administration → Licensing** page, or sends it to
`PUT /api/admin/licensing/license`. The file is verified before anything is stored, so a refused upload
leaves the license already on file in place. The refusal names what to change: the file is not a license, its
signer is not one this product accepts, it was issued for a newer product version, its term has not started,
or its term and grace window have both ended. `DELETE` on the same path removes the license, and
`GET /api/admin/licensing/history` lists the activations, replacements and removals the installation
recorded.

The accepted file is stored in your own database, protected at rest with the installation's
[encryption key ring](security.md#the-encryption-key-ring). Restore the key ring together with the database:
a stored license the key ring cannot open reads as no license until the file is activated again.

Individual capabilities can be turned off on top of the license, through
`PATCH /api/admin/licensing/overrides`. An override can only take away what the license grants.

## What a license states

One signed document names the licensee, the term, the capability keys it grants, and the limits below. It
names no machine, database, network address, installation or user account, so it is not bound to the
installation it is activated on. A trial is a license with a shorter term.

### The limits it states

Four dimensions can carry a limit: distinct pull request authors within one calendar month, clients, runners,
and reviews executing at the same time. Each is in one of three states.

| State | What it means |
|---|---|
| Absent | The license states nothing for this dimension |
| Unlimited | The license states the dimension and sets no ceiling |
| A count | The license states that ceiling. It may be zero, which constrains the dimension to nothing |

Where the license states no limit, the Community limit applies: unlimited clients, unmetered authors, one
review at a time, and no runners. So a license that grants `distributed-execution` without stating a runner
number allows no runners.

Clients, runners and concurrent reviews are enforced. Creating a client, enrolling a runner or claiming a
review is refused once the installation reaches the ceiling, and the refusal names that ceiling.

The runner number counts the runners holding a valid credential, not the runners doing work. Revoking a
runner frees its seat, and so does its credential expiring. A license that lowers the runner number leaves
the runners already enrolled at work: each is refused the next time it renews its credential.

Distinct authors per month is counted and reported, and never refuses or delays work. A month above the
number is recorded, and an administrator sees a banner naming both counts until the month falls back below
it. Automation is left out of the count: accounts the provider marks as bots, the automation identities the
product recognizes, and the reviewer identities this installation is configured to act as.

The **Administration → Licensing** page shows all four dimensions with what the license states, the effective
ceiling and what the installation currently holds.

## What happens as a license runs out

A license does not switch off at its expiry instant. The installation moves through a fixed sequence.

| Stage | When | What is available |
|---|---|---|
| `notYetValid` | Before the term starts | Community only |
| `active` | Until 30 days before the term ends | Everything the license names |
| `warning` | The last 30 days of the term | Everything the license names. Renew during this window |
| `grace` | The 14 days after the term ends | Everything the license names |
| `reverted` | From 14 days after the term ends | Community only |

A term shorter than 30 days starts in `warning` and grants everything the license names from the start.

Reverting drops the installation back to Community, and activating a renewed license restores everything
immediately. The same file can be activated again up to the end of its grace window, which an installation
rebuilt from a backup needs. After that ProPR refuses the file.

## How a license is verified

Verification is offline. ProPR checks the license signature against a root certificate built into the
product, so an installation with no internet access verifies licenses normally. A license stays valid for its
full term even after the certificate that signed it expires.

ProPR compares a license term against the host clock or the latest time it has recorded, whichever is later,
so setting the host clock back does not revive an expired license.

Every installation has a random identifier, not derived from the machine or the network. Support uses it to
identify your installation, and it distinguishes usage reports from different installations. It appears on
the **Administration → Licensing** page. What an installation reports, and when, is in
[usage statistics](usage-statistics.md).

## What this mechanism does and does not do

ProPR's source is published, and the licensing code is part of it. Someone holding the source can change or
remove the checks on this page and build an installation that reports capabilities no license granted it.
Nothing described here prevents that. The mechanism establishes what an installation is entitled to and
reports that state to its operator.

The Elastic License 2.0 the source is published under restricts this:

> You may not move, change, disable, or circumvent the license key functionality in the software, and you may
> not remove or obscure any functionality in the software that is protected by the license key.

Building or running such a modification is use outside the license. Under the license's termination clause
that use is not licensed and the rights it granted terminate automatically. They are reinstated retroactively
if the violation stops within 30 days of a notice from the licensor, and a further violation after that
terminates them permanently. The full text is in [LICENSE](../../LICENSE).

The per-file source-license notices are informational. What is commercial-only is decided by the capability
definitions in the code, not by a notice on a file. See [LICENSING.md](../../LICENSING.md).
