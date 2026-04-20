# Webhooks — User Guide

This page explains how to configure and verify Azure DevOps webhooks for ProPR from a user/administrator perspective.

Overview


Webhooks let ProPR receive pull-request events from Azure DevOps (PR created/updated/commented) so the service can queue AI review jobs automatically. A webhook consists of two parts:

- A **webhook configuration** in ProPR (Admin UI) which defines the organization/project/repository scope, enabled events, and exposes a one-time secret and listener URL.  
- A **service hook** in Azure DevOps that calls the ProPR listener URL when events occur.

Quick checklist (what must be true)


- ProPR instance is reachable by Azure DevOps (public URL / NAT / firewall); HTTPS is recommended.  
- You have Admin access to ProPR's Admin UI to create webhook configurations.  
- You can create a service hook in the target Azure DevOps project.  
- You have (or can obtain) appropriate ADO credentials for any downstream ADO API operations ProPR will perform (these are configured per-client in the Admin UI).  
- Database and background workers for ProPR are running (reviews are queued and processed asynchronously).

Create the webhook configuration (ProPR Admin UI)


1. Sign in to the ProPR Admin UI and open **Admin → Webhook Configurations**.  
2. Click **Create webhook configuration**.  
3. Select the organization (e.g. `https://dev.azure.com/my-org`).  
4. Choose a project (the Admin UI may show a project name or let you pick by discovery). Prefer using the **project name** (not a GUID) when you intend to reference repositories by name.  
5. Add repository filters (use the guided discovery to pick the repository). When available, choose the *canonical repository* entry — this stores the canonical repository identifier (GUID) and avoids name-vs-id mismatches.  
6. Select which events to enable (Pull Request Created, Updated, Commented).  
7. Create the configuration. The UI returns a **listener URL** and a one-time **generated secret** — copy the generated secret now (it is shown only once).  

Register the service hook in Azure DevOps

1. In Azure DevOps, open the target project and go to **Project settings → Service hooks**.  
2. Create a new subscription and choose **Web Hooks**.  
3. Select the trigger(s) you configured in ProPR (pull request created/updated/commented).  
4. For the endpoint URL use the `listenerUrl` from ProPR (the `/webhooks/v1/providers/ado/{pathKey}` URL).  
5. Under security choose **Basic** authentication. Any username is fine; paste the ProPR **generated secret** as the password.  
6. Test the subscription (Azure DevOps provides a test option) and save.

Testing locally (synthetic events)


If you want to simulate events without using Azure DevOps you can use the helper scripts included in the repository: `scripts/send-ado-webhook.sh` and `scripts/send-ado-webhook.ps1` for Azure DevOps-shaped deliveries, `scripts/send-github-webhook.sh` and `scripts/send-github-webhook.ps1` for GitHub deliveries, `scripts/send-codeberg-webhook.sh` and `scripts/send-codeberg-webhook.ps1` for Forgejo/Codeberg deliveries, or `scripts/send-gitlab-webhook.sh` and `scripts/send-gitlab-webhook.ps1` for GitLab merge request deliveries.

Example (send the common events for PR #24 on a repo identified by its canonical GUID):

```powershell
pwsh scripts/send-ado-webhook.ps1 `
  -u "http://your-propr-host/webhooks/v1/providers/ado/<pathKey>" `
  -s "<generated-secret>" `
  -r "<repository-guid-or-name>" `
  -i 24
```

Equivalent Bash usage:

```bash
bash scripts/send-ado-webhook.sh \
  -u "http://your-propr-host/webhooks/v1/providers/ado/<pathKey>" \
  -s "<generated-secret>" \
  -r "<repository-guid-or-name>" \
  -i 24
```

Example GitLab delivery:

```powershell
pwsh scripts/send-gitlab-webhook.ps1 `
  -u "http://your-propr-host/webhooks/v1/providers/gitlab/<pathKey>" `
  -s "<generated-secret>" `
  -p 101 `
  -P "acme/platform/propr" `
  -i 24
```

Equivalent Bash usage:

```bash
bash scripts/send-gitlab-webhook.sh \
  -u "http://your-propr-host/webhooks/v1/providers/gitlab/<pathKey>" \
  -s "<generated-secret>" \
  -p 101 \
  -P "acme/platform/propr" \
  -i 24
```

Example GitHub delivery:

```powershell
pwsh scripts/send-github-webhook.ps1 `
  -u "http://your-propr-host/webhooks/v1/providers/github/<pathKey>" `
  -s "<generated-secret>" `
  -r "101" `
  -O "acme" `
  -i 24
```

Equivalent Bash usage:

```bash
bash scripts/send-github-webhook.sh \
  -u "http://your-propr-host/webhooks/v1/providers/github/<pathKey>" \
  -s "<generated-secret>" \
  -r "101" \
  -O "acme" \
  -i 24
```

Example Codeberg/Forgejo delivery:

```powershell
pwsh scripts/send-codeberg-webhook.ps1 `
  -u "http://your-propr-host/webhooks/v1/providers/forgejo/<pathKey>" `
  -s "<generated-secret>" `
  -r "101" `
  -O "acme" `
  -i 24
```

Equivalent Bash usage:

```bash
bash scripts/send-codeberg-webhook.sh \
  -u "http://your-propr-host/webhooks/v1/providers/forgejo/<pathKey>" \
  -s "<generated-secret>" \
  -r "101" \
  -O "acme" \
  -i 24
```

Notes about repository & project identifiers


- Azure DevOps payloads include `resource.repository.id`. This is usually a repository GUID. When ProPR is configured to reference a repository by name, some ADO APIs require a *project name* rather than a project GUID. If you see an error like:

  > "A project name is required in order to reference a Git repository by name"

  it means the webhook delivery contained a repository *name* while ProPR's stored configuration used a *project GUID*. To avoid this, either:

  - Configure the webhook/repo filter using the canonical repository reference (GUID) provided by the Admin UI (recommended), or
  - Ensure the webhook configuration uses the **project name** instead of a GUID when the repository is referenced by name.

Troubleshooting (common issues & fixes)


- Authorization failed / missing: verify the Azure DevOps service hook is configured to use Basic auth and that the password equals the ProPR generated secret, or verify the GitLab webhook is using the Secret token field so GitLab sends `X-Gitlab-Token`. You can re-generate a secret in the Admin UI and update the webhook if needed.  
- Listener unreachable: ensure your ProPR instance is reachable from Azure DevOps (public URL, correct port, HTTPS).  
- "A project name is required…": see the section above about repo vs project identifiers. Use canonical repository GUIDs when possible.  
- No review jobs queued: confirm ProPR has a working database and the background workers are running (the Admin UI and `/healthz` endpoint help verify).  
- Check deliveries: open **Admin → Webhook Configurations → {your config} → Deliveries** to see recent webhook deliveries, outcomes, and failure reasons.

If you need help


- Capture a delivery entry from **Admin → Webhook Configurations → Deliveries** and include the `failureReason` and `actionSummaries` when asking for assistance.  
- For auth issues capture the service hook test response in Azure DevOps and verify the timestamped delivery in the ProPR UI.

This guide is intended for administrators setting up Azure DevOps webhooks for ProPR. For API-level automation examples (curl, scripts for automation), see the developer-oriented `docs/api.md`.
