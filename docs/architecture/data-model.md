# Data Model

This document describes the main persistence slices for ProPR. The diagrams separate guided
configuration, ProCursor persistence, the core review and access model, and tenancy and identity.

## Guided Configuration Slice

The guided configuration slice persists organization-scoped state, canonical crawl filters,
optional selected-source associations, and review-job source snapshots beneath the client boundary.

```mermaid
erDiagram
    Client ||--o{ ClientAdoOrganizationScope : "allows"
    Client ||--o{ CrawlConfiguration : "owns"
    Client ||--o{ ProCursorKnowledgeSource : "owns"
    ClientAdoOrganizationScope ||--o{ CrawlConfiguration : "selected by"
    CrawlConfiguration ||--o{ CrawlRepoFilter : "stores"
    CrawlConfiguration ||--o{ CrawlConfigurationProCursorSource : "limits to"
    ProCursorKnowledgeSource ||--o{ CrawlConfigurationProCursorSource : "referenced by"
    ReviewJob ||--o{ ReviewJobProCursorSourceScope : "snapshots"
    ProCursorKnowledgeSource ||--o{ ReviewJobProCursorSourceScope : "snapshotted"

    ClientAdoOrganizationScope {
        uuid Id PK
        uuid ClientId FK
        string OrganizationUrl
        string DisplayName
        string VerificationStatus
        bool IsEnabled
    }

    CrawlConfiguration {
        uuid Id PK
        uuid ClientId FK
        uuid OrganizationScopeId FK
        string OrganizationUrl
        string ProjectId
        string ProCursorSourceScopeMode
        int CrawlIntervalSeconds
    }

    CrawlRepoFilter {
        uuid Id PK
        uuid CrawlConfigurationId FK
        string DisplayName
        string CanonicalProvider
        string CanonicalValue
    }

    CrawlConfigurationProCursorSource {
        uuid CrawlConfigurationId FK
        uuid ProCursorKnowledgeSourceId FK
    }

    ReviewJobProCursorSourceScope {
        uuid ReviewJobId FK
        uuid ProCursorKnowledgeSourceId FK
    }
```

Read models expose invalid associations through `invalidProCursorSourceIds`. This preserves stale
references for administrative repair instead of discarding them during projection.

## ProCursor Persistence Slice

The ProCursor persistence slice extends the client boundary with a durable index-job queue,
versioned snapshots, searchable knowledge chunks, and symbol graph records.

```mermaid
erDiagram
    Client ||--o{ ProCursorKnowledgeSource : "owns"
    ProCursorKnowledgeSource ||--o{ ProCursorTrackedBranch : "tracks"
    ProCursorKnowledgeSource ||--o{ ProCursorIndexJob : "queues"
    ProCursorTrackedBranch ||--o{ ProCursorIndexSnapshot : "builds"
    ProCursorIndexSnapshot ||--o{ ProCursorKnowledgeChunk : "stores"
    ProCursorIndexSnapshot ||--o{ ProCursorSymbolRecord : "indexes"
    ProCursorIndexSnapshot ||--o{ ProCursorSymbolEdge : "relates"

    ProCursorKnowledgeSource {
        uuid Id PK
        uuid ClientId FK
        string DisplayName
        string SourceKind
        string RepositoryId
        string DefaultBranch
        bool IsEnabled
    }

    ProCursorTrackedBranch {
        uuid Id PK
        uuid KnowledgeSourceId FK
        string BranchName
        string LastSeenCommitSha
        string LastIndexedCommitSha
        bool IsEnabled
    }

    ProCursorIndexJob {
        uuid Id PK
        uuid KnowledgeSourceId FK
        uuid TrackedBranchId FK
        string JobKind
        string Status
        string DedupKey
        int AttemptCount
    }

    ProCursorIndexSnapshot {
        uuid Id PK
        uuid KnowledgeSourceId FK
        uuid TrackedBranchId FK
        string CommitSha
        string Status
        bool SupportsSymbolQueries
    }
```

## Core Review And Access Slice

The core review and access slice centers on clients, review jobs, review protocols, identity, and
client-owned configuration such as AI connections, prompt overrides, and finding dismissals.

Verification metadata is stored in review-domain records and protocol payloads:

- `CandidateReviewFinding` stores structured claim metadata, provenance, evidence references, and
    optional `VerificationOutcome`.
- `EvidenceBundle` stores concrete evidence items, evidence-source attempt records, and aggregate
    ProCursor attempt/result status so operators can distinguish fetched context from proof.
- `ReviewFileResult` stores only the surviving publishable local findings plus a verification-aligned
  per-file summary for newly reviewed files. Carried-forward rows remain outside the verification
  scope of the associated run.
- `ReviewJobProtocol` records claim extraction, local verification, evidence collection, PR-level
    verification, degraded verification states, summary reconciliation, and final-gate audit
    payloads.
- Final-gate payloads store summary reconciliation metadata, including original and final summary
    text, dropped finding ids, summary-only finding ids, and whether a summary rewrite was required.

```mermaid
erDiagram
    Client {
        uuid Id PK
        string Key "legacy plaintext (deprecated)"
        string KeyHash "BCrypt hash of active key"
        string PreviousKeyHash "BCrypt hash (7-day grace period after rotation)"
        datetime KeyExpiresAt
        datetime PreviousKeyExpiresAt
        datetime KeyRotatedAt
        int AllowedScopes
        string DisplayName
        bool IsActive
        datetime CreatedAt
    }

    ClientAdoCredential {
        uuid Id PK
        uuid ClientId FK
        string TenantId
        string AzureClientId
        string Secret "encrypted via ASP.NET Data Protection"
    }

    CrawlConfiguration {
        uuid Id PK
        uuid ClientId FK
        string OrganizationUrl
        string ProjectId
        int CrawlIntervalSeconds
        bool IsActive
    }

    ReviewJob {
        uuid Id PK
        uuid ClientId FK
        string OrganizationUrl
        string ProjectId
        string RepositoryId
        int PullRequestId
        int IterationId
        string Status
        datetime SubmittedAt
        datetime CompletedAt
        jsonb Result
        int RetryCount
        long TotalInputTokensAggregated
        long TotalOutputTokensAggregated
    }

    ReviewJobProtocol {
        uuid Id PK
        uuid JobId FK
        uuid FileResultId FK
        int PassNumber
        int InputTokens
        int OutputTokens
    }

    ReviewFileResult {
        uuid Id PK
        uuid JobId FK
        string FilePath
        bool IsComplete
        bool IsFailed
        bool IsExcluded "true when skipped by exclusion rules"
        string ExclusionReason "pattern that matched, e.g. **/Migrations/*.Designer.cs"
    }

    AppUser {
        uuid Id PK
        string Username "unique, case-insensitive"
        string PasswordHash "BCrypt"
        string GlobalRole "Admin / User"
        bool IsActive
        datetime CreatedAt
    }

    UserClientRole {
        uuid Id PK
        uuid UserId FK
        uuid ClientId FK
        string Role "ClientAdministrator / ClientUser"
        datetime AssignedAt
    }

    UserPat {
        uuid Id PK
        uuid UserId FK
        string TokenHash "BCrypt (mpr_ prefix)"
        string Label
        datetime ExpiresAt
        datetime CreatedAt
        datetime LastUsedAt
        bool IsRevoked
    }

    RefreshToken {
        uuid Id PK
        uuid UserId FK
        string TokenHash "SHA-256"
        datetime ExpiresAt
        datetime CreatedAt
        datetime RevokedAt
    }

    AiConnection {
        uuid Id PK
        uuid ClientId FK
        string DisplayName
        string EndpointUrl
        jsonb Models "array of model names"
        bool IsActive
        string ActiveModel
        string ApiKey "encrypted; null when using DefaultAzureCredential"
        datetime CreatedAt
        string ModelCategory "Standard / Reasoning"
    }

    PromptOverride {
        uuid Id PK
        uuid ClientId FK
        uuid CrawlConfigId FK "null for client-scope overrides"
        string Scope "ClientScope / CrawlConfigScope"
        string PromptKey "SystemPrompt | AgenticLoopGuidance | SynthesisSystemPrompt | QualityFilterSystemPrompt | PerFileContextPrompt"
        string OverrideText
        datetime CreatedAt
    }

    FindingDismissal {
        uuid Id PK
        uuid ClientId FK
        string PatternText "normalised, lowercase, max 200 chars"
        string Label "optional admin-provided note"
        string OriginalMessage "original AI finding message"
        datetime CreatedAt
    }

    ReviewPrScan {
        uuid Id PK
        uuid ClientId FK
        string RepositoryId
        int PullRequestId
        string LastProcessedCommitId
    }

    ReviewPrScanThread {
        uuid ReviewPrScanId FK
        int ThreadId
        int LastSeenReplyCount
    }

    Client ||--o| ClientAdoCredential : "has (optional)"
    Client ||--o{ CrawlConfiguration : "has"
    Client ||--o{ ReviewJob : "owns"
    Client ||--o{ UserClientRole : "scoped to"
    Client ||--o{ AiConnection : "has"
    Client ||--o{ PromptOverride : "has"
    Client ||--o{ FindingDismissal : "has"
    Client ||--o{ ReviewPrScan : "has"
    ReviewJob ||--o{ ReviewJobProtocol : "has passes"
    ReviewJob ||--o{ ReviewFileResult : "has file results"
    ReviewJobProtocol }o--|| ReviewFileResult : "linked to"
    AppUser ||--o{ UserClientRole : "has"
    AppUser ||--o{ UserPat : "has"
    AppUser ||--o{ RefreshToken : "has"
    ReviewPrScan ||--o{ ReviewPrScanThread : "has"
    CrawlConfiguration ||--o{ PromptOverride : "can have (crawl-config scope)"
```

## Tenancy And Identity Slice

The tenancy and identity slice places `Tenant` above the client boundary. Each user keeps one
global `AppUser` identity, while access and sign-in policy are defined by tenant-scoped membership
and provider records.

```mermaid
erDiagram
    Tenant ||--o{ Client : "owns"
    Tenant ||--o{ TenantMembership : "grants"
    Tenant ||--o{ TenantSsoProvider : "configures"
    Tenant ||--o{ ExternalIdentity : "scopes"
    Tenant ||--o{ TenantAuditEntry : "audits"
    AppUser ||--o{ TenantMembership : "belongs to"
    AppUser ||--o{ ExternalIdentity : "maps"
    AppUser ||--o{ TenantAuditEntry : "acts as"
    TenantSsoProvider ||--o{ ExternalIdentity : "issues"

    Tenant {
        uuid Id PK
        string Slug
        string DisplayName
        bool IsActive
        bool LocalLoginEnabled
        datetime CreatedAt
        datetime UpdatedAt
    }

    TenantMembership {
        uuid Id PK
        uuid TenantId FK
        uuid UserId FK
        string Role
        datetime AssignedAt
        datetime UpdatedAt
    }

    TenantSsoProvider {
        uuid Id PK
        uuid TenantId FK
        string DisplayName
        string ProviderKind
        string ProtocolKind
        string ClientId
        string ClientSecretProtected
        bool IsEnabled
        bool AutoCreateUsers
    }

    ExternalIdentity {
        uuid Id PK
        uuid TenantId FK
        uuid UserId FK
        uuid SsoProviderId FK
        string Issuer
        string Subject
        string Email
        bool EmailVerified
        datetime LastSignInAt
    }

    TenantAuditEntry {
        uuid Id PK
        uuid TenantId FK
        uuid ActorUserId FK
        string EventType
        string Summary
        string Detail
        datetime OccurredAt
    }
```

`GlobalRole` defines platform-administrator authority and the recovery path. `TenantMembership`
defines tenant-local administration and user access. `UserClientRole` defines client-local
operations. Client-scoped actions also require membership in the tenant that owns the target
client.

Tenant sign-in providers and external identities are tenant-scoped. Enabled providers, allowed
email domains, and returning-login identity matching remain isolated per tenant.
`TenantAuditEntry` stores append-only history for tenant policy, provider, and membership changes.
