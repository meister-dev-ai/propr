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
page contains fewer than `limit` rows. Each row includes the provider thread ID, severity, classified
core tags, disposition, and nullable `rejectionReason`. An absent rejection reason represents an unknown
or inapplicable reason.

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

`label` is required; `expiresAt` is optional. The plaintext `token` is returned once and cannot be
retrieved again. Send it in place of the bearer header:

```bash
curl -k https://localhost:5443/api/clients \
  -H "X-User-Pat: <token>"
```

List tokens with `GET /api/users/me/pats` and revoke one with `DELETE /api/users/me/pats/<pat-id>`. What
a PAT can do, and how it is stored, is in [automation credentials](security.md#automation-credentials).

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
| Coverage | Each load covers at most 8 sources, each source at most 100 pull requests, and each generation at most 100 sources. Partial coverage and remaining sources are explicit; observed rows are not a complete inventory. |
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

### Purpose bindings

| Field | Contract |
|---|---|
| `purpose` | Each [AI purpose](../ai/purposes.md#ai-purposes) can occur once. |
| `remoteModelId` or `configuredModelId` | The binding selects a configured model on this profile. |
| `protocolMode`, `isEnabled` | Defaults are `Auto` and true. Embedding purposes require an embedding-capable model and `Auto` or `Embeddings`; other purposes require chat capability. |

Logical-model selection takes precedence over profile bindings.

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
      "qualifiers": null
    }
  ],
  "breakdown": { "rows": "type", "columns": "qualifier", "date": "2026-09-30", "viewIndex": 0 }
}
```

`bucket` accepts `day`, `week` or `month`; `aggregation` accepts `cumulative` or `period`. Grouping accepts
`none`, `client`, `repository`, `model`, `type` or `qualifier`. Matrix axes use two distinct dimensions from
that list, excluding `none`. Matrix date selects a bucket in its zero-based `viewIndex`; the response
displays the clipped measurement window.

Null selections include all authorized values; empty arrays include none. Use returned facet IDs for
repository and model selections because they include retained identity scope. The query permits 1–366 days
per view, years 1900–9998, at most 128 selections per dimension, 100,000 grouped joint cells per view, 24
series, 2,000 timeline points and 12 values per matrix axis. Exceeded limits return 400 without truncating
counts. Unauthorized selected clients return 403.

Each response includes calculation version, capture time, database evidence revision, authorized display
facets, counts, all premise tuples, summaries, measurement windows and evidence limits. Projection timestamps
describe source freshness separately from capture time. Cumulative measurements retain earlier evidence
through the selected window; per-period measurements require activity in their own bucket. Scenario
`falseNegatives` is null when its compatible FN population is unavailable; recorded raw counts remain in
`counts`. Model-specific recall/F1 remains unavailable until
compatible human-miss model membership exists. [Reviewer performance](../concepts/reviewer-performance.md)
defines these scores and their evidence requirements.

`POST /api/reviewer-performance/reports` takes `{"id":"<guid>","name":"<name>","query":{...}}` and
captures the complete server response. Reuse the same ID and identical body for an idempotent retry; a
different request with that ID returns 409. Report names permit 1–160 characters, and capture requires at
least one authorized client. `GET /api/reviewer-performance/reports` lists up to 100 unexpired reports.
`GET /api/reviewer-performance/reports/{id}` opens the stored response without live recalculation;
`DELETE` on that path removes the complete report. Every report operation requires access to all contained
clients, including idempotent capture retries. Missing, expired or unauthorized reports return 404.

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
