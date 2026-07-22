// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { createAdminClient } from '@/services/api'
import type { components } from '@/services/generated/openapi'

export type ClientBudgetConsumption = components['schemas']['ClientBudgetConsumptionDto']
export type BudgetDailySpend = components['schemas']['BudgetDailySpendDto']
export type ClientBudgetHistory = components['schemas']['ClientBudgetHistoryDto']
export type BudgetMonthSpend = components['schemas']['BudgetMonthSpendDto']

/**
 * Fetches the client's spend against its monthly budget for a period, with a trajectory projection.
 * `period` is an optional target month as `YYYY-MM` (omit for the current month).
 * Backed by the licensed `GET /admin/clients/{clientId}/budget/consumption` endpoint.
 */
export async function getClientBudgetConsumption(clientId: string, period?: string) {
  return createAdminClient().GET('/admin/clients/{clientId}/budget/consumption', {
    params: { path: { clientId }, query: period ? { period } : {} },
  })
}

/**
 * Fetches the client's estimated USD spend per month over a trailing window, with the current caps.
 * Backed by the licensed `GET /admin/clients/{clientId}/budget/history` endpoint.
 */
export async function getClientBudgetHistory(clientId: string, months = 12) {
  return createAdminClient().GET('/admin/clients/{clientId}/budget/history', {
    params: { path: { clientId }, query: { months } },
  })
}
