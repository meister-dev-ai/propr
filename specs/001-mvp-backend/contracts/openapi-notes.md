# API Contract Notes: MVP Backend

**Branch**: `001-mvp-backend` | **Phase**: 1 — Design | **Date**: 2026-03-03

## Source of Truth

The HTTP contract is defined in **`openapi.json`** at the repository root.
Per Constitution Principle I, Swashbuckle generates this file from C# controller
XML doc comments. It is committed and kept current so the ADO extension can
regenerate its TypeScript client via `npm run generate:api`.

The `openapi.json` already present in the repo is the MVP contract. No
breaking changes are introduced by this feature (it IS the initial implementation).

---

## Required Corrections Before Implementation

### 1. `X-Ado-Token` Description (All Three Endpoints)

**Current** (incorrect — appears in `POST /reviews`, `GET /reviews`, `GET /reviews/{jobId}`):

```json
"description": "ADO access token used by the backend to fetch PR data and post review comments."
```

**Required** (per FR-015):

```json
"description": "ADO personal access token or OAuth token used solely to verify that the requesting user is an authenticated member of the configured ADO organisation. The backend uses its own managed identity for all ADO API operations (file fetching, comment posting). This token is never forwarded, stored, cached beyond the request lifetime, or included in any response."
```

This is a **non-breaking** description-only change. No TypeScript client regeneration required. Must be updated in all
three path operations.

---

## Endpoints Summary

| Method | Path               | Auth                           | Response                       | Purpose                           |
|--------|--------------------|--------------------------------|--------------------------------|-----------------------------------|
| `POST` | `/reviews`         | `X-Client-Key` + `X-Ado-Token` | `202` + `ReviewJob`            | Submit PR for review (FR-001)     |
| `GET`  | `/reviews`         | `X-Client-Key` + `X-Ado-Token` | `200` + `ReviewListItem[]`     | List all jobs for client (FR-009) |
| `GET`  | `/reviews/{jobId}` | `X-Client-Key` + `X-Ado-Token` | `200` + `ReviewStatusResponse` | Get job status/result (FR-008)    |

`/healthz` is **not** in the OpenAPI contract — it is an infrastructure endpoint
served by `app.MapHealthChecks("/healthz")` (ASP.NET Core Health Checks), not a
controller action. This is intentional and consistent with standard practice.

---

## Security Scheme

```json
"clientKey": {
  "type": "apiKey",
  "in": "header",
  "name": "X-Client-Key"
}
```

All three endpoints require `"security": [{ "clientKey": [] }]`. The middleware
validates `X-Client-Key` via `IClientRegistry` before any controller code executes.

---

## Status Enum Serialisation

`ReviewJobStatus` values are serialised as lowercase strings:
`"pending"`, `"processing"`, `"completed"`, `"failed"`.

Configure `JsonStringEnumConverter` with `JsonNamingPolicy.CamelCase` in the
ASP.NET Core JSON options to keep the enum names consistent with the OpenAPI schema.

---

## Swashbuckle XML Doc Requirements

Every controller action and its parameters must carry complete XML documentation.
Minimal required tags per action:

```csharp
/// <summary>Brief description.</summary>
/// <param name="request">Parameter description.</param>
/// <response code="202">Description of 202 response.</response>
/// <response code="401">Invalid or missing client key.</response>
```

Generate XML docs by adding to each project that contains controllers:

```xml
<GenerateDocumentationFile>true</GenerateDocumentationFile>
```

---

## `openapi.json` Regeneration Protocol

After any endpoint change (new field, renamed path, status code added/removed):

1. Run `dotnet build src/MeisterProPR.Api`
2. The `openapi.json` is regenerated at the repository root (configure
   Swashbuckle CLI or `SwaggerEndpointOptions` to write to root)
3. Commit the updated `openapi.json` in the same PR as the code change
4. Breaking changes require a coordinated version bump and extension update
