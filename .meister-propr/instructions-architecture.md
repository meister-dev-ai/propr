"""
description: Modular backend architecture and intentional implementation patterns across the ProPR control plane, provider adapters, and extracted ProCursor host.
when-to-use: When C# files change under src/, especially Program.cs, module registration, review orchestration, provider adapters, ProCursor integration, or secret handling.
"""

# Meister DEV ProPR Architecture

ProPR is no longer just a simple layered Azure DevOps app. Treat it as a modular monolith control plane with provider-neutral review workflows plus an extracted `MeisterProPR.ProCursor.Service` host.

## Composition Model

- `src/MeisterProPR.Api` is the control-plane host and composition root. It is expected to wire shared support plus feature module entry points such as `AddReviewingModule()`, `AddCrawlingModule()`, `AddClientsModule()`, `AddIdentityAndAccessModule()`, `AddMentionsModule()`, `AddPromptCustomizationModule()`, `AddUsageReportingModule()`, `AddLicensingModule()`, and ProCursor remote-mode support.
- Keep `Program.cs` focused on composition, host wiring, middleware, health checks, telemetry, and startup recovery. Do not scatter feature-internal repository/service registrations through controllers or unrelated startup code.
- Preserve the dependency rule `Api -> Application -> Domain <- Infrastructure`. Provider-specific and persistence-specific behavior belongs behind interfaces and adapter registries, not in controllers or domain types.

## Module And Provider Ownership

- The current backend is organized by vertical slices. Reviewing, Crawling, Clients, IdentityAndAccess, Mentions, PromptCustomization, UsageReporting, Licensing, and ProCursor each have explicit ownership boundaries.
- Reviewing, crawl activation, webhook intake, publication, and thread-memory handling are provider-neutral application flows. Azure DevOps, GitHub, GitLab, and Forgejo-family behavior attaches through provider registries and adapter interfaces.
- Do not fork application-layer behavior per provider when the shared provider model already exists. Provider-specific query, publication, webhook, discovery, or reviewer-identity behavior belongs in the matching infrastructure adapter.
- Reviewer-trigger identity is configuration-only state used to narrow automatic PR selection. It is not the authenticated publication identity and must not replace connection credentials.

## Reviewing Ownership

- `ReviewOrchestrationService` owns job-level review execution concerns such as carry-forward, publication context, posting policy, publication identity resolution, and empty-review cleanup.
- `FileByFileReviewOrchestrator` owns per-file AI execution, hard guards, comment relevance filtering, local verification, synthesis input shaping, and cross-file deduplication.
- `FindingDeduplicator` intentionally runs only after all file results are available. It is applied inside the reviewing flow after collection, not during individual file passes.
- The selected comment relevance filter is code-selected in `Program.GetSelectedCommentRelevanceFilterId()` and is currently `hybrid-v1`. Treat that as intentional runtime selection, not missing configuration plumbing.
- `ScmCommentPostingEnabled` can suppress outbound SCM comments while preserving internal review persistence and posting diagnostics. That behavior is intentional.

## ProCursor Boundary

- In the API host, `IProCursorGateway` is intentionally bound to remote or disabled implementations based on `PROCURSOR_REMOTE_MODE`.
- The extracted `MeisterProPR.ProCursor.Service` host owns ProCursor operational persistence behind `PROCURSOR_DB_CONNECTION_STRING` and composes through `AddProCursorModule()`.
- ProPR owns client/provider/AI/ProCursor source configuration. Do not add code paths that make ProPR read or write ProCursor operational tables directly.
- Shared contracts across the ProPR <-> ProCursor boundary live in `MeisterProPR.ProCursor.Contracts`.

## Secret Handling

- Data Protection-backed secrets go through `ISecretProtectionCodec`. Active purpose strings in the current code include:
  - `AiConnectionApiKey`
  - `ClientScmConnectionSecret`
  - `WebhookSecret`
  - `tenant-sso-provider-client-secret`
  - `tenant-sso-external-auth-state`
- `SecretBackfillService` currently backfills legacy plaintext AI connection secrets only. PATs and refresh tokens are not Data Protection-encrypted; PATs are hashed with BCrypt and refresh tokens are stored by SHA-256 hash.
- Keep `MEISTER_DATA_PROTECTION_KEYS_PATH` durable. Losing the key ring breaks decryption of previously protected values.
- DTOs and GET responses must remain write-only for secrets. Logging should continue redacting secret-looking fields instead of serializing them.

## Intentional Patterns

- Nullable constructor dependencies and optional service parameters are common feature toggles and test seams, not automatically missing registrations.
- `IDbContextFactory<T>` and scoped `DbContext` registrations intentionally coexist. Factories are used for concurrent/background or cross-scope work; scoped contexts are used for request-scoped flows.
- `ReviewExclusionRules.Default` means `.meister-propr/exclude` was absent. `ReviewExclusionRules.Empty` means the file existed but yielded no usable patterns.
- Repository instructions and exclusion files are fetched from the target branch only. Do not change that to source-branch reads; the current behavior is a prompt-injection guard.
- `AdoCommentPoster` intentionally creates one Azure DevOps thread per finding.
- Workers such as `ReviewJobWorker`, `AdoPrCrawlerWorker`, `MentionScanWorker`, and `MentionReplyWorker` are registered as singletons and then forwarded as hosted services so health checks can resolve the concrete instances.
- Stub, offline, and no-op implementations under provider `Stub/` paths or `Features/Reviewing/Offline/` are intentional harnesses. Do not flag them as incomplete production logic.
