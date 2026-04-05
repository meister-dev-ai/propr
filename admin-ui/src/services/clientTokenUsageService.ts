// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { API_BASE_URL } from '@/services/apiBase'
import type { ClientTokenUsageResponse } from '@/types/clientTokenUsage'

/**
 * Fetches daily token consumption for a client within the given date range.
 * @param clientId - UUID of the client
 * @param from - Start date (inclusive) in YYYY-MM-DD format
 * @param to - End date (inclusive) in YYYY-MM-DD format
 * @param token - Bearer token for authorization
 */
export async function getClientTokenUsage(
  clientId: string,
  from: string,
  to: string,
  token: string,
): Promise<ClientTokenUsageResponse> {
  const url = `${API_BASE_URL}/admin/clients/${encodeURIComponent(clientId)}/token-usage?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`
  const response = await fetch(url, {
    headers: {
      Authorization: `Bearer ${token}`,
    },
  })

  if (!response.ok) {
    throw new Error(`Failed to fetch token usage: ${response.status} ${response.statusText}`)
  }

  return response.json() as Promise<ClientTokenUsageResponse>
}
