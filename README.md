<p align="center">
  <img src="resources/images/logo.png" alt="ProPR" width="180">
  <br>
  <em>AI code review for Azure DevOps, GitHub, GitLab, and Forgejo</em>
</p>

<p align="center">
  <a href="https://github.com/meister-dev-ai/propr/actions/workflows/ci.yml"><img src="https://github.com/meister-dev-ai/propr/actions/workflows/ci.yml/badge.svg?branch=main" alt="CI"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-ELv2-blue.svg" alt="License: ELv2"></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet" alt=".NET 10">
  <a href="https://techcommunity.microsoft.com/blog/azure-events/announcing-the-ai-dev-days-hackathon-winners/4513528"><img src="https://img.shields.io/badge/🏆%20AI%20Dev%20Days-Best%20Enterprise%20Solution-gold" alt="AI Dev Days Hackathon — Best Enterprise Solution"></a>
</p>

---

ProPR reviews your pull and merge requests with AI, right where your code already lives. It reads
the changed files, comments on specific lines, and posts a summary — either automatically as PRs
come in, or on demand from CI.

## What we care about

- **Your code stays where it is.** Reviews run against Azure DevOps, GitHub, GitLab, and
  Forgejo-family hosts. No mirroring your repos somewhere else.
- **Your AI, your keys.** Bring your own model (currently Microsoft Foundry-hosted models). ProPR
  doesn't ship a model or route your code through ours.
- **Self-hostable and sovereign.** Run the whole stack yourself. Provider and AI credentials are
  scoped per client and protected at rest.
- **Every decision is on the record.** Reviews, AI calls, tool calls, filter decisions, and timing
  are all stored and inspectable from the management UI — no log spelunking.
- **Built to stay out of the way.** Token-aware reviews, per-file passes, and relevance filtering
  keep the signal high and the cost down.

## Get started

The fastest path is the example Docker Compose stack:

```bash
cd example/docker-compose
docker compose up --build
```

Then open `https://localhost:5443/`.

That's the short version — the [getting-started guide](docs/getting-started.md) covers the required
environment variables, first-login bootstrap, and connecting your first provider and AI model.

## Documentation

| Document | What's in it |
|---|---|
| [Getting started](docs/getting-started.md) | Deploy, bootstrap, and configure your first client |
| [Provider connections](docs/provider-connections.md) | Auth modes and onboarding per SCM provider, including on-prem Azure DevOps Server |
| [API reference](docs/api.md) | Endpoints and `curl` examples for automation |
| [Architecture](docs/architecture.md) | System shape and subsystem deep-dives |
| [Licensing & feature flags](docs/architecture/licensing-and-feature-flags.md) | How editions and licensed features resolve |

## License

ProPR is distributed under the Elastic License 2.0. Some features in the tree are commercial-only and
require a license to use, even when self-hosted.

- [LICENSE](LICENSE) — repository-wide source license
- [LICENSING.md](LICENSING.md) — path-by-path capability classification
- [COMMERCIAL.md](COMMERCIAL.md) — when a commercial license is required
- [SECURITY.md](SECURITY.md) — reporting vulnerabilities
- [CONTRIBUTING.md](CONTRIBUTING.md) — how to contribute
