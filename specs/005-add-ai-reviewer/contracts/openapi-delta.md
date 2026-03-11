# OpenAPI Contract Delta: 005-add-ai-reviewer

**Date**: 2026-03-11
**Revision**: 2 — supersedes previous openapi-delta.md; feature now has API changes.

---

## Summary of Changes

| Change | Type | Endpoint |
|--------|------|----------|
| Add `reviewerId` to `ClientResponse` | Non-breaking addition | `GET /clients`, `GET /clients/{id}`, `PATCH /clients/{id}` |
| Add `PUT /clients/{id}/reviewer-identity` | Non-breaking addition | new endpoint |
| Remove `reviewerId` from `CrawlConfigResponse` | **BREAKING** removal | `GET /clients/{id}/crawl-configs`, `POST /clients/{id}/crawl-configs` |
| Remove `reviewerDisplayName` from `CreateCrawlConfigRequest` | **BREAKING** removal | `POST /clients/{id}/crawl-configs` |

---

## New Endpoint

### `PUT /clients/{clientId}/reviewer-identity`

Sets or replaces the ADO reviewer identity for a client. Requires `X-Admin-Key`.

```
PUT /clients/{clientId}/reviewer-identity
X-Admin-Key: <key>
Content-Type: application/json

{
  "reviewerId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
```

**Responses**:
- `204 No Content` — reviewer identity stored
- `400 Bad Request` — `reviewerId` is empty or missing
- `401 Unauthorized` — missing or invalid `X-Admin-Key`
- `404 Not Found` — client not found

---

## Modified Response: `ClientResponse`

Field added: `reviewerId` (`string | null`, UUID format)

```jsonc
// Before
{
  "id": "...",
  "displayName": "...",
  "isActive": true,
  "createdAt": "...",
  "hasAdoCredentials": true,
  "adoTenantId": "...",
  "adoClientId": "..."
}

// After
{
  "id": "...",
  "displayName": "...",
  "isActive": true,
  "createdAt": "...",
  "hasAdoCredentials": true,
  "adoTenantId": "...",
  "adoClientId": "...",
  "reviewerId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"  // ← NEW (nullable)
}
```

---

## BREAKING: `CrawlConfigResponse` — Remove `reviewerId`

```jsonc
// Before
{
  "id": "...",
  "clientId": "...",
  "organizationUrl": "...",
  "projectId": "...",
  "reviewerId": "...",    // ← REMOVED
  "crawlIntervalSeconds": 60,
  "isActive": true,
  "createdAt": "..."
}

// After
{
  "id": "...",
  "clientId": "...",
  "organizationUrl": "...",
  "projectId": "...",
  "crawlIntervalSeconds": 60,
  "isActive": true,
  "createdAt": "..."
}
```

**Migration path for consumers**: Read `reviewerId` from `GET /clients/{id}` instead.

---

## BREAKING: `CreateCrawlConfigRequest` — Remove `reviewerDisplayName`

```jsonc
// Before
{
  "organizationUrl": "https://dev.azure.com/myorg",
  "projectId": "my-project",
  "reviewerDisplayName": "AI Reviewer Bot",  // ← REMOVED
  "crawlIntervalSeconds": 60
}

// After
{
  "organizationUrl": "https://dev.azure.com/myorg",
  "projectId": "my-project",
  "crawlIntervalSeconds": 60
}
```

**Migration path for consumers**: Set the reviewer identity once per client via
`PUT /clients/{id}/reviewer-identity` before creating crawl configurations.

---

## `openapi.json` Update Required

`openapi.json` at the repository root MUST be regenerated and committed as part of this
feature. The breaking changes require coordinating with any extension or client consuming
the crawl config response.

**Constitution I (API-Contract-First) status**: ✅ Breaking changes acknowledged and
documented. `openapi.json` commit required before merge.
