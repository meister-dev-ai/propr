// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import type { ClientTokenUsageResponse } from '@/types/clientTokenUsage'
import { getActiveRuntime } from '@/app/runtime/runtimeContext'
import type { RuntimeMode } from '@/app/runtime/createRuntime'
import { sanitizeErrorMessage } from '@/services/credentialSafety'

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
  return resolveClientTokenUsageService().getClientTokenUsage(clientId, from, to, token)
}

async function getClientTokenUsageInternal(
  clientId: string,
  from: string,
  to: string,
  token: string,
): Promise<ClientTokenUsageResponse> {
  const url = `${getActiveRuntime().apiBaseUrl}/admin/clients/${encodeURIComponent(clientId)}/token-usage?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`

  try {
    const response = await fetch(url, {
      headers: {
        Authorization: `Bearer ${token}`,
      },
    })

    if (!response.ok) {
      throw new Error(`Failed to fetch token usage: ${response.status} ${response.statusText}`)
    }

    return response.json() as Promise<ClientTokenUsageResponse>
  } catch (error) {
    throw new Error(sanitizeErrorMessage(error, 'Failed to fetch token usage.'))
  }
}

export interface ClientTokenUsageService {
  runtimeMode: RuntimeMode
  getClientTokenUsage: (clientId: string, from: string, to: string, token: string) => Promise<ClientTokenUsageResponse>
}

function createClientTokenUsageService(runtimeMode: RuntimeMode): ClientTokenUsageService {
  return {
    runtimeMode,
    getClientTokenUsage: getClientTokenUsageInternal,
  }
}

const liveClientTokenUsageService = createClientTokenUsageService('live')
const mockClientTokenUsageService = createClientTokenUsageService('mock')

export function resolveClientTokenUsageService(): ClientTokenUsageService {
  return getActiveRuntime().mode === 'mock'
    ? mockClientTokenUsageService
    : liveClientTokenUsageService
}
