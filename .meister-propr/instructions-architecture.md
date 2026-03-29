"""
description: Meister DEV ProPR layered architecture — intentional design patterns across Domain, Application, Infrastructure, and Api layers.
when-to-use: When any C# source file changes in src/, particularly in controllers, services, repositories, domain entities, or dependency injection configuration.
"""

# Meister DEV ProPR Architecture

ProPR follows a clean layered architecture. Each layer has a defined role — do not flag patterns that are intentional by design.

## Layers

**Domain** (`MeisterProPR.Domain`): Entities, value objects, enums. No dependencies on infrastructure or application layers. Entities use `SetXxx(...)` methods rather than public property setters — this is intentional to enforce invariants at the domain boundary, not a missing setter oversight.

**Application** (`MeisterProPR.Application`): Interfaces (`IJobRepository`, `IPullRequestFetcher`, etc.), DTOs, service orchestration. Zero infrastructure dependencies. All infrastructure access goes through interfaces defined here.

**Infrastructure** (`MeisterProPR.Infrastructure`): Implementations of application interfaces. Contains EF Core repositories, Azure DevOps API clients, and AI review core. May depend on Application and Domain. The AI review loop lives here.

**Api** (`MeisterProPR.Api`): ASP.NET Core controllers and middleware. Thin layer — controllers delegate to application services and repositories via DI.

## Key Intentional Patterns

**Optional dependencies via nullable constructor parameters**: Several services accept optional dependencies as `SomeType? dep = null` in their primary constructor. This is the project's pattern for optional features — not a missing DI registration. Guard with `if (dep is not null)` is the correct usage.

**`IDbContextFactory<T>` for parallel operations**: The parallel per-file AI review passes create a new `DbContext` per task via `IDbContextFactory`. This is the correct EF Core pattern for concurrent async access. The scoped `DbContext` is used for sequential single-request flows. Do not flag this as inconsistent — both patterns are intentional and exist side by side.

**`ReviewExclusionRules.Default` vs `.Empty`**: `Default` means "no file was found, use built-in patterns". `Empty` means "file was found but has no patterns — no exclusions". This distinction is intentional and load-bearing.

**`FindingDeduplicator`**: Cross-file deduplication runs after all per-file results are collected, before synthesis. It is applied in `ReviewOrchestrationService`, not in the per-file orchestrator, because it needs all results simultaneously.

**`AdoCommentPoster` posts one thread per comment**: Each `ReviewComment` becomes a separate ADO thread. This is intentional — ADO's API does not support bulk thread creation with line anchors in a single call.

**Stub implementations**: Files under `AzureDevOps/Stub/` are no-op or in-memory implementations for integration testing and local development. They are not production code paths. Do not flag missing functionality in stub classes.
