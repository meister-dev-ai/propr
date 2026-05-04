# Licensing and Feature Flags

This document explains how ProPR processes installation licensing, premium capabilities, and
runtime feature flags, and what needs to happen when a new feature must respect licensing.

## Overview

Licensing is installation-wide and database-backed.

- The current edition is persisted as a singleton row in `installation_edition`.
- Per-capability overrides are persisted in `premium_capability_overrides`.
- A fresh installation seeds itself into `Community` edition on first access.
- Effective capability state is calculated from:
  - the current installation edition
  - the static capability catalog
  - any stored per-capability override rows

The backend uses that calculated state in two ways:

1. Direct capability checks in handlers, controllers, and workers.
2. `Microsoft.FeatureManagement` integration via a persisted feature definition provider.

## Processing Flow

At startup, `Program.cs` composes the licensing module through `AddLicensingModule()` and, when a
database is available, configures `Microsoft.FeatureManagement` with a disabled-feature handler.

The main runtime path is:

1. `LicensingPolicyRepository` loads or seeds the current persisted installation policy.
2. `StaticPremiumCapabilityCatalog` defines the known capability keys, display names, defaults,
   and user-facing messages.
3. `LicensingCapabilityService` combines the persisted policy and the static catalog into effective
   `CapabilitySnapshot` values.
4. Controllers, application handlers, workers, and session/bootstrap endpoints consume those
   snapshots to allow, deny, or describe behavior.
5. `PersistedFeatureDefinitionProvider` mirrors the same capability state into
   `Microsoft.FeatureManagement` so MVC feature gates can use the same source of truth.

## Main Components

### Persistence

- `LicensingPolicyRepository` is the persistence boundary for installation-wide edition and
  capability overrides.
- The repository seeds a missing installation as `Community`.
- Switching to `Commercial` stamps activation metadata.
- Switching back to `Community` clears activation metadata.
- Capability overrides are generic key/value rows, so adding a new capability usually does not
  require a schema change.

### Capability Catalog

- `StaticPremiumCapabilityCatalog` is the source of truth for known capability keys.
- Each entry defines:
  - a stable key
  - a display name
  - the user-facing message when Commercial is required
  - the user-facing message when the feature is disabled inside Commercial
  - whether it defaults on in Commercial
  - whether it requires Commercial at all

The stable key is what ties together backend checks, feature management, OpenAPI contracts, and UI
behavior.

### Effective Resolution

`LicensingCapabilityService` is the resolver for effective licensing state.

- `GetSummaryAsync()` builds the installation-wide licensing snapshot for admin/session consumers.
- `GetCapabilityAsync()` resolves one concrete capability snapshot.
- `IsEnabledAsync()` is the lowest-friction check for workers and services.
- `GetAuthOptionsAsync()` builds the pre-login auth bootstrap payload.
- `UpdateAsync()` validates requested overrides and persists the new policy.

The service supports both Commercial-only features and non-Commercial toggles. The shipped catalog
entries are Commercial-only, and the resolver also supports features with
`RequiresCommercial = false`.

### Feature Management Integration

The licensing module wires `Microsoft.FeatureManagement` through two custom pieces:

- `PersistedFeatureDefinitionProvider` turns the resolved capability state into feature definitions.
- `LicensedCapabilityFeatureFilter` is the terminal filter for features that are already resolved by
  the licensing service.

When MVC feature gating is used, `PremiumFeatureDisabledHandler` converts the rejected feature key
into ProPR's structured `premium_feature_unavailable` response.

This keeps feature-gated endpoints aligned with the same capability resolution logic used by
services and workers.

## API And UI Contracts

The public and admin contracts are:

- `GET /api/admin/licensing`: returns the current edition and resolved capability list.
- `PATCH /api/admin/licensing`: updates edition and capability overrides.
- `GET /api/auth/options`: returns pre-login auth bootstrap data.
- `GET /api/auth/me`: returns authenticated session data including edition and capabilities.

The admin UI consumes generated OpenAPI types for these contracts through:

- `authOptionsService.ts` for pre-login bootstrap data
- `licensingService.ts` for admin licensing operations
- `useSession.ts` for authenticated session hydration and capability lookup

That separation is intentional: login/bootstrap concerns can evolve independently from admin
licensing management while using the same backend source of truth.

## When To Use Direct Checks vs Feature Gates

Use direct `ILicensingCapabilityService` checks when:

- the feature decision happens inside an application handler or background worker
- the feature needs contextual logic beyond a simple endpoint on/off decision
- the feature should return an application-specific conflict or flow-specific outcome

Use MVC feature gates when:

- the feature cleanly maps to enabling or disabling an endpoint or controller action
- a 403-style premium-unavailable response is sufficient
- the gated action should stay thin and declarative

The codebase uses direct capability checks for review intake, worker execution, and client provider
connection limits. Feature management is available for endpoint-level gates where that shape is a
better fit.

## Adding a New Licensed Feature

When a new feature must respect installation licensing, follow this sequence.

### 1. Add a stable capability key

Add a new entry to `PremiumCapabilityKey`.

- Keep the key stable and API-safe.
- Treat it as part of the external contract once it is shipped.

### 2. Add the capability definition

Register the capability in `StaticPremiumCapabilityCatalog`.

Define:

- display name
- Commercial-required message
- Commercial-disabled message
- whether it defaults on in Commercial
- whether it requires Commercial

If the feature just needs the standard persisted override behavior, this step does not require a new
database migration.

### 3. Enforce it at the owning behavior

Add the check at the code that directly controls the behavior.

- For application flows, inject `ILicensingCapabilityService` and call `GetCapabilityAsync()` or
  `IsEnabledAsync()`.
- For MVC endpoint gating, prefer `[FeatureGate("<capability-key>")]` when the whole action is gated.

Do not scatter unrelated boolean checks throughout the codebase. The owning abstraction should be the
place that decides whether the feature can run.

### 4. Return the standard blocked response shape

If a blocked operation is user-visible, convert the denial into ProPR's standard payload.

- Throw `PremiumFeatureUnavailableException` from application code when the controller should map the
  block into an API result.
- Or return `PremiumFeatureUnavailableResult` directly from controllers.
- MVC feature gates already route through `PremiumFeatureDisabledHandler`.

This keeps UI behavior consistent and preserves the capability-specific message.

### 5. Expose it through contracts only if a caller needs it

If the UI or external automation needs to understand the new capability:

- include it in the relevant DTO flow (`/admin/licensing`, `/auth/options`, `/auth/me`, or another
  feature-specific contract)
- regenerate `openapi.json`
- regenerate the admin UI generated client/types

If the capability is enforcement-only and no client needs to inspect it, do not widen contracts just
for completeness.

### 6. Update the UI at the consumption boundary

If the admin UI must react to the feature:

- use session/bootstrap data for global awareness
- use the generated contract types rather than hand-written payload assumptions
- show the capability's message when the user hits a blocked path
- avoid hard-coding edition logic in multiple components when a capability lookup is enough

### 7. Add focused tests

For each new licensed feature, add focused coverage for:

- capability resolution in the licensing service or repository, if needed
- the owning backend enforcement path
- the blocked response shape, if user-visible
- any UI state or messaging that depends on the capability

## Practical Notes

- A new capability key usually does not need a migration because overrides are stored generically by
  key.
- The edition and capability snapshot is intended to be the shared language between backend and UI.
- `GET /api/auth/options` is a generic pre-login bootstrap endpoint, not an SSO-only endpoint. New
  sign-in methods can extend it later without replacing the route.
- If a feature is both provider-sensitive and license-sensitive, keep provider activation and
  licensing checks separate. Provider enablement and licensing are different policy layers.
