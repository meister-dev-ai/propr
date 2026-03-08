# meister-propr Development Guidelines

Auto-generated from all feature plans. Last updated: 2026-03-03

## Active Technologies

- C# / .NET 10, TFM `net10.0` + ASP.NET Core MVC, Microsoft Agent Framework (001-mvp-backend)

## Project Structure

```text
src/
├── MeisterProPR.Domain/          # Entities, value objects — zero NuGet deps
├── MeisterProPR.Application/     # Use cases, service interfaces, DTOs
├── MeisterProPR.Infrastructure/  # ADO client, AI client, in-memory repos
└── MeisterProPR.Api/             # Controllers, middleware, workers, Program.cs
tests/
├── MeisterProPR.Domain.Tests/
├── MeisterProPR.Application.Tests/
├── MeisterProPR.Infrastructure.Tests/
└── MeisterProPR.Api.Tests/
```

Dependency rule: Api → Application → Domain ← Infrastructure (Infrastructure MUST NOT depend on Api)

## Commands

```bash
dotnet run --project src/MeisterProPR.Api        # Run locally
dotnet watch --project src/MeisterProPR.Api      # Hot-reload
dotnet test                                       # Run all tests
docker compose up                                 # Production-like container stack
```

## Code Style

- xUnit + NSubstitute for all tests (Moq is prohibited)
- Source-generated `[LoggerMessage]` for structured log statements
- XML doc comments on all public controller actions (`<summary>`, `<param>`, `<response>`)
- Serilog for logging; destructuring policies MUST scrub `X-Client-Key`, `X-Ado-Token`, `AZURE_CLIENT_SECRET`

## Recent Changes

- 001-mvp-backend: Added C# / .NET 10, TFM `net10.0` + ASP.NET Core MVC, Microsoft Agent Framework

<!-- MANUAL ADDITIONS START -->

## Speckit Workflow

Every feature follows this mandatory loop:

1. `/speckit.specify` → write spec.md
2. `/speckit.clarify` → resolve open questions
3. `/speckit.plan` → generate research.md, data-model.md, contracts/, quickstart.md, plan.md
4. `/speckit.tasks` → generate tasks.md with [TEST] tasks first
5. `/speckit.implement` → Red → Green → Refactor per task

`tasks.md` ALWAYS leads with `[TEST]` tasks confirmed failing before implementation begins.

## Constitution (7 Principles — consult before every PR)

See `.specify/memory/constitution.md` for full text. Summary:

1. **API-Contract-First**: `openapi.json` at repo root is the source of truth; Swashbuckle generates it from XML docs;
   commit on every endpoint change
2. **Test-First**: TDD mandatory; xUnit + NSubstitute; `WebApplicationFactory` for integration tests
3. **Container-First**: Linux rootless container; env-var-only config; no Windows APIs
4. **Clean Architecture**: Domain ← Application ← Infrastructure, Api → Application (Infrastructure refs only in
   Program.cs)
5. **Security**: `X-Client-Key` middleware before all controllers; `X-Ado-Token` only for identity verification, never
   logged
6. **Job Reliability**: `IJobRepository` (in-memory ConcurrentDictionary); Pending → Processing → Completed|Failed;
   idempotency on PR iteration
7. **Observability**: Serilog JSON, OpenTelemetry OTLP, ActivitySource spans on ADO/AI calls, /healthz, Prometheus
   metrics

## Key Environment Variables

| Variable              | Purpose                                             |
|-----------------------|-----------------------------------------------------|
| `MEISTER_CLIENT_KEYS` | Comma-separated valid client keys                   |
| `AI_ENDPOINT`         | Azure OpenAI endpoint URL                           |
| `AI_DEPLOYMENT`       | Model deployment name (e.g. `gpt-4o`)               |
| `AZURE_CLIENT_ID`     | Service principal client ID (local dev)             |
| `AZURE_TENANT_ID`     | Azure tenant ID (local dev)                         |
| `AZURE_CLIENT_SECRET` | Service principal secret (local dev — never commit) |

<!-- MANUAL ADDITIONS END -->
