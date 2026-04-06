# Getting Started - ProPR (Admin UI focused)

ProPR is an ASP.NET Core backend that automates PR reviews by polling source control systems and
running AI-based reviews. This guide is for operators who want to deploy ProPR and configure it
through the Admin UI. For low-level API automation and scripted examples, use `docs/api.md`.

## What you need

| Requirement                                       | Version             |
|---------------------------------------------------|---------------------|
| [.NET SDK](https://dotnet.microsoft.com/download) | 10.0.103 or later   |
| Azure subscription                                | n/a                 |
| Azure OpenAI **or** Azure AI Foundry resource     | n/a                 |
| Azure DevOps organisation                         | n/a                 |
| PostgreSQL 17 (or Docker)                         | 17.x                |
| Docker (optional, for container runs)             | any recent version  |

> `global.json` pins `10.0.103` as the minimum SDK and allows any compatible `10.0.x` feature band.

## Quick start

1. Create a `.env` file at the repository root.

```env
MEISTER_BOOTSTRAP_ADMIN_USER=admin
MEISTER_BOOTSTRAP_ADMIN_PASSWORD=<strong-password-here>
MEISTER_JWT_SECRET=<random-string-at-least-32-chars>

AZURE_CLIENT_ID=<global-service-principal-appId>
AZURE_TENANT_ID=<azure-tenant-id>
AZURE_CLIENT_SECRET=<global-service-principal-secret>
```

2. Start the example stack.

```bash
docker compose --env-file .env -f example/docker-compose/docker-compose.yml up --build
```

3. Open the Admin UI at `https://localhost:5443/` and log in with the bootstrap admin credentials.

## Configure the system in the Admin UI

Use this order when setting up a new client.

1. Create users.
   - The first admin is seeded from the bootstrap env vars.
   - Add any additional operator users from the Admin UI.

2. Create a client.
   - Each client owns its own AI connections, ADO scopes, sources, and crawl configuration.

3. Configure Azure DevOps credentials.
   - Add a per-client service principal if the client should not use the global backend identity.

4. Configure the reviewer identity.
   - Resolve the Azure DevOps identity that should own reviews and save it on the client.

5. Register Azure DevOps organization scopes.
   - Add the organizations this client is allowed to access.

6. Configure ProCursor sources.
   - Use guided discovery to pick repositories or wikis, then create sources from the selected scope.

7. Configure crawl jobs.
   - Create crawl configurations and choose whether to use all client sources or only selected sources.

8. Configure AI connections.
   - Add the client's AI endpoint and activate the model it should use.

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

## Observability

- Health endpoint: `GET /healthz`
- Swagger UI in Development: `https://localhost:5443/swagger`
- Optional tracing: `OTLP_ENDPOINT`

## Related docs

- `docs/api.md` for admin API examples and scripting
