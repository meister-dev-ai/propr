import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createRuntime } from '@/app/runtime/createRuntime'
import { resetActiveRuntime, setActiveRuntime } from '@/app/runtime/runtimeContext'
import {
  buildTenantExternalCallbackPath,
  buildTenantExternalChallengePath,
  buildTenantLocalLoginPath,
  buildTenantProvidersPath,
  tenantApiRequest,
} from '@/services/tenantApiClient'

const getAccessTokenMock = vi.fn(() => null)

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    getAccessToken: getAccessTokenMock,
  }),
}))

describe('tenantApiRequest', () => {
  const originalFetch = globalThis.fetch

  beforeEach(() => {
    getAccessTokenMock.mockReset()
    getAccessTokenMock.mockReturnValue(null)
  })

  afterEach(() => {
    globalThis.fetch = originalFetch
    resetActiveRuntime()
  })

  it('resolves tenant admin paths against the active runtime api base url', async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify([]), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    }))
    globalThis.fetch = fetchMock as typeof fetch
    setActiveRuntime(createRuntime({ mode: 'live', apiBaseUrl: '/api' }))

    await tenantApiRequest('/admin/tenants', { requireAuth: true })

    expect(fetchMock).toHaveBeenCalledWith('/api/admin/tenants', expect.objectContaining({
      headers: expect.any(Headers),
    }))
  })

  it('does not double-prefix absolute urls', async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify([]), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    }))
    globalThis.fetch = fetchMock as typeof fetch
    setActiveRuntime(createRuntime({ mode: 'live', apiBaseUrl: '/api' }))

    await tenantApiRequest('https://example.test/admin/tenants', {})

    expect(fetchMock).toHaveBeenCalledWith('https://example.test/admin/tenants', expect.any(Object))
  })

  it('builds tenant auth request paths without duplicating the api base', () => {
    setActiveRuntime(createRuntime({ mode: 'live', apiBaseUrl: '/api' }))

    expect(buildTenantProvidersPath('acme')).toBe('/auth/tenants/acme/providers')
    expect(buildTenantLocalLoginPath('acme')).toBe('/auth/tenants/acme/local-login')
    expect(buildTenantExternalChallengePath('acme', 'oidc')).toBe('/api/auth/external/challenge/acme/oidc')
    expect(buildTenantExternalCallbackPath('acme')).toBe('/api/auth/external/callback/acme')
  })
})
