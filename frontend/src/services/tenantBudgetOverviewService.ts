// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { createAdminClient } from '@/services/api'
import type { components } from '@/services/generated/openapi'

export type TenantBudgetOverview = components['schemas']['TenantBudgetOverviewDto']
export type TenantBudgetOverviewClient = components['schemas']['TenantBudgetOverviewClientDto']

/**
 * Fetches current-period spend against budget for every client in a tenant.
 * Backed by the licensed `GET /admin/tenants/{tenantId}/budget/overview` endpoint.
 */
export async function getTenantBudgetOverview(tenantId: string) {
  return createAdminClient().GET('/admin/tenants/{tenantId}/budget/overview', {
    params: { path: { tenantId } },
  })
}
