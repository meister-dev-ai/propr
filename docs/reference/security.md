# Security

This page covers what ProPR does with your credentials and your code, and how sign-in works. To report a
vulnerability, see
[SECURITY.md](../../SECURITY.md).

## Container dependencies

ProPR's Dockerfiles pin official base images by release tag and immutable digest. Dependabot checks
these references weekly. The API, runner and ProCursor use the same .NET runtime base, and the frontend
updates its TLS, regular-expression and compression libraries during its image build.

CI scans the API, frontend and runner images with Grype and publishes the findings to GitHub code
scanning. Release builds scan ProCursor as well. Package findings without a vendor fix remain visible.
Install patched images by rebuilding or upgrading the affected services; see
[running published images](../operate/deploy.md#running-published-images).

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

The sweep touches archived raw content only. Review jobs, file results, findings and thread-memory records are
kept for the life of the tenant. A tenant can limit how long its protocol events are kept, and a platform
administrator can delete or export a tenant and erase a developer's identity; see
[tenant data](../operate/tenant-data.md).

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

- **Platform administrators** sign in at `/login`. The local form on this page stays available even if a
  tenant disables local login or misconfigures its identity providers, so you cannot lock yourself out. The
  endpoint behind it is `POST /api/auth/login`. An installation can add an identity provider for platform
  administrators to the same page; see
  [platform administrators and the identity provider of the installation](#platform-administrators-and-the-identity-provider-of-the-installation).
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

### Platform administrators and the identity provider of the installation

Platform administrators can sign in through one OpenID Connect identity provider that the installation
configures, for example the Keycloak realm of its staff. The `MEISTER_PLATFORM_ADMIN_SSO_*` variables under
[single sign-on for platform administrators](../operate/configuration.md#single-sign-on-for-platform-administrators)
configure it, and the license key `sso-authentication` unlocks it. Without that key, ProPR ignores the provider,
the sign-in page shows the local form only, and the API writes a warning to its log when it starts.

ProPR links an account to the identity at the provider, the pair of issuer and subject. The role in the ID token
decides at every sign-in:

| Sign-in through the provider | What ProPR does |
|---|---|
| The role claim contains the required role, and no account is linked to the identity | ProPR creates an account with platform administrator rights and links it to the identity. |
| The role claim contains the required role, and an account is linked | ProPR signs the account in. An account that lost its rights earlier receives them again. |
| The role claim does not contain the required role | ProPR refuses the sign-in with `platform_role_missing`. A linked account loses its platform administrator rights, and ProPR revokes its refresh tokens and personal access tokens. An access token the account already holds expires within 15 minutes. |
| The linked account is disabled | ProPR refuses the sign-in. |

ProPR reads the role only when someone signs in through the provider. A session that started before the role
was removed at the provider keeps its rights until it ends. To end it at once, disable the account under
**Administration → Users**.

The sign-in through the provider never links an existing account. If an account with the same email address
has a local password, the first sign-in is refused with `external_link_requires_confirmation`. If the address
belongs to an account without a password, for example one that a tenant identity provider created, ProPR creates
a separate account. The account that the sign-in creates carries no email address, so a tenant identity
provider, which finds accounts by email, cannot sign in to it.

The sign-in uses the authorization code flow with PKCE and a nonce. The state of a sign-in stays in an httpOnly
cookie of the browser that started it, and ProPR refuses a callback that arrives without that cookie.

ProPR sends the browser back with the access token in the URL fragment, and only to a page on one of these
origins: the origin of `MEISTER_PUBLIC_BASE_URL`, an origin listed in `CORS_ORIGINS`, or, when
`MEISTER_PUBLIC_BASE_URL` is not set, the origin the browser reached the API at. In Development the local
frontend origins on ports 3000 and 5173 qualify as well. The origins that the API accepts for the Azure DevOps
extension do not qualify.

Keep at least one local platform administrator account. The local sign-in still works when the provider is
unreachable, when the license no longer grants `sso-authentication`, and when no account holds the role any
more. If the last active platform administrator loses the role, ProPR writes a warning to its log. Its next start
then creates the bootstrap administrator from `MEISTER_BOOTSTRAP_ADMIN_USER` and
`MEISTER_BOOTSTRAP_ADMIN_PASSWORD`, and fails without these variables.

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

## Platform administrator access to tenant content

A platform administrator reads the content of a tenant only during an elevation that covers the tenant or the
client the content belongs to. The content of a tenant is the text from its repositories and pull request
conversations and the answers of the AI about them. Without an elevation, a platform administrator reads the
metadata of every tenant: its clients and their configuration without secrets, the review jobs with their status,
times, sizes, finding counts, token and cost figures, the usage and budget figures, and the audit history of the
tenant.

The System tenant belongs to the installation, and its clients need no elevation. Every other client needs an
elevation, also when the platform administrator holds a role on it through a tenant membership or a client
assignment. Tenant administrators and tenant members read the content of their clients through their roles,
without an elevation. A tenant machine credential belongs to its tenant and holds no platform administrator role.
It reads the reviews of its tenant without an elevation, and ProPR writes no elevation records for these reads.

Elevations are part of [multi-tenancy](editions.md#what-requires-a-commercial-license). An installation without
multi-tenancy uses the System tenant only.

### Elevations

A platform administrator starts an elevation for themselves on the page of a tenant or of one of its clients, or
with `POST /admin/elevations`. An elevation has a scope, a reason and a duration. The scope is the tenant or one of
its clients, and the reason is a text such as a ticket number. `MEISTER_PLATFORM_ADMIN_ELEVATION_MAX_MINUTES` sets
the longest duration, four hours by default; see
[the environment variable reference](../operate/configuration.md#platform-administrator-elevations).

An elevation ends at its expiry, and any platform administrator can end it earlier. While an elevation is active,
the management UI shows a banner with the remaining time on every page.

An elevation grants read access to the content of its scope, not write access. A platform administrator changes the
data of a tenant under the same rules with and without an elevation.

ProPR records the start, the end or the expiry, and every content read of an elevation in the audit history of the
tenant, with the platform administrator, the scope and the reason. The record of a read also contains the request,
the client and the review job. ProPR writes the record before it sends the content. If ProPR cannot write the
record, it answers the request with `503` and sends no content.

Tenant administrators learn of an elevation in the **Audit history** section of their tenant, not by email.
Platform administrators read every elevation with its records on the **Elevations** page under **Administration**.
The endpoints behind both pages are described under
[platform administrator elevations](api.md#platform-administrator-elevations) and
[tenant audit history](api.md#tenant-audit-history) in the API reference.

### Endpoints that return content

If no elevation covers the client, ProPR answers a content request of a platform administrator with `403` and the
code `elevation_required`. The answer contains the identifiers of the tenant and the client. In place of the
refused content, the management UI offers to start an elevation for that client. ProPR answers `400` when a
content request of a platform administrator carries no readable client identifier or names `clientId` more than
once. It answers `404` when the client or the review job does not exist.

The routes below are the routes of the API; the bundled frontend proxy serves them under `/api`. The routes under
`/jobs` are aliases of the routes under `/reviewing/jobs`.

| Endpoint | Content |
|---|---|
| `GET /reviewing/jobs/{id}/result` | ProPR returns the summary and the findings of a review job. |
| `GET /reviewing/jobs/{id}/protocol` and `GET /reviewing/jobs/{id}/protocol/{protocolId}` | ProPR returns the protocol traces of a review job or a thread pass, with their events. |
| `GET /reviewing/jobs/{jobId}/files/{fileResultId}/diff` | ProPR returns the diff of a reviewed file. |
| `GET /reviewing/jobs/{id}/code-insights/findings` | ProPR returns the classification of the findings of a review job, with the reasons of their risk assessment. |
| `GET /reviewing/jobs/{jobId}/status`, `GET /reviews/{jobId}` and `GET /clients/{clientId}/reviewing/jobs/{jobId}/status` | ProPR returns the status of a review job with its result. |
| `GET /clients/{clientId}/reviewing/pr-view` | ProPR returns the reviews, summaries and thread memory of one pull request. |
| `GET /clients/{clientId}/review-archive/pull-requests/threads`, `.../files` and `.../file-diff` | ProPR returns the retained comment threads, files and diffs of a pull request. |
| `GET /admin/reviewing/thread-memory` and `GET /admin/thread-memory` | ProPR returns the thread memory of the client named in `clientId`. |
| `POST /clients/{clientId}/review-targets/overview` and `GET /clients/{clientId}/review-targets/{targetId}/open-reviews` | ProPR returns the open pull requests with their titles and authors, read from the source-control host. |
| `GET /admin/clients/{clientId}/procursor/sources/{sourceId}/token-usage/events` and `GET /admin/clients/{clientId}/procursor/token-usage/export` | ProPR returns the ProCursor usage events with the paths of the indexed sources. |

For the endpoints that span several clients, ProPR answers a platform administrator with the metadata of every
client and limits the content to the clients whose content the administrator may read:

| Endpoint | Answer to a platform administrator |
|---|---|
| `GET /reviewing/jobs` and `GET /reviewing/jobs/pull-requests` | ProPR lists every review job. For a client that needs an elevation, it withholds the title of the pull request, the excerpt of the summary and the error message, and marks the item with `contentWithheld`. |
| `GET /reviewing/jobs/{id}` | ProPR returns the details of the review job with its token breakdown and its cost. For a client that needs an elevation, it withholds the summary and the error message. |
| `GET /admin/runners` and `GET /admin/runners/{tenantId}` | ProPR lists the runners with the reviews each one runs. For a client that needs an elevation, it withholds the title of the pull request and marks the review with `contentWithheld`. |
| `GET /code-quality/findings`, `GET /code-quality/hotspots`, `GET /reviewer-performance/findings` and `GET /reviewer-performance/misses` | ProPR answers with the findings and files of the clients whose content the administrator may read. A request that names another client in `clientId` is refused with `elevation_required`. |
| `GET /code-quality/concentration` | ProPR limits the ranking by file to the clients whose content the administrator may read. The rankings by client, repository, pull request and job cover every client. |

ProPR records a read through these endpoints under an elevation like every other content read. The endpoints
outside the two tables return metadata or no data of a tenant.

### Data requests of a customer

A platform administrator carries out the data requests of a customer without an elevation: the export of a tenant
with `GET /admin/tenants/{tenantId}/export`, the deletion of a tenant with `DELETE /admin/tenants/{tenantId}` and
the erasure of an identity with `POST /admin/tenants/{tenantId}/identity-erasures`. ProPR records these requests as
described on [tenant data](../operate/tenant-data.md).

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

Changes to user accounts go to the application log as events with an `AuditEvent` property. A platform
administrator's changes are written as `user.disabled`, `user.reenabled` and `user.deleted`. A sign-in through
the identity provider for platform administrators is written as `user.signed_in`, and a refused one as a
blocked `user.signed_in` with its reason. Every grant and every removal of platform administrator rights by
that sign-in is written as `user.platform_admin_granted` or `user.platform_admin_revoked`.

The audit history of a tenant also records the elevations of platform administrators and every content read
under them; see [platform administrator access to tenant content](#platform-administrator-access-to-tenant-content).
