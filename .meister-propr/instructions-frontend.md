"""
description: Vue 3 SPA conventions covering router-driven admin flows, generated OpenAPI client usage, session state, and frontend test/mocking patterns.
when-to-use: When files change under admin-ui/, including .vue, .ts, router, services, mocks, or frontend tests.
"""

# Frontend Architecture

## SPA Structure

- The admin UI is a Vue 3 SPA with Vue Router, not a single view with local tabs only. Major flows are route-driven (`/clients`, `/reviews`, `/settings`, `/tenants/...`, `/provider-settings`, etc.).
- Some detail screens also sync local tab state through route query parameters. `ClientDetailView` is the main example: it keeps `activeTab` aligned with `route.query.tab`, applies role/capability gating, and deep-links directly into client sub-views.
- Preserve query-synchronized tab state and lazy-loading behavior in those large detail screens instead of replacing them with unrelated local toggles or arbitrary nested routes.

## Vue And Shared State

- Composition API primitives such as `ref()`, `reactive()`, `computed()`, `watch()`, and `onMounted()` are normal and heavily used.
- `useSession()` is the shared reactive auth/licensing store. It tracks access token, refresh token, client roles, tenant roles, installation edition, premium capabilities, and local-password state using browser storage.
- Do not introduce a second global state system unless the surrounding code already uses it. Router guards and many views already depend on `useSession()` semantics.

## Generated API Types And Client

- `npm run generate:api` regenerates both `admin-ui/src/types/index.ts` and `admin-ui/src/services/generated/openapi.ts` from the root `openapi.json`. Do not edit generated OpenAPI type files by hand.
- The runtime client is `openapi-fetch`, wrapped by `createAdminClient()` in `src/services/api.ts`.
- `createAdminClient()` is responsible for adding the bearer token, attempting silent refresh near expiry, and throwing `UnauthorizedError` when refresh fails. Keep that behavior centralized.
- The standard `openapi-fetch` response shape is `{ data, error, response }`. Existing handwritten service modules check `response.ok` and translate API errors before components consume them.
- Use `API_BASE_URL` from `src/services/apiBase.ts` instead of hardcoding API origins or `/api` paths.

## Service-Layer Pattern

- Files under `admin-ui/src/services/*.ts` are the intended handwritten layer above the generated client. They provide typed convenience methods, error shaping, and small workflow helpers.
- Do not collapse those wrappers into components and do not try to hand-edit generated OpenAPI types instead.

## Security And Rendering

- Secret fields are intentionally write-only in the UI. AI/provider credentials may be sent on create or update, but GET flows do not return raw secret material.
- Markdown summaries and review comments are intentionally rendered through `markdown-it` plus `DOMPurify` before `v-html`. Do not replace that with raw HTML rendering.

## Roles, Capabilities, And Feature Gating

- UI authorization depends on both global admin state and scoped `clientRoles` / `tenantRoles`.
- Capability checks are load-bearing. Features such as multiple SCM providers, crawl configs, tenant SSO, and ProCursor often show disabled states with explanatory messages instead of disappearing entirely.
- Preserve those upgrade/unavailable messages when editing gated UI.

## Mocking And Tests

- MSW v2 handlers live in `src/mocks/handlers.ts` and use `http`, `HttpResponse`, and the shared `API_BASE_URL`. Handler paths and casing must exactly match client calls.
- Frontend tests currently live in both `admin-ui/tests/**` and some legacy `src/**/__tests__` locations. Follow the nearby pattern rather than moving tests just for consistency.
- Component and view tests commonly `vi.mock()` service modules, router helpers, and `useSession()` to isolate behavior. That is the expected unit-test style in this repo.
- Playwright E2E coverage exists under `admin-ui/tests/e2e/` for higher-level auth and tenant flows.

## Intentional Frontend Patterns

- Some admin views are intentionally large single-file components because they coordinate multiple tabs, dialogs, capability states, and service calls in one screen.
- On-demand data loading for expensive tabs or detail panels is a deliberate optimization, not missing initialization.
- Optimistic button disabling, query-driven navigation, and capability-aware empty states are established UI patterns in the current codebase.
