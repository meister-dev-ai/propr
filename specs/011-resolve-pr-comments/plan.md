# Implementation Plan: Resolve PR Comments

**Branch**: `011-resolve-pr-comments` | **Date**: 2026-03-21 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/011-resolve-pr-comments/spec.md`

## Summary

This feature allows the PR crawler to continuously evaluate active pull request comments and automatically resolve its own comments when the issue laid out by that comment is fixed by subsequent commits. It introduces a configurable comment resolution behavior per client (Disabled, Silent, WithReply), tracks the last processed commit ID per PR using a new `ReviewPrScan` entity, and extends the AI integration to evaluate if unresolved comments were fully fixed by the new diff. It also handles manually re-opened comments and ensures the AI leaves comments unresolved if uncertain.

## Technical Context

**Language/Version**: C# 13, .NET 10, TypeScript 5.x, Vue 3.5
**Primary Dependencies**: ASP.NET Core MVC, EF Core 10.0.3, Npgsql 10.0.0, Microsoft.TeamFoundationServer.Client 20.269.0-preview
**Storage**: PostgreSQL 17 (EF Core)
**Testing**: xUnit + NSubstitute (Backend), Vitest + @vue/test-utils (Admin UI)
**Target Platform**: Linux Container, Docker Compose
**Project Type**: Web Service + Background Worker + Vue Admin UI
**Performance Goals**: Avoid unnecessary thread fetches on unchanged code. Job processing SHOULD complete within 120 s.
**Constraints**: Standard ADO API rate limits.
**Scale/Scope**: Impacts DB schema, Admin UI, Client API, and Review Worker logic.

## Constitution Check

*GATE: Passed. Architecture conforms to layered rules. Testing standards will be maintained. No external unapproved dependencies are introduced. Background job reliability and telemetry principles are respected.*

## Project Structure

### Documentation (this feature)

```text
specs/011-resolve-pr-comments/
├── plan.md              # This file (/speckit.plan command output)
├── research.md          # Phase 0 output (/speckit.plan command)
├── data-model.md        # Phase 1 output (/speckit.plan command)
├── quickstart.md        # Phase 1 output (/speckit.plan command)
├── contracts/           # Phase 1 output (/speckit.plan command)
└── tasks.md             # Phase 2 output (/speckit.tasks command - NOT created by /speckit.plan)
```

### Source Code (repository root)

```text
src/
├── MeisterProPR.Domain/
│   ├── Entities/
│   │   └── ReviewPrScan.cs
│   └── Enums/
│       └── CommentResolutionBehavior.cs
├── MeisterProPR.Application/
│   ├── DTOs/
│   │   ├── ClientDto.cs
│   │   └── CreateClientRequest.cs
│   └── Interfaces/
│       └── IAdoThreadClient.cs
├── MeisterProPR.Infrastructure/
│   ├── Data/
│   │   ├── Models/
│   │   │   └── ClientRecord.cs
│   │   └── MeisterProPRDbContext.cs
│   ├── AzureDevOps/
│   └── AI/
└── MeisterProPR.Api/
    └── Controllers/

admin-ui/
└── src/
    ├── types/
    ├── components/
    └── views/
```

**Structure Decision**: Standard 4-project Clean Architecture setup for the backend. The Admin UI changes live in the standard Vue SFC structure.

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| None | N/A | N/A |
