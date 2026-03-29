"""
description: Vue 3 frontend conventions, generated OpenAPI TypeScript client, and MSW mock patterns.
when-to-use: When files change under admin-ui/, including .vue, .ts, .spec.ts files, or openapi.ts.
"""

# Frontend Architecture

## Vue 3 Composition API

All components use `<script setup>` with the Vue 3 Composition API. `ref()`, `reactive()`, `computed()`, `watch()`, `onMounted()` are Vue reactivity primitives — not custom implementations. Do not flag their usage as unusual.

Tabs are implemented by conditionally showing `<div v-show="activeTab === 'x'">` sections rather than routing. Data for lazy-loaded tabs (e.g., AI Connections) is intentionally fetched only on first tab click to avoid unnecessary API calls on page load — not a missing initialisation.

## Generated TypeScript Client

`admin-ui/src/services/generated/openapi.ts` is generated from `openapi.json` by `openapi-typescript`. It is never edited by hand. Do not suggest changes to this file. Changes to it must be made by regenerating from the source OpenAPI spec.

The `createAdminClient()` function (from `@hey-api/client-fetch` or similar) wraps the generated types into typed fetch calls. The return shape `{ data, response }` is the client's standard pattern.

## MSW (Mock Service Worker) Handlers

`admin-ui/src/mocks/handlers.ts` defines request handlers for development and test mocking. Handler paths use the same base path as the real API. Route parameter names (e.g., `:id`, `:jobId`) must match the `params` key used inside the handler — MSW v2 uses the route segment name directly.

Endpoint casing must match the generated client's calls exactly. MSW matching is case-sensitive for URL paths.

## `aiConnectionsService.ts`

This is a hand-written service layer that wraps the generated client with convenience methods. It is intentionally separate from the generated client to allow typed convenience over the raw generated types. Do not suggest merging it into the generated client.

## API Key Handling

The API key for AI connections is write-only: it is accepted on create/update calls but never returned in GET responses or DTOs. The UI reflects this — the API key input field exists on creation but the field is intentionally absent or masked on subsequent reads. This is correct security behaviour.
