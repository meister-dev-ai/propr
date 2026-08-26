# Security

This page covers what ProPR does with your credentials and your code, and how sign-in works. To report a
vulnerability, see
[SECURITY.md](../../SECURITY.md).

## Where your code goes

ProPR reads pull requests from your SCM host and sends the relevant content to the AI provider **you**
configured. No ProPR-operated service sits in that path: nothing is mirrored or proxied through us, and no
telemetry contains your code.

A tenant can constrain this further by restricting which AI provider families and which endpoint hosts
its clients may use. See [tenant compliance](../ai/compliance.md).

ProPR makes one other outbound request: a daily snapshot of the installation itself, carrying a random
installation identifier, the version, the edition and counters reported as ranges. An installation running a
commercial license also sends that license's identifier and what it holds against the limits the license
states, so its snapshot is not anonymous. Every field is listed in [usage statistics](usage-statistics.md).
An administrator can read the request body before it is sent, and a Community installation can switch the
report off.

## What ProPR stores

Reviews, findings and protocol traces are always persisted. They are the product's history and the basis for
thread memory.

Raw pull-request content is not. Archiving a connection's comment threads or its diffs is opt-in per
SCM provider connection, under **Data retention** on the connection form:

| Setting | Default | Effect |
|---|---|---|
| Store comment threads | Off | Archives the pull request's comment threads |
| Store diffs | Off | Archives the fetched diffs |
| Retention (days) | 30 when left blank | Age at which archived data for that connection is deleted |

A purge worker sweeps on the interval set by `REVIEW_ARCHIVE_PURGE_INTERVAL_SECONDS`; see
[the environment variable reference](../operate/configuration.md#background-intervals). Retention is
evaluated per pull request against its last activity - open pull requests are not exempt. If both
toggles are off, that connection's archived data is deleted wholesale on the next sweep.

The sweep touches archived raw content only. Review jobs, file results, findings, protocol traces and
thread-memory records are never deleted.

Model reasoning is captured into the protocol by default and can contain verbatim source excerpts. Set
`AI_CAPTURE_REASONING_IN_PROTOCOL=false` where policy forbids storing that. Assistant text and tool calls
are recorded in both cases.

## Outbound request protection

An AI endpoint URL is operator-supplied, so it could point at an address inside your own network. Every AI
endpoint must be reachable over `https`.

Outbound AI traffic goes through a guarded transport. It checks the connection at connect time against the
**resolved IP address**, not the hostname alone, so a name that resolves to an internal address, or is rebound
to one between check and connect, is refused. The transport never follows redirects. Private, loopback and
link-local addresses, including cloud metadata endpoints, are refused by default. That guard covers every
provider family except Azure OpenAI.

Azure OpenAI is reached through the Azure SDK, which uses its own transport and does not pass through that
check. It is constrained differently. An Azure connection's base URL is rejected unless it uses `https` and
its host is an Azure AI host: `*.openai.azure.com`, `*.services.ai.azure.com` or
`*.cognitiveservices.azure.com`. Those hostnames are Microsoft-controlled, including for private endpoints,
so an Azure profile cannot be pointed at an arbitrary internal host. An Azure-hosted endpoint configured
under the plain OpenAI family is refused, with a message naming the family to use.

To reach a self-hosted provider on a private network, set `AI_ALLOW_PRIVATE_EGRESS=true`. It permits private
addresses. Plain `http` stays refused outside local development, and the redirect block stays in force.

## What to block at your edge

The API maps an anonymous Prometheus scraping endpoint at `/metrics`, for scrapers inside your environment
reaching the API directly. A reverse proxy that forwards all of `/api/` to the API republishes it as
`/api/metrics` to the internet. Block that path at the edge on any public deployment; the nginx
configuration under `example/azure/.azure/` shows the rule.

## Secrets at rest

Provider connection secrets, AI credentials, webhook secrets, and the activated license document are
encrypted at rest with a key ring you control. They are never returned by the API, never shown again in the
UI after saving, and never written to logs, audit records or error messages. A connection rendered into a log
line shows its field names and an elided credential, never the value.

For GitHub App connections, the stored secret is the private key. Short-lived installation access
tokens are minted on demand and never written to disk or to the database.

Audit and webhook delivery history record status, failure category, and summaries - never raw secrets
or authorization headers.

### The encryption key ring

The key ring is where `MEISTER_DATA_PROTECTION_KEYS_PATH` points; see
[the environment variable reference](../operate/configuration.md#encryption-key-ring). Put it on a
durable, backed-up volume.
A key ring that lives only inside a container is gone the next time that container is replaced, and
everything it protected becomes unreadable.

**A database backup alone is not a restorable install.** Without the matching key ring, every stored
provider connection secret, AI credential, and webhook secret is undecryptable and has to be entered
again from scratch, and the license file has to be activated again; see
[activating a license](editions.md#activating-a-license). Back up the key ring with the database and restore
the two together. The full list is under
[what to back up](../operate/upgrades-and-backups.md#what-to-back-up).

When ProPR and ProCursor both run, point both at the same key ring. They share one protection identity,
so each can read what the other protected.

## Sign-in and sessions

ProPR has two sign-in surfaces.

- **Platform administrators** sign in at `/login`. This page stays available even if a tenant disables
  local login or misconfigures its identity providers, so you cannot lock yourself out. The endpoint
  behind it is `POST /api/auth/login`.
- **Tenant users** sign in at `/tenants/<tenant-slug>/login`, which selects the enabled identity
  providers, the allowed email domains, and whether local login is permitted at all.

Passwords are stored as BCrypt hashes. A session issues a short-lived access token plus an httpOnly
refresh cookie, and the browser refreshes silently. `MEISTER_JWT_SECRET` signs the access tokens and
has a minimum length; rotating it invalidates every token already issued.

A session ends when **either** of two limits is crossed: an idle timeout, renewed by activity, and an
absolute lifetime, fixed at sign-in. Activity never extends the absolute lifetime, so a continuously-used
session is forced to re-authenticate eventually.

Two controls sit in front of sign-in. A per-account lockout locks an account after consecutive failed
passwords and backs off exponentially to a cap. A per-IP rate limit caps auth requests per client IP over
a rolling window. The IP limit is set loose because colleagues behind one office egress share an address.

The variables that set these four limits, with their defaults and accepted ranges, are under
[sessions and sign-in protection](../operate/configuration.md#sessions-and-sign-in-protection).

**External sign-in does not silently merge with a local password account.** A verified, allowed email
can create a new user and tenant membership. If an account with that address already exists and has a
local password, the sign-in is refused with `external_link_requires_confirmation` and the link must be
made explicitly. An existing account with no local password is linked to the external identity.

If nobody can sign in, or sessions end sooner than you expect, start at
[troubleshooting](../operate/troubleshooting.md).

## Automation credentials

Scripts and CI authenticate with a personal access token in the `X-User-Pat` header instead of a JWT.
A PAT carries exactly the permissions of the user it belongs to. Tokens are stored hashed. The API reads
no other credential header.

## Access control

Access is scoped by tenant and by client.

- A **tenant administrator** manages their own tenant only: its memberships, client access, login
  policy, and identity providers. They hold administrator rights over every client in that tenant.
- A **tenant member** gets access to a client only through an explicit assignment. Membership alone
  grants nothing.
- A **platform administrator** is separate from tenant-local policy.

Every client-scoped operation is authorized against the specific client in the request, so access to
one client never confers access to another.

## Tenant isolation of AI credentials

An AI connection belongs either to a tenant or to one of that tenant's clients. Logical models
reference a connection by id, and a tenant-level entry resolves for every client in that tenant, so a
connection can be shared inside a tenant.

A reference that crosses tenants is always refused, and it is checked twice: when the reference is saved,
and again at review time before the credential is handed to a provider. One tenant's credentials, quota,
and egress path can never be used by another. If either side's owning tenant cannot be established, the
reference is refused, not treated as unrestricted.

## Auditing

Changes to a tenant's AI provider configuration are recorded with who made them. Provider connection
operations, webhook deliveries, and review decisions are all stored and inspectable from the management
UI.
