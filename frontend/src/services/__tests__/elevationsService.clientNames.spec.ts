// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createRuntime } from '@/app/runtime/createRuntime'
import { resetActiveRuntime, setActiveRuntime } from '@/app/runtime/runtimeContext'

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    getAccessToken: () => 'access-token',
    setAccessToken: vi.fn(),
    clearTokens: vi.fn(),
    accessTokenExpiresIn: () => 3600,
    loadClientRoles: vi.fn(),
  }),
}))

const fetchMock = vi.fn()

function requestedUrl(call: unknown[]): string {
  const input = call[0]
  return input instanceof Request ? input.url : String(input)
}

describe('getClientNames', () => {
  beforeEach(() => {
    fetchMock.mockReset()
    vi.stubGlobal('fetch', fetchMock)
    setActiveRuntime(createRuntime({ mode: 'live', apiBaseUrl: 'https://runtime.example.test/api' }))
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    resetActiveRuntime()
  })

  it('reads the names from the API of the active runtime, which the elevation requests use as well', async () => {
    fetchMock.mockResolvedValue(
      new Response(JSON.stringify({ displayName: 'Payments', tenantDisplayName: 'Acme' }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    )
    const { getClientNames } = await import('@/services/elevationsService')

    await expect(getClientNames('client-1')).resolves.toEqual({ clientName: 'Payments', tenantName: 'Acme' })

    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(requestedUrl(fetchMock.mock.calls[0])).toBe('https://runtime.example.test/api/clients/client-1')
  })

  it('names the client by its id when the lookup is refused', async () => {
    fetchMock.mockResolvedValue(
      new Response(JSON.stringify({ error: 'Forbidden.' }), {
        status: 403,
        headers: { 'Content-Type': 'application/json' },
      }),
    )
    const { getClientNames } = await import('@/services/elevationsService')

    await expect(getClientNames('client-1')).resolves.toEqual({ clientName: 'client-1', tenantName: null })
  })
})
