# Getting Started - ProPR (Admin UI focused)

ProPR is an ASP.NET Core backend that automates PR reviews by polling source control systems and
running AI-based reviews. This guide is for operators who want to deploy ProPR and configure it
through the Admin UI. For low-level API automation and scripted examples, use `docs/api.md`.

## What you need

| Requirement                                       | Version             |
|---------------------------------------------------|---------------------|
| [.NET SDK](https://dotnet.microsoft.com/download) | 10.0.103 or later   |
| Azure subscription for Azure-hosted AI            | optional            |
| Azure OpenAI **or** Azure AI Foundry resource     | n/a                 |
| At least one supported SCM provider account       | n/a                 |
| PostgreSQL 17 (or Docker)                         | 17.x                |
| Docker (optional, for container runs)             | any recent version  |

> `global.json` pins `10.0.103` as the minimum SDK and allows any compatible `10.0.x` feature band.

## Quick start

1. Create a `.env` file at the repository root.

```env
MEISTER_BOOTSTRAP_ADMIN_USER=admin
MEISTER_BOOTSTRAP_ADMIN_PASSWORD=<strong-password-here>
MEISTER_JWT_SECRET=<random-string-at-least-32-chars>
PROCURSOR_SHARED_KEY=<random-string-at-least-32-chars>
MEISTER_PUBLIC_BASE_URL=https://localhost:5443/api

AZURE_CLIENT_ID=<global-service-principal-appId>
AZURE_TENANT_ID=<azure-tenant-id>
AZURE_CLIENT_SECRET=<global-service-principal-secret>
```

2. Start the example stack.

```bash
docker compose --env-file .env -f example/docker-compose/docker-compose.yml up --build
```

3. Open the Admin UI at `https://localhost:5443/` and log in with the bootstrap admin credentials.

For the bundled local nginx reverse proxy, keep `MEISTER_PUBLIC_BASE_URL=https://localhost:5443/api` so provider setup screens and live tenant SSO redirects use the externally reachable API origin instead of the internal container host.

The compose example runs ProPR and ProCursor as separate services. `meisterpropr` is the only
public control plane. `procursor` is internal to the compose network and authenticates both
directions with `PROCURSOR_SHARED_KEY`.

## Configure the system in the Admin UI

Use this order when setting up a new client.

1. Create users.
   - The first admin is seeded from the bootstrap env vars.
   - Add any additional operator users from the Admin UI.

2. Create a client.
   - Each client owns its own SCM provider connections, scopes, reviewer-trigger identity, AI connections, ProCursor sources, and crawl or webhook configuration.

3. Add one or more provider connections.
   - Configure Azure DevOps, GitHub, GitLab, or Forgejo access from the Providers UI.
   - For Azure DevOps, add a provider connection that uses `oauthClientCredentials`.
   - For GitHub, choose either PAT or GitHub App installation authentication.

4. Add provider scopes.
   - Provider scopes define which organizations or host-level areas the client is allowed to use.
   - For Azure DevOps, add the organization scopes that guided discovery and automation may use.

5. Configure the reviewer identity when needed.
   - Reviewer identity is an optional trigger/filter for automatic PR selection.
   - Provider writes still use the authenticated provider connection identity.

6. Configure ProCursor sources.
   - Use guided discovery to pick repositories or wikis, then create sources from the selected scope.

7. Configure crawl jobs.
   - Create crawl configurations and choose whether to use all client sources or only selected sources.
   - Optionally set a review temperature between `0.0` and `2.0` when you want this automation path to run more deterministically or more creatively than the model default.

8. Configure AI connections.
   - Add the client's AI endpoint and activate the model it should use.
   - Add an optional `proRvPrefilter` purpose binding when you want ProRV to use a dedicated model instead of falling back to the main review runtime.

Webhook configurations expose the same optional review temperature field. Use it when webhook-triggered reviews should run with a different temperature than crawl-triggered reviews for the same client.

## Running locally

If you want to run the API directly from source during development:

```bash
git clone <repo-url>
cd meister-propr

dotnet user-secrets set "DB_CONNECTION_STRING" "Host=localhost;Database=meisterpropr;Username=postgres;Password=devpassword" --project src/MeisterProPR.Api
dotnet user-secrets set "MEISTER_JWT_SECRET" "dev-jwt-secret-at-least-32-chars-ok!!" --project src/MeisterProPR.Api
dotnet user-secrets set "MEISTER_BOOTSTRAP_ADMIN_USER" "admin" --project src/MeisterProPR.Api
dotnet user-secrets set "MEISTER_BOOTSTRAP_ADMIN_PASSWORD" "AdminPass1!" --project src/MeisterProPR.Api

ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/MeisterProPR.Api
```

If you want the extracted local topology from source, use:

```bash
./scripts/run-local.sh "Host=localhost;Database=meisterpropr;Username=postgres;Password=devpassword"
```

That script:

1. Generates one per-run shared key unless `PROCURSOR_SHARED_KEY` is already set.
2. Starts `MeisterProPR.Api` with `PROCURSOR_REMOTE_MODE=proprManagedRemote` and `PROCURSOR_SERVICE_BASE_URL` pointing at the local ProCursor host.
3. Starts `MeisterProPR.ProCursor.Service` with `PROCURSOR_PROPR_BASE_URL` pointing back at the local API host.
4. Sets `PROCURSOR_DB_CONNECTION_STRING` for the ProCursor host only, defaulting it to the same value as `DB_CONNECTION_STRING` unless you override it explicitly.
5. Reuses one local `MEISTER_DATA_PROTECTION_KEYS_PATH` directory for both services by default so protected local secrets stay readable across the split runtime. Set `RUN_LOCAL_KEYS_DIR` if you intentionally want an isolated key ring; a shared key ring is not required by the architecture.
6. Waits for `http://localhost:8080/healthz` and `http://localhost:8081/healthz` before starting the admin UI.

If you intentionally deploy ProPR without ProCursor, set `PROCURSOR_REMOTE_MODE=disabled` and leave the
other ProCursor remote settings unset. In that mode ProPR omits ProCursor review tools instead of
surfacing a broken dependency, and `/healthz` does not include the `procursor-remote` dependency check.

## Review runtime notes

- ProPR supports provider-neutral review execution across Azure DevOps, GitHub, GitLab, and Forgejo-family hosts.
- Azure DevOps remains the guided-discovery provider for projects, branches, crawl filters, and ProCursor source selection.
- ProRV is an optional review-knowledge prefilter that runs before file review when its AI purpose is configured. Without a dedicated `proRvPrefilter` binding, ProPR falls back to the active file-review runtime.

For release-based deployments, keep the three runtime image tags aligned:

1. `ghcr.io/meister-dev-ai/propr:<tag>`
2. `ghcr.io/meister-dev-ai/propr/procursor:<tag>`
3. `ghcr.io/meister-dev-ai/propr/admin-ui:<tag>`

Use the same `<tag>` for all three services. Stable releases also publish `latest` for all three
images; pre-release tags do not move `latest`.

If local development goes through a reverse proxy or tunnel, set `MEISTER_PUBLIC_BASE_URL` to that public API base URL. When you run the API directly without a proxy, the callback URL can fall back to the request host.

If you front the Vite dev server with an extra local reverse proxy hostname, add that hostname only in `admin-ui/.env.local` so it stays machine-local:

```env
VITE_DEV_ALLOWED_HOSTS=my-domain.tld
```

Use a comma-separated list if you need multiple local proxy hostnames. For tenant SSO through that hostname, also point the API's `MEISTER_PUBLIC_BASE_URL` at the same public proxy base, for example `https://propr-local.arahome.lan/api`.

## Observability

- Health endpoint: `GET /healthz`
- In extracted mode, ProPR `/healthz` includes a `procursor-remote` dependency entry and returns `503` if the remote service is unhealthy.
- The ProCursor host exposes its own `/healthz` endpoint for worker and service readiness.
- Swagger UI in Development: `https://localhost:5443/swagger`
- Optional tracing: `OTLP_ENDPOINT`

## Related docs

- `docs/api.md` for admin API examples and scripting
- `docs/provider-connections.md` for the supported provider auth modes, required fields, and where to obtain them
