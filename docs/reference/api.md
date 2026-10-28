# API reference - ProPR backend

The endpoints below automate administrative tasks the frontend also offers. Use the frontend for
interactive configuration and these endpoints for scripting.

Every example targets the evaluation stack's origin, `https://localhost:5443`. Its certificate is
self-signed, so the examples pass `curl -k`. Behind your own ingress, substitute your API base URL.

## PR finding metadata

`GET /api/code-quality/findings` returns collected finding metadata to callers with client access and the
Code Insights capability. Scope the read with `clientId`, `repositoryId`, and `pullRequestId`. `from` and
`to` are inclusive dates; omit them for the default thirty-day window.

`limit` is clamped to 1–200. `offset` skips matching rows and treats negative values as zero. Rows are
ordered by review observation time descending and finding ID ascending. Read successive pages until a
page contains fewer than `limit` rows. Each row includes the provider thread ID, legacy severity classification,
nullable `riskAssessment`, classified core tags, disposition and nullable `rejectionReason`. An absent rejection reason represents an unknown
or inapplicable reason.

`riskAssessment` contains `impact`, `likelihood`, `confidence`, derived `severity`, `impactReason`, `likelihoodReason`, `confidenceReason`,
`source` and `rubricVersion`. HTTP enum values use lower camel case; Unknown serializes as `unknown` and
Not applicable as `notApplicable`. Missing assessments remain null. The
[finding risk rubric](../concepts/reviews.md#finding-risk) defines severity and incomplete evidence.
Confidence uses `low`, `medium`, `high` and `certain`; `unknown` is the unassessed state. Confidence
requires a bounded rationale and does not change the severity matrix or numeric classifier confidence.
Review status, completed results and protocol finding DTOs carry the same optional assessment.

Code Quality reads accept independent `impact`, `likelihood`, `confidence` and `riskSeverity` query selections. Omit a
selection to include all values; use `Unknown` for unassessed evidence. Names are case-insensitive and
unrecognized selections select Unknown. These filters also apply to findings drill-through and the
legacy reviewer-performance metric reads. `GET /api/code-quality/risk-over-time` returns bucketed counts
with `dimension=impact`, `likelihood`, `confidence` or `riskSeverity` (default), using the existing type-series response
shape. It accepts the same date, client, repository, PR, file and risk scope and `bucket=day|week|month`.

## Admin authentication

Exchange admin credentials for a JWT:

```bash
curl -k -X POST https://localhost:5443/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username": "admin", "password": "<strong-password-here>"}'
```

The response body carries `accessToken`, `expiresIn` (seconds) and `tokenType`. The refresh token is
issued as an httpOnly cookie, not in the body. Send `Authorization: Bearer <accessToken>` on subsequent
requests. `POST /api/auth/refresh` reads that cookie and returns a new access token. It accepts a
`refreshToken` field in the request body as a fallback for callers that cannot hold cookies.

An installation that turns off the password sign-in answers `POST /api/auth/login` with `409` for every account; see
[installation sign-in policy](#installation-sign-in-policy). Scripts on such an installation
authenticate with a personal access token.

`GET /api/auth/me` returns the caller's global role, per-client and per-tenant roles, the installation
edition, and the state of every licensed capability.

### Personal access tokens

For scripts and CI, use a personal access token, not an admin password. Create one under
**Settings → Personal Access Tokens** in the frontend, or:

```bash
curl -k -X POST https://localhost:5443/api/users/me/pats \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{"label": "ci-pipeline", "expiresAt": "2027-01-01T00:00:00Z"}'
```

`label` is required; `expiresAt` is optional and must be in the future when supplied. An expired date returns
`400` without creating a token. The plaintext `token` is returned once and cannot be retrieved again. Send it in
place of the bearer header:

```bash
curl -k https://localhost:5443/api/clients \
  -H "X-User-Pat: <token>"
```

List tokens with `GET /api/users/me/pats` and revoke one with `DELETE /api/users/me/pats/<pat-id>`. What
a PAT can do, and how it is stored, is in [automation credentials](security.md#automation-credentials).
Administrators can sign in through SSO and create a token under **Settings → Personal Access Tokens**.

`GET /api/users/me/pats/policy` returns `maximumLifetimeDays` for the authenticated user. A null value means no
installation lifetime limit applies. Token creation and listing return the effective `expiresAt`, including the
limit for provisioned users. If a request omits the expiry or selects a later date, ProPR uses the limit.

## Tenant machine credentials

A platform administrator creates a credential with `POST /api/admin/tenants/{tenantId}/machine-credentials`,
supplying `label` and optional future `expiresAt`. The token is returned once. `GET` lists metadata;
`DELETE /api/admin/tenants/{tenantId}/machine-credentials/{credentialId}` revokes a credential.

External services send `X-Tenant-Machine-Token` and keep the token server-side.

| Access | Contract |
|---|---|
| Client operations | ProPR checks current tenant ownership for client configuration, review reads and configured-coordinate submission. Foreign client access returns 403. |
| Administrative operations | Tenant creation, user administration and unrelated administrative endpoints reject machine credentials. Credential lifecycle operations require a platform administrator JWT or PAT. |
| Logical models | Machine callers can read effective models and purpose routes, but cannot change logical mappings or inherited profiles. Unavailable references omit profile metadata. |
| AI configuration | Machine callers can verify candidate edits and select verified client-owned Default, High and Embedding purposes. See [AI credentials](../ai/credentials.md). |
| Rate limits | Requests can return 429. The API-host limit defaults to 256 requests/second; `MEISTER_MACHINE_AUTH_GLOBAL_PERMITS_PER_SECOND` is clamped to 1–4096. Each credential permits 16 BCrypt verifications/second. Retry after the next one-second window. |

## Provisioned users

A hosting platform keeps the accounts of its customers with these endpoints. They require a platform administrator
and the identity provider for provisioned users, which
[single sign-on for provisioned users](../operate/configuration.md#single-sign-on-for-provisioned-users) configures.
Without the provider, ProPR answers them with `404` and `provisioned_sign_in_not_configured`. `{subject}` is the subject of the
person at that provider, as the `sub` claim of the ID token carries it, encoded as a path segment. Every operation is
idempotent. Who signs in, and when the access of a user ends, is described under
[provisioned users](security.md#provisioned-users-and-the-identity-provider-of-a-hosting-platform).

| Operation | Request | Outcome |
|---|---|---|
| Create or update a user | `PUT /api/admin/provisioned-users/{subject}` with `email` and an optional `displayName` | ProPR creates the user and answers `201`, or updates the email address and the display name and answers `200`. The answer contains `subject`, `userId`, `username`, `email`, `displayName` and `isActive`. |
| Set a membership | `PUT /api/admin/tenants/{tenantId}/provisioned-members/{subject}` with `clientIds` | ProPR gives the user the role `TenantUser` in the tenant and the role `ClientUser` on exactly these clients, and answers `200` with the membership. ProPR answers `400` with `client_not_in_tenant` for a client of another tenant, `404` with `provisioned_user_not_found` for a subject without user, and `409` for the System tenant. |
| Remove a membership | `DELETE /api/admin/tenants/{tenantId}/provisioned-members/{subject}` | ProPR removes the membership and the roles on the clients of the tenant, and answers `204` also when there was nothing to remove. When the user holds no membership any more, ProPR disables it and revokes its refresh tokens and personal access tokens. |
| List the members of a tenant | `GET /api/admin/tenants/{tenantId}/provisioned-members` | ProPR returns the provisioned users with a membership in the tenant, ordered by subject, each with `subject`, `userId`, `email`, `displayName`, `isActive`, `role` and `clientIds`. |

ProPR answers a request for a subject whose account holds platform administrator rights with `409` and
`platform_administrator`, and changes nothing. To delete a provisioned user, delete its account with
`DELETE /api/admin/users/{userId}/permanent`. The platform can provision the subject again afterwards.

```bash
curl -k -X PUT "https://localhost:5443/api/admin/provisioned-users/7f1c2b9e-3a4d-4e5f-8a6b-9c0d1e2f3a4b" \
  -H "Content-Type: application/json" \
  -H "X-User-Pat: <token>" \
  -d '{"email": "ada@customer.example", "displayName": "Ada Customer"}'

curl -k -X PUT "https://localhost:5443/api/admin/tenants/<tenant-id>/provisioned-members/7f1c2b9e-3a4d-4e5f-8a6b-9c0d1e2f3a4b" \
  -H "Content-Type: application/json" \
  -H "X-User-Pat: <token>" \
  -d '{"clientIds": ["<client-id>"]}'
```

The person signs in through the button on `/login`, which opens `GET /api/auth/provisioned-user/challenge` with the
page to return to in `returnUrl`.

## Installation sign-in policy

These endpoints answer differently while the installation turns off the password sign-in
(`MEISTER_LOCAL_SIGN_IN_ENABLED=false`) or the identity providers of tenants (`MEISTER_TENANT_SSO_ENABLED=false`); see
[installation sign-in policy](../operate/configuration.md#installation-sign-in-policy).

| Setting | Request | Outcome |
|---|---|---|
| `MEISTER_LOCAL_SIGN_IN_ENABLED=false` | `POST /api/auth/login` | ProPR answers `409` with `local_sign_in_disabled` in `code` for every account and checks no password. |
| `MEISTER_LOCAL_SIGN_IN_ENABLED=false` | `POST /api/auth/tenants/{tenantSlug}/local-login` | ProPR answers `409` with `local_sign_in_disabled` in `error` for every account, also in a tenant that allows local login. |
| `MEISTER_LOCAL_SIGN_IN_ENABLED=false` | `POST /api/users/me/password` | ProPR answers `409` with `local_sign_in_disabled` in `code` and keeps the password. |
| `MEISTER_LOCAL_SIGN_IN_ENABLED=false` | `POST /api/admin/users` | ProPR answers `409` with `local_sign_in_disabled` in `code` and creates no account. |
| `MEISTER_LOCAL_SIGN_IN_ENABLED=false` | `GET /api/auth/options` and `GET /api/auth/tenants/{tenantSlug}/providers` | ProPR lists no `password` in `availableSignInMethods` and answers `localLoginEnabled` with `false`. |
| `MEISTER_TENANT_SSO_ENABLED=false` | `GET /api/auth/external/challenge/{tenantSlug}/{providerId}`, `GET /api/auth/external/callback/{tenantSlug}`, `GET /api/auth/external/pending-link`, `POST /api/auth/external/pending-link/confirm`, `POST /api/auth/external/pending-link/new-account` and `GET /api/auth/external/links` | ProPR answers `409` with `tenant_sso_disabled` in `error`. |
| `MEISTER_TENANT_SSO_ENABLED=false` | `POST /api/admin/tenants/{tenantId}/sso-providers`, and `PUT /api/admin/tenants/{tenantId}/sso-providers/{providerId}` with `isEnabled` set to `true` | ProPR answers `409` with `tenant_sso_disabled` in `error` and changes no provider. Reading, disabling and deleting a provider work as before. |
| `MEISTER_TENANT_SSO_ENABLED=false` | `GET /api/auth/options` and `GET /api/auth/tenants/{tenantSlug}/providers` | ProPR lists no `sso` in `availableSignInMethods` and answers with an empty `providers` list. |

## Platform administrator elevations

A platform administrator starts an elevation for themselves to read the content of one tenant, or of one client
of a tenant, for a limited time. The endpoints that need one are listed under
[platform administrator access to tenant content](security.md#platform-administrator-access-to-tenant-content).
All elevation endpoints require a platform administrator and the `multi-tenancy` capability.

| Operation | Request | Outcome |
|---|---|---|
| Start | `POST /api/admin/elevations` with `tenantId`, optional `clientId`, `reason` and `durationMinutes` | ProPR starts an elevation of the caller and answers `201` with it. An empty reason, a duration above the maximum of the installation, an empty `clientId` and the System tenant answer `400`; leave out `clientId` to cover every client of the tenant. |
| Active elevations | `GET /api/admin/elevations/active` | ProPR returns the active elevations of the caller and `maxDurationMinutes`. |
| End | `POST /api/admin/elevations/{elevationId}/end` | ProPR ends the elevation and answers `200`. An elevation that has already ended or expired answers `409`. |
| List | `GET /api/admin/elevations` with optional `tenantId`, `page` and `pageSize` | ProPR returns the elevations of all platform administrators, the newest first, each with its number of content reads. |
| Records | `GET /api/admin/elevations/{elevationId}/audit-entries` | ProPR returns the start, the content reads and the end or expiry of one elevation. |

```bash
curl -k -X POST https://localhost:5443/api/admin/elevations \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{"tenantId": "<tenant-id>", "clientId": "<client-id>", "reason": "SUP-1042", "durationMinutes": 60}'
```

A content request of a platform administrator without an elevation answers `403`:

```json
{ "error": "...", "code": "elevation_required", "tenantId": "<tenant-id>", "clientId": "<client-id>" }
```

## Tenant audit history

`GET /api/admin/tenants/{tenantId}/audit-entries` with optional `page` and `pageSize` returns the audit history of
a tenant, the newest entry first, to its tenant administrators and to platform administrators. Each entry has an
`eventType`, a `summary`, an optional `detail`, the acting user and the time. An entry that belongs to an
elevation carries its `elevationId`; the event types of elevations are `elevation.started`, `elevation.ended`,
`elevation.expired` and `elevation.content_read`. The endpoint requires the `multi-tenancy` capability.

## Repository review targets

All paths below start with `/api/clients/{clientId}/review-targets`. The client requires the crawl
configuration capability. Each repository has its own target identity, including repositories in the
same project or namespace.

| Operation | Input | Outcome |
|---|---|---|
| `GET /` | No provider credentials are required for saved targets. | ProPR returns configured targets. |
| `GET /repositories` | `connectionId` selects an active, verified, client-owned connection; `scopePath` selects an enabled scope. | ProPR returns repository IDs, names, project keys/display names and owner namespaces. |
| `POST /` | Supply `connectionId`, `providerProjectKey`, `repositoryId`, `repositoryName` and optional `providerScopePath` and `targetBranchPatterns`. | ProPR verifies repository access and returns 201. An identical existing target returns 200; conflicting identity or policy returns 409. New targets allow manual reviews with crawling off. |
| `PATCH /{targetId}` | Supply `connectionId`, `expectedTargetBranchPatterns` and `targetBranchPatterns`. Both arrays are required. | ProPR replaces only destination policy. A stale expected policy or identity returns 409 without changing activation or other settings. |
| `GET /management` | Accepts `search`, `provider`, `status` (`enabled` or `disabled`), `page` and `pageSize` (default 25, maximum 100). | ProPR returns saved metadata without SCM access, excluding removed targets, ordered by repository name and ID. |
| `GET /management/{targetId}` | The target must belong to the client. | ProPR returns the saved target, including removed state. Missing or foreign targets return 404. Use this read to resolve uncertain lifecycle writes. |
| `PATCH /{targetId}/lifecycle` | Supply `expectedRevision` and `lifecycle` (`enabled`, `disabled`, `removed`). Removal also requires the saved repository name in `confirmationName`. | A stale revision returns 409. Every lifecycle write leaves crawling off; already accepted reviews and history remain recorded. |
| `PATCH /{targetId}/automation` | Supply `expectedRevision` and explicit `isActive`; enabling also requires a nonempty `connectionId` with current authority. | The response contains activity and revision confirmed under the operation's admission lease, even when a later mutation changes saved state. Pausing requires no connection or provider credentials. Stale revisions return 409; unavailable enable authority returns 403. |

Azure DevOps uses the saved organization URL as `scopePath` and requires `providerScopePath` on the
selected connection's origin. Enabled organization metadata must belong to that exact connection;
a shared `dev.azure.com` origin does not establish coverage. Other providers use an owner or namespace
and derive target scope from the connection host. Discovery returns the full namespace for nested
repositories. Azure `providerProjectKey` is the stable project ID; `providerProjectDisplayName` is its
browser-visible name. Provider-family values are `azureDevOps`, `github`, `gitLab` and `forgejo`.

| Constraint | Contract |
|---|---|
| Revision | `revision`, `expectedRevision` and `expectedRemovedRevision` are positive decimal strings, from `"1"` through `"9223372036854775807"`. Preserve them as strings, including values above JavaScript's safe integer limit. |
| Management pagination | The opaque `snapshotVersion` covers **all matching target representations**, including identity, metadata, revision, lifecycle, crawl state and policy. Combine pages only when every version matches; equal counts are insufficient. |
| Removal | Removed identities continue to exclude customer submissions through overlapping configurations. A missing item on a management page does not confirm removal. |
| Restoration | Creating a removed repository returns 409 with `removedTarget`. Restore through creation with `restoreRemovedTarget: true`, `expectedRemovedRevision` and current provider access. Restoration preserves the target ID, replaces destination policy and leaves crawling off. Lifecycle PATCH cannot restore a removed target. |
| Administrative writes | Canonical target deletion returns 409 in every lifecycle state. Protected lifecycle/filter conflicts also return 409 without partial settings changes. Generic crawl configurations retain administrative deletion. |
| Branch patterns | Empty patterns allow all branches. Up to 100 patterns of at most 512 characters are accepted; this bound does not truncate provider branch names. Exact names and case-insensitive `*`/`**` path globs are supported. |
| Pattern validation | ProPR removes `refs/heads/` and surrounding whitespace. Empty and dot segments do not affect matching or duplicate detection. Blank/control-character patterns, patterns without effective segments and equivalent duplicates return field errors without a write. |
| Candidate policy | Restricted policies exclude candidates without a destination branch. Fresh coordinate submissions check the current branch and canonical identity; generic configurations cannot bypass target policy. |

## Pull request discovery

All paths below start with `/api/clients/{clientId}/review-targets`. Every read, including cached reads,
requires current client access, target policy and an active, verified selected connection covering the
enabled saved scope. Adapters use that connection's current credentials without another connection or
installation-credential fallback. Tenant machine callers require current tenant ownership.

Installed adapters require positive native pull request or merge request numbers. The domain also
stores an external review identifier; these operations do not accept opaque-only review identities.

| Operation | Input | Outcome |
|---|---|---|
| `GET /{targetId}/open-reviews` | Supply `connectionId`. | ProPR returns at most 100 policy-matching open or draft reviews with available branches, author, URL and revision. Successful empty results are empty lists; provider failures return fixed 502 feedback. |
| `GET /{targetId}/open-reviews/{number}/metadata` | Supply `connectionId` and a positive native number. | ProPR returns nullable message/discussion counts, `isComplete` and `resolutionSupported`. Provider failures return fixed 502 feedback. |
| `POST /overview` | Supply `sources` (target/connection pairs), non-secret selection `binding`, optional `cursor`, `page`, `pageSize`, `loadMore` and `reload`. | ProPR returns immutable cumulative rows, source outcomes, coverage, nullable `totalRows`, observation times, freshness, expiry and next refresh time. Responses use `Cache-Control: no-store`. |

The example ingress URL is `POST https://localhost:5443/api/clients/{clientId}/review-targets/overview`.
Backend-relative OpenAPI paths omit the ingress `/api` prefix.

| Overview constraint | Contract |
|---|---|
| Pages | `pageSize` defaults to 25 and permits at most 100. Pages above one require a generation cursor. |
| Loading | `loadMore` creates a cumulative generation returned at page 1. `reload` uses the displayed generation cursor; omitting it starts a new read. |
| Timing | Observations are fresh for 60 seconds. Refresh attempts share a 60-second cooldown, including failures. Generation eligibility is at most 15 minutes and cannot exceed original listing or metadata deadlines. |
| Coverage | Each load attempts at most 8 sources and consumes native pages under one source refresh. Each generation covers at most 100 sources. Source `isComplete` is true only after terminal native coverage; null denotes older observations without coverage evidence. Source `collectedRows` counts distinct eligible observations. Page `isComplete` reports complete listing coverage for all selected sources, independent of visible metadata completeness. `totalRows` counts collected display rows, not an upstream total. |
| Response capacity | Serialized responses are limited to 2 MiB. Capacity refusal returns no items or actionable cursor and `totalRows: null`, with source outcomes and next permitted refresh time. Unavailable totals are not zero. |
| Expiry | Expired navigation requires a new read. Replacements cannot reactivate an expired cursor. |
| Confirmed denial | `accessDenied` invalidates affected observations and retained cursors. Metadata-only permission denial keeps eligible listing rows with unavailable counts. |
| Transient failure | `throttled` and other transient failures can retain eligible stale rows with their original observation times and deadlines. |
| Metadata | Unavailable or incomplete counts are not zero. Nullable fields, `isComplete` and `resolutionSupported` describe the result; independently known counts can remain available. Complete empty collections have zero counts. Listing does not read discussions for every candidate. |

| Metadata field | Meaning |
|---|---|
| `totalComments` | Published user messages from all authors, not only the reviewer. Deleted/system messages and unpublished review drafts are excluded where the provider exposes them. |
| `resolvedDiscussions`, `unresolvedDiscussions` | Native discussion counts, not message counts or review approvals. Forgejo returns null for both and `resolutionSupported: false`. |
| `isComplete` | The bounded provider read completed. Missing states, malformed fields or overlapping pagination can leave counts unknown. |
| Read limits | REST reads use at most 12 data requests per review and at most 2 MiB per response, with pages up to 100 items. GitLab inspects at most 3,000 notes; Azure DevOps permits at most 1,000 threads and 10,000 comments. Reads have a 15-second deadline. |

GitHub counts conversation comments, review comments and nonblank published review summaries; GitLab
uses discussion notes; Azure DevOps uses live text comments and native thread status. Forgejo counts
issue comments, published code comments and nonblank published review summaries once. Selected Forgejo
verification and metadata preserve deployment prefixes; open-review discovery does not support them.

## Client review history

All paths below start with `/api/clients/{clientId}/reviewing`. Reads remain available after SCM
connections or targets are removed. Missing or foreign jobs return 404.

| Operation | Input | Outcome |
|---|---|---|
| `GET /dashboard` | The caller requires current client access. | ProPR returns processing reviews and findings persisted in completed results over `[windowStart, windowEnd)`, the preceding 30 days. Disabling comment publication does not remove findings from the count. |
| `GET /history` | `page` defaults to 1; `pageSize` defaults to 25 and permits 1–100; optional `status` is a named job status. | ProPR returns `totalCount`, paging fields and metadata items, ordered by submission time and review ID descending. Invalid bounds/status return 400. |
| `GET /jobs/{jobId}/status` | The client must own the job. | ProPR returns job status and persisted result. |

## Completed-review usage export

`GET /api/clients/{clientId}/reviewing/completed-usage` requires `X-Tenant-Machine-Token` and current
tenant ownership of the client.

| Input or field | Contract |
|---|---|
| `after` | The optional sequence cursor is exclusive. |
| `limit` | The default and maximum are 100; valid values are 1–100. |
| `items` | Only completed reviews with finalized protocols and measured execution duration produce facts. Facts have immutable sequence and content. |
| `nextCursor` | The cursor is the last returned sequence; an empty page returns null. Persist each fact before advancing. Keep the supplied cursor on an empty page, and request another page after a non-empty page to confirm completion. |
| Replay | Replaying a cursor preserves existing facts and can include newly finalized facts. |
| `executionDurationMilliseconds` | The duration covers processing across attempts and excludes queue time. |
| `aiConnectionId` | The ID identifies the connection, not BYOK or platform billing mode. Resolve billing mode from the consuming service's approved configuration. |
| `estimatedCostUsd` | The nullable decimal amount has up to six fractional digits. Null means no reliable price, not zero cost. |
| `costIsApproximate` | The flag is true when a component is estimated or the priced total excludes an unpriced component. |

Facts also contain `sequence`, `jobId`, `clientId` and `completedAt`. The response contains no
machine token, provider secret or customer credit rate.

## Client management

All paths below start with `/api/clients`.

| Operation | Input | Outcome |
|---|---|---|
| `GET /` | The caller requires client access. | ProPR lists accessible clients. |
| `POST /` | Supply `displayName` and required `tenantId`. | ProPR creates a client and returns its ID. |

Creating a client requires tenant administration; creating one in the System tenant requires platform
administration. `GET /api/admin/tenants` lists available tenants. The seeded System tenant ID is
`11111111-1111-1111-1111-111111111111`.

## SCM provider connections

All paths below start with `/api/clients/{clientId}/provider-connections`.
Provider and authentication fields depend on the [support matrix](../platforms/index.md#support-matrix).
Secrets are write-only.

| Operation | Input | Outcome |
|---|---|---|
| `POST /` | Supply `providerFamily`, `authenticationKind`, `hostBaseUrl`, display name, secret and mode-specific identifiers. | ProPR stores a protected provider connection. Creation does not verify provider access. |
| `PATCH /{connectionId}` | Supply changed connection fields. | ProPR updates the owned connection. See the platform page for credential replacement requirements. |
| `POST /{connectionId}/scopes` | Supply `scopeType`, `externalScopeId`, `scopePath`, `displayName` and `isEnabled`. | ProPR saves the provider scope. Azure DevOps uses an organization or collection URL. |
| `POST /{connectionId}/verify` | Azure DevOps requires an enabled organization scope. | ProPR verifies provider access separately from caller authorization. |
| `GET /{connectionId}/reviewer-identities/resolve` | Supply `search`. | ProPR resolves provider users. |
| `PUT /{connectionId}/reviewer-identity` | Supply `externalUserId`, `login`, `displayName` and `isBot`. | ProPR saves the reviewer identity. |

## AI connection profiles

All paths below start with `/api/clients/{clientId}/ai-connections`.
[AI credentials](../ai/credentials.md) describes provider-specific configuration.

| Operation | Input | Outcome |
|---|---|---|
| `GET /` | The caller requires client access. | ProPR lists profile metadata, including creation correlation identifiers, without secrets. Use this read to recover an interrupted creation response. |
| `GET /permitted-providers` | The caller requires client access. | ProPR returns installed family keys, tenant permissions, authentication modes, credential fields and protocol modes. |
| `POST /probe` | Supply `providerKind`, `baseUrl` and `auth`. | ProPR probes the unsaved candidate. |
| `POST /discover-models` | Supply the candidate provider, URL and authentication. | ProPR discovers available models. |
| `POST /` | Supply `displayName` (at most 200 characters), `providerKind`, `baseUrl`, `auth`, and non-empty `configuredModels` and `purposeBindings`. | ProPR creates a profile with model IDs; missing required fields return 400. |
| `POST /{connectionId}/verify` | The connection must belong to the client. | ProPR stores the verification result. |
| `POST /{connectionId}/verify-update` | Supply candidate profile edits. | ProPR verifies the candidate before replacement; concurrent configuration conflicts require readback and retry. |
| `POST /{connectionId}/activate` | The profile requires successful verification since its last configuration change. | ProPR activates the profile; an unverified profile returns 400. |
| `POST /select-purposes` | Supply `default`, `high` and `embedding`, each with `connectionId` and `configuredModelId`. | ProPR selects verified client-owned models and rejects conflicting logical or managed bindings. |

Optional `creationRequestId` is a non-empty UUID for interrupted-creation readback. Create and list
responses retain this immutable client-scoped identifier. Concurrent or later duplicate creation
returns 409 without updating the existing profile or replaying 201. Retrieve the profile list to recover
an uncertain result. The identifier is not a credential or TTL-based idempotency ledger; physical
profile deletion permits later reuse. Omitted correlation remains null.

Every profile carries `configurationRevision`, which starts at 1. ProPR advances it when an update
changes the provider, base URL, authentication mode, credential, default headers, default query
parameters or provider settings. A rename, a model change and a credential the provider family renews
on its own keep the revision.

### Purpose bindings

| Field | Contract |
|---|---|
| `purpose` | Each [AI purpose](../ai/purposes.md#ai-purposes) can occur once. |
| `remoteModelId` or `configuredModelId` | The binding selects a configured model on this profile. |
| `protocolMode`, `isEnabled` | Defaults are `Auto` and true. Embedding purposes require an embedding-capable model and `Auto` or `Embeddings`; other purposes require chat capability. |

Logical-model selection takes precedence over profile bindings.

Client model selection saves verified purpose bindings atomically and preserves them across restarts.
Explicit logical-purpose mappings take precedence. A conflicting selection returns 409 and preserves the
saved bindings. See [client model selection](../ai/credentials.md#client-model-selection).

### Per-model inputs

| Field | Contract |
|---|---|
| `remoteModelId` | The ID is required and case-insensitively unique within the request. |
| `displayName` | The name defaults to the remote ID. |
| `operationKinds` | Values are `chat`, `embedding`, or both. If omitted, embedding metadata or an ID containing `embedding` selects embedding; otherwise ProPR selects chat. |
| `supportedProtocolModes` | Use installed family declarations from `permitted-providers`. Omission infers compatible modes; incompatible explicit modes are rejected. |
| `tokenizerName`, `maxInputTokens`, `embeddingDimensions` | Embedding models require a tokenizer, positive input limit and dimensions from 64 through 4096. |
| `supportsStructuredOutput`, `supportsToolUse` | Both flags default to false. |
| `maxContextTokens` | ProPR uses the context window for budgeting. |
| `inputCostPer1MUsd`, `outputCostPer1MUsd`, `cachedInputCostPer1MUsd` | ProPR uses these prices for spend reporting. |
| `id`, `source`, `lastSeenAt` | Round-trip discovery metadata; hand-written models can omit it. Source values are `discovered`, `manual` and `knownCatalog`. |

### Logical models and purposes

A logical-model name selects one model on one connection. A purpose selects a logical-model name.

| Operation | Outcome |
|---|---|
| `GET /api/clients/{clientId}/logical-models` | ProPR returns client overrides and inherited tenant names not overridden by them. |
| `POST /api/clients/{clientId}/logical-models/overrides` | ProPR defines a client-owned name. |
| `PUT /api/clients/{clientId}/logical-models/purposes/{purpose}` | ProPR assigns a purpose to a name. |
| `GET /api/tenants/{tenantId}/logical-models` | ProPR returns the tenant catalog. |

The System tenant rejects tenant-catalog writes; its clients use overrides. Model catalog and pricing
overrides use `/api/tenants/{tenantId}/model-catalog/`; see [models and the catalog](../ai/models-and-catalog.md).

### Scripted setup

Create the profile, verify it, activate it, then define logical models and assign purposes.
Unmapped purposes cannot perform their workload.

| Input | Contract |
|---|---|
| `auth.mode` | Use the family-declared authentication key returned by `permitted-providers`. |
| `auth.apiKey` or `auth.fields` | Supply the single-string key or declared named credential fields. ProPR rejects missing required fields and undeclared names. Modes without credential fields can use host identity. |
| `defaultQueryParams` | Supply provider settings such as Bedrock `region` or Vertex AI `project`; see [provider setup notes](../ai/credentials.md#provider-specific-setup-notes). |
| `defaultHeaders` | Supply headers required by a gateway and probe the configuration. |
| Logical-model `capability` | Use `chat` or `embedding`. |
| Logical-model `reasoningEffort` | Use a supported [reasoning effort](../ai/purposes.md#reasoning-effort). |

## Guided discovery endpoints

Guided configuration uses `/admin/clients/{clientId}/connections/{connectionId}/discovery`.
Every request requires an explicit `purpose`: `crawl`, `webhook`, `mention` or `procursor`.
Crawl discovery requires `ClientUser` and the `crawl-configs` capability. Mention discovery requires
`ClientAdmin`, the `mention-answering` capability and native mention capabilities. Webhook and
ProCursor discovery require `ClientUser`. These read permissions do not grant configuration writes.

Read `descriptor` for the native scope label, optional project stage, source kinds and supported
operations. Read `scopes`, then `projects` when the descriptor declares a project stage.
`sources` returns native repository identifiers, canonical references and persistent coordinates.
`selection` resolves persistent coordinates even when a repository listing is empty. Use
`filters` for crawl and webhook repository choices, and `branches` when supported.

```bash
curl -k "https://localhost:5443/api/admin/clients/<client-id>/connections/<connection-id>/discovery/descriptor?purpose=webhook" \
  -H "Authorization: Bearer <accessToken>"

curl -k "https://localhost:5443/api/admin/clients/<client-id>/connections/<connection-id>/discovery/scopes?purpose=webhook" \
  -H "Authorization: Bearer <accessToken>"

curl -k "https://localhost:5443/api/admin/clients/<client-id>/connections/<connection-id>/discovery/sources?purpose=webhook&scopeKey=<scope-key>&projectId=<project-id>&sourceKind=repository" \
  -H "Authorization: Bearer <accessToken>"
```

Unknown or missing purposes return HTTP 400. Empty lists return HTTP 200. Unsupported native
operations return HTTP 501. Unavailable resources return HTTP 404; invalid connection selections
or native discovery failures return HTTP 400 with sanitized guidance. Entitlement refusals return
HTTP 409. A supplied connection must belong to the client and be active before native discovery runs.

Guided creates include `connectionId` and `scopeKey`. The server validates supplied provider and
scope coordinates against the native selection. Manual configuration APIs require an explicit
provider. Saved legacy configurations remain readable, and unrelated patches do not replace filters.
Source discovery rejects scopes unavailable through the selected connection before repository queries.
Saved native project names and ID casing resolve through native project metadata; patches retain
stored coordinates. Repository-filter replacement preserves unchanged legacy targets with null
canonical references. Retained mention repositories preserve omitted source metadata and claim times;
explicit conflicting source coordinates are refused.
Native project IDs take precedence over name fallback. Guided mention patches refuse changed
canonical/provider coordinates for retained repository IDs even when they match live discovery.

## ProCursor source management

Create a guided ProCursor source:

```bash
curl -k -X POST https://localhost:5443/api/admin/clients/<client-id>/procursor/sources \
  -H "Authorization: Bearer <accessToken>" \
  -H "Content-Type: application/json" \
  -d '{
    "displayName": "Platform Docs",
    "sourceKind": "repository",
    "connectionId": "<connection-id>",
    "scopeKey": "<scope-key>",
    "organizationScopeId": "<scope-id>",
    "providerProjectKey": "my-project",
    "canonicalSourceRef": {
      "provider": "azureDevOps",
      "value": "repo-1"
    },
    "sourceDisplayName": "platform-docs",
    "defaultBranch": "main",
    "rootPath": "/docs",
    "symbolMode": "auto",
    "trackedBranches": [
      {
        "branchName": "main",
        "refreshTriggerMode": "branchUpdate",
        "miniIndexEnabled": true
      }
    ]
  }'
```

`sourceKind` must be declared by the selected connection descriptor. Knowledge-source creation requires
its knowledge-source capability. Azure DevOps supports `repository` and `adoWiki`; other native adapters
do not register knowledge-source materializers. `refreshTriggerMode` is `manual` or `branchUpdate`.

## Crawl configurations

Crawl configurations require the `crawl-configs` capability; see [editions](editions.md). Resolve
repository filters, then create one:

```bash
curl -k "https://localhost:5443/api/admin/clients/<client-id>/connections/<connection-id>/discovery/filters?purpose=crawl&scopeKey=<scope-key>&projectId=my-project" \
  -H "Authorization: Bearer <accessToken>"

curl -k -X POST https://localhost:5443/api/admin/crawl-configurations \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{
    "clientId": "<client-id>",
    "connectionId": "<connection-id>",
    "scopeKey": "<scope-key>",
    "organizationScopeId": "<scope-id>",
    "providerProjectKey": "my-project",
    "crawlIntervalSeconds": 60,
    "reviewTemperature": 0.2,
    "repoFilters": [
      {
        "displayName": "platform-docs",
        "canonicalSourceRef": {
          "provider": "azureDevOps",
          "value": "repo-1"
        },
        "targetBranchPatterns": ["main"]
      }
    ],
    "proCursorSourceScopeMode": "selectedSources",
    "proCursorSourceIds": ["<source-id>"]
  }'
```

`clientId` is required. When `selectedSources` is used the chosen source IDs are snapshotted onto
queued review jobs. `reviewTemperature` is optional; what it does and what it accepts is under
[what you can tune](../concepts/reviews.md#what-you-can-tune).

## Webhook configurations

Webhook configurations are managed per client and can coexist with crawl configurations for the same
repositories. [Webhooks](../platforms/webhooks.md) explains what one is, what to do with the secret it
returns, and how to read its deliveries.

```bash
# List webhook configurations visible to the caller
curl -k https://localhost:5443/api/admin/webhook-configurations \
  -H "Authorization: Bearer <accessToken>"

# Create a webhook configuration
curl -k -X POST https://localhost:5443/api/admin/webhook-configurations \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{
    "clientId": "<client-id>",
    "provider": "azureDevOps",
    "organizationScopeId": "<scope-id>",
    "providerProjectKey": "my-project",
    "reviewTemperature": 0.15,
    "enabledEvents": [
      "pullRequestCreated",
      "pullRequestUpdated",
      "pullRequestCommented"
    ],
    "repoFilters": [
      {
        "repositoryName": "platform-docs",
        "displayName": "platform-docs",
        "canonicalSourceRef": {
          "provider": "azureDevOps",
          "value": "repo-1"
        },
        "targetBranchPatterns": ["main", "release/*"]
      }
    ]
  }'
```

`clientId` is required, `provider` is one of `azureDevOps`, `github`, `gitLab` or `forgejo`, and
`enabledEvents` accepts only the three names above. `reviewTemperature` is optional here too.

The create response carries:

- `listenerUrl`: the public HTTPS path your provider posts to
- `generatedSecret`: returned once at creation time
- `repoFilters`: the stored repository and branch scope, enforced on every delivery regardless of what
  the payload claims

Inspect recent delivery history for one webhook configuration:

```bash
curl -k "https://localhost:5443/api/admin/webhook-configurations/<config-id>/deliveries?take=20" \
  -H "Authorization: Bearer <accessToken>"
```

Each delivery-history entry records the sanitized incoming event summary, final outcome, response
status, and the downstream actions that were invoked. What each outcome and status means, and what to
change, is in [webhook troubleshooting](../platforms/webhooks.md#troubleshooting).

Update or delete an existing webhook configuration:

```bash
curl -k -X PATCH https://localhost:5443/api/admin/webhook-configurations/<config-id> \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{
    "isActive": false,
    "reviewTemperature": 0.0,
    "enabledEvents": ["pullRequestUpdated"],
    "repoFilters": []
  }'

curl -k -X DELETE https://localhost:5443/api/admin/webhook-configurations/<config-id> \
  -H "Authorization: Bearer <accessToken>"
```

## Public webhook receiver

Point Azure DevOps webhooks at the one-time `listenerUrl` returned when the webhook configuration is
created, and use Basic auth with the generated secret as the password.

```bash
curl -k -X POST https://localhost:5443/webhooks/v1/providers/ado/<path-key> \
  -H "Authorization: Basic <base64(username:generated-secret)>" \
  -H "Content-Type: application/json" \
  -d '{
    "eventType": "git.pullrequest.updated",
    "resource": {
      "pullRequestId": 42,
      "repository": { "id": "repo-1" },
      "sourceRefName": "refs/heads/feature/webhooks",
      "targetRefName": "refs/heads/main",
      "status": "active"
    }
  }'
```

The receiver acknowledges every delivery that passes validation, including events it ignores:

```json
{ "status": "accepted" }
```

```json
{ "status": "ignored" }
```

The trigger decides when a review starts, not what it produces - see
[how a review gets triggered](../concepts/how-it-works.md#how-a-review-gets-triggered).

## Trigger a review

Submit a pull request for review. Requires the `ClientAdministrator` role for the client.

```bash
curl -k -X POST https://localhost:5443/api/clients/<client-id>/reviewing/jobs \
  -H "Content-Type: application/json" \
  -H "X-User-Pat: <token>" \
  -d '{
    "provider": "azureDevOps",
    "hostBaseUrl": "https://dev.azure.com",
    "repository": {
      "externalRepositoryId": "repo-1",
      "ownerOrNamespace": "my-org",
      "projectPath": "my-project"
    },
    "codeReview": {
      "platform": "pullRequest",
      "externalReviewId": "42",
      "number": 42
    },
    "reviewRevision": {
      "headSha": "<head-sha>",
      "baseSha": "<base-sha>",
      "providerRevisionId": "3"
    }
  }'
```

`provider`, `hostBaseUrl`, `repository` and `codeReview` are required; `number` must be at least 1 and
`platform` is `pullRequest` or `mergeRequest`. `reviewRevision` is optional, but if you send it,
`headSha` and `baseSha` are required within it.

Send `reviewRevision`. ProPR treats a submission as a duplicate of an in-flight job only when the whole
revision matches. Omit it and every submission for the same pull request collapses onto one revision, so
a second push cannot be reviewed while the first review is still running.

A successful submission returns `202 Accepted` with `jobId` and `status`. A `409 Conflict` means an
active job already exists for that revision, or the pull request is blocked. A review running on another
pull request, or on this one at an earlier revision, is not a conflict. The submission is queued and
starts once the installation's concurrency ceiling allows it - see [editions](editions.md).

### Trigger a review from coordinates alone

Submit configured repository coordinates to have ProPR read the current revision from the SCM host.

```bash
curl -k -X POST https://localhost:5443/api/clients/<client-id>/reviewing/jobs/by-coordinates \
  -H "Content-Type: application/json" \
  -H "X-User-Pat: <token>" \
  -d '{
    "providerScopePath": "https://dev.azure.com/my-org",
    "providerProjectKey": "my-project",
    "repositoryId": "<repository-id>",
    "pullRequestId": 42
  }'
```

| Input constraint | Contract |
|---|---|
| Required coordinates | Supply `providerScopePath`, `providerProjectKey`, `repositoryId` and a positive native `pullRequestId`. |
| Saved coverage | Scope and project must exactly match the client's crawl or webhook configuration. Repository filters must cover the supplied repository. |
| Current policy | Canonical repository lifecycle and destination policy apply even through an overlapping generic configuration. Disabled or removed targets cannot admit customer reviews. |
| Manual-only setup | Inactive generic configurations still permit manual submission. This does not override canonical target lifecycle. |
| Execution settings | ProPR applies the current configured source scope and review temperature. |

The same request serves the first review and every re-review after new commits, because the revision is
read fresh each time. An earlier job at an older revision is retired as superseded. Unlike the automatic
triggers, this request reviews a revision that has already been reviewed, or that a previous review
failed at. A review already running at this exact revision is not started twice.

Duplicate jobs and superseded jobs belong to the submitting client. Other clients reviewing the same
pull request retain their jobs and results. Resumption and incremental result reuse also select only
the submitting client's history.

`ClientUser` is enough here, as it is for restart. Every response to a complete request carries a named
`outcome`:

| `outcome` | Status | Means |
|---|---|---|
| `submitted` | 202 | A job was queued. `jobId` is the one to poll |
| `duplicateActiveJob` | 409 | A review of this revision is already running. `jobId` is that job |
| `notSubmittable` | 409 | The pull request is closed, merged, blocked, or its configured source scope no longer resolves. `reason` says which |
| `notAuthorized` | 403 | No configuration of this client covers the coordinates, or you lack the role |
| `pullRequestNotFound` | 404 | The provider reports no such pull request |
| `submissionFailed` | 500 | The review could not be admitted or submitted inside ProPR. The server logs carry the detail |
| `revisionUnresolvable` | 502 | The provider could not be asked, or answered without commits. Check the connection and retry |

A request missing one of the four fields carries no `outcome`. It is refused with `400` and a plain
`{"error": "..."}`, the shape the other endpoints on this page use.

Coverage or admission failures return `submissionFailed` without internal diagnostics.
ProPR reads the revision fresh and rechecks current authorization, lifecycle and branch policy before
queueing with the current source scope and temperature. Provider revision failures return
`revisionUnresolvable`. Caller cancellation propagates before acceptance.

| Job operation | Access and outcome |
|---|---|
| `GET /api/reviewing/jobs/{jobId}/status` | ClientUser can read status and result. |
| `POST /api/reviewing/jobs/{jobId}/restart` | ClientUser can explicitly restart a failed review; failed jobs do not resume automatically. |
| `POST /api/reviewing/jobs/{jobId}/stop` | ClientAdministrator can stop a job. Stopping is terminal and does not requeue it. |

## Blocking and dismissing

Block a pull request so no further review jobs are created for it. This does not stop a job that is
already running - stop that job separately.

```bash
curl -k -X POST https://localhost:5443/api/clients/<client-id>/reviewing/blocked-prs \
  -H "Content-Type: application/json" \
  -H "X-User-Pat: <token>" \
  -d '{
    "providerScopePath": "https://dev.azure.com",
    "providerProjectKey": "my-project",
    "repositoryId": "repo-1",
    "pullRequestId": 42,
    "reason": "Generated code, not worth reviewing"
  }'
```

`GET` the same path to list current blocks, and `POST` the same body without `reason` to
`.../blocked-prs/unblock` to lift one. Listing needs `ClientUser`; blocking and unblocking need
`ClientAdministrator`.

Dismiss a finding so later reviews suppress similar ones:

```bash
curl -k -X POST https://localhost:5443/api/clients/<client-id>/reviewing/dismiss-finding \
  -H "Content-Type: application/json" \
  -H "X-User-Pat: <token>" \
  -d '{
    "findingMessage": "Prefer a guard clause here",
    "filePath": "src/Program.cs",
    "label": "style-preference"
  }'
```

`findingMessage` is required; `filePath` and `label` are optional. The call returns `201` with the
stored memory record and requires `ClientAdministrator`.

## Review diagnostics

`GET /api/jobs/{id}/protocol` returns one review's protocol passes and events. Each event carries an
`eventCategory` you can filter on. Pass `includeEvents=false` for an overview that keeps the event
rows and metadata but omits their bodies, which is much cheaper on large reviews.

Diagnostics are scoped to a single review; there is no cross-review trace query.

Reading a protocol to answer a specific symptom starts at
[troubleshooting](../operate/troubleshooting.md).

## Reviewer performance

These Code Insights endpoints require tenant administration, the licensed capability and access to the
selected clients. `POST /api/reviewer-performance/ranges/query` reads retained joint counts for one or two
views under a consistent database snapshot:

```json
{
  "bucket": "day",
  "aggregation": "cumulative",
  "grouping": "none",
  "views": [
    {
      "from": "2026-09-01",
      "to": "2026-09-30",
      "clientIds": null,
      "repositories": null,
      "models": null,
      "types": null,
      "qualifiers": null,
      "impacts": null,
      "likelihoods": null,
      "riskSeverities": null,
      "confidences": null
    }
  ],
  "breakdown": { "rows": "type", "columns": "qualifier", "date": "2026-09-30", "viewIndex": 0 }
}
```

`bucket` accepts `day`, `week` or `month`; `aggregation` accepts `cumulative` or `period`. Grouping accepts
`none`, `client`, `repository`, `model`, `type`, `qualifier`, `impact`, `likelihood`, `confidence` or `riskSeverity`. Matrix axes use two distinct dimensions from
that list, excluding `none`. Matrix date selects a bucket in its zero-based `viewIndex`; the response
displays the clipped measurement window.

Null selections include all authorized values; empty arrays include none. Use returned facet IDs for
repository and model selections because they include retained identity scope. The query permits 1–366 days
per view, years 1900–9998, at most 128 selections per dimension, 100,000 retained joint cells per view, 24
series, 2,000 timeline points and 12 values per matrix axis. Source identity remains attached to cells.
Each view permits 100,000 distinct retained source coordinates for optional link resolution, 100,000
exclusion catalog entries and 1,000,000 exclusion references across timeline and matrix points. Exceeded
limits return 400 without truncating counts or exclusions. Unauthorized selected clients return 403.

Assessment selections use returned facet IDs, such as `Critical`, `Certain`, `Unknown` and `NotApplicable`. These IDs use
the retained enum names; they differ from the lower-camel-case enum values in nested `riskAssessment`.
Null or absent assessment selections include every value; empty arrays select no observations. Assessment dimensions
use the same union-within-dimension and intersection-across-dimensions semantics as the other selections.
`view.evidence.riskRubricVersions` captures versions observed in the retained source population.
Risk restrictions, grouping and matrix axes make recall/F1 unavailable with
`miss-risk-assessment-unavailable` until independently assessed human concerns are available. Supported
overall precision remains available. Legacy risk-filtered whole-PR correctness returns
`sealed-risk-filter-unavailable` instead of reusing an unfiltered score.

Each response includes calculation version, capture time, database evidence revision, authorized display
facets, counts, all premise tuples, summaries, measurement windows and evidence limits. Projection timestamps
describe source freshness separately from capture time. Cumulative measurements retain earlier evidence
through the selected window; per-period measurements require activity in their own bucket. Scenario
`falseNegatives` is null when its compatible FN population is unavailable. Model-specific recall/F1 requires
complete compatible model opportunities and independent original human-concern evidence. Selected models
use union detection and canonical misses, without summing singleton misses or PR coverage. [Reviewer performance](../concepts/reviewer-performance.md)
defines these scores and their evidence requirements.

Calculation version `reviewer-performance-v2` captures the following populations and diagnostics:

| Field | Meaning |
|---|---|
| `point.score` | Covered counts and coherent scenarios using covered TP, FP and FN for recall and F1. Its precision describes that covered population. |
| `point.overallPrecision` | All selected raw counts and independent publication precision. `summary` is the precision range directly; compact scenarios contain precision and its inputs. |
| `coveragePolicy` | Advisory coverage percentage and covered source pull-request minimums, defaulting to 80 and 1. |
| `point.coverage` | Distinct candidate, covered and excluded source PR counts, percentage on a 0–100 scale, minimum/support flags, overlapping reason counts and exclusion IDs. |
| `view.exclusionCatalog` | Captured source identities, evidence reasons, attempt/completion/retry timestamps, last refresh reason, collection policy, recovery disposition, action, nullable source connection and URL. |
| `view.evidence.modelEvidenceSchema` | Optional `model-opportunity-v1` for measurements that loaded model execution/concern proof. |

Catalog IDs are zero-based and local to a view. A point's `exclusionIds` selects its exact membership;
the catalog can contain entries used only by other dates or intersections. Recovery dispositions are
`repair-required`, `retryable`, `collection-disabled`, `queued`, `in-progress`, `complete`, `ready`,
`cooldown` or `unreconstructable-history`. Optional `recoveryAction` is `recollect-source`,
`retry-model-evidence` or `none`; an absent field retains the saved legacy collection interpretation.
`sourceConnectionId`, `lastModelRetryAt` and `modelRetryNotBefore` capture the exact model source and local
classification retry policy. No command runs when opening a saved report.
For `queued`, `notBefore` records when the request becomes eligible for a sweep. For `in-progress`, it
records the observation lease expiry. `lastAttemptAt` records the observation start, and `lastCompletedAt`
records the latest successful completion. A retry timestamp does not schedule automatic work for a
repair-required source. Optional source URLs use validated captured
coordinates without provider requests. Successful open-thread judgments remain informational. Exclusion
reasons and advisory minimums do not suppress supported recall/F1. At least one covered source is
required; missing compatible model attribution still prevents those metrics. Empty denominators remain
unavailable. Saved v1 and v2 responses preserve their captured values and availability policy.

`POST /api/reviewer-performance/evidence/{aggregateId}/retry` has no body and queues an authorized retained
aggregate for a subsequent background sweep. It clears the old attempt token without changing evidence,
scores or retention timestamps. It requires tenant administration and the licensed capability.

| Status | Meaning |
|---|---|
| `202` | Queued; collection does not run in the request. |
| `401` / `403` | Authentication, tenant administration or the licensed capability is missing. |
| `404` | Aggregate absent or outside the administered client scope. |
| `409` | Collection disabled or an active lease; `ProblemDetails.detail` states the next action. |
| `503` | Database analytics services unavailable. |

`POST /api/reviewer-performance/evidence/{aggregateId}/retry-model` takes
`{"providerScope":"<captured namespace>","connectionId":"<captured connection guid>"}`. It retries bounded
retained classification for that authorized source only, under the discussion lease and captured cooldown.
It performs no provider reads or review execution and preserves original source cutoffs. `200` means the
bounded attempt completed; refresh to inspect its result. `404` protects missing or foreign aggregates.
`409` describes disabled collection, an active observation, cooldown, or unreconstructable retained proof;
`503` indicates unavailable analytics services. Authentication and capability requirements match collection
requeue. Wrong or ambiguous source identities cannot reset or replay another source's concerns.

Model proof loading permits 4,096 jobs, 100,000 checkpoints and concern/decision rows, and 32 MiB each for
execution proof and concern metadata. Before evaluating any series or matrix, model queries permit at
most 8,192 concern/model evaluations, 1,000,000 candidate references and 1,000,000 pass references. An
oversized evaluation returns `400` consistently across grouping and matrix traversal.

`POST /api/reviewer-performance/reports` takes `{"id":"<guid>","name":"<name>","query":{...}}` and
captures the complete server response. Reuse the same ID and identical body for an idempotent retry; a
different request with that ID returns 409. Report names permit 1–160 characters, and capture requires at
least one authorized client. `GET /api/reviewer-performance/reports` lists up to 100 unexpired reports.
`GET /api/reviewer-performance/reports/{id}` opens the stored response without live recalculation;
`DELETE` on that path removes the complete report. Every report operation requires access to all contained
clients, including idempotent capture retries. Missing, expired or unauthorized reports return 404.
Capture refuses payloads above 32 MiB without truncation. Stored v1 reports keep their original scores;
absent overall-precision or coverage fields are not reconstructed. Stored v2 reports retain their captured
coverage policy and exclusion catalog even after source repair or configuration changes.

The misses browse response includes `judgementFailed`. When true, its three judgement flags are
unavailable decisions, and the thread does not supply an eligible FN verdict.
`excludedAsOwnFinding` identifies a current match to a retained reviewer finding. Its original human
judgements remain present, but `countsAsMiss` is false.

## Usage statistics

These operations require platform administration. [Usage statistics](usage-statistics.md) describes the
daily snapshot fields. Paths start with `/api/admin/usage-statistics`.

| Operation | Input | Outcome |
|---|---|---|
| `GET /` | No body is required. | ProPR returns sending state, license constraints, last attempt and receiver advisories. |
| `PATCH /` | Supply `{"enabled": false}` to disable sending. | A commercial license prevents disabling and returns 409. |
| `GET /preview` | No body is required. | ProPR returns the next snapshot payload without sending it. |
| `POST /send` | No body is required. | ProPR runs a send cycle under the daily rules and returns `sent`, `disabled`, `awaitingConsent` or `notDue`. |
| `POST /notice/shown` | No body is required. | ProPR records that an administrator saw the consent notice; the operation is idempotent. |
| `POST /notice/dismiss` | No body is required. | ProPR hides the notice without changing sending policy. |

## Health endpoints behind the proxy

Through the bundled reverse proxy, the API is reached under `/api/` - that prefix is stripped before
the request arrives, so the API's own `/healthz` is `/api/healthz` from outside:

```bash
curl -k https://localhost:5443/api/healthz
```

`https://localhost:5443/healthz` without the prefix is answered by the frontend container's own static
health string and reports nothing about the API. What each check reports, and which endpoint to point a
probe at, is in [observability](../operate/observability.md).

The same prefix rule applies to `/metrics`, which becomes `/api/metrics` - a path to
[block at your edge](security.md#what-to-block-at-your-edge).

## More

For every other endpoint - prompt overrides, dismissal search, token reporting, ProCursor token usage,
tenant administration - read the OpenAPI specification.

In Development the API serves it at its own address: Swagger UI at `/swagger`, the document at
`/swagger/v1/swagger.json`. Behind a reverse proxy that strips a leading `/api`, as the bundled one does, those
become `/api/swagger` and `/api/swagger/v1/swagger.json`. Other environments serve neither, and the bundled stack
runs in Production, so a stock deployment has no Swagger UI and no served document at any path.
`https://localhost:5443/swagger` without the prefix is answered by the frontend container.

Script against `openapi.json` at the repository root instead. It is committed, and it covers every endpoint on
this page except `/healthz`, `/livez` and `/metrics`, which [observability](../operate/observability.md)
describes. Its paths carry no `/api` prefix: `/clients/{clientId}/ai-connections`, not
`/api/clients/{clientId}/ai-connections`. Through the bundled proxy, prepend `https://localhost:5443/api` as
every example above does. The public webhook receiver is forwarded at its own path.
