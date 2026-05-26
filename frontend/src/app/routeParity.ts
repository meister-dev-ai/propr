// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Route parity contract used by tests to keep the frontend router aligned with
 * the documented route inventory in
 * `specs/062-vuetify-frontend-rebuild/contracts/frontend-ui-parity.md`.
 */

export interface RouteParityItem {
  /** Stable route name; matches the route table in the parity contract. */
  id: string
  /** User-facing workflow covered by the route. */
  workflowName: string
}

export const routeParity: RouteParityItem[] = [
  { id: 'home', workflowName: 'Role-aware default redirect' },
  { id: 'login', workflowName: 'Platform recovery sign-in' },
  { id: 'tenant-directory', workflowName: 'Tenant directory access' },
  { id: 'tenant-login', workflowName: 'Tenant-scoped sign-in' },
  { id: 'tenant-login-callback', workflowName: 'External tenant sign-in completion' },
  { id: 'tenant-settings', workflowName: 'Tenant admin settings' },
  { id: 'tenant-members', workflowName: 'Tenant member administration' },
  { id: 'clients', workflowName: 'Client access' },
  { id: 'reviews', workflowName: 'Authenticated review history' },
  { id: 'job-protocol', workflowName: 'Review diagnostics' },
  { id: 'settings', workflowName: 'Account settings' },
  { id: 'users', workflowName: 'Platform admin user management' },
  { id: 'thread-memory', workflowName: 'Platform admin thread memory diagnostics' },
  { id: 'provider-settings', workflowName: 'Platform provider activation' },
  { id: 'licensing', workflowName: 'Platform admin licensing' },
  { id: 'client-detail-providers', workflowName: 'Client provider shortcut' },
  { id: 'pr-review', workflowName: 'Client-access PR review request' },
  { id: 'access-denied', workflowName: 'Access denied display' },
  { id: 'client-detail', workflowName: 'Client detail' },
  { id: 'client-procursor-source-events', workflowName: 'ProCursor source event diagnostics' },
]

export const routeParityIds = routeParity.map((item) => item.id)
