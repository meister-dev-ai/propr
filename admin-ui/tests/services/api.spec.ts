// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, it, expect, vi, beforeEach } from 'vitest'

// We test the middleware behaviour by inspecting what fetch receives
const ACCESS_TOKEN_KEY = 'meisterpropr_access_token'

function mockFetch(status: number, body: unknown = {}) {
  const response = new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
  vi.mocked(global.fetch).mockResolvedValueOnce(response)
}

describe('createAdminClient', () => {
  let createAdminClient: typeof import('@/services/api').createAdminClient
  let getApiErrorMessage: typeof import('@/services/api').getApiErrorMessage
  // Must be re-imported after vi.resetModules() so instanceof uses the same class
  let UnauthorizedError: typeof import('@/services/api').UnauthorizedError

  beforeEach(async () => {
    vi.resetModules()
    const api = await import('@/services/api')
    createAdminClient = api.createAdminClient
    getApiErrorMessage = api.getApiErrorMessage
    UnauthorizedError = api.UnauthorizedError
  })

  it('injects Authorization header from sessionStorage in requests', async () => {
    const session = await import('@/composables/useSession')
    session.useSession().setAccessToken('stored-token')
    mockFetch(200, [])
    const client = createAdminClient()
    await client.GET('/clients', {})
    // openapi-fetch calls fetch(request) — headers are on the Request object (first arg)
    const [requestArg] = vi.mocked(global.fetch).mock.calls[0]
    const headers = (requestArg as Request).headers
    expect(headers.get('authorization')).toBe('Bearer stored-token')
  })

  it('omits Authorization header when no token is present', async () => {
    mockFetch(200, [])
    const client = createAdminClient()
    await client.GET('/clients', {})
    const [requestArg] = vi.mocked(global.fetch).mock.calls[0]
    const headers = (requestArg as Request).headers
    expect(headers.get('authorization')).toBeNull()
  })

  it('throws UnauthorizedError and clears session on 401', async () => {
    const session = await import('@/composables/useSession')
    session.useSession().setAccessToken('stored-token')
    mockFetch(401)
    const client = createAdminClient()
    await expect(client.GET('/clients', {})).rejects.toBeInstanceOf(UnauthorizedError)
    expect(sessionStorage.removeItem).toHaveBeenCalledWith(ACCESS_TOKEN_KEY)
  })

  it('extracts API validation messages from problem payloads', () => {
    expect(getApiErrorMessage({ error: 'Selected repository is stale.' }, 'fallback')).toBe('Selected repository is stale.')
    expect(getApiErrorMessage({ detail: 'Scope is disabled.' }, 'fallback')).toBe('Scope is disabled.')
    expect(getApiErrorMessage({ errors: { repoFilters: ['Repository is required.'] } }, 'fallback')).toBe('Repository is required.')
    expect(getApiErrorMessage(undefined, 'fallback')).toBe('fallback')
  })
})
