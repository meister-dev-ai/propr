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

A tenant can state its own reasoning-capture policy under **Tenant → Compliance**: capture, do not capture,
or follow the installation switch. Where a tenant states one of the first two, it decides that tenant's
reviews. A tenant that withholds reasoning also stops the review asking the provider for a reasoning
summary, and reasoning token counts are still recorded, because spend and budgets are computed from them. A
review already running inside ProPR keeps the decision it started with, while a review running on an
enrolled runner picks up a saved policy for the rest of its completions and trace batches. See
[model reasoning in the job trace](../ai/compliance.md#model-reasoning-in-the-job-trace).

## Outbound request protection

An AI endpoint URL and a source-control `hostBaseUrl` are both operator-supplied, so either could point at an
address inside your own network. One installation posture covers both. Outside local development, every AI
endpoint must be reachable over `https`.

Outbound AI traffic goes through a guarded transport. It checks the connection at connect time against the
**resolved IP address**, not the hostname alone, so a name that resolves to an internal address, or is rebound
to one between check and connect, is refused. The transport never follows redirects. Private, loopback and
link-local addresses, including cloud metadata endpoints, are refused by default. That guard covers every
provider family, Azure OpenAI included.

A provider add-in reaches the network through the same transport. The host builds the client and places its
own connect-time check innermost; an add-in supplies handlers that run outside that check, so it can see and
change what it sends and what comes back, and it cannot remove the check or ask for an exemption. Everything
an add-in sends goes through it, model calls and credential exchanges alike.

Azure OpenAI carries a second restriction on top of it. The family declares the hosts it reaches —
`*.openai.azure.com`, `*.services.ai.azure.com` and `*.cognitiveservices.azure.com` — and rejects a base URL
that does not use `https` or whose host is none of them. Those hostnames are Microsoft-controlled, including
for private endpoints. An Azure connection authenticates with a resource key or a Microsoft Entra token, so
the restriction keeps that credential going to a host Microsoft controls. The declared hosts are shown beside
the family on the plugin inventory, and a tenant's endpoint allow-list is checked against them. An
Azure-hosted endpoint configured under the plain OpenAI family is refused, with a message naming the family
to use.

A source-control connection is held to the same address rule. Without the opt-in, a `hostBaseUrl` naming a
private, loopback or link-local address as a literal, or naming a loopback name, is refused when the
connection is created, when it is patched and when it is verified, and the refusal names the variable to set.
A host name is left unclassified at that point, because classifying it needs DNS: a name such as
`scm.internal.example` that resolves to a private address is accepted when the connection is saved, and
refused when it is verified and on every request it makes. A stored connection on an address the installation
no longer permits fails verification.

Source-control traffic goes through the same guarded transport, so every request is checked at connect time
against the resolved address and a host name rebound to an internal address after the connection was saved
is refused. These clients do follow redirects, because GitHub and GitLab answer some routes with one; a hop
to another host opens another connection, and that connection is checked the same way. Azure DevOps traffic
takes that transport too, through the connection the Microsoft SDK is built with.

The git binary opens its own sockets, so the remote a repository mirror is fetched from is resolved and
classified before git starts. A remote on a private address fails the review job with the reason, and no
fetch runs.

ProPR builds every remote from the source-control host address an operator entered, so a remote is an
`https` address, or an `http` address on a private host where the installation permits one. A remote on any
other transport, `ssh` among them, is refused before git starts, and the guarantees in this section cover
those two schemes.

Git resolves that host again when it connects, and a name server can answer the second question
differently. Three operations reach the remote, and each runs with the classified addresses pinned to the
remote's host and port, so git connects to an address this host approved: the fetch into the mirror, the
fetch that removes an earlier shallow boundary, and the blob fetches a checkout makes where the mirror is a
partial clone. Those three commands refuse redirects whether or not an address is pinned, so a server
answering with a 3xx to another host fails the command instead of sending git to an address nothing
classified. Submodules and Git LFS reach their own remotes and are not covered; a review reads neither. A
remote that already names a literal address carries no pin: git resolves nothing for it.

With `MEISTER_ALLOW_PRIVATE_EGRESS` set, no address is classified and none is pinned. That is deliberate:
the opt-in permits every destination the installation can reach, subject to the transport rules that stay in
force, so pinning a remote to the address it resolved to at check time would protect nothing. Two rules stay
in force. Plain `http` to a public host is refused. The git commands that reach a remote refuse redirects,
in this posture as in the default one. What the opt-in changes for a redirect is the connect-time check:
source-control clients follow redirects in both postures, and with the opt-in set the hop is no longer
checked against the address it reaches. AI clients follow no redirect in either posture.

To reach a self-hosted provider or a self-hosted source-control server on a private network, set
`MEISTER_ALLOW_PRIVATE_EGRESS=true`. It permits private addresses. For an AI endpoint, plain `http` stays
refused outside local development. For a source-control host, plain `http` is permitted to a private or
loopback address and refused for every public host. The redirect block on AI traffic stays in force.

ProPR decides the scheme from the address in the URL, before any name is resolved. A name server answers
again when the connection is made and can answer differently, so a host name carries no permission to use
plain `http`. Configure a self-hosted source-control server reached over plain `http` by its literal address,
as in `http://10.4.1.9`, or serve it over `https` and configure it by name. The remote a repository mirror is
fetched from is held to the same rule.

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

With no selected protector, ProPR uses the framework's key protection behavior. In the Linux images,
filesystem keys are stored without encryption by default. Restrict access to the key directory because its
keys decrypt stored connection credentials. `MEISTER_DATA_PROTECTION_PROTECTOR` encrypts the key files themselves, with a
certificate you hold or with a key service an add-in reaches. A restore then takes the key files and that
certificate or key: either one alone reads nothing. The variables are under
[the certificate protector](../operate/configuration.md#the-certificate-protector).

The Azure Key Vault protector with `MEISTER_DATA_PROTECTION_AZURE_BLOB_URI` set stores the ring in the blob,
and there is no key directory to copy. A restore then takes the blob and an identity that can unwrap the
vault key and read and write that blob; see
[protecting the key ring with Azure Key Vault](../operate/deploy.md#protecting-the-key-ring-with-azure-key-vault).

The certificate protector is compiled into the shared data-protection assembly and is available in both
service images. It requires no add-in publication. An unset, empty, or `none` protector selection and the
built-in certificate protector do not scan the add-in directory.

An explicitly selected external protector scans candidate assemblies and constructs compatible types to
read their protector names. Discovery executes candidate code, so restrict the add-in directory to trusted
executable deployment content. There is no separate approval or signature registry. ProPR applies the selected
protector and fails startup for unsupported selections or missing required settings.

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
A PAT carries exactly the permissions of the user it belongs to. Tokens are stored hashed.

An external application can use a tenant machine credential in the `X-Tenant-Machine-Token` header for client
lookup, review listing and result retrieval, review submission by coordinates, and client-scoped SCM and
AI connection operations. It can also list repository review targets, discover repositories through a
verified client connection, and create an inactive target for one repository. Target creation requires the
crawl configuration capability. The target's repository identity is stored as a canonical filter; an
identical request returns the existing target, while a different target for the same scope and project
returns a conflict. A platform administrator issues the credential with
`POST /admin/tenants/{tenantId}/machine-credentials`, lists metadata with `GET` on the same path, and
revokes it with `DELETE /admin/tenants/{tenantId}/machine-credentials/{id}`. Issuance returns the secret
once. ProPR stores only verification hashes and records issue and revoke events in tenant audit history.
These are direct API routes. The bundled frontend proxy exposes them with an `/api` prefix, as shown in
the [API reference](api.md#tenant-machine-credentials).
The credential has no user identity or administrator role. Each request checks its action against an
explicit allowlist and resolves current client ownership from the database. Revoked, expired, malformed,
and inactive-tenant credentials cannot authenticate.
Tenant machine callers can read a client's effective logical models and purpose routes after the current
ownership check. Logical-model override and purpose-route writes remain outside the machine allowlist.

## Access control

Access is scoped by tenant and by client.

- A **tenant administrator** manages their own tenant only: its memberships, client access, login
  policy, and identity providers. They hold administrator rights over every client in that tenant.
- A **tenant member** gets access to a client only through an explicit assignment. Membership alone
  grants nothing.
- A **platform administrator** is separate from tenant-local policy.

A platform administrator can create a tenant membership for an existing user with
`POST /admin/tenants/{tenantId}/memberships` and a JSON body containing `userId` and `role`.
The role is `TenantUser` or `TenantAdministrator`. An existing membership returns `409 Conflict`
without changing its role; update an existing role through the membership's `PATCH` endpoint.
Creating a local user account does not grant tenant access until a membership is assigned.

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
