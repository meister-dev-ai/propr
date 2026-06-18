// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, it, expect, vi, beforeEach } from 'vitest'

// We test the middleware behaviour by inspecting what fetch receives
function mockFetch(status: number, body: unknown = {}) {
  const response = new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
  vi.mocked(global.fetch).mockResolvedValueOnce(response)
}

function createJwt(exp: number) {
  const encode = (value: object) => Buffer.from(JSON.stringify(value)).toString('base64url')
  return `${encode({ alg: 'HS256', typ: 'JWT' })}.${encode({ exp })}.signature`
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

  it('injects the Authorization header from the in-memory access token', async () => {
    const session = await import('@/composables/useSession')
    // Future-dated JWT so the proactive (<60 s to expiry) refresh does not fire.
    const jwt = createJwt(Math.floor(Date.now() / 1000) + 3600)
    session.useSession().setAccessToken(jwt)
    mockFetch(200, [])
    const client = createAdminClient()
    await client.GET('/clients', {})
    // openapi-fetch calls fetch(request) — headers are on the Request object (first arg)
    const [requestArg] = vi.mocked(global.fetch).mock.calls[0]
    const headers = (requestArg as Request).headers
    expect(headers.get('authorization')).toBe(`Bearer ${jwt}`)
  })

  it('uses the active runtime base URL for admin and refresh requests', async () => {
    const runtimeContext = await import('@/app/runtime/runtimeContext')
    runtimeContext.setActiveRuntime({ mode: 'mock', isMock: true, apiBaseUrl: '/runtime-api' })

    const session = await import('@/composables/useSession')
    // Future-dated JWT so the proactive refresh does not fire (no extra fetch).
    session.useSession().setAccessToken(createJwt(Math.floor(Date.now() / 1000) + 3600))

    const requestResponse = new Response(JSON.stringify([]), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })

    vi.mocked(global.fetch).mockResolvedValueOnce(requestResponse)

    const client = createAdminClient({ baseUrl: '/runtime-api' })
    await client.GET('/clients', {})

    const fetchCalls = vi.mocked(global.fetch).mock.calls
    expect(fetchCalls).toHaveLength(1)
    expect((fetchCalls[0]?.[0] as Request).url).toContain('http://localhost:3000/runtime-api/clients')
  })

  it('omits Authorization header when no token is present', async () => {
    mockFetch(200, [])
    const client = createAdminClient()
    await client.GET('/clients', {})
    const [requestArg] = vi.mocked(global.fetch).mock.calls[0]
    const headers = (requestArg as Request).headers
    expect(headers.get('authorization')).toBeNull()
  })

  it('throws UnauthorizedError when the request is unauthorized', async () => {
    const session = await import('@/composables/useSession')
    session.useSession().setAccessToken(createJwt(Math.floor(Date.now() / 1000) + 3600))
    mockFetch(401, { error: 'Unauthorized' }) // GET → 401
    mockFetch(401, { error: 'Unauthorized' }) // cookie refresh also fails
    const client = createAdminClient()

    await expect(client.GET('/clients', {})).rejects.toBeInstanceOf(UnauthorizedError)
    // Failed refresh ends the session: the in-memory access token is cleared.
    expect(session.useSession().getAccessToken()).toBeNull()
  })

  it('refreshes the access token without surfacing UnauthorizedError on the same response', async () => {
    const session = await import('@/composables/useSession')
    const sessionApi = session.useSession()
    sessionApi.setAccessToken(createJwt(Math.floor(Date.now() / 1000) + 3600))

    vi.mocked(global.fetch)
      .mockResolvedValueOnce(new Response(JSON.stringify({ error: 'Unauthorized' }), {
        status: 401,
        headers: { 'Content-Type': 'application/json' },
      }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ accessToken: 'fresh-token' }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ clientRoles: {} }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }))
      .mockResolvedValueOnce(new Response(JSON.stringify([]), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }))

    const client = createAdminClient()
    const { response, data } = await client.GET('/clients', {})

    expect(response.status).toBe(200)
    expect(data).toEqual([])
    expect(sessionApi.getAccessToken()).toBe('fresh-token')
    expect(vi.mocked(global.fetch)).toHaveBeenCalledTimes(4)
  })

  it('extracts API validation messages from problem payloads', () => {
    expect(getApiErrorMessage({ error: 'Selected repository is stale.' }, 'fallback')).toBe('Selected repository is stale.')
    expect(getApiErrorMessage({ detail: 'Scope is disabled.' }, 'fallback')).toBe('Scope is disabled.')
    expect(getApiErrorMessage({ errors: { repoFilters: ['Repository is required.'] } }, 'fallback')).toBe('Repository is required.')
    expect(getApiErrorMessage(undefined, 'fallback')).toBe('fallback')
  })
})
