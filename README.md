<p align="center">
  <img src="resources/images/logo.png" alt="ProPR" width="180">
  <br>
  <em>AI-powered code review across Azure DevOps, GitHub, GitLab, and Forgejo-family providers</em>
</p>

<p align="center">
  <a href="https://github.com/meister-dev-ai/propr/actions/workflows/ci.yml"><img src="https://github.com/meister-dev-ai/propr/actions/workflows/ci.yml/badge.svg?branch=main" alt="CI"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-ELv2-blue.svg" alt="License: ELv2"></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet" alt=".NET 10">
</p>

---

Meister DEV's ProPR automates your pull and merge request reviews, ensuring high code quality and security standards without slowing down your team.

---

## Features

### Core Capabilities

- **AI code reviews across supported SCM providers** - The reviewer can inspect changed files in Azure DevOps, GitHub, GitLab, and Forgejo-family reviews, comment on specific lines, and provide an overall summary of the findings
- **Per-file agentic review** — each changed file gets its own AI pass with tool-calling for cross-file investigation
- **Automatic crawling** — background worker polls for PRs assigned to a configured reviewer
- **Beautiful UI** - The UI is tailored towards efficient triage of review comments, with a summary dashboard, file tree sidebar, and token consumption aggregates
- **Mixed-provider operations** — provider connections expose authoritative readiness separate from onboarding verification, plus connection-scoped status, verification history, webhook delivery logs, and categorized failures in the admin UI

### BYOAI

- Supported AI-Connections:
    - **Microsoft Foundry Models** — connect to Foundry-hosted models with your own API key; supports all Foundry-hosted models that support the responses API.
    - More AI connection types coming soon — feel free to contribute or contact us if you want to see a specific provider supported!

### Review Optimizations

- **Token-optimized reviews** — diff-only input (full file available on demand via tool call), cache-friendly parallel message structure, system prompt pruned from step 2+ of review loops, tool result excerpts capped at 1 000 chars in deep loops
- **Intelligent review summary** - The reviewer generates a concise summary of the review findings, which is posted as a comment and displayed in the admin UI
- **Per-client and per Crawl Config prompt overrides** - Override predefined prompts to improve the AI output towards specific use cases

### Privacy

- **Per-user authentication** — username/password login with 15-minute JWT + 7-day refresh tokens; no shared secrets
- **Personal access tokens** — users can issue scoped PATs for CI pipelines (`mpr_…` prefix) to submit reviews without crawling
- **Per-client dismissals** - Dismiss specific findings for a client, preventing them from appearing in future reviews

### Data sovereignty

- **Per-client provider credentials** — each client can isolate Azure DevOps service principals and protected GitHub, GitLab, or Forgejo-family connection secrets at the connection level
- **Job persistence + recovery mechanism** — review jobs survive restarts; stuck processing jobs auto-recovered

### Traceability

- **File exclusion rules** — generated files (EF Core migrations etc.) are skipped automatically; per-repo custom patterns via `.meister-propr/exclude` on the target branch using gitignore-style globs; excluded files are recorded in the audit trail with zero token cost
- **Review history** - all reviews, protocols, comments, calls to AI, tools and more are store and can be checked from the management interface
- **Token usage dashboard** — per-client AI token consumption tracked by model and date

### Knowledge

- **Contextual tool calls** — the reviewer can call tools to retrieve additional context about the PR, such as fetching the full file content, listing changed files, other PR data and ProCursor
- **ProCursor** - Knowledge indexing with source freshness, safe event logs, and symbol-aware retrieval for deep context beyond the diff; ProCursor is used by the AI reviewer to make better assessments and suggestions, and is also available as a standalone API for your own custom tools and agents

---

## Limitations

- **Provider coverage is intentionally narrow** — Azure DevOps, GitHub, GitLab, and Forgejo-family hosts are supported; Bitbucket and other SCM providers are not yet
- **No auto-fixes** - the reviewer can suggest code changes but cannot apply them directly as PR commits or suggestions; all fixes must be manually applied by the developer (yet)

---

## Quick Start

```bash
# 1. Create a minimal .env file (see docs/getting-started.md for setup guidance)
cat > .env <<'EOF'
MEISTER_JWT_SECRET=<random-32+-char-string>
MEISTER_BOOTSTRAP_ADMIN_USER=admin
MEISTER_BOOTSTRAP_ADMIN_PASSWORD=<strong-password>
EOF

# 2. Start the API + PostgreSQL (examples)

# From repository root: specify compose file and env file explicitly
docker compose --env-file .env -f example/docker-compose/docker-compose.yml up --build

# Or run from the example directory (compose will read that directory's .env automatically)
cd example/docker-compose
docker compose up --build

# 3. Verify the public API
curl -k https://localhost:5443/api/healthz
```

Open `https://localhost:5443/` for the admin UI.

The `curl -k` examples in this repository are intended for local development against the
self-signed `https://localhost:5443` endpoint only.

The default compose stack now mounts a named volume for the ASP.NET Core Data Protection key ring
at `/app/.data-protection-keys`. Preserve that volume across restarts and redeployments so stored
client ADO secrets and AI connection API keys remain decryptable.

If you are upgrading from the retired client System Azure DevOps setup, recreate each Azure DevOps
integration through the Providers tab before re-enabling crawl or webhook automation: add the
provider connection, add the organization scope, and confirm the reviewer identity on that
connection.

Alternatively you can use the script `./scripts/run_local.ps1`, which boosts faster but does not spin up a DB or grafana on its own.

See [docs/getting-started.md](docs/getting-started.md) for Admin UI setup and
[docs/api.md](docs/api.md) for API examples.

---

## Admin Authentication

Access to the admin API and admin UI requires a per-user account. On first startup the server
seeds an admin account from the bootstrap env vars:

```bash
MEISTER_BOOTSTRAP_ADMIN_USER=admin
MEISTER_BOOTSTRAP_ADMIN_PASSWORD=<strong-password>
MEISTER_JWT_SECRET=<random-32+-char-string>
```

Log in via `POST /api/auth/login` to receive a 15-minute JWT access token and a 7-day refresh token
when you are calling the default nginx front door on `https://localhost:5443/`. The admin UI handles
token refresh automatically.

---

## Editions and Premium Capabilities

Every installation starts in `Community` edition by default. Community mode is intended to be safe for
fresh deployments and small self-hosted setups:

- Username/password sign-in remains available.
- Only one active PR review job may be pending or processing at a time.
- Only one active SCM provider connection may be configured at a time.

Commercial edition keeps all Community behavior and unlocks the current premium capability set:

- Single sign-on (`sso-authentication`)
- Parallel review execution (`parallel-review-execution`)
- Multiple SCM providers (`multiple-scm-providers`)

Admins can inspect and change the current edition from `Settings -> Licensing` in the admin UI, or
via `GET /api/admin/licensing` and `PATCH /api/admin/licensing` for automation.

Before sign-in, the login screen calls `GET /api/auth/options` for generic auth bootstrap data.
Today that means edition plus password sign-in availability; future sign-in methods such as SSO can
extend the same contract without replacing the route.

For the internal processing flow and the implementation checklist for new licensed features, see
[docs/architecture/licensing-and-feature-flags.md](docs/architecture/licensing-and-feature-flags.md).

---

## Key Environment Variables

| Variable                           | Required      | Description                                                                     |
|------------------------------------|---------------|---------------------------------------------------------------------------------|
| `DB_CONNECTION_STRING`             | Yes           | PostgreSQL connection string; required for all persistent state                 |
| `MEISTER_DATA_PROTECTION_KEYS_PATH`| Docker default| File-system path used for the ASP.NET Core Data Protection key ring             |
| `MEISTER_PUBLIC_BASE_URL`          | Optional      | Public external base URL used when generating webhook listener URLs behind proxies |
| `MEISTER_JWT_SECRET`               | Yes           | HS256 signing secret — minimum 32 characters                                    |
| `MEISTER_BOOTSTRAP_ADMIN_USER`     | Yes           | Username for the initial admin account seeded on first startup                  |
| `MEISTER_BOOTSTRAP_ADMIN_PASSWORD` | Yes           | Password for the initial admin account                                          |

See [docs/getting-started.md](docs/getting-started.md) for the bootstrap setup values.

---

## Documentation

| Document | Description |
|---|---|
| [docs/getting-started.md](docs/getting-started.md) | Admin UI setup guide: deploy, bootstrap, and configure clients |
| [docs/provider-connections.md](docs/provider-connections.md) | Provider connection guide: auth modes, required fields, provider scopes, and where to get each value |
| [docs/api.md](docs/api.md) | Technical API reference and curl examples |
| [docs/architecture.md](docs/architecture.md) | Architecture overview and links to focused subsystem docs |
| [docs/architecture/licensing-and-feature-flags.md](docs/architecture/licensing-and-feature-flags.md) | Licensing resolution flow, feature flag integration, and the checklist for new licensed features |

---

## Running Tests

```bash
dotnet test
```

Integration tests that require PostgreSQL are in the `PostgresApiIntegration` collection
and are skipped automatically when `DB_CONNECTION_STRING` is not set.

---

## License · Security · Contributing

- [Elastic License 2.0](LICENSE) — free for self-hosting, internal use, and community contributions; offering ProPR as a
  hosted or managed service requires commercial terms
- [Commercial License](COMMERCIAL.md) — for businesses that need managed-service rights, negotiated commercial terms, or professional
  support
- [Security Policy](SECURITY.md) — report vulnerabilities privately


