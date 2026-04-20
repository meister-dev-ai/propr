# Security And Access

This page describes how callers authenticate into the admin and review surfaces, how provider
secrets are protected at rest, and how the backend resolves Azure credentials for Azure DevOps
compatibility operations.

## Admin Authentication Flow

```mermaid
sequenceDiagram
    actor User as User / Admin UI
    participant Auth as AuthController
    participant MW as AuthMiddleware
    participant JT as JwtTokenService
    participant UR as UserRepository
    participant PR as UserPatRepository

    User->>Auth: POST /auth/login { username, password }
    Auth->>UR: GetByUsernameAsync()
    UR-->>Auth: AppUser (BCrypt password hash)
    Auth->>Auth: BCrypt.Verify(password, hash)
    Auth->>JT: GenerateAccessToken(user)
    Auth->>UR: persist RefreshToken (SHA-256 hash, 7 days)
    Auth-->>User: { accessToken (15 min), refreshToken (7 days) }

    Note over User,Auth: Silent refresh within 60 s of expiry

    User->>Auth: POST /auth/refresh { refreshToken }
    Auth->>UR: GetActiveByHashAsync(sha256(token))
    Auth->>JT: GenerateAccessToken(user)
    Auth-->>User: { accessToken (15 min) }
```

JWTs and PATs establish the caller identity used by admin endpoints and review-submission role
checks. `X-Ado-Token` is validated separately on review intake and status endpoints so the backend
can verify the caller against Azure DevOps without storing or logging the token.

## Protected Provider Secrets

Provider connection secrets, webhook secrets, and per-client Azure DevOps credentials are stored
through the shared `ISecretProtectionCodec` path backed by ASP.NET Core Data Protection. Provider
operational audit records and webhook delivery history store normalized status, failure category,
summary data, and readiness explanations without persisting raw secrets or authorization headers.

## Request Auth Evaluation Order

```mermaid
flowchart TD
    REQ([Inbound Request]) --> B1

    B1{"Authorization: Bearer JWT?"} -- valid JWT --> SET_JWT["Set UserId + IsAdmin from claims<br/>Load ClientRoles from DB"]
    B1 -- no/invalid --> B2

    B2{"X-User-Pat header?"} -- PAT found & BCrypt match --> SET_PAT["Set UserId + IsAdmin from user record<br/>Load ClientRoles from DB"]
    B2 -- no/invalid --> DEFAULT["Anonymous request<br/>IsAdmin = false"]

    SET_JWT --> NEXT["Continue request pipeline"]
    SET_PAT --> NEXT
    DEFAULT --> NEXT
```

`AuthMiddleware` resolves application identity in-process and loads client roles eagerly so later
controllers can enforce authorization without re-deriving user context.

Client-specific controller actions must validate the caller against the target client, not just any
client assignment. For those endpoints, use the requested `clientId` in the authorization check so a
user with access to one client cannot act on another client by reusing a broad client-admin role.
Broad client-role checks are only appropriate for collection-level flows that intentionally span
multiple clients.

## Azure DevOps Credential Resolution

For Azure DevOps calls, the backend resolves the effective Azure credential per client. A client
may either use its own stored service principal or fall back to the shared
`DefaultAzureCredential` chain.

```mermaid
flowchart TD
    START([ADO Operation for Client X]) --> LOOKUP

    LOOKUP{Per-client credentials in DB?}

    LOOKUP -- "yes" --> CSC["ClientSecretCredential (tenantId, clientId, secret)"]
    LOOKUP -- "no" --> DAC["DefaultAzureCredential (env vars / managed identity)"]

    CSC --> CACHE_C["VssConnection (client-scoped cache)"]
    DAC --> CACHE_G["VssConnection (global cache)"]

    CACHE_C --> ADO[Azure DevOps API]
    CACHE_G --> ADO
```

Per-client ADO credentials are stored in PostgreSQL and protected at rest. If a client has no
dedicated credential, the backend uses the deployment-wide Azure identity configured for the host.
GitHub, GitLab, and Forgejo-family calls use the connection-scoped secret stored on the provider
connection record instead of Azure identity resolution.
