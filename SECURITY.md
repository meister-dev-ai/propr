# Security Policy

## Supported Versions

| Version | Supported |
|---------|-----------|
| main    | ✅        |

## Reporting a Vulnerability

**Please do not open public GitHub Issues for security vulnerabilities.**

Report vulnerabilities via [GitHub Private Vulnerability Reporting](https://github.com/features/security/advisories)
on this repository.

We will confirm receipt within 48 hours and aim to provide an initial assessment within
5 business days.

## Scope

Areas of particular relevance:

- Review-trigger and user authentication (`X-Client-Key`, `X-User-Pat`) — the shared `X-Admin-Key` path has been removed
- ADO token validation (`X-Ado-Token`)
- Per-client credential storage (PostgreSQL — secrets stored, never returned via API)
- Azure Identity / credential leaks (`AZURE_CLIENT_SECRET`, `AI_API_KEY`)

## Out of Scope

- Rate-limiting bypass without authentication
- Denial-of-service through normal API usage
