# Quickstart: Add AI Identity as Optional Reviewer

**Feature**: 005-add-ai-reviewer
**Date**: 2026-03-11
**Revision**: 2

---

## What Changes

1. **New `reviewer_id` on client** — each client now stores the ADO identity GUID of the
   AI service account. Review jobs fail immediately if this is not set.
2. **Reviewer added before comments** — when a review job runs, the AI identity is added
   as an optional reviewer on the PR before any comments are posted.
3. **Reviewer ID removed from crawl configurations** — set it once on the client instead.

---

## Migration Steps (Existing Deployments)

### 1. Run the database migration

```bash
dotnet ef database update --project src/MeisterProPR.Infrastructure \
  --startup-project src/MeisterProPR.Api
```

This adds `reviewer_id` (nullable) to `clients` and drops `reviewer_id` from
`crawl_configurations`.

### 2. Set the reviewer identity for each client

For each existing client, call the new endpoint with the ADO identity GUID of the AI
service account (the same GUID that was previously on the crawl configuration):

```bash
curl -X PUT https://<host>/clients/<clientId>/reviewer-identity \
  -H "X-Admin-Key: <admin-key>" \
  -H "Content-Type: application/json" \
  -d '{"reviewerId": "<guid-from-old-crawl-config>"}'
```

Until this step is done, review jobs for the client will fail with:
`"Reviewer identity not configured for client"`.

---

## Verifying the Feature

### Set reviewer identity on a client

```bash
curl -X PUT https://<host>/clients/<clientId>/reviewer-identity \
  -H "X-Admin-Key: <admin-key>" \
  -d '{"reviewerId": "<ado-identity-guid>"}'
# Expected: 204 No Content
```

### Verify it is stored

```bash
curl https://<host>/clients/<clientId> \
  -H "X-Admin-Key: <admin-key>"
# Response should include "reviewerId": "<guid>"
```

### Submit a review job and check the PR

1. `POST /jobs` to submit a review job
2. Poll `GET /jobs/{jobId}` until `status == "completed"`
3. Open the PR in Azure DevOps — the AI service account should appear as an optional reviewer

### Verify job rejection when reviewer not configured

```bash
curl -X PUT https://<host>/clients/<clientId>/reviewer-identity \
  -H "X-Admin-Key: <admin-key>" \
  -d '{"reviewerId": "00000000-0000-0000-0000-000000000000"}'
# Then submit a job — job should fail with missing-config message
```

---

## Failure Scenario

If the AI identity does not have permission to add reviewers in ADO:

1. Submit a review job
2. Job transitions to `failed`
3. `GET /jobs/{jobId}` returns the ADO error in `errorMessage`
4. No review comments appear on the PR

**Resolution**: Grant the service principal "Contribute to pull requests" in
Azure DevOps → Project Settings → Repos → Security → `[service account]`.

---

## Local Development

```bash
dotnet run --project src/MeisterProPR.Api
dotnet test
```

No new environment variables needed.
