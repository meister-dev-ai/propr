// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { createAdminClient } from '@/services/api'
import type { components } from '@/services/generated/openapi'

export type ClientBudgetConsumption = components['schemas']['ClientBudgetConsumptionDto']
export type BudgetDailySpend = components['schemas']['BudgetDailySpendDto']

/**
 * Fetches the client's spend against its monthly budget for the current period, with a trajectory projection.
 * Backed by the licensed `GET /admin/clients/{clientId}/budget/consumption` endpoint.
 */
export async function getClientBudgetConsumption(clientId: string) {
  return createAdminClient().GET('/admin/clients/{clientId}/budget/consumption', {
    params: { path: { clientId } },
  })
}
