// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'

const ACCESS_TOKEN_KEY = 'meisterpropr_access_token'
const REFRESH_TOKEN_KEY = 'meisterpropr_refresh_token'
const CLIENT_ROLES_KEY = 'meisterpropr_client_roles'
const CAPABILITIES_KEY = 'meisterpropr_capabilities'
const TENANT_ROLES_KEY = 'meisterpropr_tenant_roles'
const LOCAL_PASSWORD_KEY = 'meisterpropr_has_local_password'

function encodeBase64Url(value: string): string {
  return btoa(value)
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/g, '')
}

function createJwt(
  globalRole: 'Admin' | 'User',
  expSecondsFromNow = 3600,
  overrides: Record<string, unknown> = {},
): string {
  const header = encodeBase64Url(JSON.stringify({ alg: 'none', typ: 'JWT' }))
  const payload = encodeBase64Url(JSON.stringify({
    global_role: globalRole,
    unique_name: 'mock.user',
    exp: Math.floor(Date.now() / 1000) + expSecondsFromNow,
    probe: 'a~',
    ...overrides,
  }))

  return `${header}.${payload}.signature`
}

async function loadUseSession() {
  vi.resetModules()
  const module = await import('@/composables/useSession')
  return module.useSession
}

describe('useSession', () => {
  beforeEach(() => {
    localStorage.clear()
    sessionStorage.clear()
  })

  it('setTokens stores access and refresh tokens', async () => {
    const useSession = await loadUseSession()
    const { setTokens, getAccessToken, getRefreshToken } = useSession()

    setTokens('access-token', 'refresh-token')

    expect(sessionStorage.setItem).toHaveBeenCalledWith(ACCESS_TOKEN_KEY, 'access-token')
    expect(localStorage.setItem).toHaveBeenCalledWith(REFRESH_TOKEN_KEY, 'refresh-token')
    expect(getAccessToken()).toBe('access-token')
    expect(getRefreshToken()).toBe('refresh-token')
  })

  it('clearTokens removes tokens and client roles', async () => {
    const useSession = await loadUseSession()
    const { setTokens, setClientRoles, clearTokens, getAccessToken, getRefreshToken, clientRoles } = useSession()

    setTokens('access-token', 'refresh-token')
    setClientRoles({ 'client-1': 1 })
    clearTokens()

    expect(sessionStorage.removeItem).toHaveBeenCalledWith(ACCESS_TOKEN_KEY)
    expect(localStorage.removeItem).toHaveBeenCalledWith(REFRESH_TOKEN_KEY)
    expect(sessionStorage.removeItem).toHaveBeenCalledWith(CLIENT_ROLES_KEY)
    expect(getAccessToken()).toBeNull()
    expect(getRefreshToken()).toBeNull()
    expect(clientRoles.value).toEqual({})
  })

  it('hasClientRole returns true for an assigned client user role', async () => {
    const useSession = await loadUseSession()
    const { setClientRoles, hasClientRole } = useSession()

    setClientRoles({ 'client-1': 0 })

    expect(hasClientRole('client-1', 0)).toBe(true)
    expect(hasClientRole('client-1', 1)).toBe(false)
    expect(hasClientRole('client-2', 0)).toBe(false)
  })

  it('hasClientRole returns true for global admins without client assignments', async () => {
    const useSession = await loadUseSession()
    const { setAccessToken, hasClientRole, isAdmin } = useSession()

    setAccessToken(createJwt('Admin'))

    expect(isAdmin.value).toBe(true)
    expect(hasClientRole('any-client', 1)).toBe(true)
  })

  it('decodes base64url JWT payloads for role and username claims', async () => {
    const useSession = await loadUseSession()
    const session = useSession()
    const token = createJwt('Admin', 3600, { unique_name: 'admin.user' })

    expect(token.split('.')[1]).toMatch(/[-_]/)

    session.setAccessToken(token)

    expect(session.getGlobalRole()).toBe('Admin')
    expect(session.getUsername()).toBe('admin.user')
    expect(session.isAdmin.value).toBe(true)
    expect(session.accessTokenExpiresIn()).toBeGreaterThan(0)
  })

  it('fails closed when the access token payload is malformed', async () => {
    const useSession = await loadUseSession()
    const session = useSession()

    session.setAccessToken('header.%%%.signature')

    expect(session.getGlobalRole()).toBeNull()
    expect(session.getUsername()).toBeNull()
    expect(session.isAdmin.value).toBe(false)
    expect(session.accessTokenExpiresIn()).toBe(0)
  })

  it('isAuthenticated is true after setting an access token', async () => {
    const useSession = await loadUseSession()
    const { setAccessToken, isAuthenticated } = useSession()

    setAccessToken(createJwt('User'))

    expect(isAuthenticated.value).toBe(true)
  })

  it('loadClientRoles stores the fetched roles and session auth state', async () => {
    const useSession = await loadUseSession()
    const session = useSession()
    vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify({
      globalRole: 'User',
      clientRoles: { 'client-1': 1 },
      hasLocalPassword: true,
    }), { status: 200 }))

    session.setAccessToken(createJwt('User'))
    await session.loadClientRoles()

    expect(fetch).toHaveBeenCalledWith('http://localhost/api/auth/me', {
      headers: { Authorization: `Bearer ${session.getAccessToken()}` },
    })
    expect(session.clientRoles.value).toEqual({ 'client-1': 1 })
    expect(sessionStorage.getItem(CLIENT_ROLES_KEY)).toBe(JSON.stringify({ 'client-1': 1 }))
    expect(session.hasLocalPassword.value).toBe(true)
    expect(sessionStorage.getItem(LOCAL_PASSWORD_KEY)).toBe('true')
  })

  it('loadClientRoles clears stale roles when auth/me fails', async () => {
    const useSession = await loadUseSession()
    const session = useSession()
    vi.mocked(fetch).mockResolvedValue(new Response(null, { status: 401 }))

    session.setAccessToken(createJwt('User'))
    session.setClientRoles({ 'client-1': 1 })
    session.setHasLocalPassword(true)
    await session.loadClientRoles()

    expect(session.clientRoles.value).toEqual({})
    expect(session.hasLocalPassword.value).toBe(false)
  })

  it('falls back safely when persisted session JSON is malformed at module initialization', async () => {
    sessionStorage.setItem(CLIENT_ROLES_KEY, '{bad json')
    sessionStorage.setItem(CAPABILITIES_KEY, 'not-json')
    sessionStorage.setItem(TENANT_ROLES_KEY, 'null]')

    const useSession = await loadUseSession()
    const { clientRoles, capabilities, tenantRoles } = useSession()

    expect(clientRoles.value).toEqual({})
    expect(capabilities.value).toEqual([])
    expect(tenantRoles.value).toEqual({})
  })

  it('isAuthenticated is false after clearTokens', async () => {
    const useSession = await loadUseSession()
    const { setAccessToken, clearTokens, isAuthenticated } = useSession()

    setAccessToken(createJwt('User'))
    clearTokens()

    expect(isAuthenticated.value).toBe(false)
  })
})
